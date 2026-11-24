import Foundation

/// Where a group of coding agents reads instructions Envy can own outright, so it never edits the user's files.
public struct AgentTarget: Identifiable, Hashable, Sendable {
    public enum Kind: Hashable, Sendable {
        /// Loaded in every session (Claude Code's `~/.claude/rules`).
        case rule
        /// A skill (`SKILL.md` with a name and description), loaded when the task calls for it.
        case skill
    }

    public var id: String
    public var name: String
    /// The agents this file reaches, with the command that runs each, to find them on the shell's PATH.
    public var agents: [(command: String, name: String)]
    public var kind: Kind
    public var file: URL
    /// Folders the agents create when they're used; finding one means the agent is set up here.
    public var configDirectories: [URL]

    public static func == (a: AgentTarget, b: AgentTarget) -> Bool { a.id == b.id && a.file == b.file }
    public func hash(into hasher: inout Hasher) { hasher.combine(id); hasher.combine(file) }
}

/// Writes and removes Envy's instructions for coding agents, so they use the envy command instead of
/// `.env` files and exports in shell startup files.
///
/// Claude Code gets a rule, which it reads in every session. Codex, Gemini CLI, Cursor, Copilot CLI, opencode,
/// Amp and Goose all read skills from `~/.agents/skills`, so one skill there reaches them all.
public enum AgentInstructions {
    public enum Status: Equatable, Sendable {
        case notInstalled
        case installed
        /// Written by an older Envy; installing again brings it up to date.
        case outdated
        /// A file of the same name that Envy didn't write; Envy leaves it alone.
        case foreign
    }

    /// `environment` holds `CLAUDE_CONFIG_DIR` when the user's shell sets it.
    public static func targets(home: URL, environment: [String: String] = [:]) -> [AgentTarget] {
        var claude = home.appendingPathComponent(".claude", isDirectory: true)
        if let custom = environment["CLAUDE_CONFIG_DIR"], !custom.isEmpty {
            claude = URL(fileURLWithPath: (custom as NSString).expandingTildeInPath, isDirectory: true)
        }
        let config = home.appendingPathComponent(".config", isDirectory: true)
        return [
            AgentTarget(
                id: "claude", name: "Claude Code", agents: [("claude", "Claude Code")], kind: .rule,
                file: claude.appendingPathComponent("rules/envy.md"), configDirectories: [claude]
            ),
            AgentTarget(
                id: "agents", name: "Other coding agents",
                agents: [
                    ("codex", "Codex"), ("gemini", "Gemini CLI"), ("cursor-agent", "Cursor CLI"), ("copilot", "Copilot CLI"),
                    ("opencode", "opencode"), ("amp", "Amp"), ("goose", "Goose"),
                ],
                kind: .skill,
                file: home.appendingPathComponent(".agents/skills/envy/SKILL.md"),
                configDirectories: [
                    home.appendingPathComponent(".agents"), home.appendingPathComponent(".codex"),
                    home.appendingPathComponent(".gemini"), home.appendingPathComponent(".cursor"),
                    home.appendingPathComponent(".copilot"), config.appendingPathComponent("opencode"),
                    config.appendingPathComponent("amp"), config.appendingPathComponent("goose"),
                ]
            ),
        ]
    }

    /// The environment variables `targets(home:environment:)` reads.
    public static let configVariables = ["CLAUDE_CONFIG_DIR"]

    // MARK: Content

    static let marker = "<!-- Written by Envy.app, which keeps it up to date. Remove it in Envy › Shell Integration › Coding agents; local changes are overwritten. -->"

    /// The skill's description decides when an agent loads it, so it names the situations.
    static let description = "Environment variables and secrets on this Mac are managed by Envy. Use this whenever a task needs an environment variable, API key, token, password or connection string; when a command fails because one is missing; or before creating a .env file or adding an export to a shell startup file."

    public static func content(for kind: AgentTarget.Kind) -> String {
        switch kind {
        case .rule:
            return "\(marker)\n\n# Environment variables and secrets: Envy\n\n\(guidance)\n"
        case .skill:
            return "---\nname: envy\ndescription: \(description)\n---\n\n\(marker)\n\n# Envy\n\n\(guidance)\n"
        }
    }

    /// Short on purpose: `envy help` is the full reference, so this rarely changes.
    static let guidance = """
    This Mac manages environment variables and secrets with Envy, not with `.env` files or exports in `~/.zshrc`. \
    Global variables load in every terminal; a project's variables load inside the folders linked to it. \
    Secrets live in the macOS Keychain.

    - Run `envy help` before working with environment variables, and use the `envy` command \
    (`~/.config/envy/bin/envy` if it isn't on PATH).
    - Missing variable: `envy status` shows what loads here, `envy list` what's defined. Secret values are never shown.
    - Plain settings: `envy set NAME=value` goes to the project linked to this folder; add `--project NAME` or `--global` for another.
    - Secrets: never ask the user to paste one into the chat, and never write one into a file. Run \
    `envy add NAME --secret`: Envy opens for the user to paste it, so tell them it's waiting.
    - Found a `.env` file: offer `envy import FILE`, which moves its values into Envy without showing them.
    - Your environment was fixed when you started. After a change, run commands as `envy exec -- <command>`.
    - Never print secret values: no `echo` of secret variables, no unfiltered `env` or `printenv`, and don't \
    read `~/.config/envy/vars.sh`.
    - Ask the user before removing variables or switching anything off.
    """

    // MARK: Install and remove

    public static func status(of target: AgentTarget) -> Status {
        guard let text = try? String(contentsOf: target.file, encoding: .utf8) else { return .notInstalled }
        guard text.contains(marker) else { return .foreign }
        return text == content(for: target.kind) ? .installed : .outdated
    }

    /// Writes the file, or brings it up to date. Refuses to replace a file Envy didn't write.
    public static func install(_ target: AgentTarget) throws {
        if status(of: target) == .foreign {
            throw CocoaError(.fileWriteFileExists, userInfo: [
                NSLocalizedDescriptionKey: "\(target.file.path) already exists and wasn't written by Envy, so Envy left it alone.",
            ])
        }
        try FileManager.default.createDirectory(at: target.file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try content(for: target.kind).write(to: target.file, atomically: true, encoding: .utf8)
    }

    /// Removes the file if Envy wrote it, and the skill's own folder with it.
    public static func uninstall(_ target: AgentTarget) throws {
        guard let text = try? String(contentsOf: target.file, encoding: .utf8), text.contains(marker) else { return }
        let fm = FileManager.default
        try fm.removeItem(at: target.file)
        if target.kind == .skill {
            let folder = target.file.deletingLastPathComponent()
            if (try? fm.contentsOfDirectory(atPath: folder.path))?.isEmpty == true { try fm.removeItem(at: folder) }
        }
    }
}
