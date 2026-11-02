import XCTest
@testable import EnvyCore

final class DotEnvTests: XCTestCase {
    func testParsesCommonDotEnvSyntax() {
        let text = """
        # comment
        export A=1
        B = two words  # trailing comment
        C="quoted # not a comment"
        D='single $HOME'
        E="line1\\nline2"
        F="multi
        line"
        G=
        H=#kept
        I= # just a comment
        not an assignment
        J=a=b

        """
        let entries = DotEnv.parse(text)
        XCTAssertEqual(entries.map(\.key), ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J"])
        XCTAssertEqual(entries.map(\.value), [
            "1", "two words", "quoted # not a comment", "single $HOME", "line1\nline2", "multi\nline", "", "#kept", "", "a=b",
        ])
        XCTAssertEqual(entries[3].quote, .single)
        XCTAssertEqual(entries[9].line, 13)
    }

    func testHandlesWindowsLineEndings() {
        XCTAssertEqual(DotEnv.parse("A=1\r\nB=2\r\n").map(\.value), ["1", "2"])
    }

    func testSerializeRoundTrips() {
        let values = ["plain", "has space", "it's", "quote\"d", "multi\nline", "", "$NOT_EXPANDED", "back\\slash", "#hash"]
        let vars = values.enumerated().map { EnvVar(key: "K\($0.offset)", value: $0.element) }
        let parsed = DotEnv.parse(DotEnv.serialize(vars, header: "Exported\nfrom a test"))
        XCTAssertEqual(parsed.map(\.value), values)
    }

    func testSecretHeuristic() {
        for key in ["GITHUB_TOKEN", "OPENAI_API_KEY", "DB_PASSWORD", "client_secret", "AWS_SECRET_ACCESS_KEY", "SSH_PRIVATE", "SMTP_PASS"] {
            XCTAssertTrue(EnvVar.looksSecret(key), key)
        }
        for key in ["NODE_ENV", "PORT", "DATABASE_HOST", "KEYBOARD_LAYOUT", "PASSPORT_CITY"] {
            XCTAssertFalse(EnvVar.looksSecret(key), key)
        }
    }

    func testKeyValidation() {
        XCTAssertNil(EnvVar.keyProblem("API_URL"))
        XCTAssertNil(EnvVar.keyProblem("_private1"))
        XCTAssertNotNil(EnvVar.keyProblem(""))
        XCTAssertNotNil(EnvVar.keyProblem("1ABC"))
        XCTAssertNotNil(EnvVar.keyProblem("MY-VAR"))
        XCTAssertNotNil(EnvVar.keyProblem("PWD"))
        XCTAssertNotNil(EnvVar.keyProblem("__ENVY_VARS"))
    }

    func testMergeKeepsIDsOfReplacedVariablesAndGivesCopiesNewOnes() {
        let existing = EnvVar(key: "A", value: "old")
        var project = Project(name: "P", variables: [existing])
        let incoming = [EnvVar(key: "A", value: "new"), EnvVar(key: "B", value: "b")]
        let result = project.merge(incoming, policy: .replace)
        XCTAssertEqual(result.added, 1)
        XCTAssertEqual(result.replaced, 1)
        XCTAssertEqual(project.variables[0].id, existing.id)
        XCTAssertEqual(project.variables[0].value, "new")
        XCTAssertNotEqual(project.variables[1].id, incoming[1].id)

        let kept = project.merge([EnvVar(key: "A", value: "ignored")], policy: .keepExisting)
        XCTAssertEqual(kept.skipped, 1)
        XCTAssertEqual(project.variables[0].value, "new")
    }

    func testSecretsAreStrippedFromStoredConfigAndRefilled() {
        let secret = EnvVar(key: "TOKEN", value: "s3cret", isSecret: true)
        let config = EnvConfig(projects: [Project(name: "P", variables: [secret, EnvVar(key: "PLAIN", value: "p")])])
        let stored = config.withoutSecretValues()
        XCTAssertEqual(stored.projects[0].variables.map(\.value), ["", "p"])
        var restored = stored
        restored.fillSecrets(config.secretValues())
        XCTAssertEqual(restored, config)
    }
}
