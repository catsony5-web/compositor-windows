using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

/// <summary>Opt-in, current-user-only local transport. Each connection carries exactly one command.</summary>
public sealed class AutomationBridge : IDisposable
{
    public const int MaximumRequestBytes = 1024 * 1024;
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    const int ListenerCount = 4;
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };
    readonly Func<JsonObject, CancellationToken, Task<JsonObject>> handler;
    readonly string registryDirectory;
    readonly object gate = new();
    readonly HashSet<NamedPipeServerStream> pipes = [];
    readonly CancellationTokenSource lifetime = new();
    Task[] workers = [];
    string? registryFile;
    bool started, stopped;

    public AutomationBridge(Func<JsonObject, CancellationToken, Task<JsonObject>> handler, string? registryDirectory = null)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.registryDirectory = registryDirectory ?? DefaultRegistryDirectory;
    }

    public string SessionId { get; private set; } = "";
    public bool IsRunning { get { lock (gate) return started && !stopped; } }
    static string DefaultRegistryDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "Automation");
    internal static string PipeName(string sessionId) => "Morupixel.Automation." + Guid.Parse(sessionId).ToString("N");

    // Construction has no external effects. Only the explicit enable action starts listeners/discovery.
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (started) return;
            SessionId = Guid.NewGuid().ToString("D");
            try
            {
                var listeners = Enumerable.Range(0, ListenerCount).Select(_ => NewPipe()).ToArray();
                using var process = Process.GetCurrentProcess();
                var metadata = new JsonObject
                {
                    ["protocolVersion"] = 1, ["sessionId"] = SessionId, ["processId"] = process.Id,
                    ["processStartTimeUtcTicks"] = process.StartTime.ToUniversalTime().Ticks,
                    ["enabledAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
                };
                Directory.CreateDirectory(registryDirectory);
                registryFile = Path.Combine(registryDirectory, SessionId + ".json");
                AtomicWrite(registryFile, Utf8.GetBytes(metadata.ToJsonString()));
                started = true;
                workers = listeners.Select(pipe => Task.Run(() => AcceptLoopAsync(pipe, lifetime.Token))).ToArray();
            }
            catch
            {
                Stop();
                throw;
            }
        }
    }

    NamedPipeServerStream NewPipe()
    {
        var pipe = new NamedPipeServerStream(PipeName(SessionId), PipeDirection.InOut, ListenerCount,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        pipes.Add(pipe);
        return pipe;
    }

    async Task AcceptLoopAsync(NamedPipeServerStream initial, CancellationToken token)
    {
        var pipe = initial;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeAsync(pipe, token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) { }
            finally
            {
                lock (gate) { pipes.Remove(pipe); pipe.Dispose(); }
            }
            lock (gate)
            {
                if (stopped || token.IsCancellationRequested) return;
                try { pipe = NewPipe(); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        string requestId = Guid.NewGuid().ToString("D");
        JsonObject response;
        try
        {
            using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            inputDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var request = ParseRequest(await ReadLineAsync(pipe, MaximumRequestBytes, inputDeadline.Token).ConfigureAwait(false));
            if (request["requestId"] is JsonValue id && id.TryGetValue<string>(out var supplied) && Guid.TryParse(supplied, out var parsed))
                requestId = parsed.ToString("D");
            request["requestId"] = requestId;

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromSeconds(120));
            // A waiting client sends no more bytes. EOF or additional input cancels work, including dispatcher-queued edits.
            var disconnect = WatchDisconnectAsync(pipe, operation);
            try
            {
                var pending = handler(request, operation.Token);
                response = ValidateResponse(await pending.WaitAsync(operation.Token).ConfigureAwait(false));
            }
            finally
            {
                operation.Cancel();
                await disconnect.ConfigureAwait(false);
            }
        }
        catch (FrameException e) { response = Error(e.Code, e.Message); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        { response = Error("invalid_request", "A UTF-8 JSON object with command and arguments is required."); }
        catch (OperationCanceledException) { response = Error("cancelled", "The request was cancelled, disconnected, or exceeded its time limit."); }
        catch (Exception) { response = Error("internal_error", "The command could not be completed."); }

        response["requestId"] = requestId;
        using var outputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        outputDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] bytes;
        try { bytes = EncodeLine(response, MaximumResponseBytes); }
        catch (FrameException) { bytes = EncodeLine(Error("response_too_large", "The response exceeds the 16 MiB limit.", requestId), MaximumResponseBytes); }
        await pipe.WriteAsync(bytes, outputDeadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(outputDeadline.Token).ConfigureAwait(false);
    }

    static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource operation)
    {
        try { await pipe.ReadAsync(new byte[1], operation.Token).ConfigureAwait(false); operation.Cancel(); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { operation.Cancel(); }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            lifetime.Cancel();
            foreach (var pipe in pipes) pipe.Dispose();
            pipes.Clear();
            // This GUID-named file belongs only to this instance; never prune other or stale sessions here.
            if (registryFile != null)
                try { File.Delete(registryFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _ = Task.WhenAll(workers).ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public void Dispose() => Stop();

    public static JsonArray ListSessions(string? registryDirectory = null)
    {
        var result = new JsonArray();
        string directory = registryDirectory ?? DefaultRegistryDirectory;
        if (!Directory.Exists(directory)) return result;
        string[] files;
        try { files = Directory.EnumerateFiles(directory, "*.json").Take(256).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }
        foreach (string file in files.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(file).Length > 4096) continue;
                var record = JsonNode.Parse(File.ReadAllText(file, Utf8), documentOptions: JsonOptions) as JsonObject;
                if (record == null || !Guid.TryParse(record["sessionId"]?.GetValue<string>(), out var session)
                    || !string.Equals(Path.GetFileNameWithoutExtension(file), session.ToString("D"), StringComparison.OrdinalIgnoreCase)
                    || record["protocolVersion"]?.GetValue<int>() != 1) continue;
                int pid = record["processId"]!.GetValue<int>();
                long startedAt = record["processStartTimeUtcTicks"]!.GetValue<long>();
                if (!DateTimeOffset.TryParse(record["enabledAtUtc"]?.GetValue<string>(), out var enabledAt)) continue;
                using var process = Process.GetProcessById(pid);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedAt) continue;
                result.Add(new JsonObject
                {
                    ["protocolVersion"] = 1, ["sessionId"] = session.ToString("D"), ["processId"] = pid,
                    ["processStartTimeUtcTicks"] = startedAt, ["enabledAtUtc"] = enabledAt.ToUniversalTime().ToString("O")
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                or InvalidOperationException or System.ComponentModel.Win32Exception or NullReferenceException or FormatException) { }
        }
        return result;
    }

    public static Task<JsonObject> SendAsync(string sessionId, JsonObject request, CancellationToken token) =>
        SendCoreAsync(sessionId, request, token, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120));

    internal static async Task<JsonObject> SendCoreAsync(string sessionId, JsonObject request, CancellationToken token,
        TimeSpan connectTimeout, TimeSpan responseTimeout)
    {
        string requestId = Guid.NewGuid().ToString("D");
        if (!Guid.TryParse(sessionId, out var session)) return Error("invalid_session", "Session ID must be a GUID.", requestId);
        byte[] bytes;
        try
        {
            var outbound = (JsonObject)request.DeepClone();
            outbound["requestId"] = requestId;
            _ = ValidateRequest(outbound);
            bytes = EncodeLine(outbound, MaximumRequestBytes);
        }
        catch (FrameException e) { return Error(e.Code, e.Message, requestId); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { return Error("invalid_request", "A JSON object with command and arguments is required.", requestId); }

        using var pipe = new NamedPipeClientStream(".", PipeName(session.ToString("D")), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool connected = false;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(connectTimeout);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            connected = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(responseTimeout);
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            var response = ValidateResponse(JsonNode.Parse(await ReadLineAsync(pipe, MaximumResponseBytes, deadline.Token).ConfigureAwait(false),
                documentOptions: JsonOptions) as JsonObject);
            if (response["requestId"]?.GetValue<string>() != requestId)
                return Error("invalid_response", "The response does not match this request.", requestId);
            return response;
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) return Error("cancelled", "The request was cancelled. Check document state before issuing another edit.", requestId);
            return Error(connected ? "timeout" : "not_connected", connected
                ? "The response timed out. The command is not retried; check document state before issuing another edit."
                : "The automation session is not connected. Enable AI connection in Morupixel and choose a current session.", requestId);
        }
        catch (FrameException e) { return Error(e.Code, e.Message, requestId); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        { return Error("invalid_response", "The session returned an invalid response.", requestId); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { return Error("not_connected", "The session disconnected or is unavailable. The command is not retried; check document state before issuing another edit.", requestId); }
    }

    internal static JsonObject ParseRequest(string text) => ValidateRequest(JsonNode.Parse(text, documentOptions: JsonOptions) as JsonObject);
    static JsonObject ValidateRequest(JsonObject? request)
    {
        if (request == null || request["command"] is not JsonValue command || !command.TryGetValue<string>(out var name)
            || string.IsNullOrWhiteSpace(name) || name.Length > 128 || request["arguments"] is not JsonObject)
            throw new FrameException("invalid_request", "A JSON object with a nonempty command and an arguments object is required.");
        return request;
    }

    static JsonObject ValidateResponse(JsonObject? response)
    {
        if (response == null || response["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var success)
            || (success && response["result"] is not JsonObject)
            || (!success && (response["error"] is not JsonObject error || error["code"] is not JsonValue code
                || !code.TryGetValue<string>(out _) || error["message"] is not JsonValue message || !message.TryGetValue<string>(out _))))
            throw new FrameException("invalid_response", "The session returned an invalid response envelope.");
        return response;
    }

    internal static JsonObject Error(string code, string message, string? requestId = null)
    {
        var value = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
        if (requestId != null) value["requestId"] = requestId;
        return value;
    }

    internal static async Task<string> ReadLineAsync(Stream stream, int limit, CancellationToken token)
    {
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) throw new FrameException("incomplete_message", "A complete JSON line ending in a newline is required.");
            int end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            int length = end < 0 ? count : end;
            if (line.Length + length > limit) throw new FrameException("message_too_large", $"The JSON message exceeds {limit} bytes.");
            line.Write(buffer, 0, length);
            if (end < 0) continue;
            if (end + 1 < count) throw new FrameException("invalid_request", "Only one JSON line is permitted per connection.");
            var bytes = line.GetBuffer();
            int size = (int)line.Length;
            if (size > 0 && bytes[size - 1] == '\r') size--;
            return Utf8.GetString(bytes, 0, size);
        }
    }

    static byte[] EncodeLine(JsonObject value, int limit)
    {
        string text = value.ToJsonString();
        if (Utf8.GetByteCount(text) > limit) throw new FrameException("message_too_large", $"The JSON message exceeds {limit} bytes.");
        return Utf8.GetBytes(text + "\n");
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    sealed class FrameException(string code, string message) : IOException(message)
    { public string Code { get; } = code; }
}
