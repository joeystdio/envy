import XCTest
@testable import EnvyCore

final class AppSettingsTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("envy-dotnet-\(UUID().uuidString)")
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

    func testFlattensLikeDotNetEnvironmentVariables() throws {
        let json = """
        {
          // .NET allows comments and trailing commas
          "Logging": { "LogLevel": { "Default": "Information" } },
          "AllowedHosts": "*",
          "ConnectionStrings": { "Default": "Server=db;Password=hunter2" },
          "Features": { "Beta": true, "MaxItems": 25, "Ratio": 0.5, "Missing": null },
          "Cors": { "Origins": ["http://localhost:3000", "https://app.example.com"] },
          "Weird.Name": "skipped later because it isn't a shell name",
        }
        """
        let entries = try XCTUnwrap(AppSettings.entries(fromJSON: json))
        XCTAssertEqual(entries.map(\.key), [
            "ConnectionStrings__Default", "Cors__Origins__0", "Cors__Origins__1",
            "Features__Beta", "Features__MaxItems", "Features__Ratio", "Weird.Name",
        ])
        XCTAssertEqual(entries.map(\.value)[3...5], ["true", "25", "0.5"])
        XCTAssertTrue(EnvVar.looksSecret("ConnectionStrings__Default"))
        XCTAssertNil(AppSettings.entries(fromJSON: "[1, 2]"))
        XCTAssertEqual(DotEnv.parseAny(json).count, 7)
        XCTAssertEqual(DotEnv.parseAny("A=1").map(\.key), ["A"])
    }

    func testScannerImportsDotNetProjectsWithUserSecrets() throws {
        let home = root.appendingPathComponent("home")
        try write(#"{ "Api": { "BaseUrl": "https://localhost:5001" }, "ConnectionStrings": { "Db": "Server=localhost" } }"#, to: "code/Shop.Api/appsettings.json")
        try write(#"{ "Api": { "BaseUrl": "https://localhost:7001" } }"#, to: "code/Shop.Api/appsettings.Development.json")
        try write("<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><UserSecretsId>shop-api-1234</UserSecretsId></PropertyGroup></Project>", to: "code/Shop.Api/Shop.Api.csproj")
        try write(#"{ "Stripe:ApiKey": "sk_test_x", "ConnectionStrings:Db": "Server=localhost;Password=pw" }"#, to: "home/.microsoft/usersecrets/shop-api-1234/secrets.json")
        try write(#"{ "Api": { "BaseUrl": "stale" } }"#, to: "code/Shop.Api/bin/Debug/net8.0/appsettings.json")
        try write(#"{ "Api": { "BaseUrl": "stale" } }"#, to: "code/Shop.Api/obj/appsettings.json")

        let found = ProjectScanner.scan(root.appendingPathComponent("code"), home: home)
        XCTAssertEqual(found.map(\.name), ["Shop.Api"])
        let project = found[0]
        XCTAssertEqual(project.files, ["appsettings.json", "appsettings.Development.json", "user secrets"])
        let byKey = Dictionary(uniqueKeysWithValues: project.variables.map { ($0.key, $0) })
        XCTAssertEqual(byKey["Api__BaseUrl"]?.value, "https://localhost:7001", "Development overrides the base file")
        XCTAssertEqual(byKey["ConnectionStrings__Db"]?.value, "Server=localhost;Password=pw", "user secrets win")
        XCTAssertEqual(byKey["Stripe__ApiKey"]?.isSecret, true)
        XCTAssertEqual(byKey["Api__BaseUrl"]?.isSecret, false)
    }

    func testUserSecretsIDCantEscapeTheSecretsFolder() throws {
        try write("<UserSecretsId>../../etc</UserSecretsId>", to: "evil/Evil.csproj")
        XCTAssertNil(AppSettings.userSecretsID(in: root.appendingPathComponent("evil")))
    }
}
