import AppKit
import EnvyCore

/// The `envy` command, run as `Envy --cli …` by the launcher Envy writes to `~/.config/envy/bin/envy`.
/// Everything that reads or changes settings goes to the running app, which it starts when needed.
enum CommandLineTool {
    static func run(_ arguments: [String]) -> Int32 {
        let paths = AppPaths()
        switch arguments.first {
        case "exec":
            return exec(Array(arguments.dropFirst()), paths: paths)
        case "reload":
            print("""
            A program can't change the environment of the shell that started it. In a terminal connected to Envy, \
            envy reload is built in; anywhere else, run commands with: envy exec -- <command>
            """)
            return 0
        case "help", "--help", "-h":
            print(EnvyCommand.helpText)
            return 0
        case "--version", "version":
            print("envy \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev")")
            return 0
        default:
            return send(arguments, paths: paths)
        }
    }

    // MARK: Talking to the app

    private static func send(_ arguments: [String], paths: AppPaths) -> Int32 {
        let env = ProcessInfo.processInfo.environment
        let request = CommandRequest(
            arguments: arguments,
            directories: workingDirectories(env),
            loaded: (env["__ENVY_VARS"] ?? "").split(separator: " ").map(String.init)
        )
        let socket = paths.shellDirectory.appendingPathComponent(CommandSocket.fileName).path

        var response: CommandResponse
        do {
            response = try CommandSocket.send(request, to: socket)
        } catch CommandSocket.Failure.notRunning {
            guard launchApp(paths: paths) else {
                return offline(request, paths: paths, reason: "Couldn't start Envy. Open it once from your Applications folder.")
            }
            let deadline = Date().addingTimeInterval(20)
            while true {
                do {
                    response = try CommandSocket.send(request, to: socket)
                    break
                } catch CommandSocket.Failure.notRunning where Date() < deadline {
                    usleep(200_000)
                } catch {
                    return fail("Envy started but didn't answer. If it's asking for Keychain access, allow it and try again.")
                }
            }
        } catch CommandSocket.Failure.blocked {
            return offline(request, paths: paths, reason: """
            This process isn't allowed to reach Envy, probably because a coding agent's sandbox blocks it. \
            Run the same command again with permission to run outside the sandbox, or ask the user to run it.
            """)
        } catch {
            return fail("Couldn't reach Envy (\(error)).")
        }

        if response.activate { NSRunningApplication(processIdentifier: response.processID)?.activate() }
        if !response.output.isEmpty { print(response.output) }
        if let error = response.error { FileHandle.standardError.write(Data("envy: \(error)\n".utf8)) }
        return response.exitCode
    }

    /// When the app can't be reached, reads still work from the settings file, which holds no secret values;
    /// changes can't, and get `reason` instead.
    private static func offline(_ request: CommandRequest, paths: AppPaths, reason: String) -> Int32 {
        let command: EnvyCommand
        do {
            command = try EnvyCommand.parse(request.arguments, cwd: request.context.directories.first ?? paths.home.path)
        } catch {
            FileHandle.standardError.write(Data("envy: \(error.localizedDescription)\n".utf8))
            return 2
        }
        guard command.isReadOnly,
              let data = try? Data(contentsOf: paths.configFile),
              var config = try? JSONDecoder().decode(EnvConfig.self, from: data)
        else { return fail(reason) }
        // The file leaves secret values out; a placeholder lists them as secrets rather than as empty.
        func placeholder(_ vars: inout [EnvVar]) {
            for i in vars.indices where vars[i].isSecret { vars[i].value = "•" }
        }
        placeholder(&config.global.variables)
        for i in config.projects.indices { placeholder(&config.projects[i].variables) }
        do {
            print(try command.run(on: &config, context: request.commandContext).output)
        } catch {
            return fail(error.localizedDescription)
        }
        FileHandle.standardError.write(Data("envy: read from Envy's settings file, because Envy itself can't be reached from here.\n".utf8))
        return 0
    }

    /// Starts this app in the background, hidden. A sandbox (`ENVY_HOME`) gets its own instance.
    private static func launchApp(paths: AppPaths) -> Bool {
        let bundle = Bundle.main.bundleURL
        guard bundle.pathExtension == "app" else { return false }
        var args = ["-g", "-j"]
        if paths.isSandbox { args += ["-n", "--env", "ENVY_HOME=\(paths.home.path)"] }
        let open = Process()
        open.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        open.arguments = args + [bundle.path]
        do { try open.run() } catch { return false }
        open.waitUntilExit()
        return open.terminationStatus == 0
    }

    /// `$PWD` (which keeps symlinked folder names) when it's really where we are, then the physical path.
    private static func workingDirectories(_ env: [String: String]) -> [String] {
        let physical = FileManager.default.currentDirectoryPath
        var out: [String] = []
        if let pwd = env["PWD"], pwd.hasPrefix("/"),
           ShellScript.realPath(pwd) == ShellScript.realPath(physical) {
            out.append(pwd)
        }
        if !out.contains(physical) { out.append(physical) }
        return out
    }

    // MARK: exec

    /// Runs a command with what Envy loads in this folder right now, by letting the shell hook apply it in a
    /// throwaway bash that then becomes the command. Values never pass through this process's output.
    private static func exec(_ arguments: [String], paths: AppPaths) -> Int32 {
        var command = arguments
        if command.first == "--" { command.removeFirst() }
        guard !command.isEmpty else { return fail("Usage: envy exec -- COMMAND [ARGS...]") }
        let dir = paths.shellDirectory.path
        guard FileManager.default.isReadableFile(atPath: dir + "/init.sh") else {
            return fail("Envy hasn't written its shell files yet. Open Envy once, then try again.")
        }
        if paths.isSandbox { setenv("ENVY_DIR", dir, 1) }
        let script = #"""
        __envy_dir="$1"; shift
        . "$__envy_dir/init.sh" >/dev/null 2>&1 || { echo "envy: couldn't load $__envy_dir/init.sh" >&2; exit 127; }
        unset -f envy __envy_hook __envy_set __envy_revert __envy_match __envy_apply 2>/dev/null
        exec "$@"
        """#
        let argv = ["bash", "-c", script, "envy-exec", dir] + command
        var cArgs = argv.map { strdup($0) } + [nil]
        execv("/bin/bash", &cArgs)
        return fail("Couldn't run /bin/bash (\(String(cString: strerror(errno)))).")
    }

    private static func fail(_ message: String) -> Int32 {
        FileHandle.standardError.write(Data("envy: \(message)\n".utf8))
        return 1
    }
}
