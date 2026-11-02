import Foundation

/// One environment variable inside a project (or Global).
public struct EnvVar: Identifiable, Hashable, Codable, Sendable {
    public var id: UUID
    public var key: String
    public var value: String
    public var isEnabled: Bool
    /// Secret values are kept in the Keychain instead of the config file and are masked in the UI.
    public var isSecret: Bool
    /// Evaluate the value like a double-quoted shell string (`$HOME`, `${OTHER}`, `$(cmd)`) instead of literally.
    public var expands: Bool
    public var note: String

    public init(
        id: UUID = UUID(), key: String, value: String = "", isEnabled: Bool = true,
        isSecret: Bool = false, expands: Bool = false, note: String = ""
    ) {
        self.id = id
        self.key = key
        self.value = value
        self.isEnabled = isEnabled
        self.isSecret = isSecret
        self.expands = expands
        self.note = note
    }

    private enum CodingKeys: String, CodingKey { case id, key, value, isEnabled, isSecret, expands, note }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        key = try c.decodeIfPresent(String.self, forKey: .key) ?? ""
        value = try c.decodeIfPresent(String.self, forKey: .value) ?? ""
        isEnabled = try c.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true
        isSecret = try c.decodeIfPresent(Bool.self, forKey: .isSecret) ?? false
        expands = try c.decodeIfPresent(Bool.self, forKey: .expands) ?? false
        note = try c.decodeIfPresent(String.self, forKey: .note) ?? ""
    }
}

extension EnvVar {
    /// Names the shell owns or that would break it when exported.
    static let reservedKeys: Set<String> = [
        "IFS", "PWD", "OLDPWD", "SHLVL", "_", "UID", "EUID", "GID", "EGID", "PPID", "LINENO",
        "RANDOM", "SECONDS", "BASHOPTS", "SHELLOPTS", "BASH_VERSINFO", "ZSH_VERSION",
        "status", "pipestatus", "path", "fpath", "cdpath", "manpath", "module_path", "argv", "ARGC",
    ]

    /// Why `key` can't be exported, or nil when it's a usable name.
    public static func keyProblem(_ key: String) -> String? {
        if key.isEmpty { return "Name is required." }
        let scalars = key.unicodeScalars
        let allowed = scalars.allSatisfy {
            ($0 >= "a" && $0 <= "z") || ($0 >= "A" && $0 <= "Z") || ($0 >= "0" && $0 <= "9") || $0 == "_"
        }
        if !allowed { return "Use only letters, digits and underscores." }
        if let first = scalars.first, first >= "0" && first <= "9" { return "Names can't start with a digit." }
        if key.lowercased().hasPrefix("__envy") || reservedKeys.contains(key) {
            return "\(key) is reserved by the shell."
        }
        return nil
    }

    public var keyProblem: String? { Self.keyProblem(key) }
    public var hasValidKey: Bool { keyProblem == nil }

    /// Heuristic for names that usually hold credentials, used to pre-tick "Secret".
    public static func looksSecret(_ key: String) -> Bool {
        let k = key.uppercased()
        let fragments = ["SECRET", "TOKEN", "PASSWORD", "PASSWD", "PRIVATE", "CREDENTIAL", "APIKEY", "API_KEY", "CONNECTIONSTRING", "CONNECTION_STRING"]
        return fragments.contains { k.contains($0) } || k.hasSuffix("_KEY") || k.hasSuffix("_PASS") || k.hasSuffix("_PWD")
    }
}

/// A named set of variables. Without folders it loads in every shell; with folders it loads only inside them.
public struct Project: Identifiable, Hashable, Codable, Sendable {
    public static let globalID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    public var id: UUID
    public var name: String
    public var isEnabled: Bool
    /// Absolute paths. The project's variables load while the shell's working directory is inside one of them.
    public var folders: [String]
    public var variables: [EnvVar]

    public init(id: UUID = UUID(), name: String, isEnabled: Bool = true, folders: [String] = [], variables: [EnvVar] = []) {
        self.id = id
        self.name = name
        self.isEnabled = isEnabled
        self.folders = folders
        self.variables = variables
    }

    public static func newGlobal() -> Project { Project(id: globalID, name: "Global") }

    public var isGlobal: Bool { id == Self.globalID }
    public var isFolderScoped: Bool { !folders.isEmpty }

    /// Keys that appear more than once in this project (the last one wins in the shell).
    public var duplicateKeys: Set<String> {
        var seen = Set<String>(), dupes = Set<String>()
        for v in variables where !seen.insert(v.key).inserted { dupes.insert(v.key) }
        return dupes
    }

    private enum CodingKeys: String, CodingKey { case id, name, isEnabled, folders, variables }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Untitled"
        isEnabled = try c.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true
        folders = try c.decodeIfPresent([String].self, forKey: .folders) ?? []
        variables = try c.decodeIfPresent([EnvVar].self, forKey: .variables) ?? []
    }
}

public enum MergePolicy: String, CaseIterable, Sendable {
    case replace
    case keepExisting
}

public struct MergeResult: Equatable, Sendable {
    public var added = 0
    public var replaced = 0
    public var skipped = 0
    public init() {}
}

extension Project {
    /// Adds `incoming` by name. Copies always get fresh IDs (secrets are keyed by ID), and a replaced
    /// variable keeps the ID of the one it replaces.
    @discardableResult
    public mutating func merge(_ incoming: [EnvVar], policy: MergePolicy) -> MergeResult {
        var result = MergeResult()
        for var v in incoming {
            if let i = variables.firstIndex(where: { $0.key == v.key }) {
                guard policy == .replace else { result.skipped += 1; continue }
                v.id = variables[i].id
                variables[i] = v
                result.replaced += 1
            } else {
                v.id = UUID()
                variables.append(v)
                result.added += 1
            }
        }
        return result
    }
}

/// Everything Envy stores: the Global scope plus projects in sidebar order.
public struct EnvConfig: Hashable, Codable, Sendable {
    public var version: Int
    public var global: Project
    public var projects: [Project]

    public init(global: Project = .newGlobal(), projects: [Project] = []) {
        self.version = 1
        self.global = global
        self.projects = projects
    }

    private enum CodingKeys: String, CodingKey { case version, global, projects }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        global = try c.decodeIfPresent(Project.self, forKey: .global) ?? .newGlobal()
        global.id = Project.globalID
        global.folders = []
        projects = try c.decodeIfPresent([Project].self, forKey: .projects) ?? []
    }

    /// Global first, then projects.
    public var scopes: [Project] { [global] + projects }

    public func project(_ id: UUID) -> Project? {
        id == Project.globalID ? global : projects.first { $0.id == id }
    }

    public mutating func update(_ id: UUID, _ change: (inout Project) -> Void) {
        if id == Project.globalID {
            change(&global)
        } else if let i = projects.firstIndex(where: { $0.id == id }) {
            change(&projects[i])
        }
    }

    /// Enabled scopes other than `projectID` that also define an enabled `key`.
    public func otherDefinitions(of key: String, excluding projectID: UUID) -> [Project] {
        scopes.filter { p in
            p.id != projectID && p.isEnabled && p.variables.contains { $0.isEnabled && $0.key == key }
        }
    }

    // MARK: Secrets

    /// Values of all secret variables keyed by variable ID, for the Keychain.
    public func secretValues() -> [String: String] {
        var out: [String: String] = [:]
        for p in scopes {
            for v in p.variables where v.isSecret { out[v.id.uuidString] = v.value }
        }
        return out
    }

    /// The config as written to disk: secret values blanked out.
    public func withoutSecretValues() -> EnvConfig {
        var copy = self
        copy.global.variables = copy.global.variables.map(Self.blankingSecret)
        copy.projects = copy.projects.map { p in
            var p = p
            p.variables = p.variables.map(Self.blankingSecret)
            return p
        }
        return copy
    }

    public mutating func fillSecrets(_ secrets: [String: String]) {
        func fill(_ vars: inout [EnvVar]) {
            for i in vars.indices where vars[i].isSecret {
                vars[i].value = secrets[vars[i].id.uuidString] ?? vars[i].value
            }
        }
        fill(&global.variables)
        for i in projects.indices { fill(&projects[i].variables) }
    }

    private static func blankingSecret(_ v: EnvVar) -> EnvVar {
        var v = v
        if v.isSecret { v.value = "" }
        return v
    }
}
