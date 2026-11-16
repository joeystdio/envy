using System.Text;

namespace Envy.Core.Tests;

public class StartupFilesTests : TempDirectoryTest
{
    const string Line = "[ -r '/c/x/init.sh' ] && . '/c/x/init.sh'";

    [Fact]
    public void InstallAppendsOnceAndUninstallRestoresTheFile()
    {
        var rc = Write("export EDITOR=vim\nalias ll='ls -l'\n", ".bashrc");
        var original = File.ReadAllText(rc);

        StartupFiles.Install(rc, Line);
        StartupFiles.Install(rc, Line);
        var installed = File.ReadAllText(rc);
        Assert.StartsWith(original, installed);
        Assert.Equal(2, installed.Split(StartupFiles.BeginMarker).Length);
        Assert.True(StartupFiles.IsInstalled(rc));
        Assert.Equal(original, File.ReadAllText(rc + ".envy-backup"));
        Assert.DoesNotContain("\r", installed);

        StartupFiles.Uninstall(rc);
        Assert.Equal(original, File.ReadAllText(rc));
        Assert.False(StartupFiles.IsInstalled(rc));
    }

    [Fact]
    public void InstallCreatesMissingFileAndFolder()
    {
        var profile = Path.Combine(Root, "Documents", "PowerShell", "profile.ps1");
        StartupFiles.Install(profile, StartupFiles.PowerShellLine("'C:\\x\\init.ps1'"), preferCrLf: true);
        var bytes = File.ReadAllBytes(profile);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        Assert.Equal(StartupFiles.Snippet(StartupFiles.PowerShellLine("'C:\\x\\init.ps1'"), "\r\n") + "\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void KeepsUtf16ProfilesAndCrLfLineEndings()
    {
        // Windows PowerShell 5.1 once wrote profiles as UTF-16; appending UTF-8 to one would corrupt it.
        var profile = Path.Combine(Root, "profile.ps1");
        File.WriteAllText(profile, "Set-Alias ll Get-ChildItem\r\n# café\r\n", Encoding.Unicode);
        StartupFiles.Install(profile, StartupFiles.PowerShellLine("'C:\\x\\init.ps1'"), preferCrLf: true);
        var bytes = File.ReadAllBytes(profile);
        Assert.Equal([0xFF, 0xFE], bytes.Take(2));
        var text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.StartsWith("Set-Alias ll Get-ChildItem\r\n# café\r\n\r\n# >>> Envy >>>\r\n", text);
        StartupFiles.Uninstall(profile);
        Assert.Equal("Set-Alias ll Get-ChildItem\r\n# café\r\n", Encoding.Unicode.GetString(File.ReadAllBytes(profile)[2..]));
    }

    [Fact]
    public void InstallWritesThroughSymlinks()
    {
        var real = Write("export A=1", "dotfiles/bashrc");
        var link = Path.Combine(Root, ".bashrc");
        try { File.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert.Skip("Creating symlinks needs Developer Mode here."); }

        StartupFiles.Install(link, Line);
        Assert.Equal(real, new FileInfo(link).LinkTarget);
        Assert.StartsWith("export A=1\n\n" + StartupFiles.BeginMarker, File.ReadAllText(real));
    }

    [Fact]
    public void RemovesSecretLinesAndKeepsABackup()
    {
        var rc = Write("export A=1\nexport TOKEN=secret\nexport B=2\n", ".bashrc");
        StartupFiles.RemoveLines(rc, [2]);
        Assert.Equal("export A=1\nexport B=2\n", File.ReadAllText(rc));
        Assert.Contains("TOKEN", File.ReadAllText(rc + ".envy-backup"));
    }
}

public class AutoRunTests : TempDirectoryTest
{
    const string Init = @"C:\Users\me\AppData\Local\Envy\shell\init.cmd";

    [Fact]
    public void ChainsOntoAnExistingValueAndTakesOnlyItsOwnPartOut()
    {
        var store = new FileAutoRunStore(Path.Combine(Root, "AutoRun.txt"));
        var backup = Path.Combine(Root, "AutoRun.envy-backup");
        store.Write(@"""C:\clink\clink.bat"" inject --autorun");

        AutoRun.Install(store, Init, backup);
        AutoRun.Install(store, Init, backup);
        Assert.Equal(@"""C:\clink\clink.bat"" inject --autorun& if exist ""C:\Users\me\AppData\Local\Envy\shell\init.cmd"" call ""C:\Users\me\AppData\Local\Envy\shell\init.cmd""", store.Read());
        Assert.True(AutoRun.IsInstalled(store));
        Assert.Equal(@"""C:\clink\clink.bat"" inject --autorun", File.ReadAllText(backup));

        AutoRun.Uninstall(store, backup);
        Assert.Equal(@"""C:\clink\clink.bat"" inject --autorun", store.Read());
        Assert.False(AutoRun.IsInstalled(store));
    }

    [Fact]
    public void RemovesTheValueWhenOnlyEnvyWasThere()
    {
        var store = new FileAutoRunStore(Path.Combine(Root, "AutoRun.txt"));
        AutoRun.Install(store, Init, Path.Combine(Root, "b"));
        Assert.Equal(AutoRun.Segment(Init), store.Read());
        AutoRun.Uninstall(store, Path.Combine(Root, "b"));
        Assert.Null(store.Read());
    }

    [Fact]
    public void KeepsWhatComesAfterEnvyAndMovesEnvyLastWhenThePathChanges()
    {
        Assert.Equal("a & b", AutoRun.Removed($"a & {AutoRun.Segment(Init)} & b"));
        Assert.Equal("b", AutoRun.Removed($"{AutoRun.Segment(Init)} & b"));
        var moved = AutoRun.Added($"{AutoRun.Segment(@"D:\old\Envy\shell\init.cmd")} & b", Init);
        Assert.Equal($"b& {AutoRun.Segment(Init)}", moved);
    }
}

public class PrivateFilesTests : TempDirectoryTest
{
    [Fact]
    public void ShellFilesAreReadableOnlyByYou()
    {
        var dir = Path.Combine(Root, "shell");
        new ShellOutput(dir).Write(GlobalOnly(new EnvVar("TOKEN", "secret", isSecret: true)));
        Assert.True(PrivateFiles.IsPrivate(dir));
        foreach (var file in Directory.GetFiles(dir)) Assert.True(PrivateFiles.IsPrivate(file), file);
    }

    [Fact]
    public void UnchangedVariablesDontTriggerReload()
    {
        var dir = Path.Combine(Root, "shell");
        var config = GlobalOnly(new EnvVar("A", "1"));
        Assert.True(new ShellOutput(dir).Write(config));
        Assert.False(new ShellOutput(dir).Write(config));
        Assert.True(new ShellOutput(dir).Write(GlobalOnly(new EnvVar("A", "2"))));
    }
}
