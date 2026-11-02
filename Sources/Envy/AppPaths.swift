import Foundation

/// Where Envy keeps its files. Setting `ENVY_HOME` points everything (settings, secrets, shell files and
/// startup files) at another folder, to try the app without touching your real setup or your Keychain.
struct AppPaths {
    let home: URL
    let isSandbox: Bool

    init(environment: [String: String] = ProcessInfo.processInfo.environment) {
        if let custom = environment["ENVY_HOME"], !custom.isEmpty {
            home = URL(fileURLWithPath: (custom as NSString).expandingTildeInPath, isDirectory: true)
            isSandbox = true
        } else {
            home = FileManager.default.homeDirectoryForCurrentUser
            isSandbox = false
        }
    }

    var vault: SecretVault {
        SecretVault(service: "Envy", file: isSandbox ? home.appendingPathComponent("Library/Application Support/Envy/secrets.json") : nil)
    }

    var configFile: URL { home.appendingPathComponent("Library/Application Support/Envy/config.json") }
    var shellDirectory: URL { home.appendingPathComponent(".config/envy") }
    /// Exists once the Setup Assistant has been finished or postponed.
    var onboardedMarker: URL { configFile.deletingLastPathComponent().appendingPathComponent(".onboarded") }

    /// Common places to keep code, offered as one-click roots when looking for projects.
    var suggestedProjectRoots: [URL] {
        ["Developer", "Projects", "projects", "code", "Code", "src", "dev", "repos", "git", "GitHub", "workspace", "Sites"]
            .map { home.appendingPathComponent($0) }
            .filter { var isDir: ObjCBool = false; return FileManager.default.fileExists(atPath: $0.path, isDirectory: &isDir) && isDir.boolValue }
    }

    /// `path` with this home shown as `~`.
    func abbreviate(_ path: String) -> String {
        if path == home.path { return "~" }
        if path.hasPrefix(home.path + "/") { return "~" + path.dropFirst(home.path.count) }
        return path
    }
}

enum RcTarget: String, CaseIterable, Identifiable {
    case zshrc = ".zshrc"
    case bashrc = ".bashrc"
    case bashProfile = ".bash_profile"

    var id: String { rawValue }

    var detail: String {
        switch self {
        case .zshrc: "zsh, the default shell on macOS"
        case .bashrc: "bash, interactive shells"
        case .bashProfile: "bash, login shells (how Terminal.app starts bash)"
        }
    }
}
