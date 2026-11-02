import EnvyCore
import SwiftUI

struct VariableEditorSheet: View {
    let projectID: UUID
    /// nil adds a new variable.
    let variableID: UUID?
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var draft = EnvVar(key: "")
    @State private var secretTouched = false
    @State private var reveal = false
    @FocusState private var nameFocused: Bool

    private var isNew: Bool { variableID == nil }
    private var trimmedKey: String { draft.key.trimmingCharacters(in: .whitespaces) }
    private var keyProblem: String? { trimmedKey.isEmpty ? nil : EnvVar.keyProblem(trimmedKey) }
    private var canSave: Bool { !trimmedKey.isEmpty && keyProblem == nil }
    private var clashes: Bool {
        store.project(projectID)?.variables.contains { $0.key == trimmedKey && $0.id != variableID } ?? false
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(isNew ? "New Variable in \(store.project(projectID)?.name ?? "")" : "Edit Variable")
                .font(.headline)
                .padding([.horizontal, .top], 20)
            Form {
                Section {
                    TextField("Name", text: $draft.key, prompt: Text("OPENAI_API_KEY"))
                        .font(.body.monospaced())
                        .focused($nameFocused)
                        .onChange(of: draft.key) { _, key in
                            if !secretTouched { draft.isSecret = EnvVar.looksSecret(key) }
                        }
                    if let keyProblem {
                        Text(keyProblem).font(.caption).foregroundStyle(.red)
                    } else if clashes {
                        Text(isNew ? "\(trimmedKey) already exists here; adding replaces its value." : "Another \(trimmedKey) exists here; the last one wins.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                    HStack(alignment: .firstTextBaseline) {
                        if draft.isSecret && !reveal {
                            SecureField("Value", text: $draft.value)
                        } else {
                            TextField("Value", text: $draft.value, axis: .vertical)
                                .lineLimit(1...8)
                                .font(.body.monospaced())
                        }
                        if draft.isSecret {
                            Button { reveal.toggle() } label: { Image(systemName: reveal ? "eye.slash" : "eye") }
                                .buttonStyle(.borderless)
                                .help(reveal ? "Hide value" : "Show value")
                        }
                    }
                }
                Section {
                    Toggle(isOn: Binding(get: { draft.isSecret }, set: { draft.isSecret = $0; secretTouched = true })) {
                        Text("Secret")
                        Text("Kept in your Keychain instead of the settings file, and hidden in Envy.")
                    }
                    Toggle(isOn: $draft.expands) {
                        Text("Expand variables")
                        Text("Evaluate like a double-quoted shell string: $HOME, ${OTHER}, ~ and $(command) are expanded.")
                    }
                    TextField("Note", text: $draft.note, prompt: Text("Optional"))
                }
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                if isNew {
                    Button("Add & New") {
                        save()
                        draft = EnvVar(key: "")
                        secretTouched = false
                        nameFocused = true
                    }
                    .disabled(!canSave)
                }
                Button(isNew ? "Add" : "Save") {
                    save()
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!canSave)
            }
            .padding(20)
        }
        .frame(width: 540)
        .onAppear {
            if let variableID, let existing = store.variable(projectID, variableID) {
                secretTouched = true
                draft = existing
            } else {
                nameFocused = true
            }
        }
    }

    private func save() {
        var variable = draft
        variable.key = trimmedKey
        if isNew {
            store.addVariables([variable], to: projectID, policy: .replace, actionName: "Add Variable")
        } else {
            store.replaceVariable(variable, in: projectID)
        }
    }
}

struct ImportSheet: View {
    let projectID: UUID
    @State private var text: String
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var policy: MergePolicy = .replace
    @State private var detectSecrets = true
    @State private var expandVariables = false

    init(projectID: UUID, text: String) {
        self.projectID = projectID
        _text = State(initialValue: text)
    }

    private struct Row: Identifiable {
        let id: Int
        let variable: EnvVar
        let result: String
        let importable: Bool
    }

    private var rows: [Row] {
        let existing = Set(store.project(projectID)?.variables.map(\.key) ?? [])
        return DotEnv.parseAny(text).enumerated().map { index, entry in
            let v = EnvVar(
                key: entry.key, value: entry.value,
                isSecret: detectSecrets && EnvVar.looksSecret(entry.key),
                expands: expandVariables && entry.quote != .single && entry.value.contains("$")
            )
            if let problem = v.keyProblem { return Row(id: index, variable: v, result: "Skipped: \(problem)", importable: false) }
            if existing.contains(v.key) {
                return policy == .replace
                    ? Row(id: index, variable: v, result: "Replaces current value", importable: true)
                    : Row(id: index, variable: v, result: "Skipped: already exists", importable: false)
            }
            return Row(id: index, variable: v, result: "New", importable: true)
        }
    }

    var body: some View {
        let rows = rows
        let importable = rows.filter(\.importable).map(\.variable)
        VStack(alignment: .leading, spacing: 12) {
            Text("Import into \(store.project(projectID)?.name ?? "")").font(.headline)
            HStack {
                Text("Paste the contents of a .env or appsettings.json file, or open one.").foregroundStyle(.secondary)
                Spacer()
                Button("Open File…") { if let opened = Panels.openDotEnv() { text = opened } }
            }
            TextEditor(text: $text)
                .font(.body.monospaced())
                .frame(height: 130)
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color(nsColor: .separatorColor)))
            if rows.isEmpty {
                Text(text.isEmpty ? "Nothing to import yet." : "No KEY=value lines or JSON settings found.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 160)
            } else {
                Table(rows) {
                    TableColumn("Name") { r in
                        Text(r.variable.key).font(.body.monospaced()).foregroundStyle(r.importable ? Color.primary : Color.secondary)
                    }
                    TableColumn("Value") { r in
                        Text(r.variable.isSecret ? "••••••" : r.variable.value).lineLimit(1).foregroundStyle(.secondary)
                    }
                    TableColumn("") { r in
                        HStack(spacing: 4) {
                            if r.variable.isSecret { Image(systemName: "lock.fill") }
                            if r.variable.expands { Image(systemName: "dollarsign") }
                        }
                        .foregroundStyle(.secondary)
                    }
                    .width(36)
                    TableColumn("Result") { r in
                        Text(r.result).foregroundStyle(r.importable ? Color.primary : Color.secondary)
                    }
                }
                .frame(height: 160)
            }
            Picker("If a name already exists:", selection: $policy) {
                Text("Replace its value").tag(MergePolicy.replace)
                Text("Keep the current value").tag(MergePolicy.keepExisting)
            }
            .pickerStyle(.radioGroup)
            .horizontalRadioGroupLayout()
            Toggle("Treat names like *_TOKEN, *_SECRET, *_KEY and *PASSWORD* as secrets", isOn: $detectSecrets)
            Toggle("Expand $VARIABLES in values that aren't single-quoted", isOn: $expandVariables)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(importable.count == 1 ? "Import 1 Variable" : "Import \(importable.count) Variables") {
                    store.addVariables(importable, to: projectID, policy: .replace, actionName: "Import Variables")
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(importable.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 660)
    }
}

struct CopyFromProjectSheet: View {
    let targetID: UUID
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var sourceID: UUID?
    @State private var chosen = Set<UUID>()
    @State private var policy: MergePolicy = .replace

    private var sources: [Project] { store.config.scopes.filter { $0.id != targetID && !$0.variables.isEmpty } }
    private var source: Project? { sourceID.flatMap { store.project($0) } }
    private var targetKeys: Set<String> { Set(store.project(targetID)?.variables.map(\.key) ?? []) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Copy Variables into \(store.project(targetID)?.name ?? "")").font(.headline)
            if sources.isEmpty {
                Text("No other project has variables yet.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                Picker("From:", selection: $sourceID) {
                    ForEach(sources) { p in
                        Text("\(p.name) (\(p.variables.count))").tag(Optional(p.id))
                    }
                }
                if let source {
                    List {
                        ForEach(source.variables) { v in
                            Toggle(isOn: chosenBinding(v.id)) {
                                HStack {
                                    Text(v.key).font(.body.monospaced())
                                    if targetKeys.contains(v.key) {
                                        Text("exists").font(.caption).foregroundStyle(.orange)
                                    }
                                    Spacer()
                                    Text(v.isSecret ? "••••••" : v.value).lineLimit(1).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                    .frame(height: 240)
                    HStack {
                        Button("Select All") { chosen = Set(source.variables.map(\.id)) }
                        Button("Select None") { chosen = [] }
                    }
                    .buttonStyle(.link)
                }
                Picker("If a name already exists:", selection: $policy) {
                    Text("Replace its value").tag(MergePolicy.replace)
                    Text("Keep the current value").tag(MergePolicy.keepExisting)
                }
                .pickerStyle(.radioGroup)
                .horizontalRadioGroupLayout()
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(chosen.count == 1 ? "Copy 1 Variable" : "Copy \(chosen.count) Variables") {
                    if let source {
                        store.addVariables(source.variables.filter { chosen.contains($0.id) }, to: targetID, policy: policy, actionName: "Copy Variables")
                    }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(chosen.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 580)
        .onAppear {
            if sourceID == nil { sourceID = sources.first?.id }
        }
        .onChange(of: sourceID, initial: true) {
            chosen = Set(source?.variables.map(\.id) ?? [])
        }
    }

    private func chosenBinding(_ id: UUID) -> Binding<Bool> {
        Binding(
            get: { chosen.contains(id) },
            set: { if $0 { chosen.insert(id) } else { chosen.remove(id) } }
        )
    }
}

struct NewProjectSheet: View {
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var folder: String?
    @State private var copyFrom: UUID?
    @State private var found: FoundProject?
    @State private var importFound = true

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("New Project").font(.headline).padding([.horizontal, .top], 20)
            Form {
                TextField("Name", text: $name, prompt: Text("my-app"))
                LabeledContent("Folder") {
                    HStack {
                        if let folder {
                            Text((folder as NSString).abbreviatingWithTildeInPath)
                                .font(.callout.monospaced())
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Button { self.folder = nil; found = nil } label: { Image(systemName: "xmark.circle.fill") }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.secondary)
                        } else {
                            Text("None: loads in every terminal").foregroundStyle(.secondary)
                        }
                        Button("Choose…") { chooseFolder() }
                    }
                }
                if let found {
                    Toggle("Import its \(found.variables.count) variables (\(found.files.joined(separator: " + ")))", isOn: $importFound)
                }
                Picker("Start with", selection: $copyFrom) {
                    Text("No variables").tag(UUID?.none)
                    ForEach(store.config.scopes.filter { !$0.variables.isEmpty }) { p in
                        Text("A copy of \(p.name) (\(p.variables.count))").tag(Optional(p.id))
                    }
                }
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Create Project") { create() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(20)
        }
        .frame(width: 540)
    }

    private func chooseFolder() {
        let message = "The project's variables will load only while a terminal is inside this folder."
        guard let path = Panels.chooseFolder(prompt: "Choose", message: message) else { return }
        folder = path
        if name.trimmingCharacters(in: .whitespaces).isEmpty { name = (path as NSString).lastPathComponent }
        found = ProjectScanner.project(in: URL(fileURLWithPath: path), home: store.paths.home)
    }

    private func create() {
        let extra = importFound ? found?.variables ?? [] : []
        store.createProject(name: name, folder: folder, copyFrom: copyFrom, extra: extra)
        dismiss()
    }
}
