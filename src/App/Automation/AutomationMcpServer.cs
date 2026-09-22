using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

/// <summary>Local stdio MCP adapter. It never starts an editor or grants access to a window.</summary>
public static class AutomationMcpServer
{
    public const string ModernVersion = "2026-07-28";
    public const string LegacyVersion = "2025-11-25";
    internal const int MaximumLineCharacters = 1_048_576;

    public static Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error, CancellationToken token = default)
        => RunAsync(input, output, error, AutomationBridge.SendAsync, () => AutomationBridge.ListSessions(), token);

    public static Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>> send, Func<JsonArray> listSessions, CancellationToken token = default)
        => new Server(input, output, error, send, listSessions).RunAsync(token);

    sealed class RpcException(int code, string message, JsonObject? data = null) : Exception(message)
    {
        public int Code { get; } = code;
        public JsonObject? ErrorData { get; } = data;
    }
    sealed class Pending(JsonNode id, string key, CancellationTokenSource cancellation)
    {
        public JsonNode Id { get; } = id;
        public string Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
    sealed class Server(TextReader input, TextWriter output, TextWriter error,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>> send, Func<JsonArray> listSessions)
    {
        readonly SemaphoreSlim writer = new(1, 1);
        readonly object gate = new();
        readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
        readonly CancellationTokenSource lifetime = new();
        string? legacyVersion;
        bool legacyReady;
        bool stopping;
        int exitCode;
        static JsonObject Identity() => new()
        {
            ["name"] = "morupixel",
            ["version"] = typeof(AutomationMcpServer).Assembly.GetName().Version?.ToString() ?? "1.0.0"
        };

        public async Task<int> RunAsync(CancellationToken token)
        {
            using var registration = token.Register(lifetime.Cancel);
            var reader = new LineReader(input);
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false); }
                    catch (InvalidDataException e)
                    {
                        await WriteErrorAsync(null, -32600, e.Message).ConfigureAwait(false);
                        continue;
                    }
                    if (line == null) break;
                    JsonNode? parsed;
                    try
                    {
                        using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                        CheckUniqueProperties(document.RootElement);
                        parsed = JsonNode.Parse(document.RootElement.GetRawText());
                    }
                    catch (Exception e) when (e is JsonException or ArgumentException)
                    {
                        await WriteErrorAsync(null, -32700, "Invalid JSON.").ConfigureAwait(false);
                        continue;
                    }
                    if (parsed is not JsonObject message || !IsString(message["jsonrpc"], "2.0") ||
                        message["method"]?.GetValueKind() != JsonValueKind.String)
                    {
                        await WriteErrorAsync(null, -32600, "Expected a JSON-RPC 2.0 request object.").ConfigureAwait(false);
                        continue;
                    }
                    if (!message.ContainsKey("id")) { Notify(message); continue; }
                    var id = message["id"];
                    string? key = IdKey(id);
                    if (id == null || key == null)
                    {
                        await WriteErrorAsync(null, -32600, "Request id must be a string or integer.").ConfigureAwait(false);
                        continue;
                    }
                    Pending? request = null;
                    string? rejected = null;
                    lock (gate)
                    {
                        if (pending.ContainsKey(key)) rejected = "Request id is already in use.";
                        else if (pending.Count >= 64) rejected = "Too many in-flight requests (maximum 64).";
                        else
                        {
                            request = new Pending(id.DeepClone(), key, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
                            pending.Add(key, request);
                        }
                    }
                    if (rejected != null) await WriteErrorAsync(id, -32600, rejected).ConfigureAwait(false);
                    else request!.Task = DispatchAsync(message, request);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception e)
            {
                exitCode = 1;
                await LogAsync(e.Message).ConfigureAwait(false);
            }
            finally
            {
                lock (gate) stopping = true;
                lifetime.Cancel();
                Task[] tasks;
                lock (gate) tasks = pending.Values.Select(p => p.Task).ToArray();
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
            }
            return exitCode;
        }

        void Notify(JsonObject message)
        {
            string method = message["method"]!.GetValue<string>();
            if (method == "notifications/initialized")
            {
                if (legacyVersion != null) legacyReady = true;
                return;
            }
            if (method != "notifications/cancelled" || message["params"] is not JsonObject parameters) return;
            string? key = IdKey(parameters["requestId"]);
            if (key == null) return;
            lock (gate)
                if (pending.TryGetValue(key, out var request)) request.Cancellation.Cancel();
        }

        async Task DispatchAsync(JsonObject message, Pending request)
        {
            try
            {
                string method = message["method"]!.GetValue<string>();
                if (message.ContainsKey("params") && message["params"] is not JsonObject)
                    throw new RpcException(-32602, "Request params must be an object.");
                var parameters = message["params"] as JsonObject ?? new JsonObject();
                if (parameters.ContainsKey("_meta") && parameters["_meta"] is not JsonObject)
                    throw new RpcException(-32602, "Request _meta must be an object.");
                JsonObject result;
                if (method == "initialize") result = Initialize(parameters);
                else if (method == "ping" && !HasModernMetadata(parameters)) result = new JsonObject();
                else
                {
                    bool modern = CheckVersion(parameters, method);
                    result = method switch
                    {
                        "server/discover" when modern => new JsonObject
                        {
                            ["supportedVersions"] = new JsonArray(ModernVersion), ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["instructions"] = AutomationCatalog.Instructions,
                            ["ttlMs"] = 0, ["cacheScope"] = "public"
                        },
                        "ping" => new JsonObject(),
                        "tools/list" => ListTools(parameters, modern),
                        "tools/call" => await CallToolAsync(parameters, request.Cancellation.Token).ConfigureAwait(false),
                        _ => throw new RpcException(-32601, "Unknown method: " + method)
                    };
                    if (modern)
                    {
                        result["resultType"] = "complete";
                        result["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = Identity() };
                    }
                }
                request.Cancellation.Token.ThrowIfCancellationRequested();
                await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id.DeepClone(), ["result"] = result }, request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (RpcException e)
            {
                await WriteErrorAsync(request.Id, e.Code, e.Message, e.ErrorData, request).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                await LogAsync("MCP request failed: " + e.Message).ConfigureAwait(false);
                await WriteErrorAsync(request.Id, -32603, "Internal server error.", request: request).ConfigureAwait(false);
            }
            finally
            {
                lock (gate) pending.Remove(request.Key);
                request.Cancellation.Dispose();
            }
        }

        JsonObject Initialize(JsonObject parameters)
        {
            if (legacyVersion != null) throw new RpcException(-32600, "The legacy connection is already initialized.");
            if (parameters["protocolVersion"]?.GetValueKind() != JsonValueKind.String ||
                parameters["capabilities"] is not JsonObject || parameters["clientInfo"] is not JsonObject client ||
                client["name"]?.GetValueKind() != JsonValueKind.String || client["version"]?.GetValueKind() != JsonValueKind.String)
                throw new RpcException(-32602, "initialize requires protocolVersion, capabilities and clientInfo{name,version}.");
            legacyVersion = LegacyVersion;
            return new JsonObject
            {
                ["protocolVersion"] = LegacyVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = Identity(),
                ["instructions"] = AutomationCatalog.Instructions
            };
        }

        static bool HasModernMetadata(JsonObject parameters) => parameters["_meta"] is JsonObject meta &&
            meta.Any(p => p.Key is "io.modelcontextprotocol/protocolVersion" or "io.modelcontextprotocol/clientCapabilities" or "io.modelcontextprotocol/clientInfo");

        bool CheckVersion(JsonObject parameters, string method)
        {
            if (HasModernMetadata(parameters) || method == "server/discover" || legacyVersion == null)
            {
                if (parameters["_meta"] is not JsonObject meta || meta["io.modelcontextprotocol/protocolVersion"]?.GetValueKind() != JsonValueKind.String ||
                    meta["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                    throw new RpcException(-32602, "Modern requests require protocolVersion and clientCapabilities in params._meta; legacy clients must initialize first.");
                string version = meta["io.modelcontextprotocol/protocolVersion"]!.GetValue<string>();
                if (meta.ContainsKey("io.modelcontextprotocol/clientInfo") &&
                    (meta["io.modelcontextprotocol/clientInfo"] is not JsonObject client ||
                        client["name"]?.GetValueKind() != JsonValueKind.String || client["version"]?.GetValueKind() != JsonValueKind.String))
                    throw new RpcException(-32602, "clientInfo must contain string name and version fields.");
                if (version != ModernVersion) throw new RpcException(-32022, "Unsupported protocol version.", new JsonObject
                {
                    ["supported"] = new JsonArray(ModernVersion), ["requested"] = version
                });
                return true;
            }
            if (!legacyReady) throw new RpcException(-32600, "Send notifications/initialized before calling tools.");
            return false;
        }

        static JsonObject ListTools(JsonObject parameters, bool modern)
        {
            if (parameters.ContainsKey("cursor")) throw new RpcException(-32602, "This tool list has one page; omit cursor.");
            var result = new JsonObject { ["tools"] = AutomationCatalog.Tools() };
            if (modern) { result["ttlMs"] = 0; result["cacheScope"] = "public"; }
            return result;
        }

        async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken token)
        {
            if (parameters["name"]?.GetValueKind() != JsonValueKind.String ||
                parameters.ContainsKey("arguments") && parameters["arguments"] is not JsonObject)
                throw new RpcException(-32602, "tools/call requires a tool name and object arguments.");
            string name = parameters["name"]!.GetValue<string>();
            if (!name.StartsWith("morupixel_", StringComparison.Ordinal) || !AutomationCatalog.Known(name[10..]))
                throw new RpcException(-32602, "Unknown tool: " + name);
            string command = name[10..];
            var arguments = (JsonObject)(parameters["arguments"]?.DeepClone() ?? new JsonObject());
            try
            {
                if (command == "list_sessions")
                {
                    AutomationCatalog.Validate(command, arguments);
                    return ToolResult(new JsonObject { ["sessions"] = listSessions() });
                }
                if (arguments["sessionId"]?.GetValueKind() != JsonValueKind.String ||
                    !Guid.TryParseExact(arguments["sessionId"]!.GetValue<string>(), "D", out var session) || session == Guid.Empty)
                    throw new ArgumentException("sessionId must be a GUID returned by morupixel_list_sessions.");
                arguments.Remove("sessionId");
                AutomationCatalog.Validate(command, arguments);
                var reply = await send(session.ToString("D"), new JsonObject { ["command"] = command, ["arguments"] = arguments }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (reply["ok"]?.GetValueKind() == JsonValueKind.False)
                    return ToolResult(new JsonObject { ["error"] = reply["error"]?.DeepClone() ?? new JsonObject { ["code"] = "operation_failed", ["message"] = "Morupixel rejected the operation." } }, true);
                if (reply["ok"]?.GetValueKind() != JsonValueKind.True || reply["result"] is not JsonObject payload)
                    throw new InvalidDataException("Invalid response from the Morupixel connection.");
                var result = (JsonObject)payload.DeepClone();
                string? png = null;
                if (command == "preview")
                {
                    if (result["pngBase64"]?.GetValueKind() != JsonValueKind.String) throw new InvalidDataException("Preview PNG is missing.");
                    png = result["pngBase64"]!.GetValue<string>();
                    result.Remove("pngBase64");
                    if (png.Length > 8 * 1024 * 1024) throw new InvalidDataException("Preview PNG exceeds the response limit.");
                    byte[] bytes = Convert.FromBase64String(png);
                    if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new InvalidDataException("Preview data is not PNG.");
                }
                return ToolResult(result, png: png);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or TimeoutException)
            {
                return ToolResult(new JsonObject { ["error"] = AutomationErrors.Describe(e is ArgumentException ? "invalid_arguments" : "operation_failed", e.Message) }, true);
            }
        }

        static JsonObject ToolResult(JsonObject payload, bool isError = false, string? png = null)
        {
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() });
            if (png != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = png, ["mimeType"] = "image/png" });
            return new JsonObject { ["content"] = content, ["structuredContent"] = payload, ["isError"] = isError };
        }

        static bool IsString(JsonNode? node, string expected) => node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() == expected;
        static void CheckUniqueProperties(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                    CheckUniqueProperties(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) CheckUniqueProperties(item);
        }
        static string? IdKey(JsonNode? node)
        {
            if (node?.GetValueKind() == JsonValueKind.String)
            {
                string value = node.GetValue<string>();
                return value.Length <= 256 ? "s:" + value : null;
            }
            if (node?.GetValueKind() == JsonValueKind.Number)
            {
                try
                {
                    decimal value = JsonSerializer.Deserialize<decimal>(node.ToJsonString());
                    if (value == decimal.Truncate(value)) return "n:" + value.ToString("G29", CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is JsonException or OverflowException) { }
            }
            return null;
        }

        Task WriteErrorAsync(JsonNode? id, int code, string message, JsonObject? data = null, Pending? request = null)
        {
            var body = new JsonObject { ["code"] = code, ["message"] = message };
            if (data != null) body["data"] = data;
            return WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = body }, request);
        }
        async Task WriteAsync(JsonObject message, Pending? request)
        {
            await writer.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (gate) if (stopping || request?.Cancellation.IsCancellationRequested == true) return;
                await output.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                exitCode = 1;
                lifetime.Cancel();
                await LogAsync("MCP output closed: " + e.Message).ConfigureAwait(false);
            }
            finally { writer.Release(); }
        }
        async Task LogAsync(string message)
        {
            try { await error.WriteLineAsync(message).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    sealed class LineReader(TextReader reader)
    {
        readonly char[] buffer = new char[4096];
        int position, available;
        public async Task<string?> ReadLineAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            bool oversized = false;
            while (true)
            {
                if (position == available)
                {
                    available = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    position = 0;
                    if (available == 0)
                    {
                        if (oversized) throw new InvalidDataException("MCP input line exceeds 1,048,576 characters.");
                        return line.Length == 0 ? null : line.ToString();
                    }
                }
                char value = buffer[position++];
                if (value == '\n')
                {
                    if (oversized) throw new InvalidDataException("MCP input line exceeds 1,048,576 characters.");
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    return line.ToString();
                }
                if (line.Length >= MaximumLineCharacters) oversized = true;
                if (!oversized) line.Append(value);
            }
        }
    }
}
