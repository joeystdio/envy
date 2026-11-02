import Foundation

/// The user's shell setup, as far as Envy can tell without changing anything.
public struct ShellEnvironment: Equatable, Sendable {
    public enum Kind: String, Sendable { case zsh, bash, fish, other }

    /// The login shell from the user database, e.g. `/bin/zsh`.
    public var shellPath: String
    /// Where zsh reads `.zshrc` from when `ZDOTDIR` points somewhere other than home.
    public var zdotdir: String?

    public init(shellPath: String, zdotdir: String? = nil) {
        self.shellPath = shellPath
        self.zdotdir = zdotdir
    }

    public var name: String { (shellPath as NSString).lastPathComponent }

    public var kind: Kind {
        switch name {
        case "zsh": .zsh
        case "bash": .bash
        case "fish": .fish
        default: .other
        }
    }

    /// The shell Envy can drive: the login shell when it's zsh or bash, otherwise zsh.
    public var supportedShell: String { kind == .zsh || kind == .bash ? shellPath : "/bin/zsh" }

    public static func loginShellPath() -> String {
        if let entry = getpwuid(getuid()), let shell = entry.pointee.pw_shell {
            let path = String(cString: shell)
            if !path.isEmpty { return path }
        }
        return ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"
    }

    /// Asks zsh where its startup files live. Non-interactive zsh only reads `~/.zshenv`, which is where
    /// `ZDOTDIR` is normally set, so this is quick and has no side effects worth worrying about.
    public static func detect(home: URL, shellPath: String = loginShellPath()) -> ShellEnvironment {
        let script = #"print -r -- "${ZDOTDIR:-$HOME}" > "$ENVY_PROBE_OUT""#
        let dir = ShellProbe.run("/bin/zsh", arguments: ["-c"], script: script, home: home, timeout: 3)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let zdotdir = dir.flatMap { $0.isEmpty || $0 == home.path ? nil : $0 }
        return ShellEnvironment(shellPath: shellPath, zdotdir: zdotdir)
    }
}

/// What a freshly opened terminal looks like.
public struct ShellInspection: Equatable, Sendable {
    public var hookLoaded: Bool
    /// Display names of the coding agents found on the shell's PATH.
    public var agents: [String]
}

/// Runs short scripts in the user's own shell, outside the app's environment.
public enum ShellProbe {
    public static let knownAgents: [(command: String, name: String)] = [
        ("claude", "Claude Code"), ("codex", "Codex"), ("cursor-agent", "Cursor CLI"), ("gemini", "Gemini CLI"),
        ("aider", "Aider"), ("opencode", "opencode"), ("amp", "Amp"), ("goose", "Goose"), ("copilot", "Copilot CLI"),
    ]

    /// Opens an interactive login shell the way a terminal tab does, then reports whether Envy's hook loaded
    /// and which coding agents are on its PATH. Nil when the shell didn't get that far within `timeout`.
    public static func inspect(shell: String, home: URL, timeout: TimeInterval = 15) -> ShellInspection? {
        let commands = knownAgents.map(\.command).joined(separator: " ")
        let script = """
        if type __envy_hook >/dev/null 2>&1; then echo hook:yes; else echo hook:no; fi > "$ENVY_PROBE_OUT"
        for c in \(commands); do command -v "$c" >/dev/null 2>&1 && echo "agent:$c" >> "$ENVY_PROBE_OUT"; done
        :
        """
        guard let out = run(shell, arguments: ["-l", "-i", "-c"], script: script, home: home, timeout: timeout) else { return nil }
        let lines = Set(out.split(separator: "\n").map(String.init))
        guard lines.contains("hook:yes") || lines.contains("hook:no") else { return nil }
        return ShellInspection(
            hookLoaded: lines.contains("hook:yes"),
            agents: knownAgents.filter { lines.contains("agent:\($0.command)") }.map(\.name)
        )
    }

    /// Runs `script` with HOME set to `home` and returns what it wrote to `$ENVY_PROBE_OUT`.
    /// Output goes to a file rather than a pipe: startup files often print banners, and a background job they
    /// start could keep a pipe open long after the shell exits.
    static func run(_ shell: String, arguments: [String], script: String, home: URL, timeout: TimeInterval) -> String? {
        let outFile = FileManager.default.temporaryDirectory.appendingPathComponent("envy-probe-\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath: outFile.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { return nil }
        defer { try? FileManager.default.removeItem(at: outFile) }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: shell)
        process.arguments = arguments + [script]
        var environment = ProcessInfo.processInfo.environment.filter {
            !$0.key.hasPrefix("__ENVY") && $0.key != "ENVY_DIR" && $0.key != "ZDOTDIR"
        }
        environment["HOME"] = home.path
        environment["ENVY_PROBE_OUT"] = outFile.path
        process.environment = environment
        process.currentDirectoryURL = home
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice

        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        do { try process.run() } catch { return nil }
        if finished.wait(timeout: .now() + timeout) == .timedOut {
            process.terminate()
            return nil
        }
        return try? String(contentsOf: outFile, encoding: .utf8)
    }
}

/// An `export NAME=value` line found in a shell startup file.
public struct RcExport: Identifiable, Hashable, Sendable {
    public var file: URL
    public var line: Int
    public var key: String
    public var value: String
    public var quote: DotEnv.Quote

    public var id: String { "\(file.path):\(line)" }

    /// The variable to import: secret when the name looks like a credential, and expanding when the shell
    /// would have expanded it (anything not single-quoted that contains `$`).
    public var variable: EnvVar {
        EnvVar(key: key, value: value, isSecret: EnvVar.looksSecret(key), expands: quote != .single && value.contains("$"))
    }
}

public enum RcScanner {
    /// Search paths are left alone: importing them would add the same entries twice.
    static let ignoredKeys: Set<String> = ["PATH", "MANPATH", "INFOPATH", "FPATH"]

    /// Single-line `export NAME=value` statements outside Envy's own block.
    public static func exports(in text: String, file: URL) -> [RcExport] {
        var inEnvyBlock = false
        var found: [RcExport] = []
        for (index, line) in text.components(separatedBy: "\n").enumerated() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == RcIntegration.beginMarker { inEnvyBlock = true; continue }
            if trimmed == RcIntegration.endMarker { inEnvyBlock = false; continue }
            guard !inEnvyBlock, trimmed.hasPrefix("export "), trimmed.contains("=") else { continue }
            // Parsed one line at a time so an unbalanced quote can't swallow the rest of the file.
            for entry in DotEnv.parse(trimmed) where EnvVar.keyProblem(entry.key) == nil && !ignoredKeys.contains(entry.key) {
                found.append(RcExport(file: file, line: index + 1, key: entry.key, value: entry.value, quote: entry.quote))
            }
        }
        return found
    }

    /// Exports from every readable file in `files`, in order.
    public static func exports(inFiles files: [URL]) -> [RcExport] {
        files.flatMap { file -> [RcExport] in
            guard let text = try? String(contentsOf: file.resolvingSymlinksInPath(), encoding: .utf8) else { return [] }
            return exports(in: text, file: file)
        }
    }
}
