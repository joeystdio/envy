import EnvyCore
import SwiftUI

/// First-run Setup Assistant: shows what was detected, connects the right startup file and checks it in a fresh
/// shell, offers to move plain-text secrets out of dotfiles, and imports projects from a folder of code.
struct OnboardingView: View {
    @Environment(Store.self) private var store
    @Environment(\.dismiss) private var dismiss

    private enum Step: Hashable { case welcome, connect, secrets, projects, done }

    @State private var step = Step.welcome
    @State private var includeBash = false
    @State private var connectError: String?
    @State private var exports: [RcExport] = []
    @State private var chosenExports = Set<String>()
    @State private var importedExports: [RcExport]?
    @State private var importedProjects: Int?

    private var steps: [Step] {
        [.welcome, .connect] + (exports.isEmpty ? [] : [.secrets]) + [.projects, .done]
    }

    private var shellName: String { (store.environment.supportedShell as NSString).lastPathComponent }

    private var targets: [RcTarget] {
        var targets = store.recommendedTargets
        if includeBash { targets += [.bashProfile, .bashrc].filter { !targets.contains($0) } }
        return targets
    }

    private var targetNames: String {
        ListFormatter.localizedString(byJoining: targets.map { store.displayPath(store.rcURL($0)) })
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                ForEach(steps, id: \.self) { s in
                    Capsule()
                        .fill(s == step ? Color.accentColor : Color.secondary.opacity(0.3))
                        .frame(width: s == step ? 20 : 7, height: 7)
                }
            }
            .animation(.snappy, value: step)
            .padding(.top, 18)

            Group {
                switch step {
                case .welcome: welcome
                case .connect: connect
                case .secrets: secrets
                case .projects: projects
                case .done: done
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(.horizontal, 36)
            .padding(.vertical, 20)

            Divider()
            footer.padding(16)
        }
        .frame(width: 680, height: 600)
        .task {
            exports = store.plaintextExports()
            chosenExports = Set(exports.filter(\.variable.isSecret).map(\.id))
            if store.inspection == .notChecked { await store.inspectShell() }
        }
    }

    // MARK: Steps

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 16) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 76, height: 76)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Welcome to Envy").font(.largeTitle.weight(.bold))
                    Text("Secrets and environment variables for your terminal, and for the coding agents you run in it.")
                        .foregroundStyle(.secondary)
                }
            }
            VStack(alignment: .leading, spacing: 12) {
                feature("switch.2", "Switch variables on and off", "Global and per-project sets. Terminals follow at their next prompt.")
                feature("folder", "Follows your folders", "A project linked to a folder loads when you cd in and unloads when you leave.")
                feature("lock.shield", "Secrets stay in your Keychain", "Not in plain-text dotfiles that every tool, and every agent, can read.")
            }
            GroupBox {
                VStack(alignment: .leading, spacing: 9) {
                    detected("terminal", "Shell", shellSummary, warning: store.environment.kind == .fish || store.environment.kind == .other)
                    detected("doc.text", "Startup file", startupSummary)
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Image(systemName: "sparkles").frame(width: 18).foregroundStyle(.secondary)
                        Text("Coding agents").frame(width: 110, alignment: .leading).foregroundStyle(.secondary)
                        agentsSummary
                    }
                }
                .padding(8)
                .frame(maxWidth: .infinity, alignment: .leading)
            } label: {
                Text("Your setup")
            }
        }
    }

    private var connect: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Connect your terminal").font(.title.weight(.bold))
            if store.isConnected(targets) {
                connectedResult
            } else {
                Text("Envy adds these lines to \(targetNames), so every new terminal loads the variables you switch on:")
                    .fixedSize(horizontal: false, vertical: true)
                Text(RcIntegration.snippet)
                    .font(.callout.monospaced())
                    .textSelection(.enabled)
                    .padding(12)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 6) {
                    Label("A copy of each file is saved first, as …envy-backup.", systemImage: "arrow.uturn.backward.circle")
                    Label("Disconnect any time from Shell Integration.", systemImage: "xmark.circle")
                    ForEach(targets.compactMap { t in store.symlinkDestination(t).map { (t, $0) } }, id: \.0) { target, destination in
                        Label("\(store.displayPath(store.rcURL(target))) links to \(store.displayPath(destination)); Envy writes there.", systemImage: "link")
                    }
                }
                .foregroundStyle(.secondary)
                if store.environment.kind == .zsh && (store.rcFileExists(.bashProfile) || store.rcFileExists(.bashrc)) {
                    Toggle("Also connect bash (\(store.displayPath(store.rcURL(.bashProfile))) and \(store.displayPath(store.rcURL(.bashrc))))", isOn: $includeBash)
                }
                if let connectError {
                    Label(connectError, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red)
                }
            }
        }
    }

    private var connectedResult: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Connected to \(targetNames)", systemImage: "checkmark.circle.fill")
                .font(.headline)
                .foregroundStyle(.green)
            VerificationLine(shellName: shellName)
            GroupBox {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Terminals that are already open load Envy once you run:")
                    CopyableCommand(command: "source \(store.displayPath(store.rcURL(targets[0])))")
                }
                .padding(6)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }

    private var secrets: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Move secrets out of your dotfiles").font(.title.weight(.bold))
            if let imported = importedExports {
                Label("Imported \(imported.count) into Global.", systemImage: "checkmark.circle.fill")
                    .font(.headline)
                    .foregroundStyle(.green)
                Text("They're still in your startup files as well. Delete these lines there, so the secrets no longer sit in plain text:")
                    .fixedSize(horizontal: false, vertical: true)
                ForEach(Dictionary(grouping: imported, by: \.file).sorted { $0.key.path < $1.key.path }, id: \.key) { file, items in
                    HStack {
                        Text("\(store.displayPath(file)), \(items.count == 1 ? "line" : "lines") \(items.map { String($0.line) }.joined(separator: ", "))")
                            .font(.callout.monospaced())
                        Spacer()
                        Button("Open in TextEdit") { openInTextEdit(file) }
                    }
                }
            } else {
                let secretCount = exports.filter(\.variable.isSecret).count
                Text("Your startup files export \(exports.count) variables in plain text\(secretCount > 0 ? ", \(secretCount) of which look like secrets" : ""). Any program you run, coding agents included, can read those files. Import them into Global and Envy keeps the secret ones in your Keychain.")
                    .fixedSize(horizontal: false, vertical: true)
                List {
                    ForEach(exports) { export in
                        Toggle(isOn: membership(export.id, in: $chosenExports)) {
                            HStack(spacing: 8) {
                                Text(export.key).font(.body.monospaced())
                                if export.variable.isSecret {
                                    Image(systemName: "lock.fill").foregroundStyle(.secondary)
                                }
                                Spacer()
                                Text(export.variable.isSecret ? "••••••" : export.value)
                                    .lineLimit(1)
                                    .foregroundStyle(.secondary)
                                Text("\(store.displayPath(export.file)):\(export.line)")
                                    .font(.caption.monospaced())
                                    .foregroundStyle(.tertiary)
                            }
                        }
                    }
                }
                .frame(minHeight: 180)
            }
        }
    }

    private var projects: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Bring in your projects").font(.title.weight(.bold))
            Text("Pick the folder where you keep your code. Envy looks for .env, .env.local and .envrc files, and for .NET appsettings.json and user secrets. Each folder it finds becomes a project that loads when a terminal enters it.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            ProjectScanView { importedProjects = $0 }
        }
    }

    private var done: some View {
        VStack(spacing: 16) {
            Spacer()
            Image(systemName: "checkmark.seal.fill")
                .font(.system(size: 60))
                .foregroundStyle(.green)
            Text("You're set").font(.largeTitle.weight(.bold))
            Text(store.isShellInstalled
                 ? "Open a new terminal tab and run envy status to see what's loaded. Toggle anything from the sidebar or the hexagon in your menu bar."
                 : "Your terminal isn't connected yet. Connect it any time from Shell Integration.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 440)
            if store.isShellInstalled { CopyableCommand(command: "envy status") }
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: Footer

    @ViewBuilder private var footer: some View {
        HStack {
            if step == .welcome {
                Button("Set Up Later") { finish() }
                    .keyboardShortcut(.cancelAction)
            } else if step != .done {
                Button("Back") { move(-1) }
            }
            Spacer()
            switch step {
            case .welcome:
                Button("Continue") { move(1) }.keyboardShortcut(.defaultAction)
            case .connect:
                if store.isConnected(targets) {
                    Button("Continue") { move(1) }.keyboardShortcut(.defaultAction)
                } else {
                    Button("Skip") { move(1) }
                    Button("Connect") { performConnect() }.keyboardShortcut(.defaultAction)
                }
            case .secrets:
                if importedExports == nil {
                    Button("Skip") { move(1) }
                    Button(chosenExports.count == 1 ? "Import 1 Variable" : "Import \(chosenExports.count) Variables") { importExports() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(chosenExports.isEmpty)
                } else {
                    Button("Continue") { move(1) }.keyboardShortcut(.defaultAction)
                }
            case .projects:
                Button(importedProjects == nil ? "Skip" : "Continue") { move(1) }.keyboardShortcut(.defaultAction)
            case .done:
                Button("Add a Global Variable") { finish(then: .addVariable(Project.globalID)) }
                Button("Start Using Envy") { finish() }.keyboardShortcut(.defaultAction)
            }
        }
    }

    // MARK: Actions

    private func move(_ offset: Int) {
        guard let index = steps.firstIndex(of: step) else { return }
        step = steps[min(max(index + offset, 0), steps.count - 1)]
    }

    private func performConnect() {
        do {
            try store.connect(targets)
            connectError = nil
            Task { await store.inspectShell() }
        } catch {
            connectError = error.localizedDescription
        }
    }

    private func importExports() {
        let picked = exports.filter { chosenExports.contains($0.id) }
        store.addVariables(picked.map(\.variable), to: Project.globalID, policy: .replace, actionName: "Import from Startup Files")
        importedExports = picked
    }

    private func finish(then next: AppSheet? = nil) {
        store.finishOnboarding()
        dismiss()
        if let next {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { store.activeSheet = next }
        }
    }

    private func openInTextEdit(_ file: URL) {
        NSWorkspace.shared.open(
            [file.resolvingSymlinksInPath()],
            withApplicationAt: URL(fileURLWithPath: "/System/Applications/TextEdit.app"),
            configuration: NSWorkspace.OpenConfiguration()
        )
    }

    // MARK: Pieces

    private var shellSummary: String {
        let env = store.environment
        switch env.kind {
        case .zsh, .bash: return "\(env.name)  (\(env.shellPath))"
        default: return "\(env.name) isn't supported yet; Envy will connect zsh"
        }
    }

    private var startupSummary: String {
        store.recommendedTargets.map { target in
            var text = store.displayPath(store.rcURL(target))
            if let destination = store.symlinkDestination(target) { text += " → \(store.displayPath(destination))" }
            if store.installed[target] == true { text += "  (connected)" } else if !store.rcFileExists(target) { text += "  (will be created)" }
            return text
        }
        .joined(separator: ", ")
    }

    @ViewBuilder private var agentsSummary: some View {
        switch store.inspection {
        case .notChecked, .checking:
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("Looking…").foregroundStyle(.secondary)
            }
        case .done(let result) where result.agents.isEmpty:
            Text("None found on your PATH")
        case .done(let result):
            Text(ListFormatter.localizedString(byJoining: result.agents))
        case .unavailable:
            Text("Couldn't check (your shell took too long to start)").foregroundStyle(.secondary)
        }
    }

    private func feature(_ symbol: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.title3)
                .foregroundStyle(Color.accentColor)
                .frame(width: 26)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(detail).foregroundStyle(.secondary)
            }
        }
    }

    private func detected(_ symbol: String, _ label: String, _ value: String, warning: Bool = false) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: symbol).frame(width: 18).foregroundStyle(.secondary)
            Text(label).frame(width: 110, alignment: .leading).foregroundStyle(.secondary)
            Text(value).foregroundStyle(warning ? Color.orange : Color.primary).textSelection(.enabled)
        }
    }
}

/// Whether a freshly opened shell loads Envy, with a button to check again.
struct VerificationLine: View {
    let shellName: String
    @Environment(Store.self) private var store

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            switch store.inspection {
            case .notChecked:
                Label("Not tested yet.", systemImage: "questionmark.circle").foregroundStyle(.secondary)
            case .checking:
                ProgressView().controlSize(.small)
                Text("Opening a new \(shellName) to check that it loads Envy…").foregroundStyle(.secondary)
            case .done(let result) where result.hookLoaded:
                Label("Verified: a new \(shellName) loads Envy.", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
            case .done:
                Label("A new \(shellName) didn't load Envy. Something earlier in its startup files may stop it (an exec or return), or your terminal uses a different file.", systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            case .unavailable:
                Label("Couldn't check: your shell took too long to start. Open a new terminal and run envy status.", systemImage: "questionmark.circle")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if store.inspection != .checking {
                Button("Check Again") { Task { await store.inspectShell() } }
                    .buttonStyle(.link)
            }
        }
    }
}

struct CopyableCommand: View {
    let command: String

    var body: some View {
        HStack(spacing: 8) {
            Text(command).font(.body.monospaced()).textSelection(.enabled)
            Button { Panels.copy(command) } label: { Image(systemName: "doc.on.doc") }
                .buttonStyle(.borderless)
                .help("Copy")
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 6))
    }
}

/// Picks a folder of code, finds projects with environment files in it, and imports the chosen ones.
struct ProjectScanView: View {
    var onImported: (Int) -> Void = { _ in }
    @Environment(Store.self) private var store
    @State private var root: URL?
    @State private var scanning = false
    @State private var found: [FoundProject]?
    @State private var chosen = Set<String>()
    @State private var imported: Int?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                ForEach(store.paths.suggestedProjectRoots.prefix(3), id: \.self) { suggestion in
                    Button("Scan \(store.displayPath(suggestion))") { scan(suggestion) }
                }
                Button("Choose Folder…") {
                    if let path = Panels.chooseFolder(prompt: "Scan", message: "Choose the folder where you keep your code.") {
                        scan(URL(fileURLWithPath: path))
                    }
                }
                Spacer()
            }
            .disabled(scanning)

            if scanning {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Looking for environment files in \(root.map { store.displayPath($0) } ?? "")…").foregroundStyle(.secondary)
                }
            } else if let imported {
                Label(imported == 1 ? "Created 1 project." : "Created \(imported) projects.", systemImage: "checkmark.circle.fill")
                    .font(.headline)
                    .foregroundStyle(.green)
                Text("Each loads automatically while a terminal is inside its folder. Find them in the sidebar.")
                    .foregroundStyle(.secondary)
            } else if let found, let root {
                if found.isEmpty {
                    Text("No folders with .env files or .NET settings in \(store.displayPath(root)).")
                        .foregroundStyle(.secondary)
                } else {
                    List {
                        ForEach(found) { project in
                            let linked = store.isLinked(project.folder)
                            Toggle(isOn: membership(project.id, in: $chosen)) {
                                VStack(alignment: .leading, spacing: 2) {
                                    HStack(spacing: 6) {
                                        Text(project.name).font(.headline)
                                        Text(project.files.joined(separator: " + "))
                                            .font(.caption.monospaced())
                                            .foregroundStyle(.secondary)
                                        if linked { Text("already added").font(.caption).foregroundStyle(.orange) }
                                    }
                                    Text("\(store.displayPath(project.folder)) · \(summary(project))")
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                        .lineLimit(1)
                                        .truncationMode(.middle)
                                }
                            }
                            .disabled(linked)
                        }
                    }
                    .frame(minHeight: 190)
                    HStack {
                        Text("Found \(found.count) in \(store.displayPath(root))").foregroundStyle(.secondary)
                        Spacer()
                        Button(chosen.count == 1 ? "Import 1 Project" : "Import \(chosen.count) Projects") {
                            let count = store.importProjects(found.filter { chosen.contains($0.id) })
                            imported = count
                            onImported(count)
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(chosen.isEmpty)
                    }
                }
            }
        }
    }

    private func summary(_ project: FoundProject) -> String {
        let secrets = project.variables.filter(\.isSecret).count
        let vars = project.variables.count == 1 ? "1 variable" : "\(project.variables.count) variables"
        return secrets > 0 ? "\(vars), \(secrets) secret" : vars
    }

    private func scan(_ folder: URL) {
        root = folder
        scanning = true
        imported = nil
        Task {
            let home = store.paths.home
            let result = await Task.detached { ProjectScanner.scan(folder, home: home) }.value
            found = result
            chosen = Set(result.filter { !store.isLinked($0.folder) }.map(\.id))
            scanning = false
        }
    }
}

struct ProjectScanSheet: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Import Projects").font(.headline)
            Text("Envy looks for .env, .env.local and .envrc files, and for .NET appsettings.json and user secrets. Each folder it finds becomes a project that loads when a terminal enters it.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            ProjectScanView()
            Spacer(minLength: 0)
            HStack {
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 640, height: 480)
    }
}

/// A checkbox binding for `id`'s membership in `set`.
func membership<ID: Hashable>(_ id: ID, in set: Binding<Set<ID>>) -> Binding<Bool> {
    Binding(
        get: { set.wrappedValue.contains(id) },
        set: { if $0 { set.wrappedValue.insert(id) } else { set.wrappedValue.remove(id) } }
    )
}
