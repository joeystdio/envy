import Foundation

/// The `envy` command line, minus what runs in the caller's own shell (`exec`, `reload`).
///
/// The command is parsed and run against the app's config here, so the app stays the only writer:
/// the CLI sends its arguments to the running app, which applies them as one undoable change.
/// Output never contains secret values.
public enum EnvyCommand: Equatable, Sendable {
    case help
    case status
    case list(project: String?)
    case set([Assignment], target: Target, expand: Bool, plain: Bool)
    case addSecret(String, target: Target)
    case remove([String], target: Target)
    case enable([String], target: Target, on: Bool)
    case addProject(name: String, folders: [String], enabled: Bool)
    case link(String, target: Target)
    case unlink(String, target: Target)
    case importFile(String, target: Target, keepExisting: Bool)
    case open(project: String?)

    public struct Assignment: Equatable, Sendable {
        public var key: String
        public var value: String
    }

    /// Which scope a change goes to. `.here` is the project linked to the working directory.
    public enum Target: Equatable, Sendable {
        case here
        case global
        case named(String)
    }

    public struct UsageError: LocalizedError, Equatable {
        public var message: String
        public var errorDescription: String? { message }
        init(_ message: String) { self.message = message }
    }

    // MARK: Parsing

    /// Parses arguments as typed after `envy`. Relative folders are resolved against `cwd`.
    public static func parse(_ arguments: [String], cwd: String) throws -> EnvyCommand {
        var args = arguments
        let verb = args.isEmpty ? "status" : args.removeFirst()

        var target = Target.here
        var targetGiven = false
        var flags = Set<String>()
        var folders: [String] = []
        var positional: [String] = []

        var i = 0
        while i < args.count {
            let arg = args[i]
            func value() throws -> String {
                guard i + 1 < args.count else { throw UsageError("\(arg) needs a value.") }
                i += 1
                return args[i]
            }
            switch arg {
            case "--project", "-p":
                let name = try value()
                guard !targetGiven else { throw UsageError("Choose one of --project and --global.") }
                target = name.caseInsensitiveCompare("Global") == .orderedSame ? .global : .named(name)
                targetGiven = true
            case "--global", "-g":
                guard !targetGiven else { throw UsageError("Choose one of --project and --global.") }
                target = .global
                targetGiven = true
            case "--folder":
                folders.append(absolute(try value(), cwd: cwd))
            case "--secret", "--expand", "--plain", "--off", "--keep-existing":
                flags.insert(arg)
            case "--help", "-h":
                return .help
            default:
                if arg.hasPrefix("-"), arg.count > 1, !arg.contains("=") {
                    throw UsageError("Unknown option \(arg). Run envy help.")
                }
                positional.append(arg)
            }
            i += 1
        }

        func allow(_ allowed: Set<String>, targetAllowed: Bool = true) throws {
            if let extra = flags.subtracting(allowed).sorted().first {
                throw UsageError("\(extra) doesn't apply to envy \(verb).")
            }
            if !targetAllowed && targetGiven { throw UsageError("envy \(verb) doesn't take --project or --global.") }
            if !folders.isEmpty && verb != "project" { throw UsageError("--folder only applies to envy project add.") }
        }

        switch verb {
        case "help", "-h", "--help":
            return .help
        case "status":
            try allow([], targetAllowed: false)
            return .status
        case "list", "ls":
            try allow([], targetAllowed: true)
            guard positional.count <= 1 else { throw UsageError("Usage: envy list [PROJECT]") }
            switch target {
            case .global: return .list(project: "Global")
            case .named(let name): return .list(project: name)
            case .here: return .list(project: positional.first)
            }
        case "set", "add":
            if flags.contains("--secret") {
                try allow(["--secret"])
                guard positional.count == 1, let key = positional.first, !key.contains("=") else {
                    throw UsageError("Usage: envy add NAME --secret  (no value: the user pastes it into Envy)")
                }
                try checkKey(key)
                return .addSecret(key, target: target)
            }
            try allow(["--expand", "--plain"])
            guard !positional.isEmpty else { throw UsageError("Usage: envy set NAME=VALUE [NAME=VALUE…]") }
            let assignments = try positional.map { item -> Assignment in
                guard let eq = item.firstIndex(of: "=") else {
                    throw UsageError("\(item) has no value. Use NAME=VALUE, or envy add \(item) --secret for a secret.")
                }
                let key = String(item[..<eq])
                try checkKey(key)
                return Assignment(key: key, value: String(item[item.index(after: eq)...]))
            }
            return .set(assignments, target: target, expand: flags.contains("--expand"), plain: flags.contains("--plain"))
        case "remove", "rm", "unset":
            try allow([])
            guard !positional.isEmpty else { throw UsageError("Usage: envy remove NAME… [--project PROJECT | --global]") }
            return .remove(positional, target: target)
        case "on", "off":
            try allow([])
            if positional.isEmpty && !targetGiven {
                throw UsageError("Name the variables, or the project with --project PROJECT (or --global).")
            }
            return .enable(positional, target: target, on: verb == "on")
        case "project":
            guard positional.first == "add" || positional.first == "new" else {
                throw UsageError("Usage: envy project add NAME [--folder PATH]")
            }
            try allow(["--off"], targetAllowed: false)
            let name = positional.dropFirst().joined(separator: " ").trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty else { throw UsageError("Usage: envy project add NAME [--folder PATH]") }
            return .addProject(name: name, folders: folders, enabled: !flags.contains("--off"))
        case "link", "unlink":
            try allow([])
            guard positional.count <= 1 else { throw UsageError("Usage: envy \(verb) [FOLDER] --project PROJECT") }
            guard case .named = target else { throw UsageError("Name the project: envy \(verb) [FOLDER] --project PROJECT") }
            let folder = absolute(positional.first ?? ".", cwd: cwd)
            return verb == "link" ? .link(folder, target: target) : .unlink(folder, target: target)
        case "import":
            try allow(["--keep-existing"])
            guard positional.count == 1 else { throw UsageError("Usage: envy import FILE [--project PROJECT | --global]") }
            return .importFile(absolute(positional[0], cwd: cwd), target: target, keepExisting: flags.contains("--keep-existing"))
        case "open":
            try allow([], targetAllowed: true)
            switch target {
            case .global: return .open(project: "Global")
            case .named(let name): return .open(project: name)
            case .here: return .open(project: positional.first)
            }
        default:
            throw UsageError("Unknown command \(verb). Run envy help.")
        }
    }

    static func checkKey(_ key: String) throws {
        if let problem = EnvVar.keyProblem(key) { throw UsageError("\(key): \(problem)") }
    }

    static func absolute(_ path: String, cwd: String) -> String {
        let expanded = (path as NSString).expandingTildeInPath
        let full = expanded.hasPrefix("/") ? expanded : (cwd as NSString).appendingPathComponent(expanded)
        return (full as NSString).standardizingPath
    }

    /// Commands that only read, so they can also run from the settings file when the app can't be reached.
    public var isReadOnly: Bool {
        switch self {
        case .help, .status, .list: return true
        default: return false
        }
    }

    // MARK: Running

    /// What the caller's process knows that the app doesn't.
    public struct Context: Sendable {
        /// The working directory, logical (`$PWD`) first, then physical, so symlinked folders match either way.
        public var directories: [String]
        /// Names Envy has set in the caller's environment (`__ENVY_VARS`).
        public var loaded: [String]

        public init(directories: [String], loaded: [String]) {
            self.directories = directories
            self.loaded = loaded
        }
    }

    /// What running a command did.
    public struct Outcome: Equatable, Sendable {
        public var output: String
        /// The undo menu's name for the change, when the config changed.
        public var actionName: String?
        public var request: AppRequest?
    }

    /// Something only the app's window can do.
    public enum AppRequest: Equatable, Sendable {
        /// Ask the user to paste the value of a secret into the add-variable sheet.
        case pasteSecret(project: UUID, key: String)
        case show(project: UUID?)
    }

    /// Runs the command against `config`. Reads a file only for `import`.
    public func run(on config: inout EnvConfig, context: Context) throws -> Outcome {
        switch self {
        case .help:
            return Outcome(output: Self.helpText)

        case .status:
            return Outcome(output: Self.status(config, context: context))

        case .list(let name):
            if let name {
                let p = try Self.project(named: name, in: config)
                return Outcome(output: Self.describe(p, config: config, withVariables: true))
            }
            return Outcome(output: config.scopes.map { Self.describe($0, config: config, withVariables: true) }.joined(separator: "\n"))

        case .set(let assignments, let target, let expand, let plain):
            let p = try Self.resolve(target, in: config, context: context)
            for a in assignments {
                if let existing = p.variables.first(where: { $0.key == a.key }), existing.isSecret {
                    throw UsageError("\(a.key) is a secret in \(p.name). Only the user changes secrets: run envy add \(a.key) --secret\(Self.flag(for: p)) to ask them.")
                }
                if !plain && EnvVar.looksSecret(a.key) {
                    throw UsageError("\(a.key) looks like a secret. Run envy add \(a.key) --secret\(Self.flag(for: p)) so the user pastes it into Envy, or add --plain if it isn't one.")
                }
            }
            var added: [String] = [], changed: [String] = []
            config.update(p.id) { project in
                for a in assignments {
                    if let i = project.variables.firstIndex(where: { $0.key == a.key }) {
                        project.variables[i].value = a.value
                        project.variables[i].isEnabled = true
                        if expand { project.variables[i].expands = true }
                        changed.append(a.key)
                    } else {
                        project.variables.append(EnvVar(key: a.key, value: a.value, expands: expand))
                        added.append(a.key)
                    }
                }
            }
            var lines: [String] = []
            if !added.isEmpty { lines.append("Added \(Self.names(added)) to \(p.name).") }
            if !changed.isEmpty { lines.append("Updated \(Self.names(changed)) in \(p.name).") }
            lines.append(contentsOf: Self.notes(for: p.id, in: config))
            return Outcome(output: lines.joined(separator: "\n"), actionName: assignments.count == 1 ? "Set Variable" : "Set Variables")

        case .addSecret(let key, let target):
            let p = try Self.resolve(target, in: config, context: context)
            let has = p.variables.contains { $0.key == key && $0.isSecret && !$0.value.isEmpty }
            let output = """
            Opened Envy so the user can paste the value of \(key) into \(p.name). It's kept in the Keychain; you won't see it.
            \(has ? "\(key) already has a value there; saving replaces it.\n" : "")Tell the user it's waiting for them, then check with: envy list \(Self.quoted(p.name))
            """
            return Outcome(output: output, request: .pasteSecret(project: p.id, key: key))

        case .remove(let keys, let target):
            let p = try Self.resolve(target, in: config, context: context)
            let missing = keys.filter { k in !p.variables.contains { $0.key == k } }
            guard missing.isEmpty else { throw UsageError("\(p.name) has no \(Self.names(missing)).") }
            config.update(p.id) { $0.variables.removeAll { keys.contains($0.key) } }
            return Outcome(
                output: "Removed \(Self.names(keys)) from \(p.name).",
                actionName: keys.count == 1 ? "Delete Variable" : "Delete Variables"
            )

        case .enable(let keys, let target, let on):
            let p = try Self.resolve(target, in: config, context: context)
            if keys.isEmpty {
                config.update(p.id) { $0.isEnabled = on }
                return Outcome(output: "\(p.name) is \(on ? "on" : "off").", actionName: on ? "Enable Project" : "Disable Project")
            }
            let missing = keys.filter { k in !p.variables.contains { $0.key == k } }
            guard missing.isEmpty else { throw UsageError("\(p.name) has no \(Self.names(missing)).") }
            config.update(p.id) { project in
                for i in project.variables.indices where keys.contains(project.variables[i].key) {
                    project.variables[i].isEnabled = on
                }
            }
            var lines = ["Switched \(Self.names(keys)) \(on ? "on" : "off") in \(p.name)."]
            if on { lines.append(contentsOf: Self.notes(for: p.id, in: config)) }
            return Outcome(output: lines.joined(separator: "\n"), actionName: on ? "Enable Variables" : "Disable Variables")

        case .addProject(let name, let folders, let enabled):
            if config.scopes.contains(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) {
                throw UsageError("There's already a project called \(name).")
            }
            for folder in folders {
                if let owner = config.projects.first(where: { $0.folders.contains(folder) }) {
                    throw UsageError("\(folder) is already linked to \(owner.name).")
                }
            }
            config.projects.append(Project(name: name, isEnabled: enabled, folders: folders))
            let scope = folders.isEmpty ? "It loads in every terminal" : "It loads inside \(folders.joined(separator: ", "))"
            return Outcome(output: "Created \(name). \(scope)\(enabled ? "" : " once it's switched on").", actionName: "New Project")

        case .link(let folder, let target):
            let p = try Self.resolve(target, in: config, context: context)
            if let owner = config.projects.first(where: { $0.folders.contains(folder) }) {
                if owner.id == p.id { return Outcome(output: "\(folder) is already linked to \(p.name).") }
                throw UsageError("\(folder) is already linked to \(owner.name).")
            }
            config.update(p.id) { $0.folders.append(folder) }
            return Outcome(output: "Linked \(folder) to \(p.name).", actionName: "Link Folder")

        case .unlink(let folder, let target):
            let p = try Self.resolve(target, in: config, context: context)
            guard p.folders.contains(folder) else { throw UsageError("\(folder) isn't linked to \(p.name).") }
            config.update(p.id) { $0.folders.removeAll { $0 == folder } }
            let left = p.folders.count == 1 ? " It has no folders left, so it now loads in every terminal while it's on." : ""
            return Outcome(output: "Unlinked \(folder) from \(p.name).\(left)", actionName: "Unlink Folder")

        case .importFile(let path, let target, let keepExisting):
            let p = try Self.resolve(target, in: config, context: context)
            let text: String
            do { text = try String(contentsOfFile: path, encoding: .utf8) } catch {
                throw UsageError("Couldn't read \(path).")
            }
            let vars = Self.variables(fromFile: path, text: text)
            guard !vars.isEmpty else { throw UsageError("\(path) has no variables Envy can use.") }
            var result = MergeResult()
            config.update(p.id) { result = $0.merge(vars, policy: keepExisting ? .keepExisting : .replace) }
            let secrets = vars.filter(\.isSecret).map(\.key)
            var lines = ["Imported \(vars.count) variable\(vars.count == 1 ? "" : "s") from \((path as NSString).lastPathComponent) into \(p.name): \(result.added) added, \(result.replaced) replaced, \(result.skipped) kept as they were."]
            lines.append("Names: \(Self.names(vars.map(\.key)))")
            if !secrets.isEmpty { lines.append("Stored as secrets in the Keychain: \(Self.names(secrets)). Values aren't shown.") }
            lines.append("The file is unchanged. Ask the user before deleting it.")
            lines.append(contentsOf: Self.notes(for: p.id, in: config))
            return Outcome(output: lines.joined(separator: "\n"), actionName: "Import .env")

        case .open(let name):
            let id = try name.map { try Self.project(named: $0, in: config).id }
            return Outcome(output: "", request: .show(project: id))
        }
    }

    // MARK: Lookup

    static func project(named name: String, in config: EnvConfig) throws -> Project {
        if let p = config.scopes.first(where: { $0.name == name }) ?? config.scopes.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) {
            return p
        }
        let all = config.scopes.map { quoted($0.name) }.joined(separator: ", ")
        throw UsageError("No project called \(name). Projects: \(all).")
    }

    static func resolve(_ target: Target, in config: EnvConfig, context: Context) throws -> Project {
        switch target {
        case .global: return config.global
        case .named(let name): return try project(named: name, in: config)
        case .here:
            if let p = config.folderProjects(containing: context.directories).last { return p }
            let here = context.directories.first ?? "this folder"
            throw UsageError("""
            No Envy project is linked to \(here). Add --global for every terminal, --project NAME for an existing project, \
            or create one for this folder first: envy project add NAME --folder .
            """)
        }
    }

    // MARK: Output

    static func status(_ config: EnvConfig, context: Context) -> String {
        let scopes = config.loadedScopes(at: context.directories)
        var lines: [String] = []
        if scopes.isEmpty {
            lines.append("Envy loads nothing here.")
        } else {
            lines.append("Envy loads here: \(scopes.map(\.name).joined(separator: ", ")) (later ones win).")
        }
        var wanted: [String] = []
        for p in scopes {
            for v in ShellScript.loadable(p) where !wanted.contains(v.key) { wanted.append(v.key) }
        }
        if !wanted.isEmpty { lines.append("Variables: \(wanted.joined(separator: " "))") }
        let loaded = Set(context.loaded)
        let missing = wanted.filter { !loaded.contains($0) }
        let stale = context.loaded.filter { !wanted.contains($0) }
        if !missing.isEmpty || !stale.isEmpty {
            var parts: [String] = []
            if !missing.isEmpty { parts.append("missing \(missing.joined(separator: " "))") }
            if !stale.isEmpty { parts.append("still has \(stale.joined(separator: " "))") }
            lines.append("This process started before the latest changes (\(parts.joined(separator: "; "))). Run commands with: envy exec -- <command>")
        }
        if let p = config.folderProjects(containing: context.directories).last, !p.isEnabled {
            lines.append("\(p.name) is linked to this folder but switched off.")
        }
        return lines.joined(separator: "\n")
    }

    static func describe(_ p: Project, config: EnvConfig, withVariables: Bool) -> String {
        var scope: String
        if p.isGlobal { scope = "every terminal" }
        else if p.folders.isEmpty { scope = "every terminal" }
        else { scope = p.folders.joined(separator: ", ") }
        var lines = ["\(p.name) (\(p.isEnabled ? "on" : "off"), \(scope))"]
        guard withVariables else { return lines[0] }
        if p.variables.isEmpty { lines.append("  no variables") }
        for v in p.variables {
            var line = "  \(v.key)"
            if v.isSecret {
                line += v.value.isEmpty ? "  secret, no value yet" : "  secret"
            } else {
                line += "=\(v.value)"
                if v.expands { line += "  (expands)" }
            }
            if !v.isEnabled { line += "  [off]" }
            lines.append(line)
        }
        return lines.joined(separator: "\n")
    }

    /// Why a change might not show up in the caller's shell.
    static func notes(for id: UUID, in config: EnvConfig) -> [String] {
        guard let p = config.project(id) else { return [] }
        var notes: [String] = []
        if !p.isEnabled { notes.append("\(p.name) is switched off, so this doesn't load anywhere yet.") }
        notes.append("New terminals and prompts pick this up. Your own environment doesn't: run commands with envy exec -- <command>.")
        return notes
    }

    static func names(_ keys: [String]) -> String { keys.joined(separator: ", ") }

    static func quoted(_ name: String) -> String {
        name.contains(" ") ? "\"\(name)\"" : name
    }

    static func flag(for p: Project) -> String {
        p.isGlobal ? " --global" : " --project \(quoted(p.name))"
    }

    /// Variables from a `.env`, `.envrc` or `appsettings*.json` file, secrets marked by name.
    static func variables(fromFile path: String, text: String) -> [EnvVar] {
        let name = (path as NSString).lastPathComponent
        let isJSON = name.lowercased().hasSuffix(".json") || AppSettings.looksLikeJSON(text)
        let entries = isJSON ? (AppSettings.entries(fromJSON: text) ?? []) : DotEnv.parse(text)
        var out: [EnvVar] = []
        for entry in entries where EnvVar.keyProblem(entry.key) == nil {
            let v = EnvVar(
                key: entry.key, value: entry.value, isSecret: EnvVar.looksSecret(entry.key),
                expands: name == ".envrc" && entry.quote != .single && entry.value.contains("$")
            )
            if let i = out.firstIndex(where: { $0.key == v.key }) { out[i] = v } else { out.append(v) }
        }
        return out
    }

    public static let helpText = """
    envy: environment variables and secrets from Envy.app

    Envy loads Global, then projects that load everywhere, then the project linked to the folder you're in
    (deepest folder last). Changes reach new terminals and the next prompt of open ones.

    See what's there
      envy status                      what loads here, and whether this process has it
      envy list [PROJECT]              projects and their variables (secret values are never shown)

    Change it (the change goes to the project linked to this folder; pick another with
    --project NAME, or --global for every terminal)
      envy set NAME=VALUE [...]        add or update plain variables; --expand evaluates $VARS in the value
      envy add NAME --secret           ask the user to paste a secret into Envy (it goes to the Keychain)
      envy remove NAME [...]           delete variables
      envy on|off NAME [...]           switch variables on or off; with only --project, the whole project
      envy import FILE                 move a .env, .envrc or appsettings.json into Envy; secrets go to the Keychain
      envy project add NAME [--folder PATH]
                                       new project; with a folder it loads only inside it
      envy link|unlink [FOLDER] --project NAME

    Run with the latest values
      envy exec -- COMMAND [ARGS...]   run a command with what Envy loads here right now. A process keeps the
                                       environment it started with, so use this after a change.
      envy open [PROJECT]              open the app

    For coding agents
      - Never print secret values: no echo of secret variables, no unfiltered env or printenv, and don't read
        ~/.config/envy/vars.sh (it holds them in plain text).
      - Never put secrets in .env files, shell startup files, code or commits, and never ask the user to paste one
        into the chat. Run envy add NAME --secret and let the user paste it into Envy.
      - Prefer Envy over creating .env files or adding exports to ~/.zshrc. Found a .env? Offer envy import.
      - Ask the user before removing variables or switching things off.
      - If envy says a sandbox blocks it, run the same command again with permission to run outside the
        sandbox, or ask the user to run it. envy status and envy list still work from inside one.
    """
}

extension EnvConfig {
    /// Folder projects whose folders contain one of `directories`, shallowest first.
    public func folderProjects(containing directories: [String]) -> [Project] {
        projects.enumerated()
            .filter { _, p in p.isFolderScoped && directories.contains { Self.folder(p.folders, contains: $0) } }
            .sorted { (ShellScript.depth($0.element), $0.offset) < (ShellScript.depth($1.element), $1.offset) }
            .map(\.element)
    }

    /// What a shell in one of `directories` loads, in the order the hook applies it.
    public func loadedScopes(at directories: [String]) -> [Project] {
        var out: [Project] = []
        if global.isEnabled { out.append(global) }
        out += projects.filter { $0.isEnabled && !$0.isFolderScoped }
        out += folderProjects(containing: directories).filter(\.isEnabled)
        return out.filter { !ShellScript.loadable($0).isEmpty }
    }

    static func folder(_ folders: [String], contains directory: String) -> Bool {
        let dirs = Set([directory, ShellScript.realPath(directory)].map { $0.hasSuffix("/") ? $0 : $0 + "/" })
        for folder in folders {
            let expanded = (folder as NSString).expandingTildeInPath
            for path in [expanded, ShellScript.realPath(expanded)] {
                var p = path
                while p.count > 1, p.hasSuffix("/") { p.removeLast() }
                let prefix = p == "/" ? "/" : p + "/"
                if dirs.contains(where: { $0.hasPrefix(prefix) }) { return true }
            }
        }
        return false
    }
}
