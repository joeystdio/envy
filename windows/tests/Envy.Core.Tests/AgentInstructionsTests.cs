using System.Text.RegularExpressions;

namespace Envy.Core.Tests;

/// <summary>The files Envy writes for coding agents, and only those.</summary>
public class AgentInstructionsTests : TempDirectoryTest
{
    AgentTarget Target(string id, string? claudeConfigDir = null) => AgentInstructions.Targets(Root, claudeConfigDir).Single(t => t.Id == id);

    [Fact]
    public void WritesFilesEnvyOwns()
    {
        AgentTarget claude = Target("claude"), agents = Target("agents");
        Assert.Equal(Path.Combine(Root, ".claude", "rules", "envy.md"), claude.File);
        Assert.Equal(Path.Combine(Root, ".agents", "skills", "envy", "SKILL.md"), agents.File);

        foreach (var t in new[] { claude, agents })
        {
            Assert.Equal(AgentInstructionStatus.NotInstalled, AgentInstructions.Status(t));
            AgentInstructions.Install(t);
            Assert.Equal(AgentInstructionStatus.Installed, AgentInstructions.Status(t));
        }
        Assert.StartsWith("---\nname: envy\ndescription: Environment variables", File.ReadAllText(agents.File));
        Assert.StartsWith("<!-- Written by Envy", File.ReadAllText(claude.File));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(claude.File)!, ".*"));
    }

    [Fact]
    public void UpdatesWhatAnOlderEnvyWrote()
    {
        var agents = Target("agents");
        AgentInstructions.Install(agents);
        File.WriteAllText(agents.File, $"---\nname: envy\n---\n{AgentInstructions.Marker}\nOld advice.\n");
        Assert.Equal(AgentInstructionStatus.Outdated, AgentInstructions.Status(agents));
        AgentInstructions.Install(agents);
        Assert.Equal(AgentInstructionStatus.Installed, AgentInstructions.Status(agents));
    }

    [Fact]
    public void CheckedOutWithWindowsLineEndingsStillCountsAsCurrent()
    {
        var claude = Target("claude");
        AgentInstructions.Install(claude);
        File.WriteAllText(claude.File, File.ReadAllText(claude.File).ReplaceLineEndings("\r\n"));
        Assert.Equal(AgentInstructionStatus.Installed, AgentInstructions.Status(claude));
    }

    [Fact]
    public void RemovesOnlyItsOwnFiles()
    {
        AgentTarget claude = Target("claude"), agents = Target("agents");
        var mine = Write("Use tabs.\n", ".claude/rules/style.md");
        AgentInstructions.Install(claude);
        AgentInstructions.Install(agents);

        AgentInstructions.Uninstall(claude);
        AgentInstructions.Uninstall(agents);
        Assert.False(File.Exists(claude.File));
        Assert.Equal("Use tabs.\n", File.ReadAllText(mine));
        Assert.False(Directory.Exists(Path.GetDirectoryName(agents.File)));
        Assert.True(Directory.Exists(Path.Combine(Root, ".agents", "skills")));
    }

    [Fact]
    public void LeavesSomeoneElsesFileAlone()
    {
        var agents = Target("agents");
        Write("my own envy skill\n", ".agents/skills/envy/SKILL.md");
        Assert.Equal(AgentInstructionStatus.Foreign, AgentInstructions.Status(agents));
        Assert.Throws<IOException>(() => AgentInstructions.Install(agents));
        AgentInstructions.Uninstall(agents);
        Assert.Equal("my own envy skill\n", File.ReadAllText(agents.File));
    }

    [Fact]
    public void HonoursClaudesConfigFolder()
    {
        WindowsOnly.Check();
        Assert.Equal(@"D:\cc\rules\envy.md", Target("claude", @"D:\cc").File);
        Assert.Equal(Path.Combine(Root, "cc", "rules", "envy.md"), Target("claude", "~/cc").File);
    }

    [Fact]
    public void InstructionsMentionOnlyRealCommands()
    {
        // Every envy … the instructions suggest must parse.
        var text = AgentInstructions.Content(AgentTarget.Kinds.Rule) + AgentInstructions.Content(AgentTarget.Kinds.Skill);
        var matches = Regex.Matches(text, "`envy ([^`]+)`");
        Assert.True(matches.Count > 5);
        foreach (Match match in matches)
        {
            var line = match.Groups[1].Value;
            if (line.StartsWith("exec")) continue;
            var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var error = Record.Exception(() => EnvyCommand.Parse(args, @"C:\code"));
            Assert.True(error is null, $"{line}: {error?.Message}");
        }
        Assert.DoesNotContain("  ", AgentInstructions.Guidance.Replace("\n\n", "\n"));
    }

    [Fact]
    public void UninstallingEnvyRemovesTheInstructions()
    {
        var store = new EnvyStore(new AppPaths(Root));
        store.InstallInstructions(store.AgentTargets);
        Assert.All(store.AgentTargets, t => Assert.True(File.Exists(t.File)));
        store.Uninstall();
        Assert.All(store.AgentTargets, t => Assert.False(File.Exists(t.File)));
    }

    [Fact]
    public void OffersOnceWhenAnAgentIsHereAndNothingIsInstalled()
    {
        var store = new EnvyStore(new AppPaths(Root));
        Folder(".codex");
        Assert.False(store.ShouldOfferAgentInstructions); // Not set up yet: the Setup Assistant asks instead.
        store.FinishOnboarding();
        Assert.True(store.ShouldOfferAgentInstructions);
        store.AcceptAgentOffer();
        Assert.False(store.ShouldOfferAgentInstructions);
        Assert.True(File.Exists(Target("agents").File));
        Assert.False(File.Exists(Target("claude").File)); // Claude Code isn't here.
    }
}
