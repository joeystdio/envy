namespace Envy.Core;

/// <summary>
/// Where Envy keeps its files. Setting ENVY_HOME points everything (settings, secrets, shell files, startup files and
/// the Command Prompt AutoRun value) at another folder, to try the app without touching your real setup or Credential
/// Manager. The folder then mirrors a user profile: AppData\Roaming, AppData\Local, Documents, .bashrc.
/// </summary>
public sealed class AppPaths
{
    public string Home { get; }
    public bool IsSandbox { get; }
    public string RoamingAppData { get; }
    public string LocalAppData { get; }
    public string Documents { get; }

    public AppPaths(string? envyHome = null)
    {
        envyHome ??= Environment.GetEnvironmentVariable("ENVY_HOME");
        if (!string.IsNullOrEmpty(envyHome))
        {
            Home = Path.GetFullPath(PathStyle.ExpandHome(envyHome, null));
            IsSandbox = true;
            RoamingAppData = Path.Combine(Home, "AppData", "Roaming");
            LocalAppData = Path.Combine(Home, "AppData", "Local");
            Documents = Path.Combine(Home, "Documents");
        }
        else
        {
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Follows OneDrive's folder redirection, as PowerShell does for $PROFILE.
            Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    /// <summary>Settings without secret values: %APPDATA%\Envy.</summary>
    public string SettingsDirectory => Path.Combine(RoamingAppData, "Envy");
    public string ConfigFile => Path.Combine(SettingsDirectory, "config.json");
    /// <summary>Exists once the Setup Assistant has been finished or postponed.</summary>
    public string OnboardedMarker => Path.Combine(SettingsDirectory, ".onboarded");
    /// <summary>Exists once you've answered the offer to teach coding agents about Envy.</summary>
    public string AgentOfferMarker => Path.Combine(SettingsDirectory, ".agents-offered");
    /// <summary>A copy of the AutoRun value before Envy last changed it.</summary>
    public string AutoRunBackup => Path.Combine(SettingsDirectory, "AutoRun.envy-backup");

    /// <summary>
    /// The generated shell files hold secret values, so they live in %LOCALAPPDATA% (which never roams to a server)
    /// in a folder only you can read.
    /// </summary>
    public string ShellDirectory => Path.Combine(LocalAppData, "Envy", "shell");

    /// <summary>The envy command (envy.exe and envy.json), which the shell hooks put on PATH.</summary>
    public string CommandDirectory => Path.Combine(ShellDirectory, "bin");

    /// <summary>Where the app's own copy of the envy command lives: cli\ next to Envy.exe.</summary>
    public static string? BundledCommandDirectory(string? appPath)
    {
        if (appPath is null || Path.GetDirectoryName(appPath) is not { } folder) return null;
        var cli = Path.Combine(folder, "cli");
        return File.Exists(Path.Combine(cli, "envy.exe")) ? cli : null;
    }

    public ISecretVault Vault => IsSandbox || !OperatingSystem.IsWindows()
        ? new FileSecretVault(Path.Combine(SettingsDirectory, "secrets.json"))
        : new CredentialManagerVault();

    public IAutoRunStore AutoRunStore => IsSandbox || !OperatingSystem.IsWindows()
        ? new FileAutoRunStore(Path.Combine(Home, "AutoRun.txt"))
        : new RegistryAutoRunStore();

    public IUserEnvironment UserEnvironment => IsSandbox || !OperatingSystem.IsWindows()
        ? new FileUserEnvironment(Path.Combine(Home, "UserEnvironment.json"))
        : new RegistryUserEnvironment();

    /// <summary>Where dotnet user-secrets keeps each project's secrets.json (under Microsoft\UserSecrets).</summary>
    public string UserSecretsRoot => RoamingAppData;

    public string BashRc => Path.Combine(Home, ".bashrc");
    public string BashProfile => Path.Combine(Home, ".bash_profile");
    public string BashLogin => Path.Combine(Home, ".bash_login");
    public string Profile => Path.Combine(Home, ".profile");

    /// <summary>The all-hosts profile, so the console, Windows Terminal and VS Code's PowerShell all load Envy.</summary>
    public string PowerShell7Profile => Path.Combine(Documents, "PowerShell", "profile.ps1");
    public string WindowsPowerShellProfile => Path.Combine(Documents, "WindowsPowerShell", "profile.ps1");

    /// <summary>Every PowerShell profile a variable might be set in.</summary>
    public IEnumerable<string> AllPowerShellProfiles =>
        from folder in new[] { "PowerShell", "WindowsPowerShell" }
        from name in new[] { "profile.ps1", "Microsoft.PowerShell_profile.ps1", "Microsoft.VSCode_profile.ps1" }
        select Path.Combine(Documents, folder, name);

    public IEnumerable<string> AllBashFiles => [Profile, BashProfile, BashLogin, BashRc];

    /// <summary>Common places to keep code, offered as one-click roots when looking for projects.</summary>
    public IEnumerable<string> SuggestedProjectRoots =>
        new[] { Path.Combine("source", "repos"), "Developer", "Projects", "projects", "code", "Code", "src", "dev", "repos", "git", "GitHub", "workspace" }
            .Select(n => Path.Combine(Home, n))
            .Where(Directory.Exists)
            .DistinctBy(p => p.ToLowerInvariant());

    /// <summary><paramref name="path"/> with this home shown as ~.</summary>
    public string Abbreviate(string path)
    {
        if (string.Equals(path, Home, StringComparison.OrdinalIgnoreCase)) return "~";
        foreach (var sep in new[] { '\\', '/' })
            if (path.StartsWith(Home + sep, StringComparison.OrdinalIgnoreCase)) return "~" + sep + path[(Home.Length + 1)..];
        return path;
    }
}
