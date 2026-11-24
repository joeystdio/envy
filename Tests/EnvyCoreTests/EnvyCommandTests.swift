import XCTest
@testable import EnvyCore

final class EnvyCommandTests: XCTestCase {
    private let repo = "/Users/me/Developer/api"

    private func config() -> EnvConfig {
        var cfg = EnvConfig()
        cfg.global.variables = [
            EnvVar(key: "EDITOR", value: "nvim"),
            EnvVar(key: "OPENAI_API_KEY", value: "sk-live-123", isSecret: true),
        ]
        cfg.projects = [
            Project(name: "api", folders: [repo], variables: [EnvVar(key: "PORT", value: "8080")]),
            Project(name: "api web", folders: [repo + "/web"], variables: []),
            Project(name: "Tools", isEnabled: false, variables: [EnvVar(key: "DEBUG", value: "1")]),
        ]
        return cfg
    }

    private func run(_ args: String..., in cwd: String? = nil, loaded: [String] = [], on cfg: inout EnvConfig) throws -> EnvyCommand.Outcome {
        let dir = cwd ?? repo
        let command = try EnvyCommand.parse(args, cwd: dir)
        return try command.run(on: &cfg, context: .init(directories: [dir], loaded: loaded))
    }

    private func usageError(_ args: String..., in cwd: String? = nil, on cfg: inout EnvConfig) -> String? {
        do {
            let dir = cwd ?? repo
            _ = try EnvyCommand.parse(args, cwd: dir).run(on: &cfg, context: .init(directories: [dir], loaded: []))
            return nil
        } catch let error as EnvyCommand.UsageError {
            return error.message
        } catch {
            return "\(error)"
        }
    }

    // MARK: Parsing

    func testParsesTargetsAndFolders() throws {
        XCTAssertEqual(try EnvyCommand.parse([], cwd: repo), .status)
        XCTAssertEqual(try EnvyCommand.parse(["set", "A=1", "--global"], cwd: repo), .set([.init(key: "A", value: "1")], target: .global, expand: false, plain: false))
        XCTAssertEqual(try EnvyCommand.parse(["set", "URL=a=b", "-p", "api"], cwd: repo), .set([.init(key: "URL", value: "a=b")], target: .named("api"), expand: false, plain: false))
        XCTAssertEqual(try EnvyCommand.parse(["project", "add", "My", "App", "--folder", "."], cwd: repo), .addProject(name: "My App", folders: [repo], enabled: true))
        XCTAssertEqual(try EnvyCommand.parse(["link", "../x", "--project", "api"], cwd: repo), .link("/Users/me/Developer/x", target: .named("api")))
        XCTAssertEqual(try EnvyCommand.parse(["add", "TOKEN", "--secret"], cwd: repo), .addSecret("TOKEN", target: .here))
        XCTAssertEqual(try EnvyCommand.parse(["list", "--project", "global"], cwd: repo), .list(project: "Global"))
    }

    func testRejectsBadInput() {
        var cfg = config()
        let before = cfg
        XCTAssertNotNil(usageError("set", "NOVALUE", on: &cfg))
        XCTAssertNotNil(usageError("set", "1BAD=x", on: &cfg))
        XCTAssertNotNil(usageError("add", "TOKEN=abc", "--secret", on: &cfg))
        XCTAssertNotNil(usageError("set", "A=1", "--global", "--project", "api", on: &cfg))
        XCTAssertNotNil(usageError("off", on: &cfg))
        XCTAssertNotNil(usageError("frobnicate", on: &cfg))
        XCTAssertNotNil(usageError("set", "A=1", "--wat", on: &cfg))
        XCTAssertEqual(cfg, before)
    }

    // MARK: Targets

    func testChangesGoToTheDeepestProjectForThisFolder() throws {
        var cfg = config()
        _ = try run("set", "A=1", in: repo + "/web/src", on: &cfg)
        XCTAssertEqual(cfg.projects[1].variables.map(\.key), ["A"])
        _ = try run("set", "B=2", in: repo + "/lib", on: &cfg)
        XCTAssertEqual(cfg.projects[0].variables.map(\.key), ["PORT", "B"])
    }

    func testOutsideAnyProjectAsksForATarget() throws {
        var cfg = config()
        let message = usageError("set", "A=1", in: "/tmp", on: &cfg)
        XCTAssertTrue(message?.contains("envy project add") == true, message ?? "")
        _ = try run("set", "A=1", "--global", in: "/tmp", on: &cfg)
        XCTAssertTrue(cfg.global.variables.contains { $0.key == "A" })
    }

    // MARK: Secrets

    func testAgentsCantWriteOrSeeSecrets() throws {
        var cfg = config()
        XCTAssertTrue(usageError("set", "GITHUB_TOKEN=ghp_x", on: &cfg)?.contains("envy add GITHUB_TOKEN --secret --project api") == true)
        XCTAssertTrue(usageError("set", "OPENAI_API_KEY=x", "--global", "--plain", on: &cfg)?.contains("is a secret") == true)
        _ = try run("set", "PUBLIC_KEY=abc", "--plain", on: &cfg)
        XCTAssertEqual(cfg.projects[0].variables.last?.isSecret, false)

        let list = try run("list", on: &cfg).output
        XCTAssertFalse(list.contains("sk-live-123"))
        XCTAssertTrue(list.contains("OPENAI_API_KEY  secret"))
        XCTAssertTrue(list.contains("EDITOR=nvim"))
    }

    func testAddSecretAsksTheApp() throws {
        var cfg = config()
        let before = cfg
        let outcome = try run("add", "STRIPE_KEY", "--secret", on: &cfg)
        XCTAssertEqual(outcome.request, .pasteSecret(project: cfg.projects[0].id, key: "STRIPE_KEY"))
        XCTAssertNil(outcome.actionName)
        XCTAssertEqual(cfg, before)
    }

    func testImportKeepsValuesOutOfTheOutput() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("envy-import-\(UUID().uuidString).env")
        try "PORT=9090\nDATABASE_PASSWORD=hunter2\nexport NAME='x y'\n".write(to: file, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: file) }

        var cfg = config()
        let outcome = try run("import", file.path, on: &cfg)
        XCTAssertFalse(outcome.output.contains("hunter2"))
        XCTAssertFalse(outcome.output.contains("9090"))
        XCTAssertTrue(outcome.output.contains("DATABASE_PASSWORD"))
        let vars = cfg.projects[0].variables
        XCTAssertEqual(vars.first { $0.key == "PORT" }?.value, "9090")
        XCTAssertEqual(vars.first { $0.key == "DATABASE_PASSWORD" }?.isSecret, true)
        XCTAssertEqual(vars.first { $0.key == "NAME" }?.value, "x y")
    }

    // MARK: Other changes

    func testOnOffRemoveAndProjects() throws {
        var cfg = config()
        _ = try run("off", "PORT", on: &cfg)
        XCTAssertEqual(cfg.projects[0].variables[0].isEnabled, false)
        _ = try run("on", "--project", "tools", on: &cfg)
        XCTAssertTrue(cfg.projects[2].isEnabled)
        _ = try run("remove", "PORT", on: &cfg)
        XCTAssertTrue(cfg.projects[0].variables.isEmpty)
        XCTAssertNotNil(usageError("remove", "NOPE", on: &cfg))

        _ = try run("project", "add", "cli", "--folder", "/Users/me/Developer/cli", on: &cfg)
        XCTAssertEqual(cfg.projects.last?.folders, ["/Users/me/Developer/cli"])
        XCTAssertNotNil(usageError("project", "add", "CLI", on: &cfg))
        XCTAssertNotNil(usageError("project", "add", "other", "--folder", repo, on: &cfg))
        _ = try run("unlink", "--project", "cli", in: "/Users/me/Developer/cli", on: &cfg)
        XCTAssertEqual(cfg.projects.last?.folders, [])
    }

    func testStatusReportsWhatThisProcessIsMissing() throws {
        var cfg = config()
        let fresh = try run("status", in: repo + "/src", loaded: ["EDITOR", "OPENAI_API_KEY", "PORT"], on: &cfg).output
        XCTAssertTrue(fresh.contains("Global, api"), fresh)
        XCTAssertFalse(fresh.contains("envy exec"), fresh)

        let stale = try run("status", in: repo, loaded: ["EDITOR"], on: &cfg).output
        XCTAssertTrue(stale.contains("missing OPENAI_API_KEY PORT"), stale)
        XCTAssertTrue(stale.contains("envy exec"), stale)
    }

    func testFolderMatchingIsByWholeFolder() {
        let cfg = config()
        XCTAssertEqual(cfg.folderProjects(containing: [repo]).map(\.name), ["api"])
        XCTAssertEqual(cfg.folderProjects(containing: [repo + "/web/x"]).map(\.name), ["api", "api web"])
        XCTAssertEqual(cfg.folderProjects(containing: [repo + "-old"]).map(\.name), [])
    }

    // MARK: Launcher and socket

    func testLauncherRunsTheAppAsTheCommand() {
        let script = ShellOutput.launcher(executable: "/Applications/Envy.app/Contents/MacOS/Envy", environment: ["ENVY_HOME": "/tmp/it's"])
        XCTAssertTrue(script.hasPrefix("#!/bin/sh\n"))
        XCTAssertTrue(script.contains("export ENVY_HOME='/tmp/it'\\''s'\n"))
        XCTAssertTrue(script.hasSuffix("exec '/Applications/Envy.app/Contents/MacOS/Envy' --cli \"$@\"\n"))
        XCTAssertNil(ShellSyntax.check(script))
    }

    func testSocketRoundTrip() async throws {
        let dir = URL(fileURLWithPath: "/tmp/envy-sock-\(UUID().uuidString.prefix(8))")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let path = dir.appendingPathComponent(CommandSocket.fileName).path

        XCTAssertThrowsError(try CommandSocket.send(CommandRequest(arguments: [], directories: [], loaded: []), to: path)) {
            XCTAssertEqual($0 as? CommandSocket.Failure, .notRunning)
        }
        let server = CommandSocket.Server(path: path) { request in
            CommandResponse(output: request.arguments.joined(separator: ","), exitCode: 3)
        }
        try server.start()
        defer { server.stop() }

        let attributes = try FileManager.default.attributesOfItem(atPath: path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
        let response = try await Task.detached {
            try CommandSocket.send(CommandRequest(arguments: ["list", "api"], directories: ["/"], loaded: []), to: path)
        }.value
        XCTAssertEqual(response.output, "list,api")
        XCTAssertEqual(response.exitCode, 3)
    }
}
