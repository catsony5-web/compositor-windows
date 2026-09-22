using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

public static class AutomationCommandLine
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "--automation-list" or "--automation-command";

    public static int Run(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return RunCore(args, Console.In, Console.Out, token: cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static int RunCore(string[] args, TextReader input, TextWriter output, string? registryDirectory = null, CancellationToken token = default)
    {
        JsonNode response;
        string? outputPath = null;
        bool success;
        try
        {
            token.ThrowIfCancellationRequested();
            if (args.Length == 1 && args[0] == "--automation-list")
            { response = AutomationBridge.ListSessions(registryDirectory); success = true; }
            else
            {
                if (args.Length < 4 || args[0] != "--automation-command") throw new ArgumentException(Usage);
                string requestPath = args[1];
                string? session = null;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--session" && session == null && i + 1 < args.Length) session = args[++i];
                    else if (args[i] == "--output" && outputPath == null && i + 1 < args.Length) outputPath = args[++i];
                    else throw new ArgumentException(Usage);
                }
                if (!Guid.TryParse(session, out _)) throw new ArgumentException("--session requires a GUID from --automation-list.");
                string json;
                if (requestPath == "-") json = ReadBounded(input, token);
                else
                {
                    if (new FileInfo(requestPath).Length > AutomationBridge.MaximumRequestBytes) throw new ArgumentException("The command file exceeds 1 MiB.");
                    using var reader = new StreamReader(requestPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                    json = ReadBounded(reader, token);
                }
                var request = AutomationBridge.ParseRequest(json);
                var reply = AutomationBridge.SendAsync(session!, request, token).GetAwaiter().GetResult();
                response = reply; success = reply["ok"]?.GetValue<bool>() == true;
            }
        }
        catch (OperationCanceledException)
        { response = AutomationBridge.Error("cancelled", "The command was cancelled."); success = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { response = AutomationBridge.Error("cli_error", e.Message); success = false; }

        string replyText = response.ToJsonString() + Environment.NewLine;
        if (outputPath != null)
        {
            try
            {
                string destination = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                AutomationBridge.AtomicWrite(destination, new UTF8Encoding(false).GetBytes(replyText));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { replyText = AutomationBridge.Error("output_error", "The JSON response could not be written to the requested output file.").ToJsonString() + Environment.NewLine; success = false; }
        }
        output.Write(replyText); output.Flush();
        return success ? 0 : 1;
    }

    static string ReadBounded(TextReader input, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (text.Length + count > AutomationBridge.MaximumRequestBytes) throw new ArgumentException("The command exceeds 1 MiB.");
            text.Append(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        string result = text.ToString();
        if (Encoding.UTF8.GetByteCount(result) > AutomationBridge.MaximumRequestBytes) throw new ArgumentException("The UTF-8 command exceeds 1 MiB.");
        return result;
    }

    const string Usage = "Use --automation-list, or --automation-command <JSON file or -> --session <GUID> [--output <path>].";
}
