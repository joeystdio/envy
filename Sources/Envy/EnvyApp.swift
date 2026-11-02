import EnvyCore
import SwiftUI

@main
struct EnvyApp: App {
    @State private var store = Store()

    var body: some Scene {
        Window("Envy", id: "main") {
            ContentView()
                .environment(store)
        }
        .defaultSize(width: 1060, height: 660)
        // Opens at launch even if the window was closed last time; the menu bar extra keeps the app alive.
        .defaultLaunchBehavior(.presented)
        .commands { EnvyCommands(store: store) }

        MenuBarExtra("Envy", systemImage: "hexagon") {
            MenuBarView()
                .environment(store)
        }
    }
}

struct EnvyCommands: Commands {
    let store: Store

    var body: some Commands {
        CommandGroup(after: .appInfo) {
            Button("Setup Assistant…") { store.activeSheet = .onboarding }
        }
        CommandGroup(replacing: .newItem) {
            Button("New Project…") { store.activeSheet = .newProject }
                .keyboardShortcut("n", modifiers: [.command, .shift])
            Button("Import Projects from Folder…") { store.activeSheet = .scanProjects }
                .keyboardShortcut("i", modifiers: [.command, .shift])
            Button("New Variable…") {
                if let id = store.selectedProjectID { store.activeSheet = .addVariable(id) }
            }
            .keyboardShortcut("n")
            .disabled(store.selectedProjectID == nil)
            Divider()
            Button("Import .env…") {
                if let id = store.selectedProjectID { store.activeSheet = .importEnv(id, "") }
            }
            .keyboardShortcut("i")
            .disabled(store.selectedProjectID == nil)
            Button("Export .env…") {
                if let id = store.selectedProjectID { store.exportDotEnv(id) }
            }
            .keyboardShortcut("e", modifiers: [.command, .shift])
            .disabled(store.selectedProjectID == nil)
        }
    }
}

/// Quick switches in the menu bar; the app keeps running here when its window is closed.
struct MenuBarView: View {
    @Environment(Store.self) private var store
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Toggle("Global", isOn: store.projectBinding(Project.globalID, \.isEnabled, fallback: false))
        if !store.config.projects.isEmpty {
            Section("Projects") {
                ForEach(store.config.projects) { project in
                    Toggle(title(project), isOn: store.projectBinding(project.id, \.isEnabled, fallback: false))
                }
            }
        }
        Divider()
        if !store.isShellInstalled {
            Button("Set Up Envy…") {
                open(nil)
                store.activeSheet = .onboarding
            }
        }
        Button("Open Envy") { open(nil) }
            .keyboardShortcut("o")
        Divider()
        Button("Quit Envy") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    private func title(_ project: Project) -> String {
        guard let folder = project.folders.first else { return project.name }
        return "\(project.name)  ·  \((folder as NSString).lastPathComponent)/"
    }

    private func open(_ item: SidebarItem?) {
        if let item { store.selection = item }
        openWindow(id: "main")
        NSApp.activate()
    }
}
