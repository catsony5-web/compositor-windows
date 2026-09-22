using System.Text;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Compositor.Windows;

public static class AutomationProtocolTests
{
    const string Session = "11111111-1111-4111-8111-111111111111";
    const string Document = "22222222-2222-4222-8222-222222222222";
    const string Revision = "33333333-3333-4333-8333-333333333333";
    const string Layer = "44444444-4444-4444-8444-444444444444";
    static readonly Func<string, JsonObject, CancellationToken, Task<JsonObject>> EmptySend = (_, _, _) => Task.FromResult(Ok(new JsonObject()));

    public static void Run(Action<string, Action> test)
    {
        test("automation catalog advertises session revision and bounded typed arguments without sharing mutable schemas", () =>
        {
            var tools = AutomationCatalog.Tools();
            Check(tools.Count == 23 && tools.Select(t => t!["name"]!.GetValue<string>()).Distinct().Count() == tools.Count, "Unexpected or duplicate tools.");
            foreach (var tool in tools)
            {
                var schema = tool!["inputSchema"]!.AsObject();
                Check(!schema["additionalProperties"]!.GetValue<bool>(), "Unknown arguments must be rejected.");
                string name = tool["name"]!.GetValue<string>();
                var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                Check(required.Contains("sessionId") == (name != "morupixel_list_sessions"), "Session targeting missing.");
                if (name is not ("morupixel_list_sessions" or "morupixel_get_state" or "morupixel_get_capabilities" or "morupixel_query_layers" or "morupixel_get_layer" or "morupixel_preview" or "morupixel_new_document" or "morupixel_open_document" or "morupixel_activate_document"))
                    Check(required.Contains("documentId") && required.Contains("expectedRevision"), "Mutation must require an explicit document and revision.");
            }
            tools[0]!["name"] = "changed";
            Check(AutomationCatalog.Tools()[0]!["name"]!.GetValue<string>() == "morupixel_list_sessions", "Caller altered shared catalog state.");
            AutomationCatalog.Validate("new_document", new JsonObject { ["name"] = "새 문서", ["width"] = 10, ["height"] = 20 });
        });

        test("automation catalog rejects unknown fields wrong types ranges identifiers and incompatible adjustment parameters", () =>
        {
            void Reject(string command, JsonObject args)
            {
                try { AutomationCatalog.Validate(command, args); }
                catch (ArgumentException) { return; }
                throw new Exception("Expected argument rejection: " + command + " " + args.ToJsonString());
            }
            Reject("not_a_command", new());
            Reject("list_sessions", new JsonObject { ["sessionId"] = Session });
            Reject("new_document", new JsonObject { ["name"] = "x", ["width"] = "10", ["height"] = 10 });
            Reject("new_document", new JsonObject { ["name"] = "x", ["width"] = 1.5, ["height"] = 10 });
            Reject("new_document", new JsonObject { ["name"] = "x", ["width"] = 8192, ["height"] = 8192 });
            Reject("preview", new JsonObject { ["documentId"] = Document, ["maxSide"] = 1025 });
            Reject("preview", new JsonObject { ["documentId"] = "../wrong" });
            Reject("undo", new JsonObject { ["documentId"] = Document });
            var args = Mutation(); args["layerId"] = Layer; args["visible"] = "true"; Reject("set_layer", args);
            args = Mutation(); args["layerId"] = Layer; args["opacity"] = 1.01; Reject("set_layer", args);
            args = Mutation(); args["kind"] = "levels"; args["black"] = 100; args["white"] = 100; Reject("add_adjustment", args);
            args = Mutation(); args["kind"] = "photo_develop"; args["exposure"] = 5.1; Reject("add_adjustment", args);
            args = Mutation(); args["kind"] = "levels"; args["temperature"] = 50; Reject("add_adjustment", args);
            args = Mutation(); args["layerId"] = Layer; args["opacity"] = double.NaN; Reject("set_layer", args);
            args = Mutation(); args["kind"] = "photo_develop"; args["exposure"] = -5; args["temperature"] = 100;
            string before = args.ToJsonString(); AutomationCatalog.Validate("add_adjustment", args);
            Check(args.ToJsonString() == before, "Validation must not rewrite or clamp arguments.");
        });

        test("MCP legacy initialization negotiates supported version and notifications do not produce replies", () =>
        {
            var initialize = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "initialize-id", ["method"] = "initialize",
                ["params"] = new JsonObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "offline-test", ["version"] = "1" } }
            };
            var replies = Exchange([initialize.ToJsonString(), "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unknown\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}"]);
            Check(replies.Count == 3 && replies[0]["id"]!.GetValue<string>() == "initialize-id", "Notification reply or changed request ID.");
            Check(replies[0]["result"]!["protocolVersion"]!.GetValue<string>() == AutomationMcpServer.LegacyVersion, "Unsupported legacy version must negotiate the supported one.");
            Check(replies[1]["result"]!["tools"]!.AsArray().Count == 23 && replies[2]["result"]!.AsObject().Count == 0, "Legacy tool list/ping failed.");
        });

        test("MCP modern discovery requires request metadata reports versions and includes complete cache metadata", () =>
        {
            var badVersion = Request(3, "tools/list"); badVersion["params"]!["_meta"]!["io.modelcontextprotocol/protocolVersion"] = "1900-01-01";
            var missing = Request(4, "tools/list"); missing["params"]!["_meta"]!.AsObject().Remove("io.modelcontextprotocol/clientCapabilities");
            var replies = Exchange([Request(1, "server/discover").ToJsonString(), Request(2, "tools/list").ToJsonString(), badVersion.ToJsonString(), missing.ToJsonString(), Request(5, "ping").ToJsonString()]);
            foreach (var reply in replies.Take(2))
            {
                var result = reply["result"]!;
                Check(result["resultType"]!.GetValue<string>() == "complete" && result["ttlMs"]!.GetValue<int>() == 0 &&
                    result["cacheScope"]!.GetValue<string>() == "public" && result["_meta"]!["io.modelcontextprotocol/serverInfo"] != null, "Modern metadata missing.");
            }
            Check(replies[0]["result"]!["supportedVersions"]!.AsArray().Any(v => v!.GetValue<string>() == AutomationMcpServer.ModernVersion), "Discovery missing current version.");
            Check(ErrorCode(replies[2]) == -32022 && ErrorCode(replies[3]) == -32602, "Wrong version/metadata errors.");
            Check(replies[4]["result"]!["resultType"]!.GetValue<string>() == "complete", "Modern ping did not return a complete result.");
        });

        test("MCP tool calls validate and target the bridge without leaking sessionId into editor arguments", () =>
        {
            int calls = 0;
            var args = Mutation(); args["sessionId"] = Session;
            var replies = Exchange([Tool(1, "undo", args).ToJsonString(), Tool(2, "set_layer", new JsonObject { ["sessionId"] = Session }).ToJsonString()],
                (session, command, _) =>
                {
                    calls++;
                    Check(session == Session && command["command"]!.GetValue<string>() == "undo", "Bridge targeted wrong operation.");
                    Check(command["arguments"]!["sessionId"] == null && command["arguments"]!["expectedRevision"]!.GetValue<string>() == Revision, "Session/revision routing corrupted.");
                    return Task.FromResult(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "stale_revision", ["message"] = "Inspect the current revision." } });
                });
            Check(calls == 1 && args.ContainsKey("sessionId"), "Validation forwarded invalid arguments or altered caller input.");
            Check(replies.All(r => r["result"]!["isError"]!.GetValue<bool>()), "Tool failures must be actionable tool results.");
            Check(replies[0]["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>() == "stale_revision", "Bridge error code lost.");
        });

        test("MCP atomic batch advertises strict nested schemas and forwards one validated plan", () =>
        {
            var schema = AutomationCatalog.Tools().Single(t => t!["name"]!.GetValue<string>() == "morupixel_apply_batch")!["inputSchema"]!;
            var stepsSchema = schema["properties"]!["steps"]!;
            Check(stepsSchema["maxItems"]!.GetValue<int>() == 64 && stepsSchema["items"]!["oneOf"]!.AsArray().Count == 7,
                "Atomic edits must advertise their supported typed steps");
            var args = Mutation(); args["sessionId"] = Session; args["operationId"] = "55555555-5555-4555-8555-555555555555";
            args["dryRun"] = true;
            args["steps"] = new JsonArray(new JsonObject { ["command"] = "set_layer", ["arguments"] = new JsonObject { ["layerId"] = Layer, ["x"] = 20 } });
            int calls = 0;
            var replies = Exchange([Tool(1, "apply_batch", args).ToJsonString()], (session, command, _) =>
            {
                calls++;
                Check(session == Session && command["command"]!.GetValue<string>() == "apply_batch" &&
                    command["arguments"]!["steps"]![0]!["arguments"]!["x"]!.GetValue<int>() == 20,
                    "MCP expanded, dropped or rerouted the atomic request");
                return Task.FromResult(Ok(new JsonObject { ["validated"] = true, ["committed"] = false }));
            });
            Check(calls == 1 && !replies[0]["result"]!["isError"]!.GetValue<bool>() &&
                replies[0]["result"]!["structuredContent"]!["validated"]!.GetValue<bool>(), "Batch result was not structured");
            var invalid = (JsonObject)args.DeepClone(); invalid["steps"]![0]!["arguments"]!["sessionId"] = Session;
            replies = Exchange([Tool(2, "apply_batch", invalid).ToJsonString()]);
            Check(replies[0]["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>() == "invalid_arguments" &&
                replies[0]["result"]!["structuredContent"]!["error"]!["suggestedAction"] != null, "Invalid nested arguments lacked schema recovery guidance");
        });

        test("MCP preview returns an image block without duplicating base64 in text or structured output", () =>
        {
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX6kAAAAASUVORK5CYII=";
            var args = new JsonObject { ["sessionId"] = Session, ["documentId"] = Document };
            var replies = Exchange([Tool(1, "preview", args).ToJsonString()], (_, _, _) => Task.FromResult(Ok(new JsonObject
            {
                ["documentId"] = Document, ["revision"] = Revision, ["width"] = 1, ["height"] = 1, ["pngBase64"] = png
            })));
            var result = replies[0]["result"]!;
            var content = result["content"]!.AsArray();
            Check(content.Count == 2 && content[1]!["type"]!.GetValue<string>() == "image" && content[1]!["data"]!.GetValue<string>() == png &&
                content[1]!["mimeType"]!.GetValue<string>() == "image/png", "Preview image block is invalid.");
            Check(result["structuredContent"]!["pngBase64"] == null && !content[0]!["text"]!.GetValue<string>().Contains(png), "Base64 duplicated in model text/structured metadata.");
            var malformed = Exchange([Tool(2, "preview", args).ToJsonString()], (_, _, _) => Task.FromResult(Ok(new JsonObject { ["pngBase64"] = "not PNG" })));
            Check(malformed[0]["result"]!["isError"]!.GetValue<bool>(), "Invalid preview bytes were accepted.");
        });

        test("MCP protocol errors and oversized input recover without executing editor commands", () =>
        {
            int calls = 0;
            var unknown = Request(3, "tools/call", new JsonObject { ["name"] = "morupixel_shell", ["arguments"] = new JsonObject() });
            var replies = Exchange(["{broken", "[]", Request(2, "not/a/method").ToJsonString(), unknown.ToJsonString(),
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"id\":5,\"method\":\"ping\"}",
                new string('x', AutomationMcpServer.MaximumLineCharacters + 1), Tool(6, "list_sessions", new JsonObject()).ToJsonString()],
                (_, _, _) => { calls++; return Task.FromResult(Ok(new JsonObject())); });
            Check(replies.Count == 7 && calls == 0, "Invalid protocol executed a bridge call or prevented recovery.");
            Check(ErrorCode(replies[0]) == -32700 && ErrorCode(replies[1]) == -32600 && ErrorCode(replies[2]) == -32601 &&
                ErrorCode(replies[3]) == -32602 && ErrorCode(replies[4]) == -32700 && ErrorCode(replies[5]) == -32600, "Wrong protocol error codes.");
            Check(replies[6]["result"]!["structuredContent"]!["sessions"]!.AsArray().Count == 1, "Session discovery failed after bad input.");
        });

        test("MCP processes cancellation during a pending tool call and ignores notification responses", () =>
            CancellationCaseAsync().GetAwaiter().GetResult());
        test("MCP stdin EOF cancels pending bridge work and exits without opening a window", () =>
            EofCaseAsync().GetAwaiter().GetResult());
    }

    static async Task CancellationCaseAsync()
    {
        using var input = new QueueReader();
        var output = new ReplyWriter();
        using var error = new StringWriter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = AutomationMcpServer.RunAsync(input, output, error, async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Ok(new JsonObject());
        }, Sessions);
        input.Send(Tool(10, "get_state", new JsonObject { ["sessionId"] = Session }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Send(Tool(10, "get_state", new JsonObject { ["sessionId"] = Session }));
        Check(ErrorCode(await output.NextAsync()) == -32600, "Duplicate in-flight request ID was accepted.");
        input.Send(Tool(11, "list_sessions", new JsonObject()));
        Check((await output.NextAsync())["id"]!.GetValue<int>() == 11, "Reader blocked while tool was running.");
        input.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = 10 } });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Send(Tool(12, "list_sessions", new JsonObject()));
        Check((await output.NextAsync())["id"]!.GetValue<int>() == 12, "Cancellation or canceled request emitted a response.");
        input.Complete();
        Check(await run.WaitAsync(TimeSpan.FromSeconds(5)) == 0 && output.Count == 3, "Cancellation did not finish cleanly.");
    }

    static async Task EofCaseAsync()
    {
        using var input = new QueueReader();
        var output = new ReplyWriter();
        using var error = new StringWriter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = AutomationMcpServer.RunAsync(input, output, error, async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Ok(new JsonObject());
        }, Sessions);
        input.Send(Tool(1, "get_state", new JsonObject { ["sessionId"] = Session }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Complete();
        Check(await run.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "EOF should be a clean shutdown.");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(output.Count == 0, "EOF-canceled request sent output after shutdown.");
    }

    static List<JsonObject> Exchange(string[] lines, Func<string, JsonObject, CancellationToken, Task<JsonObject>>? send = null)
    {
        using var input = new StringReader(string.Join('\n', lines) + "\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = AutomationMcpServer.RunAsync(input, output, error, send ?? EmptySend, Sessions).GetAwaiter().GetResult();
        Check(code == 0 && error.ToString().Length == 0, "Unexpected adapter error: " + error);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }
    static JsonArray Sessions() => new(new JsonObject { ["sessionId"] = Session });
    static JsonObject Ok(JsonObject result) => new() { ["ok"] = true, ["result"] = result };
    static JsonObject Mutation() => new() { ["documentId"] = Document, ["expectedRevision"] = Revision };
    static JsonObject Request(int id, string method, JsonObject? parameters = null)
    {
        parameters ??= new();
        parameters["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = AutomationMcpServer.ModernVersion,
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
        };
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
    }
    static JsonObject Tool(int id, string command, JsonObject args) => Request(id, "tools/call", new JsonObject { ["name"] = "morupixel_" + command, ["arguments"] = args.DeepClone() });
    static int ErrorCode(JsonObject reply) => reply["error"]!["code"]!.GetValue<int>();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    sealed class QueueReader : TextReader
    {
        readonly Channel<string> queue = Channel.CreateUnbounded<string>();
        string current = "";
        int offset;
        public void Send(JsonObject message) => queue.Writer.TryWrite(message.ToJsonString() + "\n");
        public void Complete() => queue.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (offset == current.Length)
            {
                try { current = await queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (ChannelClosedException) { return 0; }
                offset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count;
            return count;
        }
    }
    sealed class ReplyWriter : TextWriter
    {
        readonly Channel<JsonObject> replies = Channel.CreateUnbounded<JsonObject>();
        public override Encoding Encoding => Encoding.UTF8;
        public int Count { get; private set; }
        public override Task WriteLineAsync(string? value)
        {
            Count++;
            replies.Writer.TryWrite(JsonNode.Parse(value!)!.AsObject());
            return Task.CompletedTask;
        }
        public Task<JsonObject> NextAsync() => replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
