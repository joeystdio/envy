import AppKit
import EnvyCore
import SwiftUI

enum SidebarItem: Hashable {
    case project(UUID)
    case shell
}

enum AppSheet: Identifiable {
    case onboarding
    case scanProjects
    case newProject
    case addVariable(UUID)
    case editVariable(UUID, UUID)
    case importEnv(UUID, String)
    case copyFrom(UUID)

    var id: String {
        switch self {
        case .onboarding: "onboarding"
        case .scanProjects: "scan-projects"
        case .newProject: "new-project"
        case .addVariable(let p): "add-\(p)"
        case .editVariable(let p, let v): "edit-\(p)-\(v)"
        case .importEnv(let p, _): "import-\(p)"
        case .copyFrom(let p): "copy-\(p)"
        }
    }
}

/// App state. Every change to `config` is saved shortly after: settings to disk, secrets to the Keychain,
/// and the generated shell files, which open terminals pick up at their next prompt.
@MainActor
@Observable
final class Store {
    var config: EnvConfig {
        didSet { if config != oldValue { scheduleSave() } }
    }
    var selection: SidebarItem? = .project(Project.globalID)
    var activeSheet: AppSheet?
    private(set) var problem: String?
    private(set) var secretsLocked = false
    private(set) var lastWrite: Date?
    private(set) var installed: [RcTarget: Bool] = [:]
    private(set) var environment = ShellEnvironment(shellPath: ShellEnvironment.loginShellPath())
    private(set) var inspection = Inspection.notChecked

    /// The result of opening a fresh terminal shell in the background.
    enum Inspection: Equatable {
        case notChecked
        case checking
        case done(ShellInspection)
        case unavailable
    }

    let paths: AppPaths
    @ObservationIgnored weak var undoManager: UndoManager?
    @ObservationIgnored private let vault: SecretVault
    @ObservationIgnored private var savedSecrets: [String: String] = [:]
    @ObservationIgnored private var saveTask: Task<Void, Never>?

    init(paths: AppPaths = AppPaths()) {
        let vault = paths.vault
        var loaded = EnvConfig()
        var startupProblem: String?
        do {
            loaded = try Self.readConfig(at: paths.configFile)
        } catch {
            Self.setAside(paths.configFile)
            startupProblem = "Envy couldn't read its settings (\(error.localizedDescription)). The unreadable file was kept as config.unreadable.json."
        }
        var secrets: [String: String] = [:]
        var locked = false
        do {
            secrets = try vault.load()
            loaded.fillSecrets(secrets)
        } catch {
            locked = true
            startupProblem = "Envy can't read your secrets from the Keychain (\(error.localizedDescription)), so your terminals won't be updated until it can."
        }

        self.paths = paths
        self.vault = vault
        self.config = loaded
        savedSecrets = secrets
        secretsLocked = locked
        problem = startupProblem
        refreshInstallStatus()

        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.saveNow() }
        }
        // Brings the generated files up to date, e.g. after an app update changed the hook.
        if startupProblem == nil { saveNow() }

        Task {
            await detectEnvironment()
            if !isShellInstalled && !hasOnboarded && activeSheet == nil { activeSheet = .onboarding }
        }
    }

    // MARK: Saving

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            self?.saveNow()
        }
    }

    func saveNow() {
        saveTask?.cancel()
        saveTask = nil
        do {
            try writeConfig()
            guard !secretsLocked else { return }
            let secrets = config.secretValues()
            if secrets != savedSecrets {
                try vault.save(secrets)
                savedSecrets = secrets
            }
            try ShellOutput(directory: paths.shellDirectory).write(config)
            lastWrite = Date()
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func retrySecrets() {
        do {
            let secrets = try vault.load()
            savedSecrets = secrets
            secretsLocked = false
            config.fillSecrets(secrets)
            saveNow()
        } catch {
            problem = "Envy still can't read your secrets from the Keychain (\(error.localizedDescription))."
        }
    }

    private func writeConfig() throws {
        let url = paths.configFile
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(config.withoutSecretValues()).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private static func readConfig(at url: URL) throws -> EnvConfig {
        guard FileManager.default.fileExists(atPath: url.path) else { return EnvConfig() }
        return try JSONDecoder().decode(EnvConfig.self, from: Data(contentsOf: url))
    }

    private static func setAside(_ url: URL) {
        let aside = url.deletingLastPathComponent().appendingPathComponent("config.unreadable.json")
        try? FileManager.default.removeItem(at: aside)
        try? FileManager.default.moveItem(at: url, to: aside)
    }

    // MARK: Undoable changes

    /// Applies `change` as one undoable step named `actionName`.
    func perform(_ actionName: String, _ change: (inout EnvConfig) -> Void) {
        let before = config
        change(&config)
        guard config != before else { return }
        registerUndo(actionName, restoring: before)
    }

    private func registerUndo(_ actionName: String, restoring snapshot: EnvConfig) {
        guard let undoManager else { return }
        let current = config
        undoManager.registerUndo(withTarget: self) { store in
            MainActor.assumeIsolated {
                store.config = snapshot
                store.registerUndo(actionName, restoring: current)
            }
        }
        undoManager.setActionName(actionName)
    }

    // MARK: Lookup and bindings

    var selectedProjectID: UUID? {
        if case .project(let id)? = selection, config.project(id) != nil { return id }
        return nil
    }

    func project(_ id: UUID) -> Project? { config.project(id) }

    func variable(_ projectID: UUID, _ variableID: UUID) -> EnvVar? {
        config.project(projectID)?.variables.first { $0.id == variableID }
    }

    func projectBinding<Value>(_ id: UUID, _ keyPath: WritableKeyPath<Project, Value>, fallback: Value) -> Binding<Value> {
        Binding(
            get: { self.config.project(id)?[keyPath: keyPath] ?? fallback },
            set: { value in self.config.update(id) { $0[keyPath: keyPath] = value } }
        )
    }

    func variableBinding<Value>(_ projectID: UUID, _ variableID: UUID, _ keyPath: WritableKeyPath<EnvVar, Value>) -> Binding<Value> {
        Binding(
            get: { self.variable(projectID, variableID)?[keyPath: keyPath] ?? EnvVar(key: "")[keyPath: keyPath] },
            set: { value in
                self.config.update(projectID) { p in
                    if let i = p.variables.firstIndex(where: { $0.id == variableID }) { p.variables[i][keyPath: keyPath] = value }
                }
            }
        )
    }

    func subtitle(for p: Project) -> String {
        let count = p.variables.count == 1 ? "1 variable" : "\(p.variables.count) variables"
        if !p.isEnabled { return "\(count) · off" }
        if let first = p.folders.first {
            let more = p.folders.count > 1 ? " +\(p.folders.count - 1)" : ""
            return "\(count) · \(paths.abbreviate(first))\(more)"
        }
        return "\(count) · every terminal"
    }

    // MARK: Projects

    func uniqueProjectName(_ base: String) -> String {
        let taken = Set(config.scopes.map(\.name))
        let trimmed = base.trimmingCharacters(in: .whitespacesAndNewlines)
        let root = trimmed.isEmpty ? "New Project" : trimmed
        guard taken.contains(root) else { return root }
        var n = 2
        while taken.contains("\(root) \(n)") { n += 1 }
        return "\(root) \(n)"
    }

    func createProject(name: String, folder: String?, copyFrom source: UUID?, extra: [EnvVar]) {
        var project = Project(name: uniqueProjectName(name), folders: folder.map { [$0] } ?? [])
        if let source, let original = config.project(source) {
            project.merge(original.variables, policy: .replace)
        }
        project.merge(extra, policy: .replace)
        perform("New Project") { $0.projects.append(project) }
        selection = .project(project.id)
    }

    /// The copy starts switched off and without folders, so nothing changes in any terminal until you set it up.
    func duplicateProject(_ id: UUID) {
        guard let original = config.project(id), !original.isGlobal else { return }
        var copy = Project(name: uniqueProjectName(original.name + " copy"), isEnabled: false)
        copy.merge(original.variables, policy: .replace)
        perform("Duplicate Project") { cfg in
            let index = cfg.projects.firstIndex { $0.id == id }.map { $0 + 1 } ?? cfg.projects.endIndex
            cfg.projects.insert(copy, at: index)
        }
        selection = .project(copy.id)
    }

    func deleteProject(_ id: UUID) {
        guard id != Project.globalID else { return }
        perform("Delete Project") { $0.projects.removeAll { $0.id == id } }
        if selection == .project(id) { selection = .project(Project.globalID) }
    }

    func moveProjects(from source: IndexSet, to destination: Int) {
        perform("Move Project") { $0.projects.move(fromOffsets: source, toOffset: destination) }
    }

    func setProjectEnabled(_ id: UUID, _ enabled: Bool) {
        perform(enabled ? "Enable Project" : "Disable Project") { $0.update(id) { $0.isEnabled = enabled } }
    }

    func linkFolder(_ path: String, to id: UUID) {
        perform("Link Folder") { $0.update(id) { p in if !p.folders.contains(path) { p.folders.append(path) } } }
    }

    func unlinkFolder(_ path: String, from id: UUID) {
        perform("Unlink Folder") { $0.update(id) { $0.folders.removeAll { $0 == path } } }
    }

    // MARK: Variables

    @discardableResult
    func addVariables(_ vars: [EnvVar], to id: UUID, policy: MergePolicy, actionName: String) -> MergeResult {
        var result = MergeResult()
        perform(actionName) { $0.update(id) { result = $0.merge(vars, policy: policy) } }
        return result
    }

    func replaceVariable(_ variable: EnvVar, in id: UUID) {
        perform("Edit Variable") { cfg in
            cfg.update(id) { p in
                if let i = p.variables.firstIndex(where: { $0.id == variable.id }) { p.variables[i] = variable }
            }
        }
    }

    func updateVariables(_ ids: Set<UUID>, in id: UUID, actionName: String, _ change: (inout EnvVar) -> Void) {
        perform(actionName) { cfg in
            cfg.update(id) { p in
                for i in p.variables.indices where ids.contains(p.variables[i].id) { change(&p.variables[i]) }
            }
        }
    }

    func deleteVariables(_ ids: Set<UUID>, in id: UUID) {
        perform(ids.count == 1 ? "Delete Variable" : "Delete Variables") { $0.update(id) { $0.variables.removeAll { ids.contains($0.id) } } }
    }

    func copyVariables(_ ids: Set<UUID>, from source: UUID, to target: UUID, move: Bool) {
        guard let original = config.project(source) else { return }
        let vars = original.variables.filter { ids.contains($0.id) }
        perform(move ? "Move Variables" : "Copy Variables") { cfg in
            cfg.update(target) { _ = $0.merge(vars, policy: .replace) }
            if move { cfg.update(source) { $0.variables.removeAll { ids.contains($0.id) } } }
        }
    }

    func dotEnvText(_ ids: Set<UUID>?, in id: UUID) -> String {
        guard let p = config.project(id) else { return "" }
        return DotEnv.serialize(p.variables.filter { ids?.contains($0.id) ?? true })
    }

    func exportDotEnv(_ id: UUID) {
        guard let p = config.project(id) else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = ".env"
        panel.showsHiddenFiles = true
        panel.canCreateDirectories = true
        panel.message = "Exports the enabled variables of “\(p.name)”, secrets included, as plain text."
        if let folder = p.folders.first { panel.directoryURL = URL(fileURLWithPath: folder) }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let text = DotEnv.serialize(p.variables.filter(\.isEnabled), header: "Exported from Envy: \(p.name)")
        do {
            try text.write(to: url, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        } catch {
            NSAlert(error: error).runModal()
        }
    }

    // MARK: Shell integration

    func detectEnvironment() async {
        let home = paths.home
        let shell = environment.shellPath
        environment = await Task.detached { ShellEnvironment.detect(home: home, shellPath: shell) }.value
        refreshInstallStatus()
    }

    /// Opens a fresh terminal shell in the background to see whether it loads Envy and which agents it can run.
    func inspectShell() async {
        guard inspection != .checking else { return }
        inspection = .checking
        let home = paths.home
        let shell = environment.supportedShell
        let result = await Task.detached { ShellProbe.inspect(shell: shell, home: home) }.value
        inspection = result.map(Inspection.done) ?? .unavailable
    }

    /// Where `target` lives; zsh's file follows `ZDOTDIR`.
    func rcURL(_ target: RcTarget) -> URL {
        if target == .zshrc, let zdotdir = environment.zdotdir {
            return URL(fileURLWithPath: zdotdir).appendingPathComponent(target.rawValue)
        }
        return paths.home.appendingPathComponent(target.rawValue)
    }

    func displayPath(_ url: URL) -> String { paths.abbreviate(url.path) }

    /// The startup files that matter for the login shell; zsh for anything Envy doesn't support.
    var recommendedTargets: [RcTarget] {
        environment.kind == .bash ? [.bashProfile, .bashrc] : [.zshrc]
    }

    var otherTargets: [RcTarget] { RcTarget.allCases.filter { !recommendedTargets.contains($0) } }

    func refreshInstallStatus() {
        installed = Dictionary(uniqueKeysWithValues: RcTarget.allCases.map { ($0, RcIntegration.isInstalled(in: rcURL($0))) })
    }

    var isShellInstalled: Bool { installed.values.contains(true) }

    func isConnected(_ targets: [RcTarget]) -> Bool { targets.allSatisfy { installed[$0] ?? false } }

    func rcFileExists(_ target: RcTarget) -> Bool {
        FileManager.default.fileExists(atPath: rcURL(target).path)
    }

    /// The real file `target` writes to when it's a symlink (dotfile managers), else nil.
    func symlinkDestination(_ target: RcTarget) -> URL? {
        let url = rcURL(target)
        // Checks the file itself: comparing resolved paths would also flag /private/tmp-style folder aliases.
        guard (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true else { return nil }
        return url.resolvingSymlinksInPath()
    }

    func connect(_ targets: [RcTarget]) throws {
        saveNow()
        for target in targets { try RcIntegration.install(in: rcURL(target)) }
        refreshInstallStatus()
    }

    func uninstall(_ target: RcTarget) throws {
        try RcIntegration.uninstall(from: rcURL(target))
        refreshInstallStatus()
    }

    // MARK: Setup

    var hasOnboarded: Bool { FileManager.default.fileExists(atPath: paths.onboardedMarker.path) }

    func finishOnboarding() {
        let marker = paths.onboardedMarker
        try? FileManager.default.createDirectory(at: marker.deletingLastPathComponent(), withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: marker.path, contents: nil)
    }

    /// Every startup file a zsh or bash user might export variables from.
    var startupFiles: [URL] {
        let zdot = environment.zdotdir.map { URL(fileURLWithPath: $0) } ?? paths.home
        return [".zshenv", ".zprofile", ".zshrc"].map { zdot.appendingPathComponent($0) }
            + [".profile", ".bash_profile", ".bashrc"].map { paths.home.appendingPathComponent($0) }
    }

    /// Plain-text exports in the startup files that Global doesn't already have; the last one per name wins.
    func plaintextExports() -> [RcExport] {
        var latest: [String: RcExport] = [:]
        var order: [String] = []
        for export in RcScanner.exports(inFiles: startupFiles) {
            if latest[export.key] == nil { order.append(export.key) }
            latest[export.key] = export
        }
        return order.compactMap { latest[$0] }.filter { export in
            !config.global.variables.contains { $0.key == export.key && $0.value == export.value }
        }
    }

    func isLinked(_ folder: URL) -> Bool {
        config.projects.contains { $0.folders.contains(folder.path) }
    }

    /// One folder-linked project per found folder.
    @discardableResult
    func importProjects(_ found: [FoundProject]) -> Int {
        let fresh = found.filter { !isLinked($0.folder) }
        perform(fresh.count == 1 ? "Import Project" : "Import Projects") { cfg in
            for item in fresh {
                var project = Project(name: uniqueName(item.name, in: cfg), folders: [item.folder.path])
                project.merge(item.variables, policy: .replace)
                cfg.projects.append(project)
            }
        }
        return fresh.count
    }

    private func uniqueName(_ base: String, in cfg: EnvConfig) -> String {
        let taken = Set(cfg.scopes.map(\.name))
        guard taken.contains(base) else { return base }
        var n = 2
        while taken.contains("\(base) \(n)") { n += 1 }
        return "\(base) \(n)"
    }
}
