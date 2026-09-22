using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

public static class AutomationTransportTests
{
    public static void Run(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string Registry() => Path.Combine(directory, "automation-transport-" + Guid.NewGuid().ToString("N"));
        static JsonObject Request(string name = "status") => new() { ["command"] = name, ["arguments"] = new JsonObject() };
        static JsonObject Success(JsonObject? result = null) => new() { ["ok"] = true, ["result"] = result ?? new JsonObject() };
        static string? Code(JsonObject reply) => reply["error"]?["code"]?.GetValue<string>();
        static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        static T AwaitValue<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();

        test("automation transport stays disabled until explicit start and unregisters only itself", () =>
        {
            string registry = Registry();
            using var first = new AutomationBridge((_, _) => Task.FromResult(Success()), registry);
            Check(!first.IsRunning && first.SessionId == "" && !Directory.Exists(registry), "Construction created external automation state");
            Check(AutomationBridge.ListSessions(registry).Count == 0 && !Directory.Exists(registry), "Listing created a registry");
            first.Start(); first.Start();
            using var second = new AutomationBridge((_, _) => Task.FromResult(Success()), registry); second.Start();
            Check(Guid.TryParse(first.SessionId, out _) && first.SessionId != second.SessionId, "Sessions need distinct GUIDs");
            Check(AutomationBridge.ListSessions(registry).Count == 2, "Concurrent sessions are not discoverable");
            first.Stop(); first.Stop();
            var remaining = AutomationBridge.ListSessions(registry);
            Check(!first.IsRunning && remaining.Count == 1 && remaining[0]!["sessionId"]!.GetValue<string>() == second.SessionId,
                "Stopping one bridge removed another session");
            Check(!File.Exists(Path.Combine(registry, first.SessionId + ".json")), "Stopped registry file remains");
        });

        test("automation real pipe preserves Unicode responses and correlates unique requests without mutating input", () =>
        {
            int calls = 0;
            using var bridge = new AutomationBridge((request, token) =>
            {
                token.ThrowIfCancellationRequested(); Interlocked.Increment(ref calls);
                return Task.FromResult(Success(new JsonObject { ["echo"] = request["arguments"]!.DeepClone() }));
            }, Registry());
            bridge.Start();
            var request = Request(); request["arguments"]!["name"] = "바다 · 편집\n문서";
            var first = AwaitValue(AutomationBridge.SendAsync(bridge.SessionId, request, CancellationToken.None));
            var second = AwaitValue(AutomationBridge.SendAsync(bridge.SessionId, request, CancellationToken.None));
            Check(first["ok"]!.GetValue<bool>() && first["result"]!["echo"]!["name"]!.GetValue<string>() == "바다 · 편집\n문서", "Unicode payload changed");
            Check(first["requestId"]!.GetValue<string>() != second["requestId"]!.GetValue<string>() && !request.ContainsKey("requestId"),
                "Request IDs were reused or caller request was mutated");
            Check(calls == 2, "Transport retried or duplicated a command");
        });

        test("automation one idle connection cannot starve another client", () =>
        {
            using var bridge = new AutomationBridge((_, _) => Task.FromResult(Success()), Registry()); bridge.Start();
            using var idle = new NamedPipeClientStream(".", AutomationBridge.PipeName(bridge.SessionId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Await(idle.ConnectAsync(limit.Token));
            var reply = AwaitValue(AutomationBridge.SendAsync(bridge.SessionId, Request(), limit.Token));
            Check(reply["ok"]!.GetValue<bool>(), "An idle client blocked all command listeners");
        });

        test("automation malformed oversized and invalid UTF-8 frames cannot execute commands", () =>
        {
            int calls = 0;
            using var bridge = new AutomationBridge((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Success()); }, Registry()); bridge.Start();
            foreach (byte[] bytes in new[]
            {
                Encoding.UTF8.GetBytes("{\n"), Encoding.UTF8.GetBytes("[]\n"),
                Encoding.UTF8.GetBytes("{\"command\":\"status\",\"arguments\":[]}\n"),
                new byte[] { 255, 10 },
                Encoding.UTF8.GetBytes(new string('x', AutomationBridge.MaximumRequestBytes + 1) + "\n")
            })
            {
                var response = AwaitValue(RawAsync(bridge.SessionId, bytes));
                Check(response["ok"]?.GetValue<bool>() == false, "Malformed frame was accepted");
            }
            Check(calls == 0, "Malformed input reached the editor");
            Check(AwaitValue(AutomationBridge.SendAsync(bridge.SessionId, Request(), CancellationToken.None))["ok"]!.GetValue<bool>(),
                "Bad input permanently stopped the server");
            Check(calls == 1, "Malformed input was later retried");
        });

        test("automation bounds both directions and rejects nonexistent or invalid sessions without retry", () =>
        {
            Check(Code(AwaitValue(AutomationBridge.SendAsync("not-a-session", Request(), CancellationToken.None))) == "invalid_session", "Invalid GUID was accepted");
            var absent = AwaitValue(AutomationBridge.SendCoreAsync(Guid.NewGuid().ToString("D"), Request(), CancellationToken.None,
                TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(1)));
            Check(Code(absent) == "not_connected", "Missing session needs a clear not-connected response");
            var request = Request(); request["arguments"]!["text"] = new string('x', AutomationBridge.MaximumRequestBytes);
            Check(Code(AwaitValue(AutomationBridge.SendAsync(Guid.NewGuid().ToString("D"), request, CancellationToken.None))) == "message_too_large",
                "Oversized request attempted a connection");
            using var bridge = new AutomationBridge((_, _) => Task.FromResult(Success(new JsonObject
                { ["text"] = new string('x', AutomationBridge.MaximumResponseBytes) })), Registry()); bridge.Start();
            var huge = AwaitValue(AutomationBridge.SendAsync(bridge.SessionId, Request(), CancellationToken.None));
            Check(Code(huge) == "response_too_large", "Oversized server result escaped the response limit");
        });

        test("automation stopping cancels both active and queued handler work", () =>
        {
            int entered = 0, cancelled = 0, committed = 0;
            var bothEntered = Signal(); var bothCancelled = Signal();
            using var serial = new SemaphoreSlim(1, 1);
            using var bridge = new AutomationBridge(async (_, token) =>
            {
                if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult(true);
                try
                {
                    await serial.WaitAsync(token).ConfigureAwait(false);
                    try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); Interlocked.Increment(ref committed); }
                    finally { serial.Release(); }
                    return Success();
                }
                catch (OperationCanceledException)
                { if (Interlocked.Increment(ref cancelled) == 2) bothCancelled.TrySetResult(true); throw; }
            }, Registry());
            bridge.Start();
            var first = AutomationBridge.SendAsync(bridge.SessionId, Request(), CancellationToken.None);
            var second = AutomationBridge.SendAsync(bridge.SessionId, Request(), CancellationToken.None);
            Await(bothEntered.Task); bridge.Stop(); Await(bothCancelled.Task);
            Check(!AwaitValue(first)["ok"]!.GetValue<bool>() && !AwaitValue(second)["ok"]!.GetValue<bool>() && committed == 0,
                "Stopping did not cancel active/queued mutations");
        });

        test("automation client cancellation disconnects and cancels the editor token", () =>
        {
            var entered = Signal(); var observed = Signal(); int calls = 0;
            using var bridge = new AutomationBridge(async (_, token) =>
            {
                Interlocked.Increment(ref calls); entered.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return Success(); }
                finally { if (token.IsCancellationRequested) observed.TrySetResult(true); }
            }, Registry()); bridge.Start();
            using var cancel = new CancellationTokenSource();
            var pending = AutomationBridge.SendAsync(bridge.SessionId, Request(), cancel.Token);
            Await(entered.Task); cancel.Cancel();
            Check(Code(AwaitValue(pending)) == "cancelled", "Client cancellation was not reported");
            Await(observed.Task); Check(calls == 1, "Cancelled command was retried");
        });

        test("automation discovery excludes stale malformed and extra private metadata", () =>
        {
            string registry = Registry();
            using var bridge = new AutomationBridge((_, _) => Task.FromResult(Success()), registry); bridge.Start();
            string metadataPath = Path.Combine(registry, bridge.SessionId + ".json");
            var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
            metadata["privatePath"] = "DO_NOT_PUBLISH"; File.WriteAllText(metadataPath, metadata.ToJsonString());
            var stale = (JsonObject)metadata.DeepClone(); string staleId = Guid.NewGuid().ToString("D");
            stale["sessionId"] = staleId; stale["processStartTimeUtcTicks"] = 1;
            string stalePath = Path.Combine(registry, staleId + ".json"); File.WriteAllText(stalePath, stale.ToJsonString());
            File.WriteAllText(Path.Combine(registry, Guid.NewGuid().ToString("D") + ".json"), "not-json");
            File.WriteAllText(Path.Combine(registry, Guid.NewGuid().ToString("D") + ".json"), "{}");
            var sessions = AutomationBridge.ListSessions(registry);
            Check(sessions.Count == 1 && !sessions.ToJsonString().Contains("DO_NOT_PUBLISH"), "Discovery leaked metadata or reported a stale PID generation");
            bridge.Stop(); Check(File.Exists(stalePath), "Stop pruned metadata belonging to another instance");
        });

        test("automation CLI prints JSON and honors explicit session input and output paths", () =>
        {
            string registry = Registry();
            using var bridge = new AutomationBridge((_, _) => Task.FromResult(Success(new JsonObject { ["document"] = "테스트" })), registry); bridge.Start();
            using var listOut = new StringWriter();
            Check(AutomationCommandLine.RunCore(["--automation-list"], new StringReader(""), listOut, registry) == 0
                && JsonNode.Parse(listOut.ToString())!.AsArray().Count == 1, "List CLI did not return sessions JSON");
            string outputPath = Path.Combine(registry, "reply.json"); using var commandOut = new StringWriter();
            int code = AutomationCommandLine.RunCore(["--automation-command", "-", "--session", bridge.SessionId, "--output", outputPath],
                new StringReader(Request().ToJsonString()), commandOut, registry);
            Check(code == 0 && File.ReadAllText(outputPath) == commandOut.ToString()
                && JsonNode.Parse(commandOut.ToString())!["result"]!["document"]!.GetValue<string>() == "테스트", "CLI response/output mismatch");
            using var invalidOut = new StringWriter();
            Check(AutomationCommandLine.RunCore(["--automation-command", "-", "--session", bridge.SessionId],
                new StringReader("{broken"), invalidOut, registry) == 1 && JsonNode.Parse(invalidOut.ToString())!["ok"]!.GetValue<bool>() == false,
                "Invalid CLI input did not return a JSON error and exit 1");
            Check(AutomationCommandLine.Handles(["--automation-list"]) && !AutomationCommandLine.Handles(["--mcp"])
                && !AutomationCommandLine.Handles([]), "CLI steals another startup mode");
        });
    }

    static async Task<JsonObject> RawAsync(string sessionId, byte[] bytes)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var pipe = new NamedPipeClientStream(".", AutomationBridge.PipeName(sessionId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
        await pipe.WriteAsync(bytes, limit.Token).ConfigureAwait(false);
        await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
        string text = await AutomationBridge.ReadLineAsync(pipe, AutomationBridge.MaximumResponseBytes, limit.Token).ConfigureAwait(false);
        return JsonNode.Parse(text)!.AsObject();
    }
}
