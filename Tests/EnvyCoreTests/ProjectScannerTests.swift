import XCTest
@testable import EnvyCore

final class ProjectScannerTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("envy-scan-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func write(_ text: String, to path: String) throws {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try text.write(to: url, atomically: true, encoding: .utf8)
    }

    func testFindsProjectsAndSkipsNoise() throws {
        try write("API_URL=http://localhost\nSTRIPE_SECRET_KEY=sk_test_1\n", to: "shop/.env")
        try write("API_URL=http://127.0.0.1\n", to: "shop/.env.local")
        try write("export DATA=\"$PWD/data\"\nuse nix\nPATH_add bin\n", to: "lab/.envrc")
        try write("PORT=3000\n", to: "work/monorepo/apps/web/.env")
        try write("X=1\n", to: "shop/node_modules/dep/.env")
        try write("X=1\n", to: "a/b/c/d/e/too-deep/.env")
        try write("X=1\n", to: ".hidden/app/.env")
        try write("X=placeholder\n", to: "templated/.env.example")
        try write("# nothing here\n", to: "empty/.env")

        let found = ProjectScanner.scan(root)
        XCTAssertEqual(found.map(\.name), ["lab", "shop", "web"])

        let shop = try XCTUnwrap(found.first { $0.name == "shop" })
        XCTAssertEqual(shop.files, [".env", ".env.local"])
        XCTAssertEqual(shop.variables.map(\.key), ["API_URL", "STRIPE_SECRET_KEY"])
        XCTAssertEqual(shop.variables[0].value, "http://127.0.0.1", ".env.local wins")
        XCTAssertTrue(shop.variables[1].isSecret)

        let lab = try XCTUnwrap(found.first { $0.name == "lab" })
        XCTAssertEqual(lab.variables.map(\.key), ["DATA"])
        XCTAssertTrue(lab.variables[0].expands, ".envrc values are shell values")
    }

    func testStopsWhenCancelled() throws {
        try write("A=1\n", to: "one/.env")
        XCTAssertTrue(ProjectScanner.scan(root, isCancelled: { true }).isEmpty)
    }
}
