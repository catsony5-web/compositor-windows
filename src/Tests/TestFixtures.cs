using System.IO;

namespace Compositor.Windows;

// External sample files (tools/qa/compatibility/fixtures) are test data, not part of the editor,
// so they stay out of Morupixel.dll and the release folder. Self-tests read them from
// MORUPIXEL_TEST_FIXTURES (Publish.ps1 sets it for the packaged run, so CI keeps full coverage)
// or from the source checkout the executable was built in. An installed release has neither:
// only the tests that need these files are then reported as skipped, never as passed.
internal static class TestFixtures
{
    internal const string Variable = "MORUPIXEL_TEST_FIXTURES";
    static readonly Lazy<string?> root = new(Locate);

    internal static string? Root => root.Value;

    static string? Locate()
    {
        string? configured = Environment.GetEnvironmentVariable(Variable);
        // An explicit folder is a promise: a missing file there fails the test instead of skipping it.
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        // Source builds run from src/bin/<configuration>/<framework>/<runtime>/ below the checkout.
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; folder != null && depth < 8; depth++, folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "tools", "qa", "compatibility", "fixtures");
            if (File.Exists(Path.Combine(candidate, "sources.json"))) return candidate;
        }
        return null;
    }

    // Copies one fixture to a test-owned path and returns that path.
    internal static string CopyTo(string name, string destination)
    {
        string folder = Root ?? throw new SkippedTestException($"needs the external test file {name}, which is not shipped with the release");
        string source = Path.Combine(folder, name);
        if (!File.Exists(source)) throw new FileNotFoundException("Missing test fixture " + name, source);
        File.Copy(source, destination, true);
        return destination;
    }
}

// Thrown by a self-test that cannot run in this installation; SelfTests reports it as SKIP.
internal sealed class SkippedTestException(string reason) : Exception(reason);
