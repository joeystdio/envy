import Foundation

/// A folder with environment or .NET configuration files, ready to become a folder-linked project.
public struct FoundProject: Identifiable, Hashable, Sendable {
    public var folder: URL
    /// Where the variables came from, in the order they were read (later ones win).
    public var files: [String]
    public var variables: [EnvVar]

    public var id: String { folder.path }
    public var name: String { folder.lastPathComponent }
}

/// Walks a folder tree looking for projects with `.env`-style files or .NET `appsettings.json` and user secrets.
public enum ProjectScanner {
    /// Read in this order, so `.env.local` overrides `.env`. Templates such as `.env.example` are ignored.
    public static let envFileNames = [".env", ".env.local", ".envrc"]

    /// Dependency, build and system folders that never hold a project of their own and can be huge.
    /// `bin` and `obj` also hold stale copies of appsettings.json.
    static let skippedDirectories: Set<String> = [
        "node_modules", "vendor", "Pods", "Carthage", "DerivedData", "build", "dist", "target", "out",
        "bin", "obj", "packages", "TestResults", "venv", "__pycache__", "Library", "Applications",
    ]

    /// Breadth-first, at most `maxDepth` folders below `root`, never following symlinks or entering hidden folders
    /// or packages. `home` is where .NET user secrets live. Results are sorted by path.
    public static func scan(
        _ root: URL, home: URL = FileManager.default.homeDirectoryForCurrentUser, maxDepth: Int = 4,
        isCancelled: () -> Bool = { false }
    ) -> [FoundProject] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .isPackageKey]
        var found: [FoundProject] = []
        var queue: [(url: URL, depth: Int)] = [(root, 0)]
        var next = 0
        while next < queue.count, !isCancelled() {
            let (dir, depth) = queue[next]
            next += 1
            if let project = project(in: dir, home: home) { found.append(project) }
            guard depth < maxDepth,
                  let children = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: keys)
            else { continue }
            for child in children {
                let name = child.lastPathComponent
                guard !name.hasPrefix("."), !skippedDirectories.contains(name),
                      let values = try? child.resourceValues(forKeys: Set(keys)),
                      values.isDirectory == true, values.isSymbolicLink != true, values.isPackage != true
                else { continue }
                queue.append((child, depth + 1))
            }
        }
        return found.sorted { $0.folder.path.localizedStandardCompare($1.folder.path) == .orderedAscending }
    }

    /// The project in `folder` itself, if it has any usable configuration.
    public static func project(in folder: URL, home: URL = FileManager.default.homeDirectoryForCurrentUser) -> FoundProject? {
        var files: [String] = []
        var variables: [EnvVar] = []

        func add(_ entries: [DotEnv.Entry], from source: String, expandShellValues: Bool = false, allSecret: Bool = false) {
            let usable = entries.filter { EnvVar.keyProblem($0.key) == nil }
            guard !usable.isEmpty else { return }
            files.append(source)
            for entry in usable {
                let variable = EnvVar(
                    key: entry.key, value: entry.value,
                    isSecret: allSecret || EnvVar.looksSecret(entry.key),
                    expands: expandShellValues && entry.quote != .single && entry.value.contains("$")
                )
                if let i = variables.firstIndex(where: { $0.key == variable.key }) {
                    variables[i] = variable
                } else {
                    variables.append(variable)
                }
            }
        }

        func read(_ name: String) -> String? {
            try? String(contentsOf: folder.appendingPathComponent(name), encoding: .utf8)
        }

        for name in envFileNames {
            // .envrc is a shell script, so its values expand; .env files are taken literally.
            if let text = read(name) { add(DotEnv.parse(text), from: name, expandShellValues: name == ".envrc") }
        }
        for name in AppSettings.fileNames {
            if let text = read(name), let entries = AppSettings.entries(fromJSON: text) { add(entries, from: name) }
        }
        if let secrets = AppSettings.userSecrets(for: folder, home: home) {
            add(secrets, from: "user secrets", allSecret: true)
        }
        return files.isEmpty ? nil : FoundProject(folder: folder, files: files, variables: variables)
    }
}
