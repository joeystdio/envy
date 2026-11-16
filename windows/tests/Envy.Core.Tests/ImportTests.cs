namespace Envy.Core.Tests;

public class AppSettingsTests : TempDirectoryTest
{
    [Fact]
    public void FlattensLikeDotNetEnvironmentVariables()
    {
        var json = """
            {
              // .NET allows comments and trailing commas
              "Logging": { "LogLevel": { "Default": "Information" } },
              "AllowedHosts": "*",
              "ConnectionStrings": { "Default": "Server=db;Password=hunter2" },
              "Features": { "Beta": true, "MaxItems": 25, "Ratio": 0.5, "Missing": null },
              "Cors": { "Origins": ["http://localhost:3000", "https://app.example.com"] },
              "Weird.Name": "skipped later because it isn't a shell name",
            }
            """;
        var entries = AppSettings.EntriesFromJson(json)!;
        Assert.Equal(["ConnectionStrings__Default", "Cors__Origins__0", "Cors__Origins__1", "Features__Beta", "Features__MaxItems", "Features__Ratio", "Weird.Name"],
            entries.Select(e => e.Key));
        Assert.Equal(["true", "25", "0.5"], entries.Skip(3).Take(3).Select(e => e.Value));
        Assert.True(EnvVar.LooksSecret("ConnectionStrings__Default"));
        Assert.Null(AppSettings.EntriesFromJson("[1, 2]"));
        Assert.Equal(7, DotEnv.ParseAny(json).Count);
        Assert.Equal(["A"], DotEnv.ParseAny("A=1").Select(e => e.Key));
    }

    [Fact]
    public void ScannerImportsDotNetProjectsWithUserSecrets()
    {
        var appData = Folder("AppData/Roaming");
        Write("""{ "Api": { "BaseUrl": "https://localhost:5001" }, "ConnectionStrings": { "Db": "Server=localhost" } }""", "code/Shop.Api/appsettings.json");
        Write("""{ "Api": { "BaseUrl": "https://localhost:7001" } }""", "code/Shop.Api/appsettings.Development.json");
        Write("<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><UserSecretsId>shop-api-1234</UserSecretsId></PropertyGroup></Project>", "code/Shop.Api/Shop.Api.csproj");
        Write("""{ "Stripe:ApiKey": "sk_test_x", "ConnectionStrings:Db": "Server=localhost;Password=pw" }""", "AppData/Roaming/Microsoft/UserSecrets/shop-api-1234/secrets.json");
        Write("""{ "Api": { "BaseUrl": "stale" } }""", "code/Shop.Api/bin/Debug/net8.0/appsettings.json");
        Write("""{ "Api": { "BaseUrl": "stale" } }""", "code/Shop.Api/obj/appsettings.json");

        var found = ProjectScanner.Scan(Path.Combine(Root, "code"), appData);
        Assert.Equal(["Shop.Api"], found.Select(f => f.Name));
        var project = found[0];
        Assert.Equal(["appsettings.json", "appsettings.Development.json", "user secrets"], project.Files);
        var byKey = project.Variables.ToDictionary(v => v.Key);
        Assert.Equal("https://localhost:7001", byKey["Api__BaseUrl"].Value);
        Assert.Equal("Server=localhost;Password=pw", byKey["ConnectionStrings__Db"].Value);
        Assert.True(byKey["Stripe__ApiKey"].IsSecret);
        Assert.False(byKey["Api__BaseUrl"].IsSecret);
    }

    [Fact]
    public void UserSecretsIdCantEscapeTheSecretsFolder()
    {
        Write("<UserSecretsId>../../etc</UserSecretsId>", "evil/Evil.csproj");
        Assert.Null(AppSettings.UserSecretsId(Path.Combine(Root, "evil")));
    }
}

public class ProjectScannerTests : TempDirectoryTest
{
    [Fact]
    public void FindsProjectsAndSkipsNoise()
    {
        Write("API_URL=http://localhost\nSTRIPE_SECRET_KEY=sk_test_1\n", "shop/.env");
        Write("API_URL=http://127.0.0.1\n", "shop/.env.local");
        Write("export DATA=\"$PWD/data\"\nuse nix\nPATH_add bin\n", "lab/.envrc");
        Write("PORT=3000\n", "work/monorepo/apps/web/.env");
        Write("X=1\n", "shop/node_modules/dep/.env");
        Write("X=1\n", "a/b/c/d/e/too-deep/.env");
        Write("X=1\n", ".hidden/app/.env");
        Write("X=placeholder\n", "templated/.env.example");
        Write("# nothing here\n", "empty/.env");

        var found = ProjectScanner.Scan(Root, Root);
        Assert.Equal(["lab", "shop", "web"], found.Select(f => f.Name));

        var shop = found.Single(f => f.Name == "shop");
        Assert.Equal([".env", ".env.local"], shop.Files);
        Assert.Equal(["API_URL", "STRIPE_SECRET_KEY"], shop.Variables.Select(v => v.Key));
        Assert.Equal("http://127.0.0.1", shop.Variables[0].Value);
        Assert.True(shop.Variables[1].IsSecret);

        var lab = found.Single(f => f.Name == "lab");
        Assert.Equal(["DATA"], lab.Variables.Select(v => v.Key));
        Assert.True(lab.Variables[0].Expands, ".envrc values are shell values");
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        Write("A=1\n", "one/.env");
        Assert.Empty(ProjectScanner.Scan(Root, Root, isCancelled: () => true));
    }
}

public class PlaintextScannerTests : TempDirectoryTest
{
    [Fact]
    public void FindsBashExports()
    {
        var text = $"""
            export OPENAI_API_KEY="sk-live-123"
              export GITHUB_TOKEN=ghp_abc # personal
            export PATH="$HOME/bin:$PATH"
            export KUBECONFIG='~/kube/$config'
            export NOT_AN_ASSIGNMENT
            alias ll='ls -l'
            {StartupFiles.Snippet(StartupFiles.BashLine("/c/x/init.sh"))}
            export DATA_DIR="$HOME/data"
            """;
        var found = PlaintextScanner.BashExports(text, ".bashrc");
        Assert.Equal(["OPENAI_API_KEY", "GITHUB_TOKEN", "KUBECONFIG", "DATA_DIR"], found.Select(f => f.Key));
        Assert.Equal([1, 2, 4, 11], found.Select(f => f.Line));
        Assert.Equal("ghp_abc", found[1].Value);
        Assert.True(found[0].Variable.IsSecret);
        Assert.False(found[2].Variable.Expands, "single-quoted values stay literal");
        Assert.True(found[3].Variable.Expands);
    }

    [Fact]
    public void FindsPowerShellAssignments()
    {
        var text = """
            $env:OPENAI_API_KEY = 'sk-live-123'
            $Env:GITHUB_TOKEN="ghp_abc"   # personal
            $env:IT_S = 'it''s'
            $env:TOOLS = "$env:USERPROFILE\tools"
            $env:RUNS = "$(Get-Secret x)"
            $env:FROM_VAR = $other
            [Environment]::SetEnvironmentVariable('AZURE_CLIENT_SECRET', 'abc123', 'User')
            [System.Environment]::SetEnvironmentVariable("PLAIN_NAME", "v")
            $env:Path = "C:\tools;$env:Path"
            Set-Alias ll Get-ChildItem
            """;
        var found = PlaintextScanner.PowerShellExports(text, "profile.ps1");
        Assert.Equal(["OPENAI_API_KEY", "GITHUB_TOKEN", "IT_S", "TOOLS", "AZURE_CLIENT_SECRET", "PLAIN_NAME"], found.Select(f => f.Key));
        Assert.Equal(["sk-live-123", "ghp_abc", "it's", @"$env:USERPROFILE\tools", "abc123", "v"], found.Select(f => f.Value));
        Assert.Equal([false, false, false, true, false, false], found.Select(f => f.Expands));
        Assert.Equal(7, found[4].Line);
    }

    [Fact]
    public void FindsUserEnvironmentVariablesButNotWindowsOwn()
    {
        var file = Path.Combine(Root, "UserEnvironment.json");
        File.WriteAllText(file, """{ "Path": "C:\\x", "TEMP": "C:\\t", "OPENAI_API_KEY": "sk", "JAVA_HOME": "%ProgramFiles%\\Java", "bad-name": "x" }""");
        var environment = new FileUserEnvironment(file);
        var found = PlaintextScanner.UserEnvironmentExports(environment);
        Assert.Equal(["JAVA_HOME", "OPENAI_API_KEY"], found.Select(f => f.Key));
        Assert.True(found[0].Expands);
        Assert.True(found[1].IsUserEnvironment);
        environment.Remove(["OPENAI_API_KEY"]);
        Assert.False(environment.Read().ContainsKey("OPENAI_API_KEY"));
        Assert.True(environment.Read().ContainsKey("Path"));
    }
}

public class ExpansionTests
{
    [Fact]
    public void ParsesReferencesInEveryStyle()
    {
        var tokens = Expansion.Parse(@"~\bin;%USERPROFILE%\x;$HOME/y:${ProgramFiles(x86)}\z;$env:Path;100% sure;$5;$(whoami)");
        Assert.Equal(new Expansion.Token[]
        {
            new Expansion.Home(), new Expansion.Literal(@"\bin;"), new Expansion.Reference("USERPROFILE"), new Expansion.Literal(@"\x;"),
            new Expansion.Reference("HOME"), new Expansion.Literal("/y:"), new Expansion.Reference("ProgramFiles(x86)"),
            new Expansion.Literal(@"\z;"), new Expansion.Reference("Path"), new Expansion.Literal(";100% sure;$5;$(whoami)"),
        }, tokens);
    }

    [Fact]
    public void TildeOnlyCountsAtTheStartOfAPath()
    {
        Assert.Equal([new Expansion.Literal("a~b")], Expansion.Parse("a~b"));
        Assert.Equal([new Expansion.Literal("x;"), new Expansion.Home()], Expansion.Parse("x;~"));
    }
}

public class PathStyleTests
{
    [Theory]
    [InlineData(@"C:\Users\me\proj", "/c/Users/me/proj")]
    [InlineData(@"D:\", "/d/")]
    [InlineData(@"\\server\share\x", "//server/share/x")]
    [InlineData("/already/posix", "/already/posix")]
    public void ConvertsBothWays(string windows, string posix)
    {
        Assert.Equal(posix, PathStyle.ToPosix(windows));
        if (windows.StartsWith('/')) return;
        Assert.Equal(windows, PathStyle.FromPosix(posix));
    }

    [Fact]
    public void PrefixesKeepRootsAndUseThePathsOwnSeparator()
    {
        Assert.Equal(@"C:\", PathStyle.AsPrefix(@"C:\"));
        Assert.Equal(@"C:\code\", PathStyle.AsPrefix(@"C:\code\\"));
        Assert.Equal("/c/code/", PathStyle.AsPrefix("/c/code"));
        Assert.Equal("/", PathStyle.AsPrefix("/"));
    }
}
