import AppKit
import EnvyCore
import SwiftUI
import UniformTypeIdentifiers

/// Back Up Everything and Restore from Backup (spec/backup-format.md): the same file as Envy for Windows.

extension UTType {
    /// Declared in Info.plist, so save panels add `.envybackup` once; an undeclared type made them add it twice.
    static let envyBackup = UTType("dev.envy.backup") ?? UTType(filenameExtension: Backup.fileExtension) ?? .data
}

/// A backup file picked for restoring.
struct ChosenBackup: Identifiable {
    let url: URL
    let data: Data
    var id: String { url.path }
}

extension Panels {
    static func openBackup() -> ChosenBackup? {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.envyBackup]
        panel.prompt = "Restore"
        panel.message = "Choose an Envy backup, made on a Mac or a PC."
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        do {
            return ChosenBackup(url: url, data: try Data(contentsOf: url))
        } catch {
            let alert = NSAlert(error: error)
            alert.messageText = "Couldn't open the backup"
            alert.informativeText = error.localizedDescription
            alert.runModal()
            return nil
        }
    }
}

func plural(_ count: Int, _ one: String, _ many: String) -> String {
    "\(count) \(count == 1 ? one : many)"
}

// MARK: - Back up

/// A password typed twice, then a file. The password lives only in this sheet: never in settings or the Keychain.
struct BackUpSheet: View {
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var password = ""
    @State private var again = ""
    @State private var saving = false
    @State private var saved: URL?
    @State private var failure: String?
    @FocusState private var passwordFocused: Bool

    private var problem: String? { Backup.passwordProblem(password, confirmation: again) }

    private var summary: String {
        let config = store.config
        let variables = config.scopes.reduce(0) { $0 + $1.variables.count }
        let secrets = config.scopes.reduce(0) { $0 + $1.variables.filter(\.isSecret).count }
        return "Saves Global and \(plural(config.projects.count, "project", "projects")), with their folders and \(plural(variables, "variable", "variables")), \(secrets) of them secret, in one file encrypted with this password. Envy on another Mac or a PC can restore it."
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Back Up Everything").font(.headline)
            if let saved {
                Label("Saved \(saved.lastPathComponent)", systemImage: "checkmark.circle.fill")
                    .font(.headline)
                    .foregroundStyle(.green)
                Text("Keep the password somewhere safe, such as your password manager: without it, this backup can't be opened. To restore it, choose File > Restore from Backup in Envy on a Mac, or Restore from Backup in Envy for Windows.")
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text(summary).fixedSize(horizontal: false, vertical: true)
                Form {
                    SecureField("Password", text: $password).focused($passwordFocused)
                    SecureField("Type it again", text: $again)
                }
                .disabled(saving)
                // Only once there's something to compare, so the first field doesn't nag while you type.
                if !again.isEmpty, let problem {
                    Text(problem).font(.callout).foregroundStyle(.orange)
                }
                Text("At least \(Backup.minimumPasswordLength) characters. Envy doesn't keep this password: without it, the backup can't be opened.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let failure {
                    Label(failure, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack {
                if saving {
                    ProgressView().controlSize(.small)
                    Text("Encrypting…").foregroundStyle(.secondary)
                }
                Spacer()
                if saved != nil {
                    Button("Show in Finder") { if let saved { NSWorkspace.shared.activateFileViewerSelecting([saved]) } }
                    Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
                } else {
                    Button("Cancel", role: .cancel) { dismiss() }
                        .keyboardShortcut(.cancelAction)
                    Button("Save Backup…") { save() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(problem != nil || saving)
                }
            }
        }
        .padding(20)
        .frame(width: 480)
        .onAppear { passwordFocused = true }
    }

    private func save() {
        let content: BackupContent
        do {
            content = try store.backupContent()
        } catch {
            failure = error.localizedDescription
            return
        }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = Backup.suggestedFileName(Date())
        panel.allowedContentTypes = [.envyBackup]
        panel.canCreateDirectories = true
        panel.message = "The backup holds your secrets, encrypted with your password."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let secret = password
        saving = true
        failure = nil
        Task {
            do {
                // The key takes 600,000 rounds of PBKDF2: off the main thread.
                let data = try await Task.detached { try Backup.write(content, password: secret) }.value
                try data.write(to: url, options: .atomic)
                try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
                password = ""
                again = ""
                saved = url
            } catch {
                failure = "The backup wasn't saved: \(error.localizedDescription)"
            }
            saving = false
        }
    }
}

// MARK: - Restore

/// The password, then what restoring does (Add or Replace) and what to do with expanding values from the other system.
struct RestoreBackupSheet: View {
    let backup: ChosenBackup
    /// Called after a restore, once the sheet is closing.
    var onRestored: () -> Void = {}
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var password = ""
    @State private var opening = false
    @State private var failure: String?
    @State private var content: BackupContent?
    @State private var issues: [ExpandIssue] = []
    @State private var mode = RestoreMode.add
    @State private var actions: [String: ExpandAction] = [:]
    @State private var edited: [String: String] = [:]
    @FocusState private var passwordFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Restore from Backup").font(.headline).padding([.horizontal, .top], 20)
            if let content {
                choices(content)
            } else {
                passwordStep
            }
        }
    }

    // MARK: Password

    private var passwordStep: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Enter the password “\(backup.url.lastPathComponent)” was saved with.")
                .fixedSize(horizontal: false, vertical: true)
            SecureField("Password", text: $password)
                .focused($passwordFocused)
                .disabled(opening)
                .onSubmit(open)
            if let failure {
                Label(failure, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                if opening {
                    ProgressView().controlSize(.small)
                    Text("Opening…").foregroundStyle(.secondary)
                }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Open") { open() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(password.isEmpty || opening)
            }
        }
        .padding(20)
        .frame(width: 480)
        .onAppear { passwordFocused = true }
    }

    private func open() {
        guard !password.isEmpty, !opening else { return }
        let data = backup.data
        let secret = password
        opening = true
        failure = nil
        Task {
            do {
                let opened = try await Task.detached { try Backup.read(data, password: secret) }.value
                issues = BackupRestore.expandIssues(opened, here: Backup.macOS)
                actions = Dictionary(uniqueKeysWithValues: issues.map { ($0.id, $0.defaultAction) })
                edited = Dictionary(uniqueKeysWithValues: issues.map { ($0.id, $0.suggestion ?? $0.value) })
                password = ""
                content = opened
            } catch {
                failure = error.localizedDescription
                passwordFocused = true
            }
            opening = false
        }
    }

    // MARK: Choices

    private var fixes: [ExpandFix] {
        issues.map { issue in
            let action = actions[issue.id] ?? issue.defaultAction
            return ExpandFix(scope: issue.scope, index: issue.index, action: action, value: action == .useSuggestion ? edited[issue.id] : nil)
        }
    }

    private func origin(_ content: BackupContent) -> String {
        switch content.platform {
        case Backup.windows: "Windows"
        case Backup.macOS: "a Mac"
        default: "another system"
        }
    }

    private func choices(_ content: BackupContent) -> some View {
        let report = store.previewRestore(content, mode: mode, fixes: fixes)
        let variables = content.scopes.reduce(0) { $0 + $1.variables.count }
        return VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    Text("Made on \(origin(content))\(madeOn(content))\(content.app.isEmpty ? "" : " by \(content.app)"): Global and \(plural(content.projects.count, "project", "projects")), \(plural(variables, "variable", "variables")).")
                        .fixedSize(horizontal: false, vertical: true)
                    // Plain text labels: with two-line labels the radio buttons couldn't be pressed through accessibility.
                    VStack(alignment: .leading, spacing: 6) {
                        Picker("Restore by", selection: $mode) {
                            Text("Add to what's here").tag(RestoreMode.add)
                            Text("Replace everything").tag(RestoreMode.replace)
                        }
                        .pickerStyle(.radioGroup)
                        .horizontalRadioGroupLayout()
                        Text(mode == .add
                             ? "Nothing here is deleted. Same-named projects merge, and the backup's values win."
                             : "Global and every project here are replaced by the backup's.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    GroupBox {
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(Array(describe(report).enumerated()), id: \.offset) { _, line in
                                Text(line).fixedSize(horizontal: false, vertical: true)
                            }
                        }
                        .padding(6)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    if !issues.isEmpty { expandRows(content) }
                }
                .padding(20)
            }
            // Fits the main window at its smallest; longer lists scroll.
            .frame(height: 380)
            Divider()
            HStack {
                if let failure {
                    Label(failure, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .lineLimit(2)
                }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(mode == .replace ? "Replace Everything" : "Restore") { restore(content) }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(20)
        }
        .frame(width: 640)
    }

    private func expandRows(_ content: BackupContent) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("These values expand other variables and were made for \(origin(content)). Choose what to restore for each:")
                .font(.headline)
                .fixedSize(horizontal: false, vertical: true)
            ForEach(issues) { issue in
                let action = actions[issue.id] ?? issue.defaultAction
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Text(issue.key).font(.body.monospaced().weight(.semibold))
                        Text(issue.scopeName).foregroundStyle(.secondary)
                    }
                    Text(issue.value).font(.callout.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                    Text(issue.reason).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Picker("", selection: Binding(get: { action }, set: { actions[issue.id] = $0 })) {
                            ForEach(issue.actions, id: \.self) { Text(title($0)).tag($0) }
                        }
                        .labelsHidden()
                        .fixedSize()
                        TextField("Value", text: Binding(
                            get: { action == .useSuggestion ? edited[issue.id] ?? "" : issue.value },
                            set: { edited[issue.id] = $0 }
                        ))
                        .font(.body.monospaced())
                        .disabled(action != .useSuggestion)
                    }
                    if action == .turnOff {
                        Text("Restored as plain text: Envy won't expand it.").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
            }
        }
    }

    private func title(_ action: ExpandAction) -> String {
        switch action {
        case .useSuggestion: "Use suggestion"
        case .keepAsIs: "Keep as is"
        case .turnOff: "Turn Expand off"
        }
    }

    private func madeOn(_ content: BackupContent) -> String {
        guard let date = ISO8601DateFormatter().date(from: content.created) else { return "" }
        return " on \(date.formatted(date: .long, time: .omitted))"
    }

    /// The summary lines shown before restoring.
    private func describe(_ report: RestoreReport) -> [String] {
        var lines: [String] = []
        let list = { (names: [String]) in ListFormatter.localizedString(byJoining: names) }
        if mode == .replace {
            lines.append(report.projectsRemoved.isEmpty
                ? "Global is replaced."
                : "Removes \(plural(report.projectsRemoved.count, "project", "projects")) here: \(list(report.projectsRemoved)). Global is replaced too.")
        }
        if !report.projectsAdded.isEmpty {
            lines.append("Adds \(plural(report.projectsAdded.count, "project", "projects")): \(list(report.projectsAdded)).")
        }
        if mode == .add {
            lines.append("Merges into \(list(["Global"] + report.projectsMerged)); the backup's values win.")
        }
        let secrets = report.secrets == 0 ? "" : ", \(report.secrets) of them secret, kept in your Keychain"
        lines.append(mode == .add
            ? "\(plural(report.variablesAdded, "variable", "variables")) added and \(report.variablesReplaced) replaced\(secrets). ⌘Z undoes it."
            : "\(plural(report.variablesAdded, "variable", "variables"))\(secrets). ⌘Z undoes it.")
        if !report.missingFolders.isEmpty {
            lines.append("These folders don't exist on this Mac. They're restored anyway; link the project to the right folder later:\n"
                + report.missingFolders.map { "   \($0.project): \(store.paths.abbreviate($0.folder))" }.joined(separator: "\n"))
        }
        if !report.skippedFolders.isEmpty {
            lines.append("Not linked, because another project here already has the folder:\n"
                + report.skippedFolders.map { "   \($0.project): \(store.paths.abbreviate($0.folder)) (belongs to \($0.owner ?? ""))" }.joined(separator: "\n"))
        }
        if !report.turnedOff.isEmpty {
            lines.append("Restored switched off, since all their folders belong to other projects: \(list(report.turnedOff)).")
        }
        return lines
    }

    private func restore(_ content: BackupContent) {
        do {
            try store.restoreBackup(content, mode: mode, fixes: fixes)
        } catch {
            failure = error.localizedDescription
            return
        }
        dismiss()
        onRestored()
    }
}
