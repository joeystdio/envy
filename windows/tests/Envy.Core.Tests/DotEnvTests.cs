namespace Envy.Core.Tests;

public class DotEnvTests
{
    [Fact]
    public void ParsesCommonDotEnvSyntax()
    {
        var text = """
            # comment
            export A=1
            B = two words  # trailing comment
            C="quoted # not a comment"
            D='single $HOME'
            E="line1\nline2"
            F="multi
            line"
            G=
            H=#kept
            I= # just a comment
            not an assignment
            J=a=b

            """;
        var entries = DotEnv.Parse(text);
        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H", "I", "J"], entries.Select(e => e.Key));
        Assert.Equal(["1", "two words", "quoted # not a comment", "single $HOME", "line1\nline2", "multi\nline", "", "#kept", "", "a=b"],
            entries.Select(e => e.Value));
        Assert.Equal(DotEnv.Quote.Single, entries[3].Quote);
        Assert.Equal(13, entries[9].Line);
    }

    [Fact]
    public void HandlesWindowsLineEndings() =>
        Assert.Equal(["1", "2"], DotEnv.Parse("A=1\r\nB=2\r\n").Select(e => e.Value));

    [Fact]
    public void SerializeRoundTrips()
    {
        string[] values = ["plain", "has space", "it's", "quote\"d", "multi\nline", "", "$NOT_EXPANDED", "back\\slash", "#hash", @"C:\Users\me"];
        var vars = values.Select((v, i) => new EnvVar($"K{i}", v));
        var parsed = DotEnv.Parse(DotEnv.Serialize(vars, "Exported\nfrom a test"));
        Assert.Equal(values, parsed.Select(e => e.Value));
    }

    [Fact]
    public void SecretHeuristic()
    {
        foreach (var key in new[] { "GITHUB_TOKEN", "OPENAI_API_KEY", "DB_PASSWORD", "client_secret", "AWS_SECRET_ACCESS_KEY", "SSH_PRIVATE", "SMTP_PASS" })
            Assert.True(EnvVar.LooksSecret(key), key);
        foreach (var key in new[] { "NODE_ENV", "PORT", "DATABASE_HOST", "KEYBOARD_LAYOUT", "PASSPORT_CITY" })
            Assert.False(EnvVar.LooksSecret(key), key);
    }

    [Fact]
    public void KeyValidation()
    {
        Assert.Null(EnvVar.KeyProblem("API_URL"));
        Assert.Null(EnvVar.KeyProblem("_private1"));
        Assert.NotNull(EnvVar.KeyProblem(""));
        Assert.NotNull(EnvVar.KeyProblem("1ABC"));
        Assert.NotNull(EnvVar.KeyProblem("MY-VAR"));
        Assert.NotNull(EnvVar.KeyProblem("PWD"));
        Assert.NotNull(EnvVar.KeyProblem("__ENVY_VARS"));
        // cmd's dynamic variables stop working once a real one shadows them, whatever the case.
        Assert.NotNull(EnvVar.KeyProblem("errorlevel"));
        Assert.NotNull(EnvVar.KeyProblem("CD"));
    }

    [Fact]
    public void MergeKeepsIdsOfReplacedVariablesAndGivesCopiesNewOnes()
    {
        var existing = new EnvVar("A", "old");
        var project = new Project("P", variables: [existing]);
        var incoming = new[] { new EnvVar("a", "new"), new EnvVar("B", "b") };
        var result = project.Merge(incoming, MergePolicy.Replace);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(existing.Id, project.Variables[0].Id);
        Assert.Equal("new", project.Variables[0].Value);
        Assert.NotEqual(incoming[1].Id, project.Variables[1].Id);

        var kept = project.Merge([new EnvVar("A", "ignored")], MergePolicy.KeepExisting);
        Assert.Equal(1, kept.Skipped);
        Assert.Equal("new", project.Variables[0].Value);
    }

    [Fact]
    public void DuplicateKeysIgnoreCaseAsWindowsDoes()
    {
        var project = new Project("P", variables: [new EnvVar("Path", "a"), new EnvVar("PATH", "b"), new EnvVar("X", "c")]);
        Assert.Contains("path", Assert.Single(project.DuplicateKeys), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecretsAreStrippedFromStoredConfigAndRefilled()
    {
        var secret = new EnvVar("TOKEN", "s3cret", isSecret: true);
        var config = new EnvConfig(projects: [new Project("P", variables: [secret, new EnvVar("PLAIN", "p")])]);
        var stored = config.WithoutSecretValues();
        Assert.Equal(["", "p"], stored.Projects[0].Variables.Select(v => v.Value));
        Assert.Equal("s3cret", config.Projects[0].Variables[0].Value);
        var restored = stored.Clone();
        restored.FillSecrets(config.SecretValues());
        Assert.True(restored.ContentEquals(config));
    }

    [Fact]
    public void ConfigRoundTripsThroughJsonAndToleratesMissingFields()
    {
        var config = new EnvConfig(projects: [new Project("P", folders: [@"C:\code\p"], variables: [new EnvVar("A", "1", expands: true, note: "n")])]);
        var json = ConfigFile.Serialize(config);
        Assert.True(ConfigFile.Deserialize(json).ContentEquals(config));

        var sparse = ConfigFile.Deserialize("""{ "global": { "id": "00000000-0000-0000-0000-000000000009", "folders": ["x"], "variables": [{ "key": "K" }] }, "projects": [{ "name": "Q" }] }""");
        Assert.Equal(Project.GlobalId, sparse.Global.Id);
        Assert.Empty(sparse.Global.Folders);
        Assert.True(sparse.Global.Variables[0].IsEnabled);
        Assert.Equal("Q", sparse.Projects[0].Name);
        Assert.True(sparse.Projects[0].IsEnabled);
    }
}
