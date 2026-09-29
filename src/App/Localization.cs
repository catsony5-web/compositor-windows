using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Compositor.Windows;

// Display-language layer. Source strings stay Korean in code; text is translated as it
// reaches the screen (TextBlocks, window titles, tooltips and accessible names) from an
// embedded table keyed by the Korean text. Keys with {0} placeholders match dynamic
// messages; long fragments cover strings built by concatenation. User data (layer and
// file names) is marked with Loc.Keep and never translated.
public static class Loc
{
    public static readonly (string Code, string Native)[] Languages = [("ko", "한국어"), ("en", "English"), ("ja", "日本語"), ("zh", "简体中文")];
    public static string Language { get; private set; } = "ko";
    public static bool Active => Language != "ko" && exact.Count > 0;
    static Dictionary<string, string> exact = new(StringComparer.Ordinal);
    static (Regex Pattern, string Template, int Holes)[] patterns = [];
    static Regex? fragmentPattern;
    static Dictionary<string, string> fragments = new(StringComparer.Ordinal);
    static Dictionary<string, string> templates = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);
    const int MinFragment = 4, MaxCache = 50_000;

    public static string SettingPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "language.txt");

    // Saved choice first, then the Windows display language (Korean, Japanese and Chinese map to
    // themselves, anything else to English).
    public static string Preferred()
    {
        string? env = Environment.GetEnvironmentVariable("MORUPIXEL_LANGUAGE");
        if (IsKnown(env)) return env!;
        try { if (File.Exists(SettingPath) && File.ReadAllText(SettingPath).Trim() is var saved && IsKnown(saved)) return saved; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch { "ko" => "ko", "ja" => "ja", "zh" => "zh", _ => "en" };
    }

    public static bool IsKnown(string? code) => code != null && Languages.Any(l => l.Code == code);

    public static void SavePreference(string code)
    {
        if (!IsKnown(code)) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!); File.WriteAllText(SettingPath, code); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // UI font: Segoe UI with the script's own Windows UI font as fallback.
    public static string FontFamilyName => Language switch
    {
        "ja" => "Segoe UI, Yu Gothic UI, Meiryo UI, Malgun Gothic",
        "zh" => "Segoe UI, Microsoft YaHei UI, Malgun Gothic",
        _ => "Segoe UI, Malgun Gothic"
    };

    public static void Use(string code) => Use(code, code == "ko" ? null : Embedded(code));

    internal static void Use(string code, IReadOnlyDictionary<string, string>? table)
    {
        Language = IsKnown(code) ? code : "ko"; cache.Clear();
        exact = new(StringComparer.Ordinal); fragments = new(StringComparer.Ordinal); templates = new(StringComparer.Ordinal);
        var dynamic = new List<(Regex Pattern, string Template, int Holes, int Literal)>();
        foreach (var (key, value) in table ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;
            if (Regex.IsMatch(key, @"\{\d+\}"))
            {
                int holes = 0;
                string pattern = "^" + Regex.Replace(Regex.Escape(key).Replace(@"\{", "{"), @"\{(\d+)}", m => { holes = Math.Max(holes, int.Parse(m.Groups[1].Value) + 1); return $"(?<h{m.Groups[1].Value}>.*?)"; }) + "$";
                dynamic.Add((new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant), value, holes, Regex.Replace(key, @"\{\d+\}", "").Length));
                templates[key] = value;
            }
            else
            {
                exact[key] = value;
                if (key.Length >= MinFragment) fragments[key] = value;
            }
        }
        // More specific templates (more literal text) win. Counting the pattern's own length let
        // every extra hole outweigh real text, so "{0} {1}개" beat "레이어 {0}개".
        patterns = dynamic.OrderByDescending(p => p.Literal).ThenBy(p => p.Holes).Select(p => (p.Pattern, p.Template, p.Holes)).ToArray();
        fragmentPattern = fragments.Count == 0 ? null
            : new Regex(string.Join("|", fragments.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)), RegexOptions.CultureInvariant);
    }

    static Dictionary<string, string>? Embedded(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Morupixel.i18n.{code}.json");
        if (stream == null) return null;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    static bool HasHangul(string text) { foreach (char c in text) if (c is >= '가' and <= '힣') return true; return false; }

    public static string T(string text)
    {
        if (!Active || string.IsNullOrEmpty(text) || !HasHangul(text)) return text;
        if (cache.TryGetValue(text, out var hit)) return hit;
        string result = Translate(text);
        if (cache.Count < MaxCache) cache[text] = result;
        return result;
    }

    static string Translate(string text)
    {
        if (exact.TryGetValue(text, out var direct)) return direct;
        string trimmed = text.Trim();
        if (trimmed.Length != text.Length && exact.TryGetValue(trimmed, out var inner)) return text.Replace(trimmed, inner);
        foreach (var (pattern, template, holes) in patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            string output = template;
            for (int i = 0; i < holes; i++) output = output.Replace("{" + i + "}", T(match.Groups["h" + i].Value));
            return output;
        }
        // Multi-line notices translate line by line; concatenated messages by known fragments.
        if (text.Contains('\n')) return string.Join("\n", text.Split('\n').Select(T));
        // " · " joins independent parts (kind · opacity, name · shortcut); translate each part.
        if (text.Contains(" · ")) return string.Join(" · ", text.Split(" · ").Select(T));
        return fragmentPattern == null ? text : fragmentPattern.Replace(text, m => fragments[m.Value]);
    }

    // A sentence whose holes carry user data (file or layer names): the template is translated
    // as a whole and the values are inserted as they are, so a name is never partly translated.
    public static string Format(string template, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Active && templates.TryGetValue(template, out var translated) ? translated : template, values);

    // The same for a TextBlock that the display layer translates: the translated template becomes
    // plain runs and every value its own kept run, so later translation passes leave the values alone.
    public static void SetText(TextBlock block, string template, params object?[] values)
    {
        string text = Active && templates.TryGetValue(template, out var translated) ? translated : template;
        block.Inlines.Clear();
        int last = 0;
        foreach (Match hole in Regex.Matches(text, @"\{(\d+)\}"))
        {
            if (hole.Index > last) block.Inlines.Add(new Run(text[last..hole.Index]));
            int index = int.Parse(hole.Groups[1].Value, CultureInfo.InvariantCulture);
            block.Inlines.Add(Keep(new Run(index < values.Length ? Convert.ToString(values[index], CultureInfo.CurrentCulture) ?? "" : hole.Value)));
            last = hole.Index + hole.Length;
        }
        if (last < text.Length) block.Inlines.Add(new Run(text[last..]));
    }

    // ---- Applying translations to WPF elements ------------------------------------------

    public static readonly DependencyProperty KeepProperty = DependencyProperty.RegisterAttached("Keep", typeof(bool), typeof(Loc), new PropertyMetadata(false));
    public static T Keep<T>(T element) where T : DependencyObject { element.SetValue(KeepProperty, true); return element; }
    static bool Kept(DependencyObject element) => (bool)element.GetValue(KeepProperty);

    static bool attached;
    static readonly DependencyPropertyDescriptor textDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static void Attach()
    {
        if (attached || !Active) return;
        attached = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler((sender, _) =>
        { if (sender is TextBlock block) textDescriptor.RemoveValueChanged(block, OnTextChanged); }));
    }

    internal static void Apply(FrameworkElement element)
    {
        if (!Active || Kept(element) || element.TemplatedParent is FrameworkElement { } parent && Kept(parent)) return;
        switch (element)
        {
            case TextBlock block:
                TranslateBlock(block);
                textDescriptor.RemoveValueChanged(block, OnTextChanged); textDescriptor.AddValueChanged(block, OnTextChanged);
                break;
            case Window window:
                window.Title = T(window.Title);
                break;
        }
        if (element.ToolTip is string tip) element.ToolTip = T(tip);
        if (AutomationProperties.GetName(element) is { Length: > 0 } name) AutomationProperties.SetName(element, T(name));
    }

    // Offscreen captures never raise Loaded; translate the whole tree, then lay it out again.
    public static void PrepareOffscreen(FrameworkElement root)
    {
        if (!Active) return;
        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement element) { if (Kept(element)) return; Apply(element); }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
            if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
        Walk(root); root.UpdateLayout();
    }

    static bool translating;
    static void OnTextChanged(object? sender, EventArgs e) { if (!translating && sender is TextBlock block && !Kept(block)) TranslateBlock(block); }

    static void TranslateBlock(TextBlock block)
    {
        translating = true;
        try
        {
            // Runs keep their own formatting; a plain TextBlock keeps any binding through SetCurrentValue.
            // A block built from Inlines reports an empty Text, even with a single run, so the runs
            // are translated one by one; runs marked Keep hold user data and stay as they are.
            var first = block.Inlines.FirstInline;
            if (block.Inlines.Count > 1 || first is not Run and not null || first is Run single && single.Text != block.Text)
            {
                foreach (var run in block.Inlines.OfType<Run>().ToArray()) { if (Kept(run)) continue; string next = T(run.Text); if (next != run.Text) run.Text = next; }
                return;
            }
            string text = block.Text, translated = T(text);
            if (!ReferenceEquals(text, translated) && text != translated) block.SetCurrentValue(TextBlock.TextProperty, translated);
        }
        finally { translating = false; }
    }
}
