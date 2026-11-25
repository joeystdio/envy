import Foundation

public enum ShellOutputError: LocalizedError, Equatable {
    case invalidSyntax(details: String)

    public var errorDescription: String? {
        switch self {
        case .invalidSyntax(let details):
            "Your shells weren't updated because the generated script has a syntax error. \(details)"
        }
    }
}

/// Writes `init.sh`, `vars.sh` and `state` into the shell directory (normally `~/.config/envy`), and with an
/// `executable`, the `bin/envy` launcher that runs it as the envy command.
public struct ShellOutput {
    public let directory: URL
    public var executable: String?
    /// Exported by the launcher, e.g. `ENVY_HOME` for a sandbox.
    public var environment: [String: String]

    public init(directory: URL, executable: String? = nil, environment: [String: String] = [:]) {
        self.directory = directory
        self.executable = executable
        self.environment = environment
    }

    public var launcherURL: URL { directory.appendingPathComponent("bin/envy") }
    public var hookURL: URL { directory.appendingPathComponent("init.sh") }
    public var varsURL: URL { directory.appendingPathComponent("vars.sh") }
    public var stateURL: URL { directory.appendingPathComponent("state") }

    /// Brings the files up to date. The state stamp, which makes open shells reload, only changes when
    /// the variables did. Returns whether they changed.
    @discardableResult
    public func write(_ config: EnvConfig) throws -> Bool {
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        // The directory holds secrets; keep it private even if it already existed.
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)

        if (try? String(contentsOf: hookURL, encoding: .utf8)) != ShellScript.hook {
            try Self.atomicWrite(ShellScript.hook, to: hookURL, permissions: 0o644)
        }
        if let executable {
            let launcher = Self.launcher(executable: executable, environment: environment)
            if (try? String(contentsOf: launcherURL, encoding: .utf8)) != launcher {
                try fm.createDirectory(at: launcherURL.deletingLastPathComponent(), withIntermediateDirectories: true)
                try Self.atomicWrite(launcher, to: launcherURL, permissions: 0o755)
            }
        }

        let vars = ShellScript.vars(for: config)
        if (try? String(contentsOf: varsURL, encoding: .utf8)) == vars, fm.fileExists(atPath: stateURL.path) {
            return false
        }
        if let problem = ShellSyntax.check(vars) {
            throw ShellOutputError.invalidSyntax(details: Self.diagnose(config) ?? problem)
        }
        try Self.atomicWrite(vars, to: varsURL, permissions: 0o600)
        try Self.atomicWrite(UUID().uuidString + "\n", to: stateURL, permissions: 0o600)
        return true
    }

    /// A script that runs the app's executable as the envy command. Rewritten at every launch, so it follows
    /// the app when it moves.
    public static func launcher(executable: String, environment: [String: String] = [:]) -> String {
        var script = "#!/bin/sh\n# The envy command, for terminals, scripts and coding agents. Written by Envy.app; local changes are overwritten.\n"
        for (key, value) in environment.sorted(by: { $0.key < $1.key }) {
            script += "export \(key)=\(ShellScript.singleQuoted(value))\n"
        }
        script += "exec \(ShellScript.singleQuoted(executable)) --cli \"$@\"\n"
        return script
    }

    /// Names the expanding variable that breaks the script, which is the only way user input can.
    static func diagnose(_ config: EnvConfig) -> String? {
        for project in config.scopes where project.isEnabled {
            for v in ShellScript.loadable(project) where v.expands {
                if let problem = ShellSyntax.check("x=\(ShellScript.expandable(v.value))\n") {
                    return "Check \(v.key) in \(project.name), or turn off “Expand” for it. (\(problem))"
                }
            }
        }
        return nil
    }

    static func atomicWrite(_ text: String, to url: URL, permissions: Int) throws {
        let temp = url.deletingLastPathComponent().appendingPathComponent(".\(url.lastPathComponent).\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath: temp.path, contents: Data(text.utf8), attributes: [.posixPermissions: permissions]) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        if rename(temp.path, url.path) != 0 {
            let code = errno
            try? FileManager.default.removeItem(at: temp)
            throw POSIXError(POSIXErrorCode(rawValue: code) ?? .EIO)
        }
    }
}

/// Parses a script with `zsh -n` and `bash -n` without running it.
public enum ShellSyntax {
    /// The first shell's complaint, or nil when every available shell accepts the script.
    public static func check(_ script: String) -> String? {
        let shells: [(path: String, args: [String])] = [
            ("/bin/zsh", ["-f", "-n"]),
            ("/bin/bash", ["--norc", "--noprofile", "-n"]),
        ]
        for shell in shells where FileManager.default.isExecutableFile(atPath: shell.path) {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: shell.path)
            process.arguments = shell.args
            process.environment = ["PATH": "/usr/bin:/bin"]
            let input = Pipe(), errors = Pipe()
            process.standardInput = input
            process.standardOutput = FileHandle.nullDevice
            process.standardError = errors
            do { try process.run() } catch { continue }
            input.fileHandleForWriting.write(Data(script.utf8))
            try? input.fileHandleForWriting.close()
            let message = String(decoding: errors.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            process.waitUntilExit()
            if process.terminationStatus != 0 {
                let name = (shell.path as NSString).lastPathComponent
                return "\(name): \(message.trimmingCharacters(in: .whitespacesAndNewlines))"
            }
        }
        return nil
    }
}
