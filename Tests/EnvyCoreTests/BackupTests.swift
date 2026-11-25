import CryptoKit
import XCTest
@testable import EnvyCore

/// Backups (spec/backup-format.md): the encrypted file, the shared samples, restoring and the Expand suggestions.
final class BackupTests: XCTestCase {
    private let samplePassword = "correct horse battery"
    private let fastIterations = 100_000

    private static let samples = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("spec/backup-samples")

    private func sample(_ name: String) throws -> Data {
        try Data(contentsOf: Self.samples.appendingPathComponent(name))
    }

    private func open(_ name: String) throws -> BackupContent {
        try Backup.read(sample(name), password: samplePassword)
    }

    /// JSON as Foundation objects, so key order and whitespace don't matter but every field and value does.
    private func object(_ data: Data) throws -> NSObject {
        try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? NSObject)
    }

    private func error(_ data: Data, password: String? = nil) -> String? {
        do {
            _ = try Backup.read(data, password: password ?? samplePassword)
            return nil
        } catch {
            return (error as? BackupError)?.message ?? "not a BackupError: \(error)"
        }
    }

    // MARK: The file

    func testOpensTheSharedSamples() throws {
        for name in ["windows", "macos"] {
            let content = try open("\(name).envybackup")
            // Written back out, the contents are exactly the expected file: the same fields, names and values.
            XCTAssertEqual(try object(JSONEncoder().encode(content)), try object(sample("\(name).expected.json")), name)
            XCTAssertEqual(content.platform, name)
        }
        let windows = try open("windows.envybackup")
        XCTAssertEqual(windows.projects.map(\.name), ["api", "Tools", "Work site"])
        XCTAssertEqual(windows.global.variables.first { $0.key == "OPENAI_API_KEY" }?.value, "sk-test-sample-123")
        XCTAssertEqual(windows.global.variables.first { $0.key == "OLD_FLAG" }?.isEnabled, false)
    }

    func testRefusesWithAMessageForTheUser() throws {
        let windows = try String(decoding: sample("windows.envybackup"), as: UTF8.self)
        func edited(_ from: String, _ to: String) -> Data {
            XCTAssertTrue(windows.contains(from), from)
            return Data(windows.replacingOccurrences(of: from, with: to).utf8)
        }

        XCTAssertEqual(error(try sample("windows.envybackup"), password: "correct horse batterY"), "Wrong password, or the file is damaged.")
        XCTAssertEqual(error(try sample("windows.envybackup"), password: ""), "Wrong password, or the file is damaged.")
        XCTAssertEqual(error(try sample("damaged.envybackup")), "Wrong password, or the file is damaged.")
        XCTAssertEqual(error(try sample("newer-version.envybackup")), "This backup was made by a newer version of Envy. Update Envy to restore it.")

        XCTAssertEqual(error(Data("PORT=1\n".utf8)), "This isn't an Envy backup.")
        XCTAssertEqual(error(Data()), "This isn't an Envy backup.")
        XCTAssertEqual(error(Data("[1, 2]".utf8)), "This isn't an Envy backup.")
        XCTAssertEqual(error(Data(#"{"format": "something-else", "version": 1}"#.utf8)), "This isn't an Envy backup.")
        XCTAssertEqual(error(Data(#"{"version": 1}"#.utf8)), "This isn't an Envy backup.")
        XCTAssertEqual(error(Data(#"{"format": 1, "version": 1}"#.utf8)), "This isn't an Envy backup.")
        XCTAssertEqual(error(Data(#""envy-backup""#.utf8)), "This isn't an Envy backup.")
        // A newer version is reported as such even when everything else changed.
        XCTAssertEqual(error(Data(#"{"format": "envy-backup", "version": 2, "kdf": "argon2"}"#.utf8)), "This backup was made by a newer version of Envy. Update Envy to restore it.")

        let damaged = "This backup is damaged."
        XCTAssertEqual(error(edited(#""iterations": 600000"#, #""iterations": 1000"#)), damaged)
        XCTAssertEqual(error(edited(#""iterations": 600000"#, #""iterations": 10000001"#)), damaged)
        XCTAssertEqual(error(edited("PBKDF2-HMAC-SHA256", "PBKDF2-HMAC-SHA1")), damaged)
        XCTAssertEqual(error(edited("AES-256-GCM", "AES-256-CBC")), damaged)
        XCTAssertEqual(error(edited("UU/k76wNJP2w1tI65q/vEg==", "UU/k76wNJP2w1tI65q/v")), damaged)         // salt of 15 bytes
        XCTAssertEqual(error(edited("s5e1gcIf9JZ9xuuV", "s5e1gcIf9JZ9xuuVAAAA")), damaged)                 // nonce of 15 bytes
        XCTAssertEqual(error(edited("s5e1gcIf9JZ9xuuV", "not base64!")), damaged)
        XCTAssertEqual(error(edited(#""version": 1"#, #""version": 0"#)), damaged)
        XCTAssertEqual(error(Data(#"{"format": "envy-backup", "version": 1}"#.utf8)), damaged)
        // With the right format, a field that is missing or of the wrong type is damage.
        XCTAssertEqual(error(Data(#"{"format": "envy-backup"}"#.utf8)), damaged)
        XCTAssertEqual(error(edited(#""version": 1"#, #""version": "1""#)), damaged)
        XCTAssertEqual(error(edited(#""iterations": 600000"#, #""iterations": "600000""#)), damaged)
        XCTAssertEqual(error(edited(#""nonce": "s5e1gcIf9JZ9xuuV""#, #""nonce": 12"#)), damaged)
        let short = Data(#"{"format": "envy-backup", "version": 1, "kdf": {"name": "PBKDF2-HMAC-SHA256", "iterations": 100000, "salt": "UU/k76wNJP2w1tI65q/vEg=="}, "cipher": {"name": "AES-256-GCM", "nonce": "s5e1gcIf9JZ9xuuV"}, "data": "AAAAAAAAAAAAAAAAAAAA"}"#.utf8)
        XCTAssertEqual(error(short), damaged) // 15 bytes of data: shorter than a tag

        // Decrypts, but what's inside isn't the contents.
        XCTAssertEqual(error(try Backup.seal(Data("not json".utf8), password: samplePassword, iterations: fastIterations)), damaged)
    }

    func testRoundTripsWithAFreshSaltAndNonceEachTime() throws {
        var content = Backup.content(of: EnvConfig(), home: "/Users/me", app: "Envy test", created: Date(timeIntervalSince1970: 1_795_582_800))
        XCTAssertEqual(content.created, "2026-11-25T05:00:00Z")
        content.global.variables.append(BackupVariable(key: "TOKEN", value: "ünïcødé secret ✓", isSecret: true))
        let first = try Backup.write(content, password: "twelve chars!", iterations: fastIterations)
        let second = try Backup.write(content, password: "twelve chars!", iterations: fastIterations)
        XCTAssertNotEqual(first, second)
        let text = String(decoding: first, as: UTF8.self)
        XCTAssertFalse(text.contains("secret"))
        XCTAssertFalse(text.contains("TOKEN"))
        XCTAssertFalse(text.contains("Global"))
        XCTAssertEqual(try Backup.read(second, password: "twelve chars!"), content)
        // The same password typed in decomposed form opens it too, and the other way round.
        XCTAssertEqual(try Backup.read(Backup.write(content, password: "café au lait!", iterations: fastIterations), password: "cafe\u{301} au lait!"), content)
        XCTAssertEqual(try Backup.read(Backup.write(content, password: "cafe\u{301} au lait!", iterations: fastIterations), password: "café au lait!"), content)
    }

    /// What the Mac writes, read the way the spec describes it rather than through `Backup.read`.
    func testWritesTheFileTheSpecDescribes() throws {
        let config = EnvConfig(
            global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "EDITOR", value: "code --wait")]),
            projects: [Project(name: "api", isEnabled: false, folders: ["/Users/me/code/api", "/Volumes/Code/old"], variables: [
                EnvVar(key: "DB_PASSWORD", value: "s3cret", isSecret: true, note: "dummy"),
                EnvVar(key: "OFF", value: "1", isEnabled: false, expands: true),
            ])]
        )
        let content = Backup.content(of: config, home: "/Users/me", app: "Envy 1.2.0", created: Date())
        let file = try Backup.write(content, password: "correct horse battery")

        let outer = try XCTUnwrap(JSONSerialization.jsonObject(with: file) as? [String: Any])
        XCTAssertEqual(Set(outer.keys), ["format", "version", "kdf", "cipher", "data"])
        XCTAssertEqual(outer["format"] as? String, "envy-backup")
        XCTAssertEqual(outer["version"] as? Int, 1)
        let kdf = try XCTUnwrap(outer["kdf"] as? [String: Any])
        let cipher = try XCTUnwrap(outer["cipher"] as? [String: Any])
        XCTAssertEqual(kdf["name"] as? String, "PBKDF2-HMAC-SHA256")
        XCTAssertEqual(kdf["iterations"] as? Int, 600_000)
        let salt = try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(kdf["salt"] as? String)))
        XCTAssertEqual(salt.count, 16)
        XCTAssertEqual(cipher["name"] as? String, "AES-256-GCM")
        let nonce = try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(cipher["nonce"] as? String)))
        XCTAssertEqual(nonce.count, 12)
        let data = try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(outer["data"] as? String)))

        // AES.GCM.SealedBox(combined: nonce + data), as the spec puts it, with the 14-byte additional data.
        let key = try Backup.deriveKey("correct horse battery", salt: salt, iterations: 600_000)
        let plain = try AES.GCM.open(AES.GCM.SealedBox(combined: nonce + data), using: key, authenticating: Data("envy-backup v1".utf8))
        let inner = try XCTUnwrap(JSONSerialization.jsonObject(with: plain) as? [String: Any])
        XCTAssertEqual(Set(inner.keys), ["app", "platform", "created", "global", "projects"])
        XCTAssertEqual(inner["platform"] as? String, "macos")
        XCTAssertEqual(inner["app"] as? String, "Envy 1.2.0")
        XCTAssertNotNil(ISO8601DateFormatter().date(from: try XCTUnwrap(inner["created"] as? String)))
        let projects = try XCTUnwrap(inner["projects"] as? [[String: Any]])
        XCTAssertEqual(Set(projects[0].keys), ["name", "isEnabled", "folders", "variables"])
        XCTAssertEqual(projects[0]["folders"] as? [String], ["~/code/api", "/Volumes/Code/old"])
        XCTAssertEqual(projects[0]["isEnabled"] as? Bool, false)
        let variables = try XCTUnwrap(projects[0]["variables"] as? [[String: Any]])
        XCTAssertEqual(Set(variables[0].keys), ["key", "value", "isEnabled", "isSecret", "expands", "note"])
        XCTAssertEqual(variables[0]["value"] as? String, "s3cret")
        XCTAssertEqual(variables[0]["isSecret"] as? Bool, true)
        XCTAssertEqual(variables[1]["isEnabled"] as? Bool, false)
        XCTAssertEqual(variables[1]["expands"] as? Bool, true)
        XCTAssertFalse(String(decoding: plain, as: UTF8.self).contains("\"id\""))
        let global = try XCTUnwrap(inner["global"] as? [String: Any])
        XCTAssertEqual(global["name"] as? String, "Global")
        XCTAssertEqual(global["folders"] as? [String], [])

        XCTAssertEqual(try Backup.read(file, password: "correct horse battery"), content)
    }

    func testMissingFieldsTakeTheSettingsFileDefaults() throws {
        let plain = Data(#"{"platform": "windows", "global": {"variables": [{"key": "A"}]}, "projects": [{"name": "p", "variables": [{"key": "B", "isSecret": true, "extra": 1}]}], "unknown": true}"#.utf8)
        let content = try Backup.read(Backup.seal(plain, password: samplePassword, iterations: fastIterations), password: samplePassword)
        XCTAssertEqual(content.global.variables, [BackupVariable(key: "A")])
        XCTAssertTrue(content.global.isEnabled)
        XCTAssertEqual(content.projects, [BackupScope(name: "p", variables: [BackupVariable(key: "B", isSecret: true)])])
        XCTAssertEqual(content.app, "")
    }

    func testPasswordRules() {
        XCTAssertEqual(Backup.passwordProblem("short", confirmation: "short"), "Use at least 12 characters.")
        XCTAssertEqual(Backup.passwordProblem("eleven char", confirmation: "eleven char"), "Use at least 12 characters.")
        XCTAssertEqual(Backup.passwordProblem("twelve chars!", confirmation: "twelve chars?"), "The passwords don't match.")
        XCTAssertNil(Backup.passwordProblem("twelve chars", confirmation: "twelve chars"))
        XCTAssertNil(Backup.passwordProblem("café au lait!", confirmation: "cafe\u{301} au lait!"))
        let amsterdam = TimeZone(identifier: "Europe/Amsterdam")!
        // 23:30 UTC on 24 November is already the 25th in Amsterdam.
        XCTAssertEqual(Backup.suggestedFileName(Date(timeIntervalSince1970: 1_795_563_000), timeZone: amsterdam), "Envy Backup 2026-11-25.envybackup")
    }

    func testFoldersUnderHomeTravelAsTildePaths() {
        XCTAssertEqual(Backup.portableFolder("/Users/me/code/api", home: "/Users/me"), "~/code/api")
        XCTAssertEqual(Backup.portableFolder("/users/ME/code/api/", home: "/Users/me"), "~/code/api")
        XCTAssertEqual(Backup.portableFolder("/Users/me", home: "/Users/me/"), "~")
        XCTAssertEqual(Backup.portableFolder("/Users/meow/x", home: "/Users/me"), "/Users/meow/x")
        XCTAssertEqual(Backup.portableFolder("/Volumes/Code/api", home: "/Users/me"), "/Volumes/Code/api")
        XCTAssertEqual(Backup.localFolder("~/Developer/api", home: "/Users/you"), "/Users/you/Developer/api")
        XCTAssertEqual(Backup.localFolder("~/code/api", home: "/Users/you/"), "/Users/you/code/api")
        XCTAssertEqual(Backup.localFolder("~", home: "/Users/you"), "/Users/you")
        XCTAssertEqual(Backup.localFolder("/Volumes/Code/api-old", home: "/Users/you"), "/Volumes/Code/api-old")
        XCTAssertEqual(Backup.localFolder(#"D:\work\site"#, home: "/Users/you"), #"D:\work\site"#)
        // Global never has folders in a backup.
        var config = EnvConfig()
        config.global.folders = ["/Users/me/x"]
        XCTAssertEqual(Backup.content(of: config, home: "/Users/me", app: "", created: Date()).global.folders, [])
    }

    // MARK: Restoring

    func testAddMergesByNameAndNeverDeletes() throws {
        let home = "/Users/you"
        let web = "/Users/you/Developer/web"
        let tools = EnvVar(key: "TOOLS", value: "old")
        var config = EnvConfig(
            global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "path", value: "mine"), EnvVar(key: "KEEP", value: "1"), tools]),
            projects: [
                Project(name: "api", isEnabled: false, variables: [EnvVar(key: "PORT", value: "1"), EnvVar(key: "LOCAL", value: "x")]),
                Project(name: "frontend", folders: [web]),
                Project(name: "Mine"),
            ]
        )

        let report = BackupRestore.apply(try open("macos.envybackup"), to: &config, mode: .add, home: home, folderExists: { $0 == web })

        // Global: the Mac compares names exactly, so `path` stays next to the backup's PATH; TOOLS is replaced in place
        // and keeps its ID (secrets are keyed by it); KEEP stays.
        XCTAssertEqual(config.global.variables.map(\.key), ["path", "KEEP", "TOOLS", "PATH", "APP_CONFIG", "CACHE", "GITHUB_TOKEN"])
        XCTAssertEqual(config.global.variables[2].id, tools.id)
        XCTAssertEqual(config.global.variables[2].value, "$(brew --prefix)/bin")
        XCTAssertTrue(config.global.variables[2].expands)
        XCTAssertEqual(config.global.variables.first { $0.key == "GITHUB_TOKEN" }?.isSecret, true)
        XCTAssertEqual(config.global.id, Project.globalID)
        // "API" merges into "api", ignoring case: the backup's values win, local variables stay, the project stays off
        // and the folders are added.
        let api = config.projects[0]
        XCTAssertEqual(api.name, "api")
        XCTAssertFalse(api.isEnabled)
        XCTAssertEqual(api.variables.map { "\($0.key)=\($0.value)" }, ["PORT=5000", "LOCAL=x", "NODE_ENV=development"])
        XCTAssertEqual(api.folders, ["/Users/you/Developer/api", "/Volumes/Code/api-old"])
        // "web" is new, but its only folder belongs to "frontend" here: not linked again, and switched off.
        let restoredWeb = try XCTUnwrap(config.projects.first { $0.name == "web" })
        XCTAssertEqual(restoredWeb.folders, [])
        XCTAssertFalse(restoredWeb.isEnabled)
        XCTAssertEqual(config.projects.first { $0.name == "frontend" }?.folders, [web])
        XCTAssertEqual(config.projects.map(\.name), ["api", "frontend", "Mine", "web"])
        XCTAssertEqual(Set(config.scopes.flatMap(\.variables).map(\.id)).count, config.scopes.flatMap(\.variables).count)

        XCTAssertEqual(report.projectsMerged, ["api"])
        XCTAssertEqual(report.projectsAdded, ["web"])
        XCTAssertEqual(report.projectsRemoved, [])
        XCTAssertEqual(report.skippedFolders, [.init(project: "web", folder: web, owner: "frontend")])
        XCTAssertEqual(report.turnedOff, ["web"])
        XCTAssertEqual(report.secrets, 1)
        XCTAssertEqual(report.variablesReplaced, 2) // TOOLS, PORT
        XCTAssertEqual(report.variablesAdded, 6)    // PATH, APP_CONFIG, CACHE, GITHUB_TOKEN, NODE_ENV, PUBLIC_URL
        XCTAssertEqual(report.missingFolders, [
            .init(project: "api", folder: "/Users/you/Developer/api"),
            .init(project: "api", folder: "/Volumes/Code/api-old"),
        ])
    }

    func testAddKeepsFoldersAlreadyLinkedToTheSameProject() throws {
        var config = EnvConfig(projects: [Project(name: "API", folders: ["/Users/you/Developer/api"])])
        let report = BackupRestore.apply(try open("macos.envybackup"), to: &config, mode: .add, home: "/Users/you", folderExists: { _ in true })
        XCTAssertEqual(config.projects[0].folders, ["/Users/you/Developer/api", "/Volumes/Code/api-old"])
        XCTAssertEqual(report.skippedFolders, [])
        XCTAssertEqual(report.projectsMerged, ["API"])
        XCTAssertEqual(report.projectsAdded, ["web"])
        XCTAssertTrue(config.projects[1].isEnabled)
    }

    func testReplaceMakesItExactlyTheBackup() throws {
        var config = EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "KEEP", value: "1")]), projects: [Project(name: "Mine")])
        let report = BackupRestore.apply(try open("macos.envybackup"), to: &config, mode: .replace, home: "/Users/you", folderExists: { _ in true })
        XCTAssertEqual(config.global.id, Project.globalID)
        XCTAssertEqual(config.global.variables.map(\.key), ["PATH", "TOOLS", "APP_CONFIG", "CACHE", "GITHUB_TOKEN"])
        XCTAssertEqual(config.projects.map(\.name), ["API", "web"])
        XCTAssertEqual(config.projects[0].folders, ["/Users/you/Developer/api", "/Volumes/Code/api-old"])
        XCTAssertEqual(report.projectsRemoved, ["Mine"])
        XCTAssertEqual(report.projectsAdded, ["API", "web"])
        XCTAssertEqual(report.variablesAdded, 8)
        XCTAssertEqual(report.secrets, 1)
        XCTAssertEqual(report.missingFolders, [])
        XCTAssertEqual(Set(config.projects.map(\.id)).count, 2)
        XCTAssertFalse(config.projects.contains { $0.id == Project.globalID })
    }

    func testRestoresAWindowsBackupOnAMac() throws {
        var config = EnvConfig()
        let report = BackupRestore.apply(try open("windows.envybackup"), to: &config, mode: .add, home: "/Users/you", folderExists: { _ in false })
        XCTAssertEqual(config.projects.map(\.name), ["api", "Tools", "Work site"])
        XCTAssertEqual(config.projects.map(\.isEnabled), [true, false, true])
        XCTAssertEqual(config.projects.map(\.folders), [["/Users/you/code/api"], [], [#"D:\work\site"#]])
        XCTAssertEqual(report.missingFolders.map(\.folder), ["/Users/you/code/api", #"D:\work\site"#])
        XCTAssertEqual(report.secrets, 2)
        XCTAssertEqual(report.variablesAdded, 12)
        XCTAssertEqual(config.secretValues().values.sorted(), ["sk-test-sample-123", "test-db-pass"])
    }

    // MARK: Expanding values

    private func summary(_ issues: [ExpandIssue]) -> [String] {
        issues.map { "\($0.scopeName) \($0.key) → \($0.suggestion ?? "none") (\($0.defaultAction))" }
    }

    func testSuggestsRewritesForWindowsValuesOnAMac() throws {
        let windows = try open("windows.envybackup")
        let issues = BackupRestore.expandIssues(windows, here: Backup.macOS)
        XCTAssertEqual(summary(issues), [
            "Global PATH → $HOME/bin:${PATH} (useSuggestion)",
            "Global JAVA_BIN → ${JAVA_HOME}/bin (useSuggestion)",
            "Global WHO → none (turnOff)",
            "api API_URL → http://localhost:${PORT}/api (useSuggestion)",
        ])
        XCTAssertEqual(issues.map(\.reason), [
            "A Mac doesn't understand %NAME% or $env:NAME. A Mac separates folders with :.",
            "A Mac doesn't understand %NAME% or $env:NAME.",
            "This would run a command on a Mac; on Windows it was plain text.",
            "A Mac doesn't understand %NAME% or $env:NAME.",
        ])
        XCTAssertEqual(issues.map(\.value), [#"%USERPROFILE%\bin;%PATH%"#, #"$env:JAVA_HOME\bin"#, "$(whoami)", "http://localhost:%PORT%/api"])
        XCTAssertEqual(issues[2].actions, [.keepAsIs, .turnOff])
        XCTAssertEqual(issues[0].actions, [.useSuggestion, .keepAsIs, .turnOff])
        XCTAssertEqual(BackupRestore.expandIssues(windows, here: Backup.windows), [])
    }

    func testSuggestsRewritesForMacValuesOnWindows() throws {
        let mac = try open("macos.envybackup")
        let issues = BackupRestore.expandIssues(mac, here: Backup.windows)
        XCTAssertEqual(summary(issues), [
            #"Global PATH → %USERPROFILE%\bin;$PATH (useSuggestion)"#,
            "Global TOOLS → none (keepAsIs)",
            #"Global APP_CONFIG → %USERPROFILE%\.config\app (useSuggestion)"#,
        ])
        XCTAssertEqual(issues[0].reason, "$HOME isn't set in PowerShell or Command Prompt. Windows separates folders with ;.")
        XCTAssertEqual(issues[1].reason, "Runs a command. Windows doesn't run commands in values.")
        XCTAssertEqual(BackupRestore.expandIssues(mac, here: Backup.macOS), [])
    }

    func testSuggestionRules() {
        func mac(_ key: String, _ value: String) -> String? {
            guard let found = BackupRestore.suggest(key: key, value: value, from: Backup.windows, to: Backup.macOS) else { return nil }
            return found.suggestion ?? "none"
        }
        func windows(_ key: String, _ value: String) -> String? {
            guard let found = BackupRestore.suggest(key: key, value: value, from: Backup.macOS, to: Backup.windows) else { return nil }
            return found.suggestion ?? "none"
        }
        // From Windows
        XCTAssertEqual(mac("A", "%userprofile%\\x"), "$HOME/x")
        XCTAssertEqual(mac("A", "$ENV:Home\\x"), "${Home}/x")
        XCTAssertEqual(mac("A", "$ENV:userprofile"), "$HOME")
        XCTAssertEqual(mac("TOOLS_PATH", #"$env:USERPROFILE\tools;%userprofile%\bin"#), "$HOME/tools:$HOME/bin")
        XCTAssertEqual(mac("A", "%1abc% %not a name% %OK%"), "%1abc% %not a name% ${OK}")
        XCTAssertNil(mac("A", "%1abc% 50%"))
        XCTAssertNil(mac("A", "${HOME}/x ~/y $NAME"))
        XCTAssertNil(mac("A", #"C:\tools;D:\bin"#))                          // not a PATH list, nothing to rewrite
        XCTAssertEqual(mac("MYPATH", #"C:\tools;D:\bin"#), "C:/tools:D:/bin")
        XCTAssertEqual(mac("Path", "%A%;%B%"), "${A}:${B}")
        XCTAssertNil(mac("PATHS", "a;b"))
        XCTAssertEqual(mac("A", "echo `date`"), "none")
        XCTAssertEqual(mac("A", "https://%HOST%/a\\b"), "https://${HOST}/a\\b") // a URL keeps its slashes
        // From a Mac
        XCTAssertEqual(windows("A", "$HOME/x"), #"%USERPROFILE%\x"#)
        XCTAssertNil(windows("A", "$HOMEBREW_PREFIX/bin"))
        XCTAssertEqual(windows("A", "${HOME}:$HOME"), "%USERPROFILE%:%USERPROFILE%")
        XCTAssertEqual(windows("MANPATH", "/usr/share/man:/opt/man"), #"\usr\share\man;\opt\man"#)
        XCTAssertNil(windows("A", "~/bin:$OTHER"))
        XCTAssertEqual(windows("A", "$(brew --prefix)"), "none")
        XCTAssertEqual(windows("URL", "http://$HOME/x"), "http://%USERPROFILE%/x")
        // Same system or an unknown one: nothing to say.
        XCTAssertNil(BackupRestore.suggest(key: "PATH", value: "%PATH%;x", from: Backup.windows, to: Backup.windows))
        var other = BackupContent(app: "", platform: "linux", created: "", global: BackupScope(name: "Global", variables: [BackupVariable(key: "A", value: "%X%", expands: true)]), projects: [])
        XCTAssertEqual(BackupRestore.expandIssues(other, here: Backup.macOS), [])
        // Only values that expand.
        other.platform = Backup.windows
        XCTAssertEqual(BackupRestore.expandIssues(other, here: Backup.macOS).count, 1)
        other.global.variables[0].expands = false
        XCTAssertEqual(BackupRestore.expandIssues(other, here: Backup.macOS), [])
    }

    func testAppliesTheChoicesForExpandingValues() throws {
        let windows = try open("windows.envybackup")
        let issues = BackupRestore.expandIssues(windows, here: Backup.macOS)
        var fixes = issues.map { ExpandFix(scope: $0.scope, index: $0.index, action: $0.defaultAction, value: $0.suggestion) }
        fixes[0].value = "$HOME/my-bin:${PATH}"                                      // edited suggestion
        fixes[1] = ExpandFix(scope: fixes[1].scope, index: fixes[1].index, action: .keepAsIs, value: "ignored")
        var config = EnvConfig()
        BackupRestore.apply(windows, to: &config, mode: .replace, home: "/Users/you", fixes: fixes, folderExists: { _ in true })

        func variable(_ key: String) -> EnvVar? { config.scopes.flatMap(\.variables).first { $0.key == key } }
        XCTAssertEqual(variable("PATH")?.value, "$HOME/my-bin:${PATH}")
        XCTAssertEqual(variable("PATH")?.expands, true)
        XCTAssertEqual(variable("JAVA_BIN")?.value, #"$env:JAVA_HOME\bin"#)
        XCTAssertEqual(variable("JAVA_BIN")?.expands, true)
        XCTAssertEqual(variable("WHO")?.value, "$(whoami)")
        XCTAssertEqual(variable("WHO")?.expands, false)
        XCTAssertEqual(variable("API_URL")?.value, "http://localhost:${PORT}/api")
        XCTAssertEqual(variable("GREETING")?.value, "hello ${USERNAME}")
        XCTAssertEqual(variable("GREETING")?.expands, true)
    }

    // MARK: From one Mac to another

    func testBackUpOnOneMacAndRestoreOnAnother() throws {
        let first = EnvConfig(
            global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "EDITOR", value: "code")]),
            projects: [Project(name: "api", folders: ["/Users/one/code/api"], variables: [
                EnvVar(key: "PORT", value: "4000"),
                EnvVar(key: "DB_PASSWORD", value: "s3cret", isSecret: true),
                EnvVar(key: "OFF", value: "1", isEnabled: false),
            ])]
        )
        let file = try Backup.write(Backup.content(of: first, home: "/Users/one", app: "Envy test", created: Date()), password: "twelve chars!", iterations: fastIterations)

        var second = EnvConfig(projects: [Project(name: "Mine")])
        let content = try Backup.read(file, password: "twelve chars!")
        XCTAssertEqual(BackupRestore.expandIssues(content, here: Backup.macOS), [])
        var preview = second
        XCTAssertEqual(BackupRestore.apply(content, to: &preview, mode: .replace, home: "/Users/two", folderExists: { _ in true }).projectsRemoved, ["Mine"])
        XCTAssertEqual(second.projects.map(\.name), ["Mine"]) // a preview changes nothing

        BackupRestore.apply(content, to: &second, mode: .replace, home: "/Users/two", folderExists: { _ in true })
        let restored = try XCTUnwrap(second.projects.first)
        XCTAssertEqual(second.projects.count, 1)
        XCTAssertEqual(restored.name, "api")
        XCTAssertEqual(restored.folders, ["/Users/two/code/api"])
        XCTAssertEqual(restored.variables.map { "\($0.key)=\($0.value)" }, ["PORT=4000", "DB_PASSWORD=s3cret", "OFF=1"])
        XCTAssertTrue(restored.variables[1].isSecret)
        XCTAssertFalse(restored.variables[2].isEnabled)
        XCTAssertNotEqual(restored.id, first.projects[0].id)
        XCTAssertEqual(Set(restored.variables.map(\.id)).isDisjoint(with: first.projects[0].variables.map(\.id)), true)
        // The secret leaves the settings file and goes to the vault, keyed by its new ID.
        XCTAssertEqual(second.secretValues(), [restored.variables[1].id.uuidString: "s3cret"])
        XCTAssertEqual(second.withoutSecretValues().projects[0].variables[1].value, "")
        XCTAssertEqual(second.global.variables.map(\.key), ["EDITOR"])
    }
}
