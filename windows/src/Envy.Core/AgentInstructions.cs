namespace Envy.Core;

/// <summary>Where a group of coding agents reads instructions that Envy can own outright, so it never edits your files.</summary>
/// <param name="Agents">The agents this file reaches, with the command that runs each, to find them on a shell's PATH.</param>
/// <param name="ConfigDirectories">Folders the agents create when they're used; finding one means the agent is set up here.</param>
public sealed record AgentTarget(string Id, string Name, (string Command, string Name)[] Agents, AgentTarget.Kinds Kind, string File, string[] ConfigDirectories)
{
    public enum Kinds
    {
        /// <summary>Loaded in every session (Claude Code's ~/.claude/rules).</summary>
        Rule,
        /// <summary>A skill (SKILL.md with a name and description), loaded when the task calls for it.</summary>
        Skill,
    }
}

public enum AgentInstructionStatus
{
    NotInstalled,
    Installed,
    /// <summary>Written by an older Envy; installing again brings it up to date.</summary>
    Outdated,
    /// <summary>A file of the same name that Envy didn't write; Envy leaves it alone.</summary>
    Foreign,
}

/// <summary>
/// Writes and removes Envy's instructions for coding agents, so they use the envy command instead of .env files, setx
/// and variables in shell profiles. Claude Code gets a rule, which it reads in every session. Codex, Gemini CLI, Cursor,
/// Copilot CLI, opencode, Amp and Goose all read skills from ~/.agents/skills, so one skill there reaches them all.
/// </summary>
public static class AgentInstructions
{
    /// <summary><paramref name="claudeConfigDir"/> is CLAUDE_CONFIG_DIR when it's set.</summary>
    public static List<AgentTarget> Targets(string home, string? claudeConfigDir = null)
    {
        var claude = string.IsNullOrEmpty(claudeConfigDir)
            ? Path.Combine(home, ".claude")
            : Path.GetFullPath(PathStyle.FromPosix(PathStyle.ExpandHome(claudeConfigDir, home)));
        var config = Path.Combine(home, ".config");
        return
        [
            new("claude", "Claude Code", [("claude", "Claude Code")], AgentTarget.Kinds.Rule,
                Path.Combine(claude, "rules", "envy.md"), [claude]),
            new("agents", "Other coding agents",
                [("codex", "Codex"), ("gemini", "Gemini CLI"), ("cursor-agent", "Cursor CLI"), ("copilot", "Copilot CLI"),
                 ("opencode", "opencode"), ("amp", "Amp"), ("goose", "Goose")],
                AgentTarget.Kinds.Skill,
                Path.Combine(home, ".agents", "skills", "envy", "SKILL.md"),
                [Path.Combine(home, ".agents"), Path.Combine(home, ".codex"), Path.Combine(home, ".gemini"), Path.Combine(home, ".cursor"),
                 Path.Combine(home, ".copilot"), Path.Combine(config, "opencode"), Path.Combine(config, "amp"), Path.Combine(config, "goose")]),
        ];
    }

    // Content

    internal const string Marker = "<!-- Written by Envy, which keeps it up to date. Remove it in Envy › Shell Integration › Coding agents; local changes are overwritten. -->";

    /// <summary>The skill's description decides when an agent loads it, so it names the situations.</summary>
    internal const string Description = "Environment variables and secrets on this PC are managed by Envy. Use this whenever a task needs an environment variable, API key, token, password or connection string; when a command fails because one is missing; or before creating a .env file, running setx, or adding a variable to a PowerShell profile or ~/.bashrc.";

    public static string Content(AgentTarget.Kinds kind) => kind switch
    {
        AgentTarget.Kinds.Rule => $"{Marker}\n\n# Environment variables and secrets: Envy\n\n{Guidance}\n",
        _ => $"---\nname: envy\ndescription: {Description}\n---\n\n{Marker}\n\n# Envy\n\n{Guidance}\n",
    };

    /// <summary>Short on purpose: envy help is the full reference, so this rarely changes.</summary>
    internal const string Guidance = """
        This PC manages environment variables and secrets with Envy, not with `.env` files, `setx`, or variables set in PowerShell profiles or `~/.bashrc`. Global variables load in every terminal (PowerShell, Git Bash and Command Prompt); a project's variables load inside the folders linked to it. Secrets live in Windows Credential Manager.

        - Run `envy help` before working with environment variables, and use the `envy` command (`$env:LOCALAPPDATA\Envy\shell\bin\envy.exe` in PowerShell, `"$LOCALAPPDATA/Envy/shell/bin/envy.exe"` in Git Bash, if it isn't on PATH).
        - Missing variable: `envy status` shows what loads here, `envy list` what's defined. Secret values are never shown.
        - Plain settings: `envy set NAME=value` goes to the project linked to this folder; add `--project NAME` or `--global` for another.
        - Secrets: never ask the user to paste one into the chat, and never write one into a file. Run `envy add NAME --secret`: Envy opens for the user to paste it, so tell them it's waiting.
        - Found a `.env` file: offer `envy import FILE`, which moves its values into Envy without showing them.
        - Your environment was fixed when you started. After a change, run commands as `envy exec -- <command>`.
        - Never print secret values: no `echo` of secret variables, no unfiltered `env`, `printenv`, `set` or `Get-ChildItem Env:`, and don't read the files in `%LOCALAPPDATA%\Envy\shell`.
        - Ask the user before removing variables or switching anything off.
        - If `envy` says a sandbox blocks it, run the same command again with permission to run outside the sandbox, or ask the user to run it.
        """;

    // Install and remove

    static string? Read(string file)
    {
        try { return File.ReadAllText(file).Replace("\r\n", "\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static AgentInstructionStatus Status(AgentTarget target)
    {
        if (Read(target.File) is not { } text) return AgentInstructionStatus.NotInstalled;
        if (!text.Contains(Marker)) return AgentInstructionStatus.Foreign;
        return text == Content(target.Kind) ? AgentInstructionStatus.Installed : AgentInstructionStatus.Outdated;
    }

    /// <summary>Writes the file, or brings it up to date. Refuses to replace a file Envy didn't write.</summary>
    public static void Install(AgentTarget target)
    {
        if (Status(target) == AgentInstructionStatus.Foreign)
            throw new IOException($"{target.File} already exists and wasn't written by Envy, so Envy left it alone.");
        Directory.CreateDirectory(Path.GetDirectoryName(target.File)!);
        var temp = target.File + ".envy-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, Content(target.Kind), PrivateFiles.Utf8);
        File.Move(temp, target.File, overwrite: true);
    }

    /// <summary>Removes the file if Envy wrote it, and the skill's own folder with it when that's empty.</summary>
    public static void Uninstall(AgentTarget target)
    {
        if (Read(target.File) is not { } text || !text.Contains(Marker)) return;
        File.Delete(target.File);
        if (target.Kind == AgentTarget.Kinds.Skill)
        {
            var folder = Path.GetDirectoryName(target.File)!;
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
    }
}
