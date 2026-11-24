import XCTest
@testable import EnvyCore

final class AgentInstructionsTests: XCTestCase {
    private var home: URL!

    override func setUpWithError() throws {
        home = FileManager.default.temporaryDirectory.appendingPathComponent("envy-agents-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private func target(_ id: String, env: [String: String] = [:]) -> AgentTarget {
        AgentInstructions.targets(home: home, environment: env).first { $0.id == id }!
    }

    private func read(_ url: URL) throws -> String { try String(contentsOf: url, encoding: .utf8) }

    func testWritesFilesEnvyOwns() throws {
        let claude = target("claude"), agents = target("agents")
        XCTAssertEqual(claude.file.path, home.path + "/.claude/rules/envy.md")
        XCTAssertEqual(agents.file.path, home.path + "/.agents/skills/envy/SKILL.md")

        for t in [claude, agents] {
            XCTAssertEqual(AgentInstructions.status(of: t), .notInstalled)
            try AgentInstructions.install(t)
            XCTAssertEqual(AgentInstructions.status(of: t), .installed)
        }
        XCTAssertTrue(try read(agents.file).hasPrefix("---\nname: envy\ndescription: Environment variables"))
        XCTAssertTrue(try read(claude.file).hasPrefix("<!-- Written by Envy.app"))
    }

    func testUpdatesWhatAnOlderEnvyWrote() throws {
        let agents = target("agents")
        try AgentInstructions.install(agents)
        try "---\nname: envy\n---\n\(AgentInstructions.marker)\nOld advice.\n".write(to: agents.file, atomically: true, encoding: .utf8)
        XCTAssertEqual(AgentInstructions.status(of: agents), .outdated)
        try AgentInstructions.install(agents)
        XCTAssertEqual(AgentInstructions.status(of: agents), .installed)
    }

    func testRemovesOnlyItsOwnFiles() throws {
        let claude = target("claude"), agents = target("agents")
        try FileManager.default.createDirectory(at: claude.file.deletingLastPathComponent(), withIntermediateDirectories: true)
        let mine = claude.file.deletingLastPathComponent().appendingPathComponent("style.md")
        try "Use tabs.\n".write(to: mine, atomically: true, encoding: .utf8)
        try AgentInstructions.install(claude)
        try AgentInstructions.install(agents)

        try AgentInstructions.uninstall(claude)
        try AgentInstructions.uninstall(agents)
        XCTAssertFalse(FileManager.default.fileExists(atPath: claude.file.path))
        XCTAssertEqual(try read(mine), "Use tabs.\n")
        XCTAssertFalse(FileManager.default.fileExists(atPath: agents.file.deletingLastPathComponent().path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: home.appendingPathComponent(".agents/skills").path))
    }

    func testLeavesSomeoneElsesFileAlone() throws {
        let agents = target("agents")
        try FileManager.default.createDirectory(at: agents.file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try "my own envy skill\n".write(to: agents.file, atomically: true, encoding: .utf8)
        XCTAssertEqual(AgentInstructions.status(of: agents), .foreign)
        XCTAssertThrowsError(try AgentInstructions.install(agents))
        try AgentInstructions.uninstall(agents)
        XCTAssertEqual(try read(agents.file), "my own envy skill\n")
    }

    func testHonoursClaudesConfigFolder() {
        XCTAssertEqual(target("claude", env: ["CLAUDE_CONFIG_DIR": "/tmp/cc"]).file.path, "/tmp/cc/rules/envy.md")
    }

    func testInstructionsMentionOnlyRealCommands() throws {
        // Every `envy …` the instructions suggest must parse.
        let text = AgentInstructions.content(for: .rule) + AgentInstructions.content(for: .skill)
        let pattern = try NSRegularExpression(pattern: "`envy ([^`]+)`")
        let matches = pattern.matches(in: text, range: NSRange(text.startIndex..., in: text))
        XCTAssertGreaterThan(matches.count, 5)
        for match in matches {
            let line = String(text[Range(match.range(at: 1), in: text)!])
            if line.hasPrefix("exec") { continue }
            let args = line.split(separator: " ").map(String.init)
            XCTAssertNoThrow(try EnvyCommand.parse(args, cwd: "/tmp"), line)
        }
    }
}
