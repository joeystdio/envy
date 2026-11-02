import Foundation

/// Reads and writes `.env` text: `KEY=value` lines, optional `export`, quotes, comments and multi-line quoted values.
public enum DotEnv {
    public enum Quote: Sendable { case none, single, double }

    public struct Entry: Hashable, Sendable {
        public var key: String
        public var value: String
        public var quote: Quote
        public var line: Int
    }

    public static func parse(_ text: String) -> [Entry] {
        let s = Array(text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n"))
        var entries: [Entry] = []
        var i = 0
        var line = 1

        func isBlank(_ c: Character) -> Bool { c == " " || c == "\t" }
        func skipBlanks() { while i < s.count, isBlank(s[i]) { i += 1 } }
        func skipToLineEnd() { while i < s.count, s[i] != "\n" { i += 1 } }

        while i < s.count {
            skipBlanks()
            guard i < s.count else { break }
            if s[i] == "\n" { i += 1; line += 1; continue }
            if s[i] == "#" { skipToLineEnd(); continue }

            let startLine = line
            var keyChars: [Character] = []
            while i < s.count, s[i] != "=", s[i] != "\n" { keyChars.append(s[i]); i += 1 }
            // A line without "=" isn't an assignment; the newline is consumed by the loop.
            guard i < s.count, s[i] == "=" else { continue }
            i += 1

            var key = String(keyChars).trimmingCharacters(in: .whitespaces)
            if key.hasPrefix("export "), key.count > 7 || key.hasPrefix("export\t") {
                key = String(key.dropFirst(7)).trimmingCharacters(in: .whitespaces)
            }

            let valueStart = i
            skipBlanks()
            let skippedBlank = i > valueStart

            var value = ""
            var quote = Quote.none
            if i < s.count, s[i] == "\"" || s[i] == "'" || s[i] == "`" {
                let q = s[i]
                quote = q == "\"" ? .double : .single
                i += 1
                while i < s.count {
                    let c = s[i]
                    if c == q { i += 1; break }
                    if c == "\n" { line += 1 }
                    if q == "\"", c == "\\", i + 1 < s.count {
                        let next = s[i + 1]
                        switch next {
                        case "n": value.append("\n")
                        case "t": value.append("\t")
                        case "r": value.append("\r")
                        case "\"", "\\", "$": value.append(next)
                        default: value.append(c); value.append(next)
                        }
                        if next == "\n" { line += 1 }
                        i += 2
                        continue
                    }
                    value.append(c)
                    i += 1
                }
                skipToLineEnd() // anything after the closing quote, e.g. a comment
            } else {
                var raw: [Character] = []
                while i < s.count, s[i] != "\n" {
                    // " #" starts a comment; so does "#" right after "=" when there was whitespace before it.
                    if s[i] == "#", raw.last.map(isBlank) ?? skippedBlank { break }
                    raw.append(s[i])
                    i += 1
                }
                skipToLineEnd()
                value = String(raw).trimmingCharacters(in: .whitespaces)
            }

            if !key.isEmpty {
                entries.append(Entry(key: key, value: value, quote: quote, line: startLine))
            }
        }
        return entries
    }

    /// `.env` text for `vars`, quoting only where needed so the file reads back identically.
    public static func serialize(_ vars: [EnvVar], header: String? = nil) -> String {
        var out = ""
        if let header {
            out += header.split(separator: "\n", omittingEmptySubsequences: false).map { "# \($0)" }.joined(separator: "\n") + "\n"
        }
        for v in vars {
            out += "\(v.key)=\(quoted(v.value, expands: v.expands))\n"
        }
        return out
    }

    static func quoted(_ value: String, expands: Bool) -> String {
        let plain = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-./:@%+,")
        if !value.isEmpty, value.unicodeScalars.allSatisfy(plain.contains) { return value }
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n")
        if expands { return "\"\(escaped)\"" }
        if !value.contains("'"), !value.contains("\n") { return "'\(value)'" }
        return "\"\(escaped.replacingOccurrences(of: "$", with: "\\$"))\""
    }
}
