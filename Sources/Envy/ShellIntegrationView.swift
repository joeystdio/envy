import EnvyCore
import SwiftUI

struct ShellIntegrationView: View {
    @Environment(Store.self) private var store
    @State private var showPreview = false
    @State private var failure: String?

    private var shellName: String { (store.environment.supportedShell as NSString).lastPathComponent }
    private var recommended: [RcTarget] { store.recommendedTargets }
    private var connected: Bool { store.isConnected(recommended) }

    var body: some View {
        Form {
            Section { status }
            Section("Your setup") {
                LabeledContent("Login shell") {
                    Text("\(store.environment.name)  (\(store.environment.shellPath))").textSelection(.enabled)
                }
                if let zdotdir = store.environment.zdotdir {
                    LabeledContent("zsh startup files") { Text(store.displayPath(URL(fileURLWithPath: zdotdir))) }
                }
                LabeledContent("Coding agents") { agents }
            }
            Section {
                ForEach(recommended) { target in row(target, recommended: true) }
            } header: {
                Text("Startup files for \(shellName)")
            } footer: {
                Text("Connecting appends this, and keeps a copy of the previous file as …envy-backup:\n\(RcIntegration.sourceLine)")
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }
            Section("Other shells") {
                ForEach(store.otherTargets) { target in row(target, recommended: false) }
            }
            Section("In a terminal") {
                command("See what's loaded here", "envy status")
                command("Reload right now", "envy reload")
                command("Open this app", "envy open")
            }
            Section("Generated files") {
                LabeledContent("Location") {
                    HStack {
                        Text(store.displayPath(store.paths.shellDirectory)).font(.callout.monospaced())
                        Button("Show in Finder") {
                            NSWorkspace.shared.activateFileViewerSelecting([store.paths.shellDirectory.appendingPathComponent("init.sh")])
                        }
                    }
                }
                LabeledContent("Last updated") {
                    Text(store.lastWrite.map { $0.formatted(date: .omitted, time: .standard) } ?? "Not changed yet this session")
                }
                DisclosureGroup("Preview the generated script (secrets masked)", isExpanded: $showPreview) {
                    ScrollView([.vertical, .horizontal]) {
                        Text(ShellScript.vars(for: store.config, maskSecrets: true))
                            .font(.caption.monospaced())
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(8)
                    }
                    .frame(height: 260)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Shell Integration")
        .task {
            store.refreshInstallStatus()
            if store.inspection == .notChecked { await store.inspectShell() }
        }
        .alert("Couldn't change the file", isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
            Button("OK") {}
        } message: {
            Text(failure ?? "")
        }
    }

    private var status: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: connected ? "checkmark.circle.fill" : "bolt.horizontal.circle.fill")
                .font(.system(size: 30))
                .foregroundStyle(connected ? Color.green : Color.orange)
            VStack(alignment: .leading, spacing: 8) {
                Text(connected ? "Your terminal is connected" : "Connect your terminal")
                    .font(.title3.weight(.semibold))
                Text(connected
                     ? "Every new \(shellName) loads the variables you switch on, and so does every coding agent you start from it. Open terminals pick up changes at their next prompt."
                     : "Your login shell is \(store.environment.name). Envy adds one line to \(names(recommended)) so every new terminal, and every coding agent you start from one, gets the variables you switch on.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if connected { VerificationLine(shellName: shellName) }
                HStack {
                    if !connected {
                        Button("Connect \(names(recommended))") { attempt { try store.connect(recommended); Task { await store.inspectShell() } } }
                            .buttonStyle(.borderedProminent)
                    }
                    Button("Setup Assistant…") { store.activeSheet = .onboarding }
                }
            }
        }
        .padding(.vertical, 6)
    }

    @ViewBuilder private var agents: some View {
        switch store.inspection {
        case .notChecked, .checking:
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("Looking…").foregroundStyle(.secondary)
            }
        case .done(let result) where result.agents.isEmpty:
            Text("None found on your PATH").foregroundStyle(.secondary)
        case .done(let result):
            Text(ListFormatter.localizedString(byJoining: result.agents))
        case .unavailable:
            Text("Couldn't check").foregroundStyle(.secondary)
        }
    }

    private func names(_ targets: [RcTarget]) -> String {
        ListFormatter.localizedString(byJoining: targets.map { store.displayPath(store.rcURL($0)) })
    }

    private func row(_ target: RcTarget, recommended: Bool) -> some View {
        let installed = store.installed[target] ?? false
        let url = store.rcURL(target)
        var detail = target.detail
        if let destination = store.symlinkDestination(target) { detail += " · links to \(store.displayPath(destination))" }
        if !installed && !store.rcFileExists(target) { detail += " · will be created" }
        return HStack(spacing: 10) {
            Image(systemName: installed ? "checkmark.circle.fill" : "circle.dashed")
                .font(.title3)
                .foregroundStyle(installed ? Color.green : Color.secondary)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(store.displayPath(url)).font(.body.monospaced())
                    if recommended {
                        Text("Recommended")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1)
                            .background(Color.accentColor.opacity(0.2), in: Capsule())
                    }
                }
                Text(detail).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if installed {
                Button("Disconnect") { attempt { try store.uninstall(target) } }
            } else {
                Button("Connect") { attempt { try store.connect([target]); Task { await store.inspectShell() } } }
            }
        }
    }

    private func command(_ label: String, _ command: String) -> some View {
        LabeledContent(label) {
            HStack {
                Text(command).font(.body.monospaced()).textSelection(.enabled)
                Button { Panels.copy(command) } label: { Image(systemName: "doc.on.doc") }
                    .buttonStyle(.borderless)
                    .help("Copy")
            }
        }
    }

    private func attempt(_ action: () throws -> Void) {
        do { try action() } catch { failure = error.localizedDescription }
    }
}
