using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Envy.Core;

/// <summary>Where secret values live, keyed by variable ID.</summary>
public interface ISecretVault
{
    Dictionary<string, string> Load();
    /// <summary>Stores exactly <paramref name="secrets"/>: changed ones are written and ones no longer listed removed.</summary>
    void Save(IReadOnlyDictionary<string, string> secrets);
    /// <summary>Where they live, for display.</summary>
    string Location { get; }
}

/// <summary>Secrets in a private JSON file, for ENVY_HOME sandboxes so test data stays out of Credential Manager.</summary>
public sealed class FileSecretVault(string path) : ISecretVault
{
    public string Location => path;

    public Dictionary<string, string> Load()
    {
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
    }

    public void Save(IReadOnlyDictionary<string, string> secrets)
    {
        PrivateFiles.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        PrivateFiles.AtomicWrite(path, JsonSerializer.Serialize(secrets));
    }
}

/// <summary>
/// Windows Credential Manager: one generic credential per secret (target "Envy/&lt;variable id&gt;", user name the
/// variable's name), encrypted by Windows with DPAPI for your account and kept on this PC. A credential holds at most
/// 2,560 bytes, so longer values (certificates, private keys) continue in "Envy/&lt;id&gt;#2", "#3" and so on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerVault(string prefix = "Envy/") : ISecretVault
{
    const int MaxBlob = 5 * 512;
    public string Location => "Windows Credential Manager (Generic Credentials, " + prefix + "…)";

    public Dictionary<string, string> Load()
    {
        var chunks = new Dictionary<string, SortedDictionary<int, byte[]>>();
        foreach (var (target, blob) in Enumerate(prefix + "*"))
        {
            var rest = target[prefix.Length..];
            var hash = rest.IndexOf('#');
            var id = hash < 0 ? rest : rest[..hash];
            var part = hash < 0 ? 1 : int.TryParse(rest[(hash + 1)..], out var n) ? n : 0;
            if (part < 1) continue;
            if (!chunks.TryGetValue(id, out var parts)) chunks[id] = parts = [];
            parts[part] = blob;
        }
        var output = new Dictionary<string, string>();
        foreach (var (id, parts) in chunks)
        {
            // Only complete values: part 1, 2, … without gaps.
            if (!parts.Keys.SequenceEqual(Enumerable.Range(1, parts.Count))) continue;
            output[id] = Encoding.UTF8.GetString(parts.Values.SelectMany(b => b).ToArray());
        }
        return output;
    }

    /// <summary>Writes the secrets that changed and deletes the rest; <paramref name="names"/> label them in Credential Manager.</summary>
    public void Save(IReadOnlyDictionary<string, string> secrets) => Save(secrets, null);

    public void Save(IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string>? names)
    {
        var existing = Enumerate(prefix + "*").ToDictionary(e => e.Target, e => e.Blob, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in secrets)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var count = Math.Max(1, (bytes.Length + MaxBlob - 1) / MaxBlob);
            for (var part = 1; part <= count; part++)
            {
                var target = prefix + id + (part == 1 ? "" : "#" + part);
                var blob = bytes.Skip((part - 1) * MaxBlob).Take(MaxBlob).ToArray();
                wanted.Add(target);
                if (existing.TryGetValue(target, out var old) && old.AsSpan().SequenceEqual(blob)) continue;
                Write(target, names?.GetValueOrDefault(id) ?? id, blob);
            }
        }
        foreach (var target in existing.Keys.Where(t => !wanted.Contains(t))) Delete(target);
    }

    static IEnumerable<(string Target, byte[] Blob)> Enumerate(string filter)
    {
        if (!CredEnumerate(filter, 0, out var count, out var list))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) yield break;
            throw new Win32Exception(error);
        }
        try
        {
            var results = new List<(string, byte[])>();
            for (var i = 0; i < count; i++)
            {
                var credential = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                if (credential.Type != CredTypeGeneric) continue;
                var blob = new byte[credential.CredentialBlobSize];
                if (blob.Length > 0) Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                results.Add((credential.TargetName, blob));
            }
            foreach (var r in results) yield return r;
        }
        finally
        {
            CredFree(list);
        }
    }

    static void Write(string target, string userName, byte[] blob)
    {
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = userName,
                Comment = "Envy secret. Change it in Envy, not here.",
                CredentialBlob = handle.AddrOfPinnedObject(),
                CredentialBlobSize = blob.Length,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            handle.Free();
        }
    }

    static void Delete(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;
    const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    static extern void CredFree(IntPtr buffer);
}
