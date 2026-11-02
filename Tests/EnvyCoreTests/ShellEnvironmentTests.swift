import XCTest
@testable import EnvyCore

/// Detection runs the real /bin/zsh and /bin/bash against a temporary home.
final class ShellEnvironmentTests: XCTestCase {
    private var home: URL!

    override func setUpWithError() throws {
        home = FileManager.default.temporaryDirectory.appendingPathComponent("envy-env-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private func write(_ text: String, to name: String) throws {
        let url = home.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try text.write(to: url, atomically: true, encoding: .utf8)
    }

    func testFindsZdotdirSetInZshenv() throws {
        XCTAssertNil(ShellEnvironment.detect(home: home, shellPath: "/bin/zsh").zdotdir)
        try write(#"export ZDOTDIR="$HOME/.config/zsh""# + "\n", to: ".zshenv")
        XCTAssertEqual(ShellEnvironment.detect(home: home, shellPath: "/bin/zsh").zdotdir, home.path + "/.config/zsh")
    }

    func testShellKinds() {
        XCTAssertEqual(ShellEnvironment(shellPath: "/bin/zsh").kind, .zsh)
        XCTAssertEqual(ShellEnvironment(shellPath: "/opt/homebrew/bin/bash").kind, .bash)
        XCTAssertEqual(ShellEnvironment(shellPath: "/opt/homebrew/bin/fish").supportedShell, "/bin/zsh")
    }

    func testInspectionSeesTheHookOnlyOnceConnected() throws {
        try ShellOutput(directory: home.appendingPathComponent(".config/envy")).write(EnvConfig())
        for (shell, rc) in [("/bin/zsh", ".zshrc"), ("/bin/bash", ".bash_profile")] {
            XCTAssertEqual(ShellProbe.inspect(shell: shell, home: home)?.hookLoaded, false, shell)
            try RcIntegration.install(in: home.appendingPathComponent(rc))
            XCTAssertEqual(ShellProbe.inspect(shell: shell, home: home)?.hookLoaded, true, shell)
        }
    }

    func testInspectionFindsAgentsOnTheShellsPath() throws {
        try write("#!/bin/sh\n", to: "tools/claude")
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: home.appendingPathComponent("tools/claude").path)
        try write(#"export PATH="$HOME/tools:$PATH""# + "\n", to: ".zshrc")
        XCTAssertEqual(ShellProbe.inspect(shell: "/bin/zsh", home: home)?.agents.contains("Claude Code"), true)
    }

    func testInspectionGivesUpOnAHangingStartupFile() throws {
        try write("sleep 30\n", to: ".zshrc")
        XCTAssertNil(ShellProbe.inspect(shell: "/bin/zsh", home: home, timeout: 1))
    }

    func testScannerFindsPlainTextExports() {
        let file = URL(fileURLWithPath: "/tmp/.zshrc")
        let text = """
        export OPENAI_API_KEY="sk-live-123"
          export GITHUB_TOKEN=ghp_abc # personal
        export PATH="$HOME/bin:$PATH"
        export KUBECONFIG='~/kube/$config'
        export NOT_AN_ASSIGNMENT
        alias ll='ls -l'
        \(RcIntegration.snippet)
        export DATA_DIR="$HOME/data"
        """
        let found = RcScanner.exports(in: text, file: file)
        XCTAssertEqual(found.map(\.key), ["OPENAI_API_KEY", "GITHUB_TOKEN", "KUBECONFIG", "DATA_DIR"])
        XCTAssertEqual(found.map(\.line), [1, 2, 4, 11])
        XCTAssertEqual(found[1].value, "ghp_abc")
        XCTAssertTrue(found[0].variable.isSecret)
        XCTAssertFalse(found[2].variable.expands, "single-quoted values stay literal")
        XCTAssertTrue(found[3].variable.expands)
    }
}
