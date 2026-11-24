using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Envy.Core;

/// <summary>What the envy command sends the running app.</summary>
public sealed class CommandRequest
{
    public List<string> Arguments { get; set; } = [];
    public RequestContext Context { get; set; } = new();

    public CommandContext CommandContext => new(Context.Directories, Context.Loaded);
}

public sealed class RequestContext
{
    public List<string> Directories { get; set; } = [];
    public List<string> Loaded { get; set; } = [];
}

/// <summary>The app's answer. Output and error never hold secret values.</summary>
public sealed class CommandResponse
{
    public string Output { get; set; } = "";
    public string? Error { get; set; }
    public int ExitCode { get; set; }
    /// <summary>The app wants to come to the front (to show a dialog); the command lets it, which Windows allows more readily.</summary>
    public bool Activate { get; set; }
    /// <summary>The app's process.</summary>
    public int ProcessID { get; set; } = Environment.ProcessId;
}

/// <summary>Where the app's executable is, and the sandbox (ENVY_HOME) it runs in: envy.json next to the envy command.</summary>
public sealed class LauncherInfo
{
    public string? App { get; set; }
    public string? Home { get; set; }
}

public enum PipeFailure { NotRunning, Blocked, Broken }

public sealed class CommandPipeException(PipeFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public PipeFailure Failure { get; } = failure;
}

/// <summary>
/// A named pipe per user (and per ENVY_HOME sandbox) that only processes of that user can open: the app creates it with
/// an access list holding only your account, and the command only talks to a pipe your account owns. One JSON request,
/// one JSON response.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class CommandPipe
{
    public const int MaxMessage = 1 << 20;

    public static string Name(AppPaths paths)
    {
        var key = $"{Environment.UserDomainName}\\{Environment.UserName}|{(paths.IsSandbox ? paths.Home.ToLowerInvariant() : "")}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToLowerInvariant())));
        return "Envy.Command." + hash[..16];
    }

    /// <summary>
    /// Sends one request and waits for the answer. Throws <see cref="PipeFailure.NotRunning"/> when nobody listens, and
    /// <see cref="PipeFailure.Blocked"/> when Windows refuses the connection, as a sandbox does. <paramref name="connected"/>
    /// gets the app's process ID before the request goes out.
    /// </summary>
    public static CommandResponse Send(string name, CommandRequest request, TimeSpan connectTimeout, TimeSpan? timeout = null, Action<int>? connected = null)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)Math.Max(0, connectTimeout.TotalMilliseconds));
        }
        catch (TimeoutException e) { throw new CommandPipeException(PipeFailure.NotRunning, "Envy isn't running.", e); }
        catch (UnauthorizedAccessException e) { throw new CommandPipeException(PipeFailure.Blocked, e.Message, e); }
        catch (IOException e) { throw new CommandPipeException(PipeFailure.Broken, e.Message, e); }

        try
        {
            pipe.ReadMode = PipeTransmissionMode.Message;
            if (connected is not null && OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
                connected((int)pid);
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
            Write(pipe, JsonSerializer.SerializeToUtf8Bytes(request, EnvyJson.Default.CommandRequest), cancel.Token).GetAwaiter().GetResult();
            var data = Read(pipe, cancel.Token).GetAwaiter().GetResult();
            return JsonSerializer.Deserialize(data, EnvyJson.Default.CommandResponse) ?? throw new JsonException("Empty answer.");
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            throw new CommandPipeException(PipeFailure.Broken, e is OperationCanceledException ? "Envy didn't answer in time." : e.Message, e);
        }
    }

    internal static async Task Write(PipeStream pipe, byte[] data, CancellationToken token)
    {
        await pipe.WriteAsync(data, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>One message, however many reads it takes.</summary>
    internal static async Task<byte[]> Read(PipeStream pipe, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[64 * 1024];
        do
        {
            var n = await pipe.ReadAsync(buffer, token).ConfigureAwait(false);
            if (n == 0) break;
            data.Write(buffer, 0, n);
            if (data.Length > MaxMessage) throw new IOException("The message is too long.");
        } while (!pipe.IsMessageComplete);
        return data.ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}

/// <summary>Listens on the command pipe and answers each request with <c>handle</c>.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CommandPipeServer(string name, Func<CommandRequest, Task<CommandResponse>> handle) : IDisposable
{
    readonly CancellationTokenSource stop = new();

    /// <summary>Creates the first pipe instance right away, so a failure shows up here, then accepts clients in the background.</summary>
    public void Start()
    {
        var first = Create();
        _ = Task.Run(() => Accept(first));
    }

    NamedPipeServerStream Create() => new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, CommandPipe.MaxMessage, CommandPipe.MaxMessage);

    async Task Accept(NamedPipeServerStream pipe)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = Create();
                continue;
            }
            var client = pipe;
            _ = Task.Run(() => Serve(client));
            try { pipe = Create(); }
            catch (IOException) { await Task.Delay(500).ConfigureAwait(false); pipe = Create(); }
        }
        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                CommandRequest? request;
                using (var read = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    request = JsonSerializer.Deserialize(await CommandPipe.Read(pipe, read.Token).ConfigureAwait(false), EnvyJson.Default.CommandRequest);
                if (request is null) return;
                var response = await handle(request).ConfigureAwait(false);
                using var write = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await CommandPipe.Write(pipe, JsonSerializer.SerializeToUtf8Bytes(response, EnvyJson.Default.CommandResponse), write.Token).ConfigureAwait(false);
                pipe.WaitForPipeDrain();
            }
            catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
