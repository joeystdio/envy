import EnvyCore
import SwiftUI
import UniformTypeIdentifiers

struct ProjectDetailView: View {
    let projectID: UUID
    @Environment(Store.self) private var store
    @State private var selection = Set<EnvVar.ID>()
    @State private var search = ""
    @State private var revealed = Set<EnvVar.ID>()
    @State private var dropTargeted = false
    @State private var confirmDelete = false

    var body: some View {
        if let project = store.project(projectID) {
            VStack(spacing: 0) {
                ProjectHeader(project: project)
                Divider()
                if project.variables.isEmpty {
                    EmptyVariables(project: project)
                } else {
                    VariableTable(project: project, rows: filtered(project), selection: $selection, revealed: $revealed)
                }
            }
            .overlay {
                if dropTargeted {
                    RoundedRectangle(cornerRadius: 10)
                        .strokeBorder(Color.accentColor, lineWidth: 3)
                        .padding(4)
                        .allowsHitTesting(false)
                }
            }
            .dropDestination(for: URL.self) { urls, _ in
                importFiles(urls)
            } isTargeted: { dropTargeted = $0 }
            .searchable(text: $search, placement: .toolbar, prompt: "Filter variables")
            .navigationTitle(project.name)
            .toolbar { toolbar(project) }
            .confirmationDialog("Delete “\(project.name)”?", isPresented: $confirmDelete) {
                Button("Delete Project", role: .destructive) { store.deleteProject(project.id) }
            } message: {
                Text("Its \(project.variables.count) variables stop loading in your terminals. You can undo this with ⌘Z.")
            }
        } else {
            ContentUnavailableView("Project Not Found", systemImage: "questionmark.folder")
        }
    }

    @ToolbarContentBuilder
    private func toolbar(_ project: Project) -> some ToolbarContent {
        ToolbarItemGroup {
            Button { store.activeSheet = .addVariable(project.id) } label: {
                Label("Add Variable", systemImage: "plus")
            }
            .help("Add a variable (⌘N)")
            Menu {
                Button("Paste or Open .env File…") { store.activeSheet = .importEnv(project.id, "") }
                Button("Copy from Another Project…") { store.activeSheet = .copyFrom(project.id) }
            } label: {
                Label("Import", systemImage: "square.and.arrow.down")
            }
            .help("Import variables")
            Button { store.exportDotEnv(project.id) } label: {
                Label("Export .env", systemImage: "square.and.arrow.up")
            }
            .help("Export the enabled variables as a .env file")
            .disabled(project.variables.isEmpty)
            if !project.isGlobal {
                Menu {
                    Button("Duplicate Project") { store.duplicateProject(project.id) }
                    Divider()
                    Button("Delete Project…", role: .destructive) { confirmDelete = true }
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
            }
        }
    }

    private func filtered(_ project: Project) -> [EnvVar] {
        let query = search.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return project.variables }
        return project.variables.filter {
            $0.key.localizedCaseInsensitiveContains(query) || $0.note.localizedCaseInsensitiveContains(query)
                || (!$0.isSecret && $0.value.localizedCaseInsensitiveContains(query))
        }
    }

    private func importFiles(_ urls: [URL]) -> Bool {
        let text = urls.compactMap { try? String(contentsOf: $0, encoding: .utf8) }.joined(separator: "\n")
        guard !DotEnv.parseAny(text).isEmpty else { return false }
        store.activeSheet = .importEnv(projectID, text)
        return true
    }
}

struct ProjectHeader: View {
    let project: Project
    @Environment(Store.self) private var store

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: project.symbolName)
                    .font(.title2)
                    .foregroundStyle(project.isEnabled ? Color.accentColor : Color.secondary)
                    .frame(width: 28)
                if project.isGlobal {
                    Text("Global").font(.title2.weight(.semibold))
                } else {
                    TextField("Project name", text: store.projectBinding(project.id, \.name, fallback: ""))
                        .textFieldStyle(.plain)
                        .font(.title2.weight(.semibold))
                }
                Spacer()
                Toggle(project.isEnabled ? "On" : "Off", isOn: store.projectBinding(project.id, \.isEnabled, fallback: false))
                    .toggleStyle(.switch)
            }
            Group {
                Text(scopeText).foregroundStyle(.secondary)
                if !project.isGlobal { folders }
            }
            .padding(.leading, 38)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
    }

    private var scopeText: String {
        if !project.isEnabled { return "Off: none of these variables are loaded." }
        if project.isGlobal { return "Loaded in every terminal and the coding agents you start there. Projects can override these." }
        if project.isFolderScoped { return "Loaded only while a terminal is inside one of these folders (or a subfolder):" }
        return "Loaded in every terminal and the coding agents you start there."
    }

    private var folders: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(project.folders, id: \.self) { folder in
                HStack(spacing: 6) {
                    Image(systemName: "folder").foregroundStyle(.secondary)
                    Text((folder as NSString).abbreviatingWithTildeInPath)
                        .font(.callout.monospaced())
                        .textSelection(.enabled)
                    if !FileManager.default.fileExists(atPath: folder) {
                        Text("missing").font(.caption).foregroundStyle(.orange)
                    }
                    Button {
                        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: folder)])
                    } label: {
                        Image(systemName: "arrow.forward.circle")
                    }
                    .buttonStyle(.borderless)
                    .help("Show in Finder")
                    Button { store.unlinkFolder(folder, from: project.id) } label: {
                        Image(systemName: "xmark.circle.fill")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("Unlink this folder")
                }
            }
            Button {
                let message = "\(project.name)’s variables will load only while a terminal is inside this folder."
                if let path = Panels.chooseFolder(prompt: "Link Folder", message: message) {
                    store.linkFolder(path, to: project.id)
                }
            } label: {
                Label(project.isFolderScoped ? "Link Another Folder…" : "Load Only Inside a Folder…", systemImage: "folder.badge.plus")
            }
            .buttonStyle(.link)
        }
    }
}

struct EmptyVariables: View {
    let project: Project
    @Environment(Store.self) private var store

    var body: some View {
        ContentUnavailableView {
            Label("No Variables Yet", systemImage: "key.horizontal")
        } description: {
            Text("Add variables one at a time, paste or drop a .env or appsettings.json file here, or copy them from another project.")
        } actions: {
            HStack {
                Button("Add Variable") { store.activeSheet = .addVariable(project.id) }
                    .buttonStyle(.borderedProminent)
                Button("Paste or Open .env…") { store.activeSheet = .importEnv(project.id, "") }
                if store.config.scopes.contains(where: { $0.id != project.id && !$0.variables.isEmpty }) {
                    Button("Copy from Project…") { store.activeSheet = .copyFrom(project.id) }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct VariableTable: View {
    let project: Project
    let rows: [EnvVar]
    @Binding var selection: Set<EnvVar.ID>
    @Binding var revealed: Set<EnvVar.ID>
    @Environment(Store.self) private var store

    var body: some View {
        let duplicates = project.duplicateKeys
        Table(of: EnvVar.self, selection: $selection) {
            TableColumn("") { v in
                Toggle("Enabled", isOn: store.variableBinding(project.id, v.id, \.isEnabled))
                    .labelsHidden()
                    .toggleStyle(.checkbox)
                    .help(v.isEnabled ? "Loaded. Click to turn off." : "Off. Click to load it.")
            }
            .width(22)
            TableColumn("Name") { v in nameCell(v, duplicates: duplicates) }
                .width(min: 150, ideal: 230)
            TableColumn("Value") { v in valueCell(v) }
                .width(min: 180, ideal: 340)
            TableColumn("") { v in optionsCell(v) }
                .width(50)
            TableColumn("Note") { v in
                TextField("Note", text: store.variableBinding(project.id, v.id, \.note), prompt: Text(""))
                    .textFieldStyle(.plain)
                    .foregroundStyle(.secondary)
            }
            .width(min: 80, ideal: 160)
        } rows: {
            ForEach(rows) { TableRow($0) }
        }
        .contextMenu(forSelectionType: EnvVar.ID.self) { ids in
            contextMenu(ids)
        } primaryAction: { ids in
            if ids.count == 1, let id = ids.first { store.activeSheet = .editVariable(project.id, id) }
        }
        .onDeleteCommand {
            guard !selection.isEmpty else { return }
            store.deleteVariables(selection, in: project.id)
            selection = []
        }
        .onCopyCommand {
            selection.isEmpty ? [] : [NSItemProvider(object: store.dotEnvText(selection, in: project.id) as NSString)]
        }
        .onPasteCommand(of: [.plainText, .utf8PlainText]) { _ in paste() }
    }

    private func nameCell(_ v: EnvVar, duplicates: Set<String>) -> some View {
        HStack(spacing: 4) {
            TextField("Name", text: store.variableBinding(project.id, v.id, \.key))
                .textFieldStyle(.plain)
                .font(.body.monospaced())
                .foregroundStyle(v.hasValidKey ? (v.isEnabled ? Color.primary : Color.secondary) : Color.red)
            if let problem = v.keyProblem {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .help("Not loaded: \(problem)")
            } else if duplicates.contains(v.key) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .help("\(v.key) is defined more than once in this project; the last one wins.")
            } else if v.isEnabled, project.isEnabled {
                let others = store.config.otherDefinitions(of: v.key, excluding: project.id)
                if !others.isEmpty {
                    Image(systemName: "square.stack.3d.up")
                        .foregroundStyle(.secondary)
                        .help("Also set in \(ListFormatter.localizedString(byJoining: others.map(\.name))). Global loads first, then projects, then folder projects.")
                }
            }
        }
    }

    private func valueCell(_ v: EnvVar) -> some View {
        let value = store.variableBinding(project.id, v.id, \.value)
        let isRevealed = revealed.contains(v.id)
        return HStack(spacing: 4) {
            if v.value.contains("\n") {
                Text(v.isSecret && !isRevealed ? "•••••• (multi-line)" : v.value.replacingOccurrences(of: "\n", with: " ⏎ "))
                    .font(.body.monospaced())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .help("Multi-line value. Double-click the row to edit it.")
            } else if v.isSecret && !isRevealed {
                SecureField("Value", text: value)
                    .textFieldStyle(.plain)
            } else {
                TextField("Value", text: value, prompt: Text("empty"))
                    .textFieldStyle(.plain)
                    .font(.body.monospaced())
                    .foregroundStyle(v.isEnabled ? Color.primary : Color.secondary)
            }
            if v.isSecret {
                Button {
                    if isRevealed { revealed.remove(v.id) } else { revealed.insert(v.id) }
                } label: {
                    Image(systemName: isRevealed ? "eye.slash" : "eye")
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
                .help(isRevealed ? "Hide value" : "Show value")
            }
        }
    }

    private func optionsCell(_ v: EnvVar) -> some View {
        HStack(spacing: 8) {
            Button {
                store.updateVariables([v.id], in: project.id, actionName: v.isSecret ? "Make Plain" : "Make Secret") { $0.isSecret.toggle() }
            } label: {
                Image(systemName: v.isSecret ? "lock.fill" : "lock.open")
                    .foregroundStyle(v.isSecret ? Color.accentColor : Color.secondary.opacity(0.5))
            }
            .buttonStyle(.borderless)
            .help(v.isSecret ? "Secret: kept in your Keychain and hidden here. Click to make it plain." : "Plain value. Click to keep it in your Keychain and hide it.")
            Button {
                store.updateVariables([v.id], in: project.id, actionName: v.expands ? "Use Literal Value" : "Expand Variables") { $0.expands.toggle() }
            } label: {
                Image(systemName: "dollarsign")
                    .foregroundStyle(v.expands ? Color.accentColor : Color.secondary.opacity(0.5))
            }
            .buttonStyle(.borderless)
            .help(v.expands ? "Expands $VARIABLES, ~ and $(commands). Click to use the value literally." : "Literal value. Click to expand $VARIABLES, ~ and $(commands) like the shell does.")
        }
    }

    @ViewBuilder private func contextMenu(_ ids: Set<EnvVar.ID>) -> some View {
        let vars = project.variables.filter { ids.contains($0.id) }
        if vars.isEmpty {
            Button("Add Variable…") { store.activeSheet = .addVariable(project.id) }
            Button("Paste .env") { paste() }
        } else {
            if vars.count == 1 {
                Button("Edit…") { store.activeSheet = .editVariable(project.id, vars[0].id) }
            }
            let allOn = vars.allSatisfy(\.isEnabled)
            Button(allOn ? "Turn Off" : "Turn On") {
                store.updateVariables(ids, in: project.id, actionName: allOn ? "Turn Off" : "Turn On") { $0.isEnabled = !allOn }
            }
            let allSecret = vars.allSatisfy(\.isSecret)
            Button(allSecret ? "Make Plain" : "Make Secret") {
                store.updateVariables(ids, in: project.id, actionName: allSecret ? "Make Plain" : "Make Secret") { $0.isSecret = !allSecret }
            }
            Divider()
            let targets = store.config.scopes.filter { $0.id != project.id }
            Menu("Copy to") {
                ForEach(targets) { target in
                    Button(target.name) { store.copyVariables(ids, from: project.id, to: target.id, move: false) }
                }
            }
            .disabled(targets.isEmpty)
            Menu("Move to") {
                ForEach(targets) { target in
                    Button(target.name) {
                        store.copyVariables(ids, from: project.id, to: target.id, move: true)
                        selection.subtract(ids)
                    }
                }
            }
            .disabled(targets.isEmpty)
            Button("Copy as .env") { Panels.copy(store.dotEnvText(ids, in: project.id)) }
            Divider()
            Button("Delete", role: .destructive) {
                store.deleteVariables(ids, in: project.id)
                selection.subtract(ids)
            }
        }
    }

    private func paste() {
        guard let text = NSPasteboard.general.string(forType: .string), !DotEnv.parseAny(text).isEmpty else {
            NSSound.beep()
            return
        }
        store.activeSheet = .importEnv(project.id, text)
    }
}
