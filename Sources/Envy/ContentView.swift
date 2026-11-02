import EnvyCore
import SwiftUI

struct ContentView: View {
    @Environment(Store.self) private var store
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        @Bindable var store = store
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 220, ideal: 250, max: 340)
        } detail: {
            VStack(spacing: 0) {
                StatusBanner()
                detail
            }
        }
        .frame(minWidth: 900, minHeight: 540)
        .sheet(item: $store.activeSheet) { sheet in
            sheetView(sheet).environment(store)
        }
        .onAppear { store.undoManager = undoManager }
        .onChange(of: undoManager) { _, manager in store.undoManager = manager }
    }

    @ViewBuilder private var detail: some View {
        switch store.selection {
        case .project(let id)?:
            ProjectDetailView(projectID: id).id(id)
        case .shell?:
            ShellIntegrationView()
        case nil:
            ContentUnavailableView("Nothing Selected", systemImage: "sidebar.left", description: Text("Pick Global or a project in the sidebar."))
        }
    }

    @ViewBuilder private func sheetView(_ sheet: AppSheet) -> some View {
        switch sheet {
        case .onboarding: OnboardingView()
        case .scanProjects: ProjectScanSheet()
        case .newProject: NewProjectSheet()
        case .addVariable(let project): VariableEditorSheet(projectID: project, variableID: nil)
        case .editVariable(let project, let variable): VariableEditorSheet(projectID: project, variableID: variable)
        case .importEnv(let project, let text): ImportSheet(projectID: project, text: text)
        case .copyFrom(let project): CopyFromProjectSheet(targetID: project)
        }
    }
}

/// A strip above the detail pane for problems that stop variables reaching the terminal.
struct StatusBanner: View {
    @Environment(Store.self) private var store

    var body: some View {
        if let problem = store.problem {
            banner(symbol: "exclamationmark.octagon.fill", tint: .red, text: problem) {
                if store.secretsLocked { Button("Try Again") { store.retrySecrets() } }
            }
        } else if !store.isShellInstalled, store.selection != .shell {
            banner(symbol: "terminal.fill", tint: .orange, text: "Your terminal isn't connected yet, so these variables don't load anywhere.") {
                Button("Set Up…") { store.activeSheet = .onboarding }
            }
        }
    }

    private func banner<Actions: View>(symbol: String, tint: Color, text: String, @ViewBuilder actions: () -> Actions) -> some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: symbol).foregroundStyle(tint)
                // No fixedSize here: measured at a narrow width it would make the banner, and so the window, very tall.
                Text(text).lineLimit(3)
                Spacer()
                actions()
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 9)
            .background(tint.opacity(0.12))
            Divider()
        }
    }
}

extension Project {
    var symbolName: String {
        if isGlobal { return "globe" }
        return isFolderScoped ? "folder.fill" : "shippingbox.fill"
    }
}

@MainActor
enum Panels {
    static func chooseFolder(prompt: String, message: String) -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = prompt
        panel.message = message
        return panel.runModal() == .OK ? panel.url?.path : nil
    }

    static func openDotEnv() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.prompt = "Import"
        panel.message = "Choose a .env or appsettings.json file to import."
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        return try? String(contentsOf: url, encoding: .utf8)
    }

    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}
