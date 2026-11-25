using System.Text.Json;
using System.Text.Json.Serialization;

namespace Envy.Core;

/// <summary>
/// Source-generated JSON for everything Envy reads and writes, so the envy command can be trimmed (reflection-based
/// JSON is off in trimmed apps). Same options config.json has always used: camelCase, indented, comments allowed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(EnvConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(LauncherInfo))]
[JsonSerializable(typeof(ExecData))]
[JsonSerializable(typeof(BackupFile))]
[JsonSerializable(typeof(BackupContent))]
public sealed partial class EnvyJson : JsonSerializerContext;
