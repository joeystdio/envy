import Foundation

/// Adds or removes the marked block that sources Envy's hook from a shell startup file.
public enum RcIntegration {
    public static let beginMarker = "# >>> Envy >>>"
    public static let endMarker = "# <<< Envy <<<"
    public static let sourceLine = #"[ -r "$HOME/.config/envy/init.sh" ] && . "$HOME/.config/envy/init.sh""#

    public static let snippet = """
    \(beginMarker)
    # Loads the environment variables managed by Envy.app. Remove it from Envy › Shell Integration.
    \(sourceLine)
    \(endMarker)
    """

    public static func isInstalled(in url: URL) -> Bool {
        guard let text = try? String(contentsOf: url.resolvingSymlinksInPath(), encoding: .utf8) else { return false }
        return lines(of: text).contains { $0.trimmingCharacters(in: .whitespaces) == beginMarker }
    }

    /// Appends the block, creating the file if needed. Writes through symlinks so dotfile managers keep working,
    /// and leaves a copy of the previous file at `<name>.envy-backup`.
    public static func install(in url: URL) throws {
        guard !isInstalled(in: url) else { return }
        let target = url.resolvingSymlinksInPath()
        let existing = (try? String(contentsOf: target, encoding: .utf8)) ?? ""
        var addition = ""
        if !existing.isEmpty {
            try backup(target)
            addition += existing.hasSuffix("\n") ? "\n" : "\n\n"
        }
        addition += snippet + "\n"

        if FileManager.default.fileExists(atPath: target.path) {
            let handle = try FileHandle(forWritingTo: target)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(addition.utf8))
        } else {
            try addition.write(to: target, atomically: true, encoding: .utf8)
        }
    }

    public static func uninstall(from url: URL) throws {
        let target = url.resolvingSymlinksInPath()
        guard let text = try? String(contentsOf: target, encoding: .utf8) else { return }
        var all = lines(of: text)
        guard let start = all.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces) == beginMarker }) else { return }
        let end = all[start...].firstIndex { $0.trimmingCharacters(in: .whitespaces) == endMarker } ?? start
        try backup(target)
        // Also drop the blank line install put in front of the block.
        let from = start > 0 && all[start - 1].isEmpty ? start - 1 : start
        all.removeSubrange(from...end)
        // Writing in place keeps the file's permissions and inode.
        try all.joined(separator: "\n").write(to: target, atomically: false, encoding: .utf8)
    }

    static func backup(_ url: URL) throws {
        let copy = url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + ".envy-backup")
        try? FileManager.default.removeItem(at: copy)
        try FileManager.default.copyItem(at: url, to: copy)
    }

    static func lines(of text: String) -> [String] {
        text.components(separatedBy: "\n")
    }
}
