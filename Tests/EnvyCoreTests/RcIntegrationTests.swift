import XCTest
@testable import EnvyCore

final class RcIntegrationTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("envy-rc-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func testInstallAppendsOnceAndUninstallRestoresTheFile() throws {
        let rc = dir.appendingPathComponent(".zshrc")
        let original = "export EDITOR=vim\nalias ll='ls -l'\n"
        try original.write(to: rc, atomically: true, encoding: .utf8)

        try RcIntegration.install(in: rc)
        try RcIntegration.install(in: rc)
        let installed = try String(contentsOf: rc, encoding: .utf8)
        XCTAssertTrue(installed.hasPrefix(original))
        XCTAssertEqual(installed.components(separatedBy: RcIntegration.beginMarker).count, 2)
        XCTAssertTrue(RcIntegration.isInstalled(in: rc))
        XCTAssertEqual(try String(contentsOf: dir.appendingPathComponent(".zshrc.envy-backup"), encoding: .utf8), original)

        try RcIntegration.uninstall(from: rc)
        XCTAssertEqual(try String(contentsOf: rc, encoding: .utf8), original)
        XCTAssertFalse(RcIntegration.isInstalled(in: rc))
    }

    func testInstallCreatesMissingFile() throws {
        let rc = dir.appendingPathComponent(".bashrc")
        try RcIntegration.install(in: rc)
        XCTAssertEqual(try String(contentsOf: rc, encoding: .utf8), RcIntegration.snippet + "\n")
    }

    func testInstallWritesThroughSymlinks() throws {
        let real = dir.appendingPathComponent("dotfiles-zshrc")
        try "export A=1".write(to: real, atomically: true, encoding: .utf8)
        let link = dir.appendingPathComponent(".zshrc")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: real)

        try RcIntegration.install(in: link)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: link.path), real.path)
        XCTAssertTrue(try String(contentsOf: real, encoding: .utf8).hasPrefix("export A=1\n\n" + RcIntegration.beginMarker))
    }

    func testSnippetSourcesTheHookFromHome() throws {
        let home = dir!
        let shellDir = home.appendingPathComponent(".config/envy")
        try ShellOutput(directory: shellDir).write(EnvConfig(global: Project(
            id: Project.globalID, name: "Global", variables: [EnvVar(key: "FROM_RC", value: "yes")]
        )))
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-f", "-c", RcIntegration.snippet + "\nprintf '%s' \"$FROM_RC\""]
        process.environment = ["HOME": home.path, "PATH": "/usr/bin:/bin"]
        let out = Pipe()
        process.standardOutput = out
        try process.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        XCTAssertEqual(String(decoding: data, as: UTF8.self), "yes")
    }
}
