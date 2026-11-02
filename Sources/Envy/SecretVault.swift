import Foundation
import Security

/// Every secret value in one Keychain item (a JSON map of variable ID to value), so macOS asks for access at most once.
/// With a `file`, secrets go there instead; `ENVY_HOME` sandboxes use that to keep test data out of your Keychain.
struct SecretVault {
    let service: String
    var file: URL?
    private let account = "secrets"

    struct Failure: LocalizedError {
        let status: OSStatus
        var errorDescription: String? {
            (SecCopyErrorMessageString(status, nil) as String?) ?? "Keychain error \(status)"
        }
    }

    func load() throws -> [String: String] {
        if let file {
            guard FileManager.default.fileExists(atPath: file.path) else { return [:] }
            return try JSONDecoder().decode([String: String].self, from: Data(contentsOf: file))
        }
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return [:] }
        guard status == errSecSuccess, let data = result as? Data else { throw Failure(status: status) }
        return try JSONDecoder().decode([String: String].self, from: data)
    }

    func save(_ secrets: [String: String]) throws {
        let data = try JSONEncoder().encode(secrets)
        if let file {
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
            return
        }
        let status = SecItemUpdate(baseQuery as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var item = baseQuery
            item[kSecValueData as String] = data
            item[kSecAttrLabel as String] = service
            item[kSecAttrDescription as String] = "Envy secrets"
            let added = SecItemAdd(item as CFDictionary, nil)
            guard added == errSecSuccess else { throw Failure(status: added) }
        } else if status != errSecSuccess {
            throw Failure(status: status)
        }
    }

    private var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }
}
