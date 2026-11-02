import EnvyCore
import SwiftUI

struct SidebarView: View {
    @Environment(Store.self) private var store
    @State private var pendingDelete: Project?

    var body: some View {
        @Bindable var store = store
        List(selection: $store.selection) {
            Section {
                ProjectRow(project: store.config.global)
                    .tag(SidebarItem.project(Project.globalID))
            }
            Section("Projects") {
                ForEach(store.config.projects) { project in
                    ProjectRow(project: project)
                        .tag(SidebarItem.project(project.id))
                        .contextMenu {
                            Button(project.isEnabled ? "Turn Off" : "Turn On") { store.setProjectEnabled(project.id, !project.isEnabled) }
                            Button("Duplicate") { store.duplicateProject(project.id) }
                            Divider()
                            Button("Delete…", role: .destructive) { pendingDelete = project }
                        }
                }
                .onMove { store.moveProjects(from: $0, to: $1) }
                if store.config.projects.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("No projects yet").foregroundStyle(.secondary)
                        Button("Find Projects in a Folder…") { store.activeSheet = .scanProjects }
                            .buttonStyle(.link)
                    }
                }
            }
            Section {
                Label {
                    Text("Shell Integration")
                } icon: {
                    Image(systemName: "terminal").foregroundStyle(store.isShellInstalled ? Color.green : Color.orange)
                }
                .tag(SidebarItem.shell)
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom) {
            HStack {
                Button { store.activeSheet = .newProject } label: {
                    Label("New Project", systemImage: "plus")
                }
                .help("New project (⇧⌘N)")
                Spacer()
                Button { store.activeSheet = .scanProjects } label: {
                    Image(systemName: "square.and.arrow.down.on.square")
                }
                .accessibilityLabel("Import Projects from Folder")
                .help("Import projects from a folder (⇧⌘I)")
            }
            .buttonStyle(.borderless)
            .padding(12)
        }
        .confirmationDialog(
            "Delete “\(pendingDelete?.name ?? "")”?",
            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
            presenting: pendingDelete
        ) { project in
            Button("Delete Project", role: .destructive) { store.deleteProject(project.id) }
        } message: { project in
            Text("Its \(project.variables.count) variables stop loading in your terminals. You can undo this with ⌘Z.")
        }
    }
}

struct ProjectRow: View {
    let project: Project
    @Environment(Store.self) private var store

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: project.symbolName)
                .foregroundStyle(project.isEnabled ? Color.accentColor : Color.secondary)
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(project.name).lineLimit(1)
                Text(store.subtitle(for: project))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 4)
            Toggle("On", isOn: store.projectBinding(project.id, \.isEnabled, fallback: false))
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.mini)
                .help(project.isEnabled ? "Loaded. Click to turn off." : "Off. Click to load these variables.")
        }
        .padding(.vertical, 2)
    }
}
