import Foundation

/// What the `envy` command sends the running app over its socket.
public struct CommandRequest: Codable, Equatable, Sendable {
    public var arguments: [String]
    public var context: Context

    public struct Context: Codable, Equatable, Sendable {
        public var directories: [String]
        public var loaded: [String]
    }

    public init(arguments: [String], directories: [String], loaded: [String]) {
        self.arguments = arguments
        self.context = Context(directories: directories, loaded: loaded)
    }

    public var commandContext: EnvyCommand.Context {
        EnvyCommand.Context(directories: context.directories, loaded: context.loaded)
    }
}

public struct CommandResponse: Codable, Equatable, Sendable {
    public var output: String
    public var error: String?
    public var exitCode: Int32
    /// The app wants to come to the front (to show a sheet); the CLI activates it, which macOS allows more readily.
    public var activate: Bool
    /// The app's process, so a sandbox instance is activated rather than the installed one.
    public var processID: Int32

    public init(output: String = "", error: String? = nil, exitCode: Int32 = 0, activate: Bool = false) {
        self.output = output
        self.error = error
        self.exitCode = exitCode
        self.activate = activate
        self.processID = getpid()
    }
}

/// A Unix domain socket in the user's private shell folder. Only processes of the same user can connect.
public enum CommandSocket {
    public static let fileName = "envy.sock"
    static let maxMessage = 1 << 20

    public enum Failure: Error, Equatable {
        case pathTooLong
        case notRunning
        case io(Int32)
    }

    static func address(_ path: String) throws -> sockaddr_un {
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        let capacity = MemoryLayout.size(ofValue: addr.sun_path)
        guard bytes.count < capacity else { throw Failure.pathTooLong }
        withUnsafeMutableBytes(of: &addr.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        return addr
    }

    /// Sends one request and waits for the answer. Throws `.notRunning` when nobody listens at `path`.
    public static func send(_ request: CommandRequest, to path: String, timeout: TimeInterval = 120) throws -> CommandResponse {
        var addr = try address(path)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Failure.io(errno) }
        defer { close(fd) }
        let connected = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        guard connected == 0 else {
            if errno == ENOENT || errno == ECONNREFUSED { throw Failure.notRunning }
            throw Failure.io(errno)
        }
        setTimeout(fd, timeout)
        try writeAll(fd, JSONEncoder().encode(request))
        shutdown(fd, SHUT_WR)
        return try JSONDecoder().decode(CommandResponse.self, from: readAll(fd))
    }

    /// Listens at `path`, replacing a stale socket file, and calls `handle` for each request.
    public final class Server: @unchecked Sendable {
        public let path: String
        private let handle: @Sendable (CommandRequest) async -> CommandResponse
        private let queue = DispatchQueue(label: "dev.envy.command-socket")
        private var fd: Int32 = -1
        private var source: DispatchSourceRead?

        public init(path: String, handle: @escaping @Sendable (CommandRequest) async -> CommandResponse) {
            self.path = path
            self.handle = handle
        }

        public func start() throws {
            var addr = try CommandSocket.address(path)
            unlink(path)
            fd = socket(AF_UNIX, SOCK_STREAM, 0)
            guard fd >= 0 else { throw Failure.io(errno) }
            let bound = withUnsafePointer(to: &addr) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
            }
            guard bound == 0, chmod(path, 0o600) == 0, listen(fd, 16) == 0 else {
                let code = errno
                close(fd)
                fd = -1
                throw Failure.io(code)
            }
            let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
            source.setEventHandler { [weak self] in self?.acceptClient() }
            source.resume()
            self.source = source
        }

        public func stop() {
            source?.cancel()
            source = nil
            if fd >= 0 { close(fd); fd = -1 }
            unlink(path)
        }

        private func acceptClient() {
            let client = accept(fd, nil, nil)
            guard client >= 0 else { return }
            var uid: uid_t = 0, gid: gid_t = 0
            guard getpeereid(client, &uid, &gid) == 0, uid == getuid() else { close(client); return }
            CommandSocket.setTimeout(client, 10)
            guard let data = try? CommandSocket.readAll(client),
                  let request = try? JSONDecoder().decode(CommandRequest.self, from: data)
            else {
                close(client)
                return
            }
            let handle = self.handle
            let queue = self.queue
            Task {
                let response = await handle(request)
                queue.async {
                    if let out = try? JSONEncoder().encode(response) { try? CommandSocket.writeAll(client, out) }
                    close(client)
                }
            }
        }
    }

    static func setTimeout(_ fd: Int32, _ seconds: TimeInterval) {
        var tv = timeval(tv_sec: Int(seconds), tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
    }

    static func writeAll(_ fd: Int32, _ data: Data) throws {
        try data.withUnsafeBytes { raw in
            var offset = 0
            while offset < raw.count {
                let n = write(fd, raw.baseAddress! + offset, raw.count - offset)
                if n < 0 {
                    if errno == EINTR { continue }
                    throw Failure.io(errno)
                }
                offset += n
            }
        }
    }

    static func readAll(_ fd: Int32) throws -> Data {
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            let n = read(fd, &buffer, buffer.count)
            if n == 0 { return data }
            if n < 0 {
                if errno == EINTR { continue }
                throw Failure.io(errno)
            }
            data.append(buffer, count: n)
            guard data.count <= maxMessage else { throw Failure.io(EMSGSIZE) }
        }
    }
}
