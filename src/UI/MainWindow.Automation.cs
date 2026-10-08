using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    AutomationBridge? automationBridge;
    readonly SemaphoreSlim automationGate = new(1, 1);
    MenuItem? automationToggle;
    sealed class AutomationFault(string code, string message, JsonObject? details = null) : Exception(message)
    {
        public string Code { get; } = code;
        public JsonObject? Details { get; } = details;
    }

    internal string EnableAutomation(bool headless = false)
    {
        if (headless) headlessTesting = true;
        if (automationBridge?.IsRunning != true)
        {
            automationBridge?.Dispose();
            automationBridge = new AutomationBridge(ExecuteAutomationAsync);
            automationBridge.Start();
        }
        if (automationToggle != null) automationToggle.IsChecked = true;
        return automationBridge.SessionId;
    }

    internal void DisableAutomation()
    {
        automationBridge?.Dispose(); automationBridge = null;
        if (automationToggle != null) automationToggle.IsChecked = false;
    }

    MenuItem BuildAutomationMenu()
    {
        var menu = new MenuItem { Header = "AI 연결" };
        automationToggle = new MenuItem { Header = "로컬 연결 켜기", IsCheckable = true };
        automationToggle.Click += (_, _) => Guard(() =>
        {
            try { if (automationToggle.IsChecked) EnableAutomation(); else DisableAutomation(); }
            finally { automationToggle.IsChecked = automationBridge?.IsRunning == true; }
        });
        menu.Items.Add(automationToggle);
        var settings = new MenuItem { Header = "연결 설정…" };
        settings.Click += (_, _) => ShowAutomationSettings(); menu.Items.Add(settings);
        Closed += (_, _) => DisableAutomation();
        return menu;
    }

    void ShowAutomationSettings() => CreateAutomationSettingsDialog().ShowDialog();

    internal Window CreateAutomationSettingsDialog()
    {
        var dialog = new Window { Width = 620, MinWidth = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        DialogShell.Prepare(dialog, IsLoaded ? this : null, "AI 연결");
        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 22) }; dialog.Content = root;
        root.Children.Add(DialogShell.Title("AI로 편집하기"));
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(1, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var state = Theme.Label("", Theme.BodySize); state.FontWeight = FontWeights.SemiBold; state.Margin = new Thickness(0);
        void UpdateState(string? message = null)
        {
            bool running = automationBridge?.IsRunning == true;
            dot.Fill = running ? Theme.Success : Theme.Subtle;
            state.Text = message ?? (running ? "연결 켜짐" : "연결 꺼짐");
        }
        stateRow.Children.Add(dot); stateRow.Children.Add(state); root.Children.Add(stateRow);
        var detail = Theme.Label("같은 Windows 계정의 로컬 도구가 문서와 파일을 편집합니다. 연결을 끄면 새 명령을 받지 않습니다.", Theme.BodySize, Theme.Muted);
        detail.Margin = new Thickness(0); root.Children.Add(detail);
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Morupixel.exe");
        var configuration = new JsonObject { ["mcpServers"] = new JsonObject { ["morupixel"] = new JsonObject
            { ["command"] = executable, ["args"] = new JsonArray("--mcp") } } }.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        root.Children.Add(DialogShell.FieldLabel("연결할 AI 프로그램"));
        var client = new ComboBox { ItemsSource = new[] { "공통 MCP 설정", "Codex · PowerShell 명령", "Claude Code · PowerShell 명령" }, SelectedIndex = 0, Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(client, "연결할 AI 프로그램");
        root.Children.Add(client);
        root.Children.Add(DialogShell.FieldLabel("MCP 서버 설정"));
        var config = new TextBox { Text = configuration, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = Theme.CaptionSize,
            MinHeight = 150, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(config, "MCP 서버 설정");
        root.Children.Add(config);
        client.SelectionChanged += (_, _) => config.Text = client.SelectedIndex switch
        {
            1 => "codex mcp add morupixel -- " + "'" + executable.Replace("'", "''") + "' --mcp",
            2 => "claude mcp add --transport stdio --scope user morupixel -- " + "'" + executable.Replace("'", "''") + "' --mcp",
            _ => configuration
        };
        var copy = DialogShell.Secondary("설정 복사", () => { Clipboard.SetText(config.Text); UpdateState("선택한 AI 연결 설정을 복사했습니다."); });
        var toggle = DialogShell.Primary(automationBridge?.IsRunning == true ? "연결 끄기" : "연결 켜기", () => { });
        toggle.Click += (_, _) => Guard(() =>
        {
            if (automationBridge?.IsRunning == true) DisableAutomation(); else EnableAutomation();
            toggle.Content = automationBridge?.IsRunning == true ? "연결 끄기" : "연결 켜기";
            UpdateState();
        });
        root.Children.Add(DialogShell.FieldLabel("요청 예시"));
        var examples = new StackPanel();
        foreach (string example in AutomationExamplePrompts)
        {
            var line = Theme.Label("· " + example, Theme.BodySize, Theme.Text); line.TextWrapping = TextWrapping.Wrap; line.Margin = new Thickness(0, 0, 0, 3);
            examples.Children.Add(line);
        }
        root.Children.Add(examples);
        var testResult = DialogShell.Note("연결을 켠 다음 연결 테스트로 AI 프로그램이 이 창에 닿는지 확인하세요.");
        testResult.Margin = new Thickness(0, 10, 0, 0); root.Children.Add(testResult);
        Button? test = null;
        test = DialogShell.Secondary("연결 테스트", async () =>
        {
            test!.IsEnabled = false; testResult.Text = "확인 중…";
            try { var (ok, text) = await TestAutomationConnectionAsync(CancellationToken.None); testResult.Text = text; testResult.Foreground = ok ? Theme.Success : Theme.Danger; }
            finally { test.IsEnabled = true; }
        });
        var close = DialogShell.Secondary("닫기", dialog.Close); close.IsCancel = true;
        root.Children.Add(DialogShell.Footer(copy, test, close, toggle));
        UpdateState();
        return dialog;
    }

    internal static readonly string[] AutomationExamplePrompts =
    [
        "Morupixel에 열린 문서의 레이어 구성을 알려줘.",
        "현재 문서에 제목 글자를 가운데 정렬로 추가하고 미리보기를 보여줘.",
        "선택한 사진 레이어의 노출을 조금 올리고 PNG로 내보내줘.",
        "평면도의 방 영역에 등록한 바닥 재질을 적용해줘."
    ];

    /// <summary>Round trip through the real local pipe: the same get_state/get_capabilities an AI client would call.</summary>
    internal async Task<(bool Ok, string Text)> TestAutomationConnectionAsync(CancellationToken token)
    {
        if (automationBridge?.IsRunning != true) return (false, "연결이 꺼져 있습니다. 먼저 연결을 켜세요.");
        string session = automationBridge.SessionId;
        bool listed = AutomationBridge.ListSessions().OfType<JsonObject>().Any(s => s["sessionId"]?.GetValue<string>() == session);
        var state = await AutomationBridge.SendAsync(session, new JsonObject { ["command"] = "get_state", ["arguments"] = new JsonObject { ["includeLayers"] = false } }, token);
        if (state["ok"]?.GetValue<bool>() != true) return (false, $"연결 테스트 실패: {state["error"]?["message"]?.GetValue<string>()}");
        var capabilities = await AutomationBridge.SendAsync(session, new JsonObject { ["command"] = "get_capabilities", ["arguments"] = new JsonObject() }, token);
        if (capabilities["ok"]?.GetValue<bool>() != true) return (false, $"연결 테스트 실패: {capabilities["error"]?["message"]?.GetValue<string>()}");
        int documents = state["result"]?["documents"]?.AsArray().Count ?? 0;
        int commands = capabilities["result"]?["commands"]?.AsArray().Count ?? 0;
        string text = $"연결 확인됨 · 문서 {documents}개 · 명령 {commands}개 · 버전 {AutomationMcpServer.ApplicationVersion}";
        if (!listed) text += "\n세션 목록에 아직 보이지 않습니다. 잠시 후 다시 시도하세요.";
        return (true, text);
    }

    // The bridge only schedules a fixed command catalog. No script evaluation, shell commands,
    // keyboard simulation, or network listener is part of the editor control surface.
    internal async Task<JsonObject> ExecuteAutomationAsync(JsonObject request, CancellationToken token = default)
    {
        bool entered = false;
        try
        {
            await automationGate.WaitAsync(token).ConfigureAwait(false); entered = true;
            var operation = Dispatcher.InvokeAsync(() => ExecuteAutomationOnUiAsync(request, token),
                System.Windows.Threading.DispatcherPriority.Background, token);
            return new JsonObject { ["ok"] = true, ["result"] = await operation.Task.Unwrap().ConfigureAwait(false) };
        }
        catch (Exception error)
        {
            string code = error switch
            {
                AutomationFault fault => fault.Code,
                OperationCanceledException => "cancelled",
                ArgumentException or JsonException or InvalidDataException or FormatException => "invalid_arguments",
                FileNotFoundException or DirectoryNotFoundException => "file_not_found",
                UnauthorizedAccessException => "access_denied",
                NotSupportedException => "unsupported_format",
                _ => "command_failed"
            };
            return new JsonObject { ["ok"] = false, ["error"] = AutomationErrors.Describe(code, error.Message, (error as AutomationFault)?.Details) };
        }
        finally { if (entered) automationGate.Release(); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowEnabled(IntPtr window);

    bool AutomationBusy
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            return dragging || panning || resizingBrush || polygonInProgress || jobCts != null ||
                pendingInspectorCommit != null || !IsEnabled || handle != IntPtr.Zero && !IsWindowEnabled(handle) || renderShutdown;
        }
    }

    void RequireAutomationIdle(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (AutomationBusy) throw new AutomationFault("editor_busy", "현재 편집 또는 대화상자를 마친 다음 다시 시도하세요.");
    }

    WorkspaceTab AutomationTab(JsonObject arguments, bool mutation = false)
    {
        var id = Guid.Parse(AString(arguments, "documentId"));
        var tab = tabs.FirstOrDefault(t => t.Id == id) ?? throw new AutomationFault("document_not_found", "열린 문서에서 documentId를 찾을 수 없습니다.");
        if (mutation)
        {
            if (activeTab < 0 || tabs[activeTab] != tab) throw new AutomationFault("inactive_document", "activate_document로 대상 문서를 먼저 활성화하세요.");
            if (doc.Revision != Guid.Parse(AString(arguments, "expectedRevision")))
                throw new AutomationFault("stale_revision", "문서가 변경되었습니다. get_state로 최신 상태를 확인한 다음 다시 요청하세요.");
        }
        return tab;
    }

    JsonObject AutomationState(Guid? documentId = null, bool includeLayers = true)
    {
        StoreTab();
        var documents = new JsonArray();
        foreach (var tab in tabs.Where(t => !documentId.HasValue || t.Id == documentId.Value))
        {
            var d = tab.Document; var layers = new JsonArray();
            var categories = DrawingLayers.Categories(d);
            var existingIds = d.Layers.Select(l => l.Id).ToHashSet();
            var selectedIds = AutomationSelectedIds(tab).Where(existingIds.Contains).OrderBy(id => id).ToArray();
            foreach (var layer in includeLayers ? d.Layers : Enumerable.Empty<Layer>())
            {
                var item = new JsonObject
                {
                    ["layerId"] = layer.Id.ToString(), ["name"] = layer.Name, ["kind"] = layer.Kind.ToString(),
                    ["parentId"] = layer.ParentId?.ToString(), ["width"] = layer.Pixels.Width, ["height"] = layer.Pixels.Height,
                    ["x"] = layer.X, ["y"] = layer.Y, ["scaleX"] = layer.ScaleX * layer.Scale,
                    ["scaleY"] = layer.ScaleY * layer.Scale, ["rotation"] = layer.Rotation, ["opacity"] = layer.Opacity,
                    ["visible"] = layer.Visible, ["locked"] = layer.Locked, ["blend"] = layer.Blend.ToString(),
                    ["hasMask"] = layer.Mask != null, ["clipped"] = layer.Clipped
                };
                if (layer.Text != null) item["text"] = JsonSerializer.SerializeToNode(layer.Text);
                if (layer.Shape != null) item["shape"] = JsonSerializer.SerializeToNode(layer.Shape);
                if (layer.Adjustment != null) item["adjustment"] = JsonSerializer.SerializeToNode(layer.Adjustment);
                layers.Add(item);
            }
            var itemDocument = new JsonObject { ["documentId"] = tab.Id.ToString(), ["revision"] = d.Revision.ToString(),
                ["name"] = d.Name, ["width"] = d.Width, ["height"] = d.Height, ["dpi"] = d.Dpi,
                ["colorMode"] = "RGB8", ["path"] = tab.Path, ["dirty"] = tab.History.Dirty(d),
                ["activeLayerId"] = d.ActiveId.ToString(), ["canUndo"] = tab.History.CanUndo,
                ["canRedo"] = tab.History.CanRedo, ["layerCount"] = d.Layers.Count,
                ["rootLayerCount"] = d.Layers.Count(l => l.ParentId == null), ["layersIncluded"] = includeLayers,
                ["selectedLayerIds"] = new JsonArray(selectedIds.Take(200).Select(id => (JsonNode?)JsonValue.Create(id.ToString())).ToArray()),
                ["selectedLayerCount"] = selectedIds.Length, ["selectionTruncated"] = selectedIds.Length > 200,
                ["layerKinds"] = new JsonObject(d.Layers.GroupBy(l => l.Kind).Select(g => new KeyValuePair<string, JsonNode?>(g.Key.ToString(), JsonValue.Create(g.Count())))),
                ["layerCategories"] = new JsonObject(categories.Values.GroupBy(c => c).Select(g => new KeyValuePair<string, JsonNode?>(g.Key.ToString(), JsonValue.Create(g.Count())))),
                ["artboardCount"] = ArtboardEditing.Visible(d).Count,
                ["materialCount"] = MaterialEditing.Assets(d).Count, ["regionCount"] = d.MaterialRegions.Count,
                ["artboards"] = new JsonArray(ArtboardEditing.Visible(d).Select(b => (JsonNode?)new JsonObject
                {
                    ["artboardId"] = b.Id == Guid.Empty ? null : b.Id.ToString(), ["name"] = b.Name,
                    ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height,
                    ["implicit"] = b.Id == Guid.Empty, ["positionSpace"] = "document"
                }).ToArray()) };
            if (includeLayers) itemDocument["layers"] = layers;
            documents.Add(itemDocument);
        }
        return new JsonObject { ["sessionId"] = automationBridge?.SessionId,
            ["activeDocumentId"] = activeTab >= 0 ? tabs[activeTab].Id.ToString() : null,
            ["busy"] = AutomationBusy, ["contractVersion"] = AutomationCatalog.ContractVersion,
            ["coordinates"] = AutomationCatalog.Coordinates(), ["documents"] = documents };
    }

    // includeLayers=false on an edit returns only the target document without its layer list.
    bool automationResultLayers = true;

    JsonObject AutomationResult(Guid? layerId = null)
    {
        var state = automationResultLayers || activeTab < 0 ? AutomationState() : AutomationState(tabs[activeTab].Id, false);
        if (activeTab >= 0) { state["documentId"] = tabs[activeTab].Id.ToString(); state["revision"] = doc.Revision.ToString(); }
        if (layerId.HasValue) state["layerId"] = layerId.Value.ToString();
        return state;
    }

    async Task<JsonObject> ExecuteAutomationOnUiAsync(JsonObject request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Any(p => p.Key is not ("command" or "arguments" or "requestId"))) throw new ArgumentException("Unknown request field.");
        string command = AString(request, "command");
        var args = request["arguments"] as JsonObject ?? throw new ArgumentException("arguments must be an object.");
        AutomationCatalog.Validate(command, args);
        automationResultLayers = ABool(args, "includeLayers", true);
        if (command == "list_sessions") return new JsonObject { ["sessions"] = AutomationBridge.ListSessions() };
        if (command == "get_capabilities") return AutomationCatalog.Capabilities();
        if (command == "query_patterns") return AutomationPatternQuery(args);
        StoreTab();
        if (command == "get_state") return AutomationState(args.ContainsKey("documentId") ? AutomationTab(args).Id : null, ABool(args, "includeLayers", true));
        if (command == "query_styles") return AutomationStyleQuery(args);
        if (command == "query_entourage") return AutomationEntourageQuery(args);
        RequireAutomationIdle(token);
        if (command is "query_materials" or "query_regions") return AutomationMaterialQuery(command, args);
        if (command is "register_material" or "define_region") return await AutomationRegisterMaterialAsync(command, args, token);
        if (command is "query_layers" or "get_layer") return AutomationInspect(command, args);
        if (command == "inspect_file") return await AutomationInspectFileAsync(args, token);
        if (command == "apply_batch") return await AutomationBatchAsync(args, token);
        if (command == "activate_document")
        {
            var tab = AutomationTab(args); SwitchTab(tabs.IndexOf(tab)); return AutomationResult();
        }
        if (command is "new_document" or "open_document")
        {
            if (tabs.Count >= 8) throw new AutomationFault("document_limit", "문서는 최대 8개까지 열 수 있습니다.");
            var initialTab = activeTab >= 0 ? tabs[activeTab] : null; var initialRevision = doc.Revision;
            Document opened; string? path = null; IReadOnlyList<string> warnings = [];
            if (command == "new_document")
            {
                int width = (int)ANumber(args, "width"), height = (int)ANumber(args, "height");
                opened = new Document { Name = AString(args, "name"), Width = width, Height = height, Dpi = ANumber(args, "dpi", 96) };
                var fill = AColor(args, "background", Colors.Transparent);
                opened.Add(new Layer { Name = fill.A == 0 ? "레이어 1" : "배경", Pixels = Raster.Solid(width, height, fill) });
            }
            else
            {
                string source = AutomationPath(args);
                if (!File.Exists(source)) throw new FileNotFoundException("파일을 찾을 수 없습니다.", source);
                if (Path.GetExtension(source).ToLowerInvariant() is ".moruproj" or ".cwproj")
                {
                    int existing = FindPathTab(source);
                    if (existing >= 0) { SwitchTab(existing); var reopened = AutomationResult(); reopened["alreadyOpen"] = true; reopened["changed"] = false; return reopened; }
                    opened = await CompatibilityImport.OnSta(() => ProjectStore.Load(source), token); path = source;
                }
                else if (CompatibilityImport.Supports(source))
                {
                    var structure = AString(args, "cadStructure", "objects") switch { "layers" => CadImportStructure.Layers, "combined" => CadImportStructure.Combined, _ => CadImportStructure.Objects };
                    CadCleanup? cleanup = null;
                    if (ABool(args, "cadCleanup"))
                    {
                        var hatches = AString(args, "cadHatches", "suggest") switch { "keep" => HatchTreatment.Keep, "image" => HatchTreatment.Image, "pattern" => HatchTreatment.Pattern, _ => HatchTreatment.Suggest };
                        string? material = null;
                        if (hatches == HatchTreatment.Image)
                        {
                            if (!args.ContainsKey("cadMaterialImage")) throw new AutomationFault("invalid_arguments", "cadHatches=image에는 cadMaterialImage가 필요합니다.");
                            material = AutomationPath(args, "cadMaterialImage");
                            if (!File.Exists(material)) throw new FileNotFoundException("재질 이미지를 찾을 수 없습니다.", material);
                        }
                        cleanup = new CadCleanup(ABool(args, "cadLineWeights", true), hatches, material,
                            args["cadLayerRoles"] is JsonArray roles ? AutomationCatalog.LayerRoles(roles) : null);
                    }
                    else if (args.ContainsKey("cadLineWeights") || args.ContainsKey("cadHatches") || args.ContainsKey("cadMaterialImage") || args.ContainsKey("cadLayerRoles"))
                        throw new AutomationFault("invalid_arguments", "도면 정리 옵션은 cadCleanup=true와 함께 사용하세요.");
                    var options = new CompatibilityOptions(Page: (int)ANumber(args, "page", 1), Dpi: ANumber(args, "dpi", 150), CadLongEdge: (int)ANumber(args, "cadLongEdge", 2400),
                        SeparateLayers: ABool(args, "separateLayers"),
                        CadLayout: args.ContainsKey("cadLayout") ? AString(args, "cadLayout") : null, CadStructure: structure, GroupDrawingObjects: structure == CadImportStructure.Objects, Cleanup: cleanup);
                    CompatibilityResult imported;
                    try { imported = await CompatibilityImport.ReadAsync(source, options, token); }
                    catch (ArgumentOutOfRangeException e) { throw new AutomationFault("invalid_arguments", e.Message.Split('(')[0].Trim()); }
                    catch (ArgumentException e) when (args.ContainsKey("cadLayout")) { throw new AutomationFault("layout_not_found", e.Message); }
                    opened = imported.Document; warnings = imported.Warnings;
                }
                else opened = await CompatibilityImport.OnSta(() => CompatibilityImport.Single(source, ImportExport.LoadImage(source), 96, Path.GetFileName(source)), token);
            }
            RequireAutomationIdle(token);
            if ((activeTab >= 0 ? tabs[activeTab] : null) != initialTab || doc.Revision != initialRevision)
                throw new AutomationFault("workspace_changed", "작업 중인 문서가 변경되었습니다. 상태를 확인하고 다시 시도하세요.");
            opened.Validate(); AddTab(opened, path);
            if (path == null) { doc.Revision = Guid.NewGuid(); Refresh(false); } // Imported/new work must prompt before closing.
            var result = AutomationResult(); result["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
            if (command == "open_document") result["alreadyOpen"] = false;
            return result;
        }
        if (command == "preview")
        {
            var tab = AutomationTab(args); var snapshot = tab.Document.Snapshot();
            var preview = await CompatibilityImport.OnSta(() =>
            {
                var image = Imaging.Render(snapshot, token);
                int maxSide = (int)ANumber(args, "maxSide", 512);
                double scale = Math.Min(1, maxSide / (double)Math.Max(image.Width, image.Height));
                if (scale < 1) image = ImportExport.Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
                using var stream = new MemoryStream(); ImportExport.Write(image, stream, "png");
                return new JsonObject { ["documentId"] = tab.Id.ToString(), ["revision"] = snapshot.Revision.ToString(),
                    ["width"] = image.Width, ["height"] = image.Height, ["pngBase64"] = Convert.ToBase64String(stream.ToArray()) };
            }, token);
            token.ThrowIfCancellationRequested(); return preview;
        }
        if (command == "export_document") return await AutomationExportDocumentAsync(args, token);
        var targetTab = AutomationTab(args, true); var before = doc.Snapshot(); var candidate = doc.Snapshot();
        void Recheck() { RequireAutomationIdle(token); _ = AutomationTab(args, true); }
        if (command is "add_artboard" or "update_artboard" or "delete_artboard")
        {
            Recheck();
            var boardId = ApplyArtboardEdit(candidate, command, args);
            candidate.Validate(); Recheck();
            CommitAutomationCandidate("AI · " + command, before, candidate, null);
            var boardResult = AutomationResult();
            if (command != "delete_artboard") boardResult["artboardId"] = boardId.ToString();
            return boardResult;
        }
        if (command is "undo" or "redo")
        {
            Recheck();
            bool possible = command == "undo" ? history.CanUndo : history.CanRedo;
            var revisionBefore = doc.Revision;
            if (possible) { if (command == "undo") Undo(); else Redo(); }
            var undone = AutomationResult(); undone["changed"] = possible && doc.Revision != revisionBefore;
            if (!possible) undone["message"] = command == "undo" ? "Nothing to undo." : "Nothing to redo.";
            return undone;
        }
        if (command is "save_project" or "export_image")
        {
            string path = AutomationPath(args); string extension = Path.GetExtension(path).ToLowerInvariant();
            if (command == "save_project" && extension is not (".moruproj" or ".cwproj")) throw new NotSupportedException("프로젝트는 .moruproj로 저장하세요.");
            if (command == "export_image" && extension is not (".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff")) throw new NotSupportedException("PNG, JPEG, TIFF 내보내기를 지원합니다.");
            bool overwrite = ABool(args, "overwrite");
            if (File.Exists(path) && !overwrite) throw new AutomationFault("file_exists", "파일이 이미 있습니다. 새 경로를 사용하거나 overwrite=true를 지정하세요.");
            if (command == "save_project") EnsureSavePathAvailable(path);
            Guid? selected = args.ContainsKey("layerId") ? Guid.Parse(AString(args, "layerId")) : null;
            if (selected.HasValue && !candidate.Layers.Any(l => l.Id == selected.Value)) throw new AutomationFault("layer_not_found", "레이어를 찾을 수 없습니다.");
            Guid? board = args.ContainsKey("artboardId") ? Guid.Parse(AString(args, "artboardId")) : null;
            if (command == "export_image")
            {
                if (board.HasValue && selected.HasValue) throw new AutomationFault("invalid_arguments", "layerId와 artboardId 중 하나만 지정하세요.");
                if (board.HasValue && !ArtboardEditing.Visible(candidate).Any(b => b.Id == board.Value && b.Id != Guid.Empty)) throw new AutomationFault("artboard_not_found", "대지를 찾을 수 없습니다. get_state의 artboardId를 사용하세요.");
                if (extension is ".jpg" or ".jpeg" && args.ContainsKey("keepTransparency")) throw new AutomationFault("invalid_arguments", "JPEG에는 투명도가 없어 keepTransparency를 사용할 수 없습니다.");
            }
            var exportFormat = extension is ".jpg" or ".jpeg" ? ExportFormat.Jpeg : extension is ".tif" or ".tiff" ? ExportFormat.Tiff : ExportFormat.Png;
            var exportSettings = new ExportSettings(exportFormat, ANumber(args, "scale", 1), ABool(args, "keepTransparency", true), (int)ANumber(args, "quality", 95));
            int outputWidth = 0, outputHeight = 0;
            string staging = Path.Combine(Path.GetDirectoryName(path)!, $".morupixel-{Guid.NewGuid():N}{extension}");
            int detached = 0;
            try
            {
                await CompatibilityImport.OnSta(() =>
                {
                    if (command == "save_project") ProjectStore.Save(candidate, staging);
                    else
                    {
                        // Rendered at the output size: a scale above 1 redraws drawings and hatch patterns.
                        Raster image;
                        if (selected.HasValue)
                        {
                            var rendered = SelectedLayerExport.Render(candidate, [selected.Value], true, token, exportSettings);
                            image = rendered.Image; detached = rendered.IndependentClippingCount;
                        }
                        else
                        {
                            var target = board.HasValue ? ArtboardEditing.ExportDocument(candidate, board.Value) : candidate;
                            image = exportSettings.OutputSize(target.Width, target.Height) == (target.Width, target.Height)
                                ? Imaging.Render(target, token) : exportSettings.Render(target, token);
                        }
                        var output = exportSettings.Prepare(image, atOutputSize: true); outputWidth = output.Width; outputHeight = output.Height;
                        using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        ImportExport.Write(output, stream, extension, exportSettings.Quality, candidate.Dpi);
                    }
                    return true;
                }, token);
                Recheck();
                if (command == "save_project") EnsureSavePathAvailable(path);
                File.Move(staging, path, overwrite);
                if (command == "save_project") { projectPath = path; history.MarkSaved(doc); StoreTab(); Refresh(false); }
                var result = AutomationResult(); result["path"] = path; result["bytes"] = new FileInfo(path).Length;
                if (command == "export_image") { result["width"] = outputWidth; result["height"] = outputHeight; }
                result["independentClippingCount"] = detached; return result;
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        automationStyleOutcome = null;
        var affected = await ApplyAutomationEditAsync(candidate, command, args, token);
        var details = automationStepDetails;
        candidate.Validate(); Recheck();
        CommitAutomationCandidate("AI · " + command, before, candidate, affected);
        var edited = AutomationResult(affected);
        if (command == "apply_style" && automationStyleOutcome is { } styled) AutomationStyleResult(edited, styled, doc);
        if (command == "apply_material" && affected is { } mappedId && doc.Layers.FirstOrDefault(l => l.Id == mappedId)?.Material is { } mappedFill)
            edited["regionId"] = mappedFill.SourceRegionId.ToString();
        if (details != null) foreach (var (key, value) in details) edited[key] = value?.DeepClone();
        return edited;
    }

    /// <summary>Artboard add/update/delete on a candidate document; shared by single commands and apply_batch.</summary>
    internal static Guid ApplyArtboardEdit(Document candidate, string command, JsonObject args)
    {
        Guid boardId = command == "add_artboard" ? Guid.Empty : Guid.Parse(AString(args, "artboardId"));
        var existing = ArtboardEditing.Visible(candidate).FirstOrDefault(b => b.Id == boardId && b.Id != Guid.Empty);
        if (command != "add_artboard" && existing == null) throw new AutomationFault("artboard_not_found", "대지를 찾을 수 없습니다. get_state의 artboardId를 사용하세요.");
        try
        {
            if (command == "delete_artboard") { ArtboardEditing.Remove(candidate, boardId); return boardId; }
            var bounds = ArtboardEditing.Bounds(candidate);
            var basis = existing ?? new Artboard(Guid.Empty, AString(args, "name", $"대지 {ArtboardEditing.Visible(candidate).Count + 1}"), bounds.Right + 40, bounds.Top, 1, 1);
            var board = basis with
            {
                Name = args.ContainsKey("name") ? AString(args, "name") : basis.Name,
                X = ANumber(args, "x", basis.X), Y = ANumber(args, "y", basis.Y),
                Width = ANumber(args, "width", basis.Width), Height = ANumber(args, "height", basis.Height)
            };
            return ArtboardEditing.Set(candidate, board, command == "add_artboard").Id;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or OverflowException) { throw new AutomationFault("artboard_invalid", e.Message); }
    }

    async Task<JsonObject> AutomationInspectFileAsync(JsonObject args, CancellationToken token)
    {
        string source = AutomationPath(args);
        if (!File.Exists(source)) throw new FileNotFoundException("파일을 찾을 수 없습니다.", source);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        var result = new JsonObject { ["path"] = source };
        if (extension is ".pdf" or ".ai")
        {
            var info = await Task.Run(() => PdfCompatibility.InspectAsync(source, token), token);
            result["format"] = "pdf"; result["pageCount"] = info.Pages; result["layerCount"] = info.Layers;
            result["firstPageWidth"] = Math.Round(info.WidthAt96Dpi, 2); result["firstPageHeight"] = Math.Round(info.HeightAt96Dpi, 2); result["sizeUnit"] = "px@96dpi";
        }
        else if (extension is ".dwg" or ".dxf")
        {
            var spaces = await Task.Run(() => CadCompatibility.Inspect(source), token);
            result["format"] = "cad";
            result["layouts"] = new JsonArray(spaces.Select(s => (JsonNode?)new JsonObject { ["key"] = s.Key, ["name"] = s.Name, ["model"] = s.Key == "*Model_Space" }).ToArray());
            var drawing = await Task.Run(() => CadCompatibility.InspectLayers(source), token);
            result["layers"] = new JsonArray(drawing.Layers.Take(512).Select(l => (JsonNode?)new JsonObject
            {
                ["layer"] = l.Name, ["role"] = AutomationCatalog.DrawingRoleNames[(int)l.Role], ["objects"] = l.Objects, ["hatches"] = l.Hatches
            }).ToArray());
            result["layersTruncated"] = drawing.Layers.Count > 512;
            result["hatchMaterials"] = new JsonObject(drawing.HatchMaterials.Select(p => new KeyValuePair<string, JsonNode?>(p.Key.ToString().ToLowerInvariant(), JsonValue.Create(p.Value))));
            result["hatchPatterns"] = new JsonObject((drawing.HatchPatternCounts ?? new Dictionary<HatchPattern, int>()).Select(p => new KeyValuePair<string, JsonNode?>(HatchPatterns.Key(p.Key), JsonValue.Create(p.Value))));
        }
        else throw new NotSupportedException("inspect_file은 PDF/AI와 DWG/DXF 파일을 읽습니다.");
        return result;
    }

    // A built-in hatch pattern, or a pattern from the user's 내 패턴 library, named by its materialId
    // joins the document library on first use (the document's own copy wins, like the palette).
    void RegisterAutomationPattern(Document candidate, Guid materialId)
    {
        var library = MaterialEditing.Assets(candidate);
        if (library.Any(a => a.Id == materialId)) return;
        MaterialAsset? asset = HatchPatterns.TryGet(materialId, out var pattern) ? HatchPatternRenderer.Create(pattern)
            : LinePatternLibrary().FirstOrDefault(p => p.Id == materialId);
        if (asset == null) return;
        if (library.Count >= MaterialEditing.MaxAssets) throw new AutomationFault("capacity_exceeded", "재료 라이브러리가 가득 찼습니다.");
        candidate.Materials.Add(asset);
    }

    /// <summary>materialId, or the stable materialId of a patternId; null when neither is given.</summary>
    static Guid? AutomationMaterialId(JsonObject args)
    {
        if (args.ContainsKey("materialId")) return Guid.Parse(AString(args, "materialId"));
        if (!args.ContainsKey("patternId")) return null;
        string key = AString(args, "patternId");
        if (HatchPatterns.TryParseKey(key, out var pattern)) return HatchPatterns.StableId(pattern);
        if (key.StartsWith(LinePatterns.CustomKeyPrefix, StringComparison.Ordinal) && Guid.TryParseExact(key[LinePatterns.CustomKeyPrefix.Length..], "N", out var id)) return id;
        throw new AutomationFault("invalid_arguments", "query_patterns의 patternId를 사용하세요.");
    }

    /// <summary>
    /// Repeat size from pixels (tileWidth/tileHeight) or relative values (scale/verticalRatio, the
    /// properties panel's size % and vertical ratio % divided by 100). A new fill without any size
    /// uses the panel's default; an update keeps every size the request does not mention.
    /// </summary>
    static (double Width, double Height) AutomationTileSize(JsonObject args, Document document, MaterialAsset asset, MaterialFill? current)
    {
        double width = args.ContainsKey("tileWidth") ? ANumber(args, "tileWidth")
            : args.ContainsKey("scale") || current == null ? MaterialEditing.DefaultTile(document.Width, document.Height, asset) * ANumber(args, "scale", 1)
            : current.TileWidth;
        if (args.ContainsKey("tileHeight")) return (width, ANumber(args, "tileHeight"));
        // An update that names neither ratio nor scale keeps its pixel height (tileWidth alone never stretched before).
        if (current != null && !args.ContainsKey("verticalRatio") && !args.ContainsKey("scale")) return (width, current.TileHeight);
        double ratio = ANumber(args, "verticalRatio", current == null ? 1 : MaterialEditing.Stretch(current));
        return (width, Math.Max(1, width * MaterialEditing.Aspect(asset) * ratio));
    }

    /// <summary>The gradient of a dot-gradient or stipple-gradient fill with the request's gradient fields applied over `current`.</summary>
    static ToneGradient AutomationGradient(JsonObject args, ToneGradient? current)
    {
        var gradient = current ?? ToneGradient.Default;
        return gradient with
        {
            Angle = ANumber(args, "gradientAngle", gradient.Angle), Start = ANumber(args, "gradientStart", gradient.Start),
            End = ANumber(args, "gradientEnd", gradient.End), Seed = (int)ANumber(args, "gradientSeed", gradient.Seed)
        };
    }

    async Task<Guid?> ApplyAutomationEditAsync(Document candidate, string command, JsonObject args, CancellationToken token)
    {
        Guid? affected = null; automationStepDetails = null;
        Layer Target(bool allowUnlock = false)
        {
            var id = Guid.Parse(AString(args, "layerId"));
            var layer = candidate.Layers.FirstOrDefault(l => l.Id == id) ?? throw new AutomationFault("layer_not_found", "레이어를 찾을 수 없습니다.");
            if (Parents(candidate, layer).Any(l => l.Locked) || layer.Locked && !allowUnlock)
                throw new AutomationFault("layer_locked", "레이어 또는 부모 그룹의 잠금을 먼저 해제하세요.");
            affected = layer.Id; return layer;
        }
        void Add(Layer layer)
        {
            if (args.ContainsKey("name")) layer.Name = AString(args, "name");
            candidate.Add(layer); affected = layer.Id;
        }
        switch (command)
        {
            case "apply_material":
                var materialId = AutomationMaterialId(args) ?? throw new AutomationFault("invalid_arguments", "materialId 또는 patternId를 지정하세요.");
                RegisterAutomationPattern(candidate, materialId);
                var asset = MaterialEditing.Assets(candidate).SingleOrDefault(a => a.Id == materialId)
                    ?? throw new AutomationFault("material_not_found", "등록된 재료 ID가 없습니다.");
                if (AutomationMaterials.HasGradient(args) && !HatchPatterns.IsGradient(asset)) throw new AutomationFault("invalid_arguments", AutomationMaterials.GradientOnly);
                Guid? boundaryLayer = args.ContainsKey("boundaryLayerId") ? Guid.Parse(AString(args, "boundaryLayerId")) : null;
                if (args.ContainsKey("regionId"))
                {
                    if (!candidate.MaterialRegions.Any(r => r.Id == Guid.Parse(AString(args, "regionId"))))
                        throw new AutomationFault("region_not_found", "등록된 영역 ID가 없습니다.");
                }
                else
                {
                    if (candidate.MaterialRegions.Count >= MaterialEditing.MaxRegions) throw new AutomationFault("capacity_exceeded", "영역은 최대 128개를 보관합니다.");
                    if (boundaryLayer.HasValue && !candidate.Layers.Any(l => l.Id == boundaryLayer.Value)) throw new AutomationFault("layer_not_found", "경계로 쓸 레이어를 찾을 수 없습니다.");
                }
                var mapped = await CompatibilityImport.OnSta(() =>
                {
                    token.ThrowIfCancellationRequested();
                    Guid regionId;
                    if (args.ContainsKey("regionId")) regionId = Guid.Parse(AString(args, "regionId"));
                    else
                    {
                        // An inline boundary becomes a region template, exactly as define_region would store it.
                        Geometry geometry;
                        if (boundaryLayer.HasValue) geometry = MaterialEditing.ClosedLayer(candidate, boundaryLayer.Value);
                        else
                        {
                            var contours = new List<Point[]> { AutomationCatalog.MaterialPoints(args["points"]!.AsArray()) };
                            if (args["holes"] is JsonArray holes) contours.AddRange(holes.Select(h => AutomationCatalog.MaterialPoints(h!.AsArray())));
                            geometry = MaterialEditing.Polygon(contours);
                        }
                        string regionName = AString(args, "regionName", $"영역 {candidate.MaterialRegions.Count + 1}");
                        var region = MaterialEditing.Region(candidate, regionName, geometry, boundaryLayer.HasValue ? "closed_layer" : "polygon", boundaryLayer);
                        candidate.MaterialRegions.Add(region); regionId = region.Id;
                    }
                    var (tileWidth, tileHeight) = AutomationTileSize(args, candidate, asset, null);
                    var created = MaterialEditing.Apply(candidate, materialId, regionId, tileWidth, tileHeight, ANumber(args, "angle"), ANumber(args, "offsetX"), ANumber(args, "offsetY"));
                    if (args.ContainsKey("ink") || args.ContainsKey("lineWeight") || args.ContainsKey("background") || AutomationMaterials.HasGradient(args))
                    {
                        var tuned = created.Material! with { Ink = args.ContainsKey("ink") ? AutomationMaterials.ParseInk(AString(args, "ink")) : 0, LineWeight = ANumber(args, "lineWeight", 1),
                            Background = args.ContainsKey("background") ? AutomationMaterials.ParseBackground(AString(args, "background")) : 0,
                            Gradient = AutomationMaterials.HasGradient(args) ? AutomationGradient(args, null) : null };
                        MaterialEditing.ValidateFill(tuned, created.Pixels); created.Material = tuned; created.Pixels = MaterialRenderer.Render(tuned);
                    }
                    return created;
                }, token);
                mapped.Opacity = ANumber(args, "opacity", 1);
                if (args.ContainsKey("blend")) mapped.Blend = Enum.Parse<BlendMode>(AString(args, "blend"));
                Add(mapped); break;
            case "update_material":
                var materialLayer = Target();
                if (materialLayer.Material is not { } original) throw new AutomationFault("wrong_layer_kind", "재료 맵핑 레이어를 선택하세요.");
                var swapId = AutomationMaterialId(args);
                if (swapId.HasValue) RegisterAutomationPattern(candidate, swapId.Value);
                var material = swapId.HasValue ? MaterialEditing.Assets(candidate).SingleOrDefault(a => a.Id == swapId.Value)
                    ?? throw new AutomationFault("material_not_found", "등록된 재료가 없습니다.") : original.Asset;
                if (AutomationMaterials.HasGradient(args) && !HatchPatterns.IsGradient(material)) throw new AutomationFault("invalid_arguments", AutomationMaterials.GradientOnly);
                var (width, height) = AutomationTileSize(args, candidate, material, original);
                var replacement = original with { Asset = material, TileWidth = width, TileHeight = height,
                    Angle = ANumber(args, "angle", original.Angle), OffsetX = ANumber(args, "offsetX", original.OffsetX), OffsetY = ANumber(args, "offsetY", original.OffsetY),
                    Ink = args.ContainsKey("ink") ? AutomationMaterials.ParseInk(AString(args, "ink")) : original.Ink, LineWeight = ANumber(args, "lineWeight", original.LineWeight),
                    Background = args.ContainsKey("background") ? AutomationMaterials.ParseBackground(AString(args, "background")) : original.Background,
                    Gradient = AutomationMaterials.HasGradient(args) ? AutomationGradient(args, original.Gradient) : original.Gradient };
                if (replacement != original)
                {
                    MaterialEditing.ValidateFill(replacement, materialLayer.Pixels);
                    materialLayer.Pixels = await CompatibilityImport.OnSta(() => MaterialRenderer.Render(replacement), token); materialLayer.Material = replacement;
                }
                if (args.ContainsKey("opacity")) materialLayer.Opacity = ANumber(args, "opacity");
                if (args.ContainsKey("blend")) materialLayer.Blend = Enum.Parse<BlendMode>(AString(args, "blend"));
                if (args.ContainsKey("name")) materialLayer.Name = AString(args, "name"); break;
            case "add_image":
                string source = AutomationSourcePath(args);
                var pixels = await CompatibilityImport.OnSta(() => ImportExport.LoadImage(source), token);
                Add(new Layer { Name = Path.GetFileNameWithoutExtension(source), Pixels = pixels, X = ANumber(args, "x"), Y = ANumber(args, "y") }); break;
            case "add_text":
                var text = AutomationText(args, new TextSpec());
                Add(await CompatibilityImport.OnSta(() => DocumentFeatures.CreateText(text, ANumber(args, "x"), ANumber(args, "y")), token)); break;
            case "update_text":
                var textLayer = Target();
                if (textLayer.Kind != LayerKind.Text || textLayer.Text == null) throw new AutomationFault("wrong_layer_kind", "편집 가능한 텍스트 레이어가 아닙니다.");
                var updatedText = AutomationText(args, textLayer.Text);
                if (updatedText != textLayer.Text)
                    await CompatibilityImport.OnSta(() => { DocumentFeatures.UpdateText(textLayer, updatedText); return true; }, token);
                if (args.ContainsKey("x")) textLayer.X = ANumber(args, "x");
                if (args.ContainsKey("y")) textLayer.Y = ANumber(args, "y");
                if (args.ContainsKey("name")) textLayer.Name = AString(args, "name"); break;
            case "add_shape":
                var shape = new ShapeSpec { Kind = AString(args, "shape") == "ellipse" ? ShapeKind.Ellipse : ShapeKind.Rectangle,
                    Width = (int)ANumber(args, "width"), Height = (int)ANumber(args, "height"),
                    FillArgb = VectorShapes.Argb(AColor(args, "fill", Color.FromRgb(188, 217, 250))),
                    StrokeArgb = VectorShapes.Argb(AColor(args, "stroke", Colors.Transparent)), StrokeEnabled = args.ContainsKey("stroke"),
                    StrokeWidth = ANumber(args, "strokeWidth", 2), CornerRadius = ANumber(args, "cornerRadius") };
                Add(await CompatibilityImport.OnSta(() => VectorShapes.Create(shape, ANumber(args, "x"), ANumber(args, "y")), token)); break;
            case "set_layer":
                bool unlockOnly = args.Count == 4 && args.ContainsKey("locked") && !ABool(args, "locked");
                var layer = Target(unlockOnly);
                if (args.ContainsKey("name")) layer.Name = AString(args, "name");
                if (args.ContainsKey("x")) layer.X = ANumber(args, "x");
                if (args.ContainsKey("y")) layer.Y = ANumber(args, "y");
                if (args.ContainsKey("scaleX")) layer.ScaleX = ANumber(args, "scaleX") / layer.Scale;
                if (args.ContainsKey("scaleY")) layer.ScaleY = ANumber(args, "scaleY") / layer.Scale;
                if (args.ContainsKey("rotation")) layer.Rotation = ANumber(args, "rotation");
                if (args.ContainsKey("opacity")) layer.Opacity = ANumber(args, "opacity");
                if (args.ContainsKey("visible")) layer.Visible = ABool(args, "visible");
                if (args.ContainsKey("locked")) layer.Locked = ABool(args, "locked");
                if (args.ContainsKey("blend")) layer.Blend = Enum.Parse<BlendMode>(AString(args, "blend")); break;
            case "delete_layer":
                var removed = Target();
                if (candidate.Layers.Any(l => l.Locked && Parents(candidate, l).Any(p => p.Id == removed.Id)))
                    throw new AutomationFault("layer_locked", "그룹 안에 잠긴 레이어가 있습니다.");
                DocumentFeatures.Remove(candidate, removed.Id); break;
            case "reorder_layer":
                var moved = Target(); var siblings = candidate.Layers.Where(l => l.ParentId == moved.ParentId).ToList();
                int index = siblings.IndexOf(moved), next = index + (AString(args, "direction") == "up" ? 1 : -1);
                if (next >= 0 && next < siblings.Count)
                {
                    int a = candidate.Layers.IndexOf(moved), b = candidate.Layers.IndexOf(siblings[next]);
                    (candidate.Layers[a], candidate.Layers[b]) = (candidate.Layers[b], candidate.Layers[a]);
                }
                break;
            case "add_adjustment":
                Add(await CompatibilityImport.OnSta(() => DocumentFeatures.CreateAdjustment(candidate, AutomationAdjustment(args)), token)); break;
            case "apply_style":
                affected = await AutomationApplyStyleAsync(candidate, args, token); break;
            case "place_entourage":
                affected = AutomationPlaceEntourage(candidate, args); break;
            case "remove_background":
                var masked = Target();
                if (masked.Kind is LayerKind.Group or LayerKind.Adjustment) throw new AutomationFault("wrong_layer_kind", "이미지·텍스트·도형 레이어를 선택하세요.");
                masked.Mask = await Task.Run(() => BackgroundRemoval.CreateMask(masked.Pixels, cancellationToken: token), token); break;
            case "clean_sketch":
                var photo = Target();
                automationStepDetails = await AutomationCleanSketchAsync(candidate, photo, args, token);
                affected = Guid.Parse(automationStepDetails["groupId"]!.GetValue<string>()); break;
            default: throw new ArgumentException("Unknown command: " + command);
        }
        return affected;
    }

    void CommitAutomationCandidate(string label, Document before, Document candidate, Guid? affected, Action? committed = null, bool preserveSelection = false)
    {
        if (!SameDocument(before, candidate))
        {
            history.Commit(label, before, candidate); doc = candidate;
            if (!preserveSelection) { selection = null; maskEditing = false; selectedLayers.Clear(); }
            if (affected.HasValue && doc.Layers.Any(l => l.Id == affected.Value)) doc.ActiveId = affected.Value;
            if (doc.ActiveId != Guid.Empty) selectedLayers.Add(doc.ActiveId);
            StoreTab(); committed?.Invoke(); Refresh();
        }
        else committed?.Invoke();
    }

    static string AString(JsonObject args, string name) => args[name]?.GetValue<string>() ?? throw new ArgumentException($"Required string: {name}");
    static string AString(JsonObject args, string name, string fallback) => args[name]?.GetValue<string>() ?? fallback;
    static double ANumber(JsonObject args, string name, double fallback = 0) => args[name] is { } value ? JsonSerializer.Deserialize<double>(value.ToJsonString()) : fallback;
    static bool ABool(JsonObject args, string name, bool fallback = false) => args[name]?.GetValue<bool>() ?? fallback;
    static Color AColor(JsonObject args, string name, Color fallback) => args[name] is { } value
        ? value.GetValue<string>() == "transparent" ? Colors.Transparent : (Color)ColorConverter.ConvertFromString(value.GetValue<string>()) : fallback;
    static string AutomationPath(JsonObject args, string name = "path")
    {
        string path = AString(args, name);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("절대 파일 경로를 지정하세요.");
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException("대상 폴더가 없습니다.");
        return full;
    }
    /// <summary>An existing input file; reports a missing source with the same message as open_document.</summary>
    static string AutomationSourcePath(JsonObject args)
    {
        string full = AutomationPath(args);
        if (!File.Exists(full)) throw new FileNotFoundException("파일을 찾을 수 없습니다.", full);
        return full;
    }
    static TextSpec AutomationText(JsonObject args, TextSpec current) => current with
    {
        Content = args.ContainsKey("text") ? AString(args, "text") : current.Content,
        FontFamily = args.ContainsKey("fontFamily") ? AString(args, "fontFamily") : current.FontFamily,
        FontSize = ANumber(args, "fontSize", current.FontSize), ColorArgb = VectorShapes.Argb(AColor(args, "color", VectorShapes.Color(current.ColorArgb))),
        Bold = ABool(args, "bold", current.Bold), Italic = ABool(args, "italic", current.Italic),
        Alignment = args.ContainsKey("alignment") ? Enum.Parse<TextAlignment>(AString(args, "alignment")) : current.Alignment,
        LineHeight = ANumber(args, "lineHeight", current.LineHeight), Tracking = ANumber(args, "tracking", current.Tracking),
        BoxWidth = ANumber(args, "boxWidth", current.BoxWidth),
        // Giving an outline width, colour or position (or hollow letters) asks for an outline.
        Outline = args.ContainsKey("outline") ? ABool(args, "outline")
            : new[] { "outlineWidth", "outlineColor", "outlinePosition" }.Any(args.ContainsKey) || ABool(args, "outlineOnly") || current.Outline,
        OutlineWidth = ANumber(args, "outlineWidth", current.OutlineWidth),
        OutlineArgb = VectorShapes.Argb(AColor(args, "outlineColor", VectorShapes.Color(current.OutlineArgb))),
        OutlinePosition = args.ContainsKey("outlinePosition") ? AString(args, "outlinePosition") == "center" ? TextOutlinePosition.Center : TextOutlinePosition.Outside : current.OutlinePosition,
        OutlineOnly = ABool(args, "outlineOnly", current.OutlineOnly)
    };
    static AdjustmentSpec AutomationAdjustment(JsonObject args) => AutomationStyleEffect(args, new()
    {
        Kind = AString(args, "kind") switch
        {
            "exposure" => AdjustmentKind.Exposure, "levels" => AdjustmentKind.Levels, "hue_saturation" => AdjustmentKind.HueSaturation,
            "threshold" => AdjustmentKind.Threshold, "halftone" => AdjustmentKind.Halftone, "paper_texture" => AdjustmentKind.PaperTexture, "glow" => AdjustmentKind.Glow,
            _ => AdjustmentKind.PhotoDevelop
        },
        Exposure = ANumber(args, "exposure"), Offset = ANumber(args, "offset"), ExposureGamma = ANumber(args, "gamma", 1),
        Black = ANumber(args, "black"), White = ANumber(args, "white", 255), Gamma = ANumber(args, "gamma", 1),
        Hue = ANumber(args, "hue"), Saturation = ANumber(args, "saturation"), Lightness = ANumber(args, "lightness"),
        PhotoDevelop = new PhotoDevelopSpec { Temperature = ANumber(args, "temperature"), Tint = ANumber(args, "tint"), Exposure = AString(args, "kind") == "photo_develop" ? ANumber(args, "exposure") : 0,
            Contrast = ANumber(args, "contrast"), Highlights = ANumber(args, "highlights"), Shadows = ANumber(args, "shadows"), Whites = ANumber(args, "whites"), Blacks = ANumber(args, "blacks"),
            Texture = ANumber(args, "texture"), Clarity = ANumber(args, "clarity"), Dehaze = ANumber(args, "dehaze"), Vibrance = ANumber(args, "vibrance"), Saturation = ANumber(args, "saturation") }
    });
    // Settings of the design-style kinds; omitted parameters keep the same defaults as the app's dialog.
    static AdjustmentSpec AutomationStyleEffect(JsonObject args, AdjustmentSpec spec)
    {
        uint Argb(string key, uint fallback) => args.ContainsKey(key) ? VectorShapes.Argb(AColor(args, key, Colors.Transparent)) : fallback;
        switch (spec.Kind)
        {
            case AdjustmentKind.Threshold:
                var threshold = spec.Threshold;
                return spec with { Threshold = threshold with { Level = ANumber(args, "level", threshold.Level), Smoothness = ANumber(args, "smoothness", threshold.Smoothness), KeepAlpha = ABool(args, "keepAlpha", threshold.KeepAlpha) } };
            case AdjustmentKind.Halftone:
                var halftone = spec.Halftone;
                return spec with { Halftone = halftone with { CellSize = ANumber(args, "cellSize", halftone.CellSize), Angle = ANumber(args, "angle", halftone.Angle),
                    Shape = AString(args, "dotShape", "round") switch { "line" => HalftoneShape.Line, "square" => HalftoneShape.Square, _ => HalftoneShape.Round },
                    InkArgb = Argb("ink", halftone.InkArgb), PaperArgb = Argb("paper", halftone.PaperArgb) } };
            case AdjustmentKind.PaperTexture:
                var paper = spec.Paper;
                return spec with { Paper = paper with { Seed = (int)ANumber(args, "seed", paper.Seed), Scale = ANumber(args, "textureSize", paper.Scale), Tint = ANumber(args, "paperTint", paper.Tint),
                    TintArgb = Argb("paperColor", paper.TintArgb), Grain = ANumber(args, "grain", paper.Grain), Fibers = ANumber(args, "fibers", paper.Fibers), Toner = ANumber(args, "toner", paper.Toner),
                    Streaks = ANumber(args, "streaks", paper.Streaks), Edges = ANumber(args, "edges", paper.Edges), EdgeWidth = ANumber(args, "edgeWidth", paper.EdgeWidth), EdgeArgb = Argb("edgeColor", paper.EdgeArgb) } };
            case AdjustmentKind.Glow:
                var glow = spec.Glow;
                return spec with { Glow = glow with { Threshold = ANumber(args, "threshold", glow.Threshold), Radius = ANumber(args, "radius", glow.Radius),
                    Intensity = ANumber(args, "intensity", glow.Intensity), TintArgb = Argb("glowColor", glow.TintArgb) } };
            default: return spec;
        }
    }
}
