import CommonCrypto
import CryptoKit
import Foundation
import Security

/// What a backup holds once decrypted: Global and every project, with folders, variables and secret values.
/// Field names match the settings file; there are no IDs. See spec/backup-format.md.
public struct BackupContent: Codable, Equatable, Sendable {
    public var app: String
    /// `Backup.macOS` or `Backup.windows`: the system the backup was made on.
    public var platform: String
    /// ISO 8601 in UTC.
    public var created: String
    public var global: BackupScope
    public var projects: [BackupScope]

    public init(app: String, platform: String, created: String, global: BackupScope, projects: [BackupScope]) {
        self.app = app
        self.platform = platform
        self.created = created
        self.global = global
        self.projects = projects
    }

    private enum CodingKeys: String, CodingKey { case app, platform, created, global, projects }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        app = try c.decodeIfPresent(String.self, forKey: .app) ?? ""
        platform = try c.decodeIfPresent(String.self, forKey: .platform) ?? ""
        created = try c.decodeIfPresent(String.self, forKey: .created) ?? ""
        global = try c.decodeIfPresent(BackupScope.self, forKey: .global) ?? BackupScope(name: "Global")
        projects = try c.decodeIfPresent([BackupScope].self, forKey: .projects) ?? []
    }

    /// Global first, then projects.
    public var scopes: [BackupScope] { [global] + projects }
}

public struct BackupScope: Codable, Equatable, Sendable {
    public var name: String
    public var isEnabled: Bool
    public var folders: [String]
    public var variables: [BackupVariable]

    public init(name: String, isEnabled: Bool = true, folders: [String] = [], variables: [BackupVariable] = []) {
        self.name = name
        self.isEnabled = isEnabled
        self.folders = folders
        self.variables = variables
    }

    private enum CodingKeys: String, CodingKey { case name, isEnabled, folders, variables }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        isEnabled = try c.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true
        folders = try c.decodeIfPresent([String].self, forKey: .folders) ?? []
        variables = try c.decodeIfPresent([BackupVariable].self, forKey: .variables) ?? []
    }
}

public struct BackupVariable: Codable, Equatable, Sendable {
    public var key: String
    public var value: String
    public var isEnabled: Bool
    public var isSecret: Bool
    public var expands: Bool
    public var note: String

    public init(key: String, value: String = "", isEnabled: Bool = true, isSecret: Bool = false, expands: Bool = false, note: String = "") {
        self.key = key
        self.value = value
        self.isEnabled = isEnabled
        self.isSecret = isSecret
        self.expands = expands
        self.note = note
    }

    init(_ v: EnvVar) {
        self.init(key: v.key, value: v.value, isEnabled: v.isEnabled, isSecret: v.isSecret, expands: v.expands, note: v.note)
    }

    private enum CodingKeys: String, CodingKey { case key, value, isEnabled, isSecret, expands, note }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        key = try c.decodeIfPresent(String.self, forKey: .key) ?? ""
        value = try c.decodeIfPresent(String.self, forKey: .value) ?? ""
        isEnabled = try c.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true
        isSecret = try c.decodeIfPresent(Bool.self, forKey: .isSecret) ?? false
        expands = try c.decodeIfPresent(Bool.self, forKey: .expands) ?? false
        note = try c.decodeIfPresent(String.self, forKey: .note) ?? ""
    }

    var envVar: EnvVar { EnvVar(key: key, value: value, isEnabled: isEnabled, isSecret: isSecret, expands: expands, note: note) }
}

/// A backup that can't be opened or written; the message is for the user.
public struct BackupError: LocalizedError, Equatable, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }

    public static let notABackup = BackupError("This isn't an Envy backup.")
    public static let newerVersion = BackupError("This backup was made by a newer version of Envy. Update Envy to restore it.")
    public static let damaged = BackupError("This backup is damaged.")
    public static let wrongPassword = BackupError("Wrong password, or the file is damaged.")
}

/// Backups of everything Envy manages in one password-encrypted file that Envy for macOS and Envy for Windows both
/// read and write (spec/backup-format.md): PBKDF2-HMAC-SHA256 for the key, AES-256-GCM for the contents.
public enum Backup {
    public static let fileExtension = "envybackup"
    public static let macOS = "macos"
    public static let windows = "windows"
    public static let minimumPasswordLength = 12
    public static let defaultIterations = 600_000
    static let formatName = "envy-backup"
    static let formatVersion = 1
    static let kdfName = "PBKDF2-HMAC-SHA256"
    static let cipherName = "AES-256-GCM"
    static let iterationRange = 100_000...10_000_000
    static let associatedData = Data("envy-backup v1".utf8)

    /// `Envy Backup YYYY-MM-DD.envybackup`, dated in the local time zone.
    public static func suggestedFileName(_ date: Date, timeZone: TimeZone = .current) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let d = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "Envy Backup %04d-%02d-%02d.%@", d.year ?? 0, d.month ?? 0, d.day ?? 0, fileExtension)
    }

    /// Why `password` can't protect a backup, or nil.
    public static func passwordProblem(_ password: String, confirmation: String) -> String? {
        if password.count < minimumPasswordLength { return "Use at least \(minimumPasswordLength) characters." }
        if password != confirmation { return "The passwords don't match." }
        return nil
    }

    // MARK: Writing

    /// Everything in `config`, secrets included, with folders under `home` as `~/…`.
    public static func content(of config: EnvConfig, home: String, app: String, created: Date, platform: String = macOS) -> BackupContent {
        func scope(_ p: Project) -> BackupScope {
            BackupScope(
                name: p.name, isEnabled: p.isEnabled,
                folders: p.isGlobal ? [] : p.folders.map { portableFolder($0, home: home) },
                variables: p.variables.map(BackupVariable.init)
            )
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(identifier: "UTC")
        return BackupContent(
            app: app, platform: platform, created: formatter.string(from: created),
            global: scope(config.global), projects: config.projects.map(scope)
        )
    }

    /// `~` or `~/…` for a folder inside `home` (ignoring case), anything else as it is.
    public static func portableFolder(_ folder: String, home: String) -> String {
        let f = trimmingTrailingSlashes(folder)
        let h = trimmingTrailingSlashes(home)
        if f.caseInsensitiveCompare(h) == .orderedSame { return "~" }
        let prefix = h == "/" ? "/" : h + "/"
        if f.count > prefix.count, f.prefix(prefix.count).caseInsensitiveCompare(prefix) == .orderedSame {
            return "~/" + f.dropFirst(prefix.count)
        }
        return folder
    }

    /// A folder from a backup as a path on this Mac: `~` and `~/…` under `home`, anything else as it is.
    public static func localFolder(_ folder: String, home: String) -> String {
        let h = trimmingTrailingSlashes(home)
        if folder == "~" { return h }
        if folder.hasPrefix("~/") { return (h == "/" ? "" : h) + "/" + folder.dropFirst(2) }
        return folder
    }

    private static func trimmingTrailingSlashes(_ path: String) -> String {
        var p = path
        while p.count > 1, p.hasSuffix("/") { p.removeLast() }
        return p
    }

    /// The encrypted file. `iterations` is only lowered by tests.
    public static func write(_ content: BackupContent, password: String, iterations: Int = defaultIterations) throws -> Data {
        var plain = try JSONEncoder().encode(content)
        defer { plain.resetBytes(in: 0..<plain.count) }
        return try seal(plain, password: password, iterations: iterations)
    }

    /// `plain` encrypted into the file around it.
    static func seal(_ plain: Data, password: String, iterations: Int) throws -> Data {
        let salt = try randomBytes(16)
        let nonce = try randomBytes(12)
        let key = try deriveKey(password, salt: salt, iterations: iterations)
        let sealed = try AES.GCM.seal(plain, using: key, nonce: AES.GCM.Nonce(data: nonce), authenticating: associatedData)
        // Written by hand to keep the spec's field order; base64 and the names never need escaping.
        let text = """
        {
          "format": "\(formatName)",
          "version": \(formatVersion),
          "kdf": { "name": "\(kdfName)", "iterations": \(iterations), "salt": "\(salt.base64EncodedString())" },
          "cipher": { "name": "\(cipherName)", "nonce": "\(nonce.base64EncodedString())" },
          "data": "\((sealed.ciphertext + sealed.tag).base64EncodedString())"
        }

        """
        return Data(text.utf8)
    }

    // MARK: Reading

    /// The contents of a backup file. Throws `BackupError` with a message for the user.
    public static func read(_ data: Data, password: String) throws -> BackupContent {
        let file: BackupFile
        do {
            file = try JSONDecoder().decode(BackupFile.self, from: data)
        } catch {
            throw BackupError.notABackup
        }
        guard file.format == formatName else { throw BackupError.notABackup }
        guard file.version <= formatVersion else { throw BackupError.newerVersion }
        guard file.version >= 1,
              file.kdf.name == kdfName, file.cipher.name == cipherName, iterationRange.contains(file.kdf.iterations),
              let salt = Data(base64Encoded: file.kdf.salt), salt.count == 16,
              let nonce = Data(base64Encoded: file.cipher.nonce), nonce.count == 12,
              let sealed = Data(base64Encoded: file.data), sealed.count >= 16
        else { throw BackupError.damaged }

        let key = try deriveKey(password, salt: salt, iterations: file.kdf.iterations)
        var plain: Data
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce), ciphertext: sealed.dropLast(16), tag: sealed.suffix(16))
            plain = try AES.GCM.open(box, using: key, authenticating: associatedData)
        } catch {
            throw BackupError.wrongPassword
        }
        defer { plain.resetBytes(in: 0..<plain.count) }
        do {
            return try JSONDecoder().decode(BackupContent.self, from: plain)
        } catch {
            throw BackupError.damaged
        }
    }

    // MARK: Crypto

    /// PBKDF2-HMAC-SHA256 over the NFC-normalised password, 32 bytes.
    static func deriveKey(_ password: String, salt: Data, iterations: Int) throws -> SymmetricKey {
        var secret = Data(password.precomposedStringWithCanonicalMapping.utf8)
        defer { secret.resetBytes(in: 0..<secret.count) }
        var key = Data(count: 32)
        defer { key.resetBytes(in: 0..<key.count) }
        let status = key.withUnsafeMutableBytes { keyBytes in
            secret.withUnsafeBytes { secretBytes in
                salt.withUnsafeBytes { saltBytes in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        secretBytes.baseAddress?.assumingMemoryBound(to: CChar.self), secretBytes.count,
                        saltBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), saltBytes.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations),
                        keyBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), keyBytes.count
                    )
                }
            }
        }
        guard status == kCCSuccess else { throw BackupError("Envy couldn't derive a key from the password (error \(status)).") }
        return SymmetricKey(data: key)
    }

    private static func randomBytes(_ count: Int) throws -> Data {
        var bytes = Data(count: count)
        let status = bytes.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        guard status == errSecSuccess else { throw BackupError("Envy couldn't get random bytes for the backup (error \(status)).") }
        return bytes
    }
}

/// The file around the encrypted contents. Missing fields read as empty, so the reading rules decide what's wrong.
struct BackupFile: Decodable {
    struct Kdf: Decodable {
        var name = ""
        var iterations = 0
        var salt = ""

        init() {}

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
            iterations = try c.decodeIfPresent(Int.self, forKey: .iterations) ?? 0
            salt = try c.decodeIfPresent(String.self, forKey: .salt) ?? ""
        }

        private enum CodingKeys: String, CodingKey { case name, iterations, salt }
    }

    struct Cipher: Decodable {
        var name = ""
        var nonce = ""

        init() {}

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
            nonce = try c.decodeIfPresent(String.self, forKey: .nonce) ?? ""
        }

        private enum CodingKeys: String, CodingKey { case name, nonce }
    }

    var format: String
    var version: Int
    var kdf: Kdf
    var cipher: Cipher
    var data: String

    private enum CodingKeys: String, CodingKey { case format, version, kdf, cipher, data }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        format = try c.decodeIfPresent(String.self, forKey: .format) ?? ""
        // The rest leniently: once the format is right, a field that is missing or of the wrong type is damage, and
        // a newer version is reported as such even if the other fields changed shape.
        version = (try? c.decodeIfPresent(Int.self, forKey: .version)) ?? 0
        kdf = (try? c.decodeIfPresent(Kdf.self, forKey: .kdf)) ?? Kdf()
        cipher = (try? c.decodeIfPresent(Cipher.self, forKey: .cipher)) ?? Cipher()
        data = (try? c.decodeIfPresent(String.self, forKey: .data)) ?? ""
    }
}

// MARK: - Restoring

public enum RestoreMode: String, CaseIterable, Sendable {
    /// Nothing here is deleted: Global and same-named projects merge, other projects are added.
    case add
    /// Global and every project here are replaced by the backup's.
    case replace
}

/// What to do with an expanding value from the other system.
public enum ExpandAction: String, CaseIterable, Sendable {
    case useSuggestion
    case keepAsIs
    case turnOff
}

/// An expanding value from the other system that may not work here. `scope` -1 is Global, else the project's index.
public struct ExpandIssue: Equatable, Identifiable, Sendable {
    public var scope: Int
    public var index: Int
    public var scopeName: String
    public var key: String
    public var value: String
    public var reason: String
    public var suggestion: String?

    public var id: String { "\(scope)/\(index)" }

    public var defaultAction: ExpandAction {
        if suggestion != nil { return .useSuggestion }
        return reason == BackupRestore.commandFromWindows ? .turnOff : .keepAsIs
    }

    /// The choices that make sense: Use Suggestion only when there is one.
    public var actions: [ExpandAction] {
        suggestion == nil ? [.keepAsIs, .turnOff] : ExpandAction.allCases
    }
}

/// The choice made for one `ExpandIssue`; `value` is the (possibly edited) suggestion.
public struct ExpandFix: Equatable, Sendable {
    public var scope: Int
    public var index: Int
    public var action: ExpandAction
    public var value: String?

    public init(scope: Int, index: Int, action: ExpandAction, value: String? = nil) {
        self.scope = scope
        self.index = index
        self.action = action
        self.value = value
    }
}

/// What a restore did, or would do.
public struct RestoreReport: Equatable, Sendable {
    public struct Folder: Equatable, Sendable {
        public var project: String
        public var folder: String
        /// For a skipped folder: the project here that already has it.
        public var owner: String?
    }

    public var projectsAdded: [String] = []
    public var projectsMerged: [String] = []
    public var projectsRemoved: [String] = []
    public var variablesAdded = 0
    public var variablesReplaced = 0
    public var secrets = 0
    /// Folders restored that don't exist on this machine.
    public var missingFolders: [Folder] = []
    /// Folders not linked because another project here already has them.
    public var skippedFolders: [Folder] = []
    /// New projects restored switched off because all their folders were skipped.
    public var turnedOff: [String] = []

    public init() {}
}

/// Restoring a backup into the settings, and the suggestions for expanding values from the other system.
public enum BackupRestore {
    static let commandFromMac = "Runs a command. Windows doesn't run commands in values."
    static let commandFromWindows = "This would run a command on a Mac; on Windows it was plain text."

    /// Applies `backup` to `config` (pass a copy to preview). Project names compare ignoring case, variable names and
    /// folders exactly, as elsewhere on the Mac. `folderExists` decides which folders are missing here.
    @discardableResult
    public static func apply(
        _ backup: BackupContent, to config: inout EnvConfig, mode: RestoreMode, home: String,
        fixes: [ExpandFix] = [], folderExists: (String) -> Bool = BackupRestore.directoryExists
    ) -> RestoreReport {
        var report = RestoreReport()
        let global = project(from: backup.global, scope: -1, home: home, fixes: fixes, id: Project.globalID)
        var incoming = backup.projects.enumerated().map { project(from: $1, scope: $0, home: home, fixes: fixes, id: UUID()) }
        report.secrets = ([global] + incoming).reduce(0) { $0 + $1.variables.filter(\.isSecret).count }

        switch mode {
        case .replace:
            report.projectsRemoved = config.projects.map(\.name)
            report.projectsAdded = incoming.map(\.name)
            report.variablesAdded = ([global] + incoming).reduce(0) { $0 + $1.variables.count }
            config.global = global
            config.projects = incoming
        case .add:
            merge(global.variables, into: &config.global, report: &report)
            for i in incoming.indices {
                let restored = incoming[i]
                let existing = config.projects.firstIndex { $0.name.caseInsensitiveCompare(restored.name) == .orderedSame }
                var folders: [String] = []
                for folder in restored.folders {
                    if let existing, config.projects[existing].folders.contains(folder) { continue }
                    if let owner = config.projects.enumerated().first(where: { $0.offset != existing && $0.element.folders.contains(folder) }) {
                        report.skippedFolders.append(.init(project: restored.name, folder: folder, owner: owner.element.name))
                        continue
                    }
                    folders.append(folder)
                }
                if let existing {
                    merge(restored.variables, into: &config.projects[existing], report: &report)
                    config.projects[existing].folders += folders
                    report.projectsMerged.append(config.projects[existing].name)
                } else {
                    var added = restored
                    if !restored.folders.isEmpty && folders.isEmpty && restored.isEnabled {
                        added.isEnabled = false
                        report.turnedOff.append(added.name)
                    }
                    added.folders = folders
                    incoming[i] = added
                    config.projects.append(added)
                    report.projectsAdded.append(added.name)
                    report.variablesAdded += added.variables.count
                }
            }
        }

        let restoredFolders = Set(incoming.flatMap(\.folders))
        for p in config.projects {
            for folder in p.folders where restoredFolders.contains(folder) && !folderExists(folder) {
                report.missingFolders.append(.init(project: p.name, folder: folder))
            }
        }
        return report
    }

    public static func directoryExists(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) && isDirectory.boolValue
    }

    private static func merge(_ variables: [EnvVar], into project: inout Project, report: inout RestoreReport) {
        let result = project.merge(variables, policy: .replace)
        report.variablesAdded += result.added
        report.variablesReplaced += result.replaced
    }

    private static func project(from scope: BackupScope, scope index: Int, home: String, fixes: [ExpandFix], id: UUID) -> Project {
        let isGlobal = id == Project.globalID
        var folders: [String] = []
        if !isGlobal {
            for folder in scope.folders.map({ Backup.localFolder($0, home: home) }) where !folders.contains(folder) {
                folders.append(folder)
            }
        }
        let variables = scope.variables.enumerated().map { i, v in
            var variable = v.envVar
            if let fix = fixes.first(where: { $0.scope == index && $0.index == i }) {
                switch fix.action {
                case .turnOff: variable.expands = false
                case .useSuggestion: if let value = fix.value { variable.value = value }
                case .keepAsIs: break
                }
            }
            return variable
        }
        return Project(id: id, name: isGlobal ? "Global" : scope.name, isEnabled: scope.isEnabled, folders: folders, variables: variables)
    }

    // MARK: Expanding values from the other system

    /// Expanding values in a backup from the other system that may not work `here`, with suggested rewrites.
    public static func expandIssues(_ backup: BackupContent, here: String) -> [ExpandIssue] {
        guard backup.platform != here, [Backup.macOS, Backup.windows].contains(backup.platform) else { return [] }
        var issues: [ExpandIssue] = []
        for (s, scope) in backup.scopes.enumerated() {
            for (i, v) in scope.variables.enumerated() where v.expands {
                guard let found = suggest(key: v.key, value: v.value, from: backup.platform, to: here) else { continue }
                issues.append(ExpandIssue(
                    scope: s - 1, index: i, scopeName: s == 0 ? "Global" : scope.name,
                    key: v.key, value: v.value, reason: found.reason, suggestion: found.suggestion
                ))
            }
        }
        return issues
    }

    /// The reason a value from `from` may not work on `to`, and a rewrite if there is one.
    public static func suggest(key: String, value: String, from: String, to: String) -> (reason: String, suggestion: String?)? {
        let pathList = key.uppercased().hasSuffix("PATH")
        let runsCommand = value.contains("$(") || value.contains("`")
        var reasons: [String] = []
        var suggestion = value

        if from == Backup.macOS && to == Backup.windows {
            if runsCommand { return (commandFromMac, nil) }
            let home = #/\$\{HOME\}|\$HOME(?![A-Za-z0-9_])/#
            if suggestion.contains(home) {
                reasons.append("$HOME isn't set in PowerShell or Command Prompt.")
                suggestion = suggestion.replacing(home, with: "%USERPROFILE%")
            }
            if pathList && suggestion.contains(":") {
                reasons.append("Windows separates folders with ;.")
                suggestion = suggestion.replacingOccurrences(of: ":", with: ";")
            }
            if reasons.isEmpty { return nil }
            if !value.contains("://") { suggestion = suggestion.replacingOccurrences(of: "/", with: "\\") }
        } else if from == Backup.windows && to == Backup.macOS {
            if runsCommand { return (commandFromWindows, nil) }
            let percent = #/%([A-Za-z_][A-Za-z0-9_]*)%/#
            let powerShell = #/\$env:([A-Za-z_][A-Za-z0-9_]*)/#.ignoresCase()
            if suggestion.contains(percent) || suggestion.contains(powerShell) {
                reasons.append("A Mac doesn't understand %NAME% or $env:NAME.")
                // The user profile is the home folder; any other name becomes ${NAME}.
                func mac(_ name: Substring) -> String {
                    name.caseInsensitiveCompare("USERPROFILE") == .orderedSame ? "$HOME" : "${\(name)}"
                }
                suggestion = suggestion.replacing(percent) { mac($0.output.1) }
                suggestion = suggestion.replacing(powerShell) { mac($0.output.1) }
            }
            if pathList && suggestion.contains(";") {
                reasons.append("A Mac separates folders with :.")
                suggestion = suggestion.replacingOccurrences(of: ";", with: ":")
            }
            if reasons.isEmpty { return nil }
            if !value.contains("://") { suggestion = suggestion.replacingOccurrences(of: "\\", with: "/") }
        } else {
            return nil
        }
        return (reasons.joined(separator: " "), suggestion)
    }
}
