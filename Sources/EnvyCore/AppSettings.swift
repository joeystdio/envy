import Foundation

/// .NET configuration as environment variables. Nested keys are joined with `__`, which is how .NET reads
/// configuration from the environment: `{"ConnectionStrings": {"Default": …}}` becomes `ConnectionStrings__Default`,
/// and an exported value overrides the one in the file.
public enum AppSettings {
    /// Read in this order; later files win, as in the Development environment.
    public static let fileNames = ["appsettings.json", "appsettings.Development.json"]

    /// Framework boilerplate that is rarely worth exporting.
    static let boilerplateSections: Set<String> = ["Logging", "AllowedHosts"]

    public static func looksLikeJSON(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("{")
    }

    /// Flattened entries in key order, or nil when `text` isn't a JSON object. Comments and trailing commas are
    /// accepted, as .NET accepts them. Strings come back single-quoted so they're never expanded.
    public static func entries(fromJSON text: String, skipBoilerplate: Bool = true) -> [DotEnv.Entry]? {
        guard let data = text.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data, options: [.json5Allowed]) as? [String: Any]
        else { return nil }
        var out: [DotEnv.Entry] = []
        for key in root.keys.sorted() where !(skipBoilerplate && boilerplateSections.contains(key)) {
            flatten(root[key]!, path: key, into: &out)
        }
        return out
    }

    static func flatten(_ value: Any, path: String, into out: inout [DotEnv.Entry]) {
        switch value {
        case let object as [String: Any]:
            for key in object.keys.sorted() { flatten(object[key]!, path: "\(path)__\(key)", into: &out) }
        case let array as [Any]:
            for (index, item) in array.enumerated() { flatten(item, path: "\(path)__\(index)", into: &out) }
        case let string as String:
            out.append(DotEnv.Entry(key: path, value: string, quote: .single, line: 0))
        case let number as NSNumber:
            let isBool = CFGetTypeID(number) == CFBooleanGetTypeID()
            out.append(DotEnv.Entry(key: path, value: isBool ? (number.boolValue ? "true" : "false") : number.stringValue, quote: .none, line: 0))
        default:
            break // null
        }
    }

    /// The project's `dotnet user-secrets`: the `UserSecretsId` from a .csproj in `folder`, read from
    /// `~/.microsoft/usersecrets/<id>/secrets.json`, with `Section:Key` names turned into `Section__Key`.
    public static func userSecrets(for folder: URL, home: URL) -> [DotEnv.Entry]? {
        guard let id = userSecretsID(in: folder) else { return nil }
        let file = home.appendingPathComponent(".microsoft/usersecrets/\(id)/secrets.json")
        guard let text = try? String(contentsOf: file, encoding: .utf8),
              let entries = entries(fromJSON: text, skipBoilerplate: false)
        else { return nil }
        return entries.map { entry in
            var entry = entry
            entry.key = entry.key.replacingOccurrences(of: ":", with: "__")
            return entry
        }
    }

    static func userSecretsID(in folder: URL) -> String? {
        let projects = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil))?
            .filter { $0.pathExtension == "csproj" } ?? []
        for project in projects {
            guard let text = try? String(contentsOf: project, encoding: .utf8),
                  let start = text.range(of: "<UserSecretsId>"),
                  let end = text.range(of: "</UserSecretsId>", range: start.upperBound..<text.endIndex)
            else { continue }
            let id = text[start.upperBound..<end.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
            // It becomes a path component, so only accept what dotnet itself generates.
            let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_"))
            if !id.isEmpty, id.unicodeScalars.allSatisfy(allowed.contains) { return id }
        }
        return nil
    }
}

extension DotEnv {
    /// `.env` text, or a JSON object such as appsettings.json.
    public static func parseAny(_ text: String) -> [Entry] {
        if AppSettings.looksLikeJSON(text), let entries = AppSettings.entries(fromJSON: text) { return entries }
        return parse(text)
    }
}
