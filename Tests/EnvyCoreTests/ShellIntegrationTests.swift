import XCTest
@testable import EnvyCore

/// Runs the generated hook in real zsh and bash processes.
final class ShellIntegrationTests: XCTestCase {
    private var root: URL!
    private var shellDir: URL { root.appendingPathComponent("shell") }
    private let shells = ["/bin/zsh", "/bin/bash"]

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("envy-tests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    // MARK: Helpers

    private func shellCommand(_ shell: String) -> String {
        shell.hasSuffix("zsh") ? "/bin/zsh -f" : "/bin/bash --norc --noprofile"
    }

    /// Runs `script` after sourcing init.sh, the way an rc file would.
    private func run(_ shell: String, _ script: String, env extra: [String: String] = [:]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: shell)
        let flags = shell.hasSuffix("zsh") ? ["-f", "-c"] : ["--norc", "--noprofile", "-c"]
        process.arguments = flags + [". \"$ENVY_DIR/init.sh\"\n" + script]
        var env = ["HOME": root.path, "PATH": "/usr/bin:/bin", "ENVY_DIR": shellDir.path]
        env.merge(extra) { $1 }
        process.environment = env
        process.currentDirectoryURL = root
        let out = Pipe()
        process.standardOutput = out
        process.standardError = out
        try process.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
    }

    private func write(_ config: EnvConfig, to dir: URL? = nil) throws {
        try ShellOutput(directory: dir ?? shellDir).write(config)
    }

    private func folder(_ name: String) throws -> String {
        let url = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url.path
    }

    /// Shell lines that swap in the files generated into `dir`, as the app does when settings change.
    private func swapIn(_ dir: URL) -> String {
        "cp \"\(dir.path)/vars.sh\" \"$ENVY_DIR/vars.sh\"; cp \"\(dir.path)/state\" \"$ENVY_DIR/state\"\n"
    }

    // MARK: Tests

    func testLoadsEnabledScopesAndSkipsDisabledOnes() throws {
        try write(EnvConfig(
            global: Project(id: Project.globalID, name: "Global", variables: [
                EnvVar(key: "A", value: "1"),
                EnvVar(key: "B", value: "2", isEnabled: false),
            ]),
            projects: [
                Project(name: "On", variables: [EnvVar(key: "C", value: "3")]),
                Project(name: "Off", isEnabled: false, variables: [EnvVar(key: "D", value: "4")]),
            ]
        ))
        for shell in shells {
            let out = try run(shell, #"printf '%s|%s|%s|%s\n' "${A-unset}" "${B-unset}" "${C-unset}" "${D-unset}""#)
            XCTAssertEqual(out, "1|unset|3|unset\n", shell)
        }
    }

    func testLiteralValuesArriveByteForByte() throws {
        let values = [
            #"it's "quoted" $HOME `whoami` \n"#,
            "line1\nline2",
            "",
            "trailing\\",
            "ünïcødé ✓",
            "$(echo hacked)",
        ]
        let vars = values.enumerated().map { EnvVar(key: "V\($0.offset)", value: $0.element) }
        try write(EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: vars)))
        let script = values.indices.map { #"printf '<%s>\n' "$V\#($0)""# }.joined(separator: "\n")
        let expected = values.map { "<\($0)>\n" }.joined()
        for shell in shells {
            XCTAssertEqual(try run(shell, script), expected, shell)
        }
    }

    func testFolderProjectLoadsInsideAndRestoresOutside() throws {
        let proj = try folder("proj")
        _ = try folder("proj/sub")
        let sibling = try folder("project2")
        try write(EnvConfig(projects: [
            Project(name: "Proj", folders: [proj], variables: [
                EnvVar(key: "FOLDER_ONLY", value: "yes"),
                EnvVar(key: "PRE_EXISTING", value: "override"),
            ]),
        ]))
        let script = """
        s() { printf '%s,%s\\n' "${FOLDER_ONLY-unset}" "${PRE_EXISTING-unset}"; }
        s
        cd "$PROJ"; __envy_hook; s
        cd sub; __envy_hook; s
        cd "$SIBLING"; __envy_hook; s
        cd /; __envy_hook; s
        """
        for shell in shells {
            let out = try run(shell, script, env: ["PROJ": proj, "SIBLING": sibling, "PRE_EXISTING": "original"])
            XCTAssertEqual(out, "unset,original\nyes,override\nyes,override\nunset,original\nunset,original\n", shell)
        }
    }

    func testDeeperFolderWinsOverShallowerAndAlwaysOn() throws {
        let a = try folder("a")
        let ab = try folder("a/b")
        try write(EnvConfig(
            global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "FOO", value: "global")]),
            projects: [
                Project(name: "Deep", folders: [ab], variables: [EnvVar(key: "FOO", value: "deep")]),
                Project(name: "Everywhere", variables: [EnvVar(key: "FOO", value: "everywhere")]),
                Project(name: "Shallow", folders: [a], variables: [EnvVar(key: "FOO", value: "shallow")]),
            ]
        ))
        let script = """
        printf '%s\\n' "$FOO"
        cd "$A"; __envy_hook; printf '%s\\n' "$FOO"
        cd b; __envy_hook; printf '%s\\n' "$FOO"
        cd /; __envy_hook; printf '%s\\n' "$FOO"
        """
        for shell in shells {
            XCTAssertEqual(try run(shell, script, env: ["A": a]), "everywhere\nshallow\ndeep\neverywhere\n", shell)
        }
    }

    func testExpandingValuesUseOriginalsAndDontPileUpOnReload() throws {
        try write(EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [
            EnvVar(key: "PATH", value: "$HOME/bin:$PATH", expands: true),
            EnvVar(key: "TOOLS", value: "~/tools", expands: true),
            EnvVar(key: "REF", value: "${TOOLS}/x", expands: true),
        ])))
        let script = """
        envy reload >/dev/null
        envy reload >/dev/null
        printf '%s\\n' "$PATH" "$TOOLS" "$REF"
        """
        let home = root.path
        for shell in shells {
            XCTAssertEqual(try run(shell, script), "\(home)/bin:/usr/bin:/bin\n\(home)/tools\n\(home)/tools/x\n", shell)
        }
    }

    func testOpenShellPicksUpChangesAtNextPrompt() throws {
        let first = EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [
            EnvVar(key: "FOO", value: "1"), EnvVar(key: "BAR", value: "2"),
        ]))
        let next = root.appendingPathComponent("next")
        try write(EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [
            EnvVar(key: "FOO", value: "changed"), EnvVar(key: "BAZ", value: "new"),
        ])), to: next)
        let script = #"s() { printf '%s,%s,%s\n' "${FOO-unset}" "${BAR-unset}" "${BAZ-unset}"; }"# + "\ns\n" + swapIn(next) + "__envy_hook\ns\n"
        for shell in shells {
            try write(first) // the previous shell swapped in the next config
            XCTAssertEqual(try run(shell, script), "1,2,unset\nchanged,unset,new\n", shell)
        }
    }

    func testChildShellUndoesWhatItsParentApplied() throws {
        let first = EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [
            EnvVar(key: "FOO", value: "1"), EnvVar(key: "PRE", value: "override"),
        ]))
        let next = root.appendingPathComponent("next")
        try write(EnvConfig(global: Project(id: Project.globalID, name: "Global", isEnabled: false)), to: next)
        for shell in shells {
            // The parent applied FOO and PRE, then settings changed before its next prompt; a new tab or tmux
            // pane started from it inherits both and must still end up with the current settings.
            let child = #"\#(shellCommand(shell)) -c '. "$ENVY_DIR/init.sh"; printf "%s,%s\n" "${FOO-unset}" "${PRE-unset}"'"#
            let parentApplied = #"printf '%s,%s\n' "${FOO-unset}" "${PRE-unset}""# + "\n"
            let script = parentApplied + swapIn(next) + child + "\n"
            try write(first)
            XCTAssertEqual(try run(shell, script, env: ["PRE": "orig"]), "1,override\nunset,orig\n", shell)
        }
    }

    func testHookKeepsTheLastExitStatus() throws {
        try write(EnvConfig())
        for shell in shells {
            XCTAssertEqual(try run(shell, "false; __envy_hook; echo \"rc=$?\""), "rc=1\n", shell)
        }
    }

    func testStatusAndResourcingAreSafe() throws {
        try write(EnvConfig(
            global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "FOO", value: "1")]),
            projects: [Project(name: "It's mine", variables: [EnvVar(key: "BAR", value: "2")])]
        ))
        let script = """
        . "$ENVY_DIR/init.sh"
        envy status
        printf '%s\\n' "${PROMPT_COMMAND-}"
        """
        XCTAssertEqual(try run("/bin/zsh", script), "Envy: Global, It's mine\nVariables: FOO BAR\n\n")
        XCTAssertEqual(try run("/bin/bash", script), "Envy: Global, It's mine\nVariables: FOO BAR\n__envy_hook\n")
    }

    func testBrokenExpansionIsRejectedAndNamed() throws {
        try write(EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "OK", value: "1")])))
        let before = try String(contentsOf: shellDir.appendingPathComponent("state"), encoding: .utf8)
        let broken = EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [
            EnvVar(key: "BAD", value: "$(echo unclosed", expands: true),
        ]))
        XCTAssertThrowsError(try write(broken)) { error in
            XCTAssertTrue(error.localizedDescription.contains("BAD"), error.localizedDescription)
        }
        XCTAssertEqual(try String(contentsOf: shellDir.appendingPathComponent("state"), encoding: .utf8), before)
    }

    func testUnchangedVariablesDontTriggerReload() throws {
        let config = EnvConfig(global: Project(id: Project.globalID, name: "Global", variables: [EnvVar(key: "A", value: "1")]))
        XCTAssertTrue(try ShellOutput(directory: shellDir).write(config))
        XCTAssertFalse(try ShellOutput(directory: shellDir).write(config))
        let attributes = try FileManager.default.attributesOfItem(atPath: shellDir.appendingPathComponent("vars.sh").path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
    }
}
