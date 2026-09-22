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

    void ShowAutomationSettings()
    {
        var dialog = new Window
        {
            Owner = this, Title = "Morupixel · AI 연결", Icon = Theme.BrandIcon,
            Width = 620, MinWidth = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Theme.Header, Foreground = Theme.Text, FontFamily = Theme.UiFont,
            FontSize = Theme.BodySize, ResizeMode = ResizeMode.NoResize
        };
        dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog);
        var root = new StackPanel { Margin = new Thickness(24) }; dialog.Content = root;
        root.Children.Add(Theme.Label("AI로 편집하기", 20));
        var state = Theme.Label(automationBridge?.IsRunning == true ? "연결 켜짐" : "연결 꺼짐", 14);
        state.Margin = new Thickness(0, 12, 0, 10); root.Children.Add(state);
        var detail = Theme.Label("같은 Windows 계정의 로컬 도구가 문서와 파일을 편집합니다. 연결을 끄면 새 명령을 받지 않습니다.", 13);
        detail.TextWrapping = TextWrapping.Wrap; root.Children.Add(detail);
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Morupixel.exe");
        var configuration = new JsonObject { ["mcpServers"] = new JsonObject { ["morupixel"] = new JsonObject
            { ["command"] = executable, ["args"] = new JsonArray("--mcp") } } }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        root.Children.Add(Theme.Label("MCP 서버 설정", 14));
        var config = new TextBox { Text = configuration, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 160, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 12) };
        root.Children.Add(config);
        var client = new ComboBox { ItemsSource = new[] { "공통 MCP 설정", "Codex · PowerShell 명령", "Claude Code · PowerShell 명령" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(client, "연결할 AI 프로그램");
        client.SelectionChanged += (_, _) => config.Text = client.SelectedIndex switch
        {
            1 => "codex mcp add morupixel -- " + "'" + executable.Replace("'", "''") + "' --mcp",
            2 => "claude mcp add --transport stdio --scope user morupixel -- " + "'" + executable.Replace("'", "''") + "' --mcp",
            _ => configuration
        };
        root.Children.Add(client);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Theme.Button("설정 복사", () => { Clipboard.SetText(config.Text); state.Text = "선택한 AI 연결 설정을 복사했습니다."; }));
        var toggle = Theme.Button(automationBridge?.IsRunning == true ? "연결 끄기" : "연결 켜기", () => { });
        toggle.Click += (_, _) => Guard(() =>
        {
            if (automationBridge?.IsRunning == true) DisableAutomation(); else EnableAutomation();
            toggle.Content = automationBridge?.IsRunning == true ? "연결 끄기" : "연결 켜기";
            state.Text = automationBridge?.IsRunning == true ? "연결 켜짐" : "연결 꺼짐";
        });
        actions.Children.Add(toggle); actions.Children.Add(Theme.Button("닫기", dialog.Close)); root.Children.Add(actions);
        dialog.ShowDialog();
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

    JsonObject AutomationResult(Guid? layerId = null)
    {
        var state = AutomationState();
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
        if (command == "list_sessions") return new JsonObject { ["sessions"] = AutomationBridge.ListSessions() };
        if (command == "get_capabilities") return AutomationCatalog.Capabilities();
        StoreTab();
        if (command == "get_state") return AutomationState(args.ContainsKey("documentId") ? AutomationTab(args).Id : null, ABool(args, "includeLayers", true));
        RequireAutomationIdle(token);
        if (command is "query_materials" or "query_regions") return AutomationMaterialQuery(command, args);
        if (command is "register_material" or "define_region") return await AutomationRegisterMaterialAsync(command, args, token);
        if (command is "query_layers" or "get_layer") return AutomationInspect(command, args);
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
                    if (existing >= 0) { SwitchTab(existing); return AutomationResult(); }
                    opened = await CompatibilityImport.OnSta(() => ProjectStore.Load(source), token); path = source;
                }
                else if (CompatibilityImport.Supports(source))
                {
                    var imported = await CompatibilityImport.ReadAsync(source, new CompatibilityOptions(CadStructure: CadImportStructure.Objects, GroupDrawingObjects: true), token);
                    opened = imported.Document; warnings = imported.Warnings;
                }
                else opened = await CompatibilityImport.OnSta(() => CompatibilityImport.Single(source, ImportExport.LoadImage(source), 96, Path.GetFileName(source)), token);
            }
            RequireAutomationIdle(token);
            if ((activeTab >= 0 ? tabs[activeTab] : null) != initialTab || doc.Revision != initialRevision)
                throw new AutomationFault("workspace_changed", "작업 중인 문서가 변경되었습니다. 상태를 확인하고 다시 시도하세요.");
            opened.Validate(); AddTab(opened, path);
            if (path == null) { doc.Revision = Guid.NewGuid(); Refresh(false); } // Imported/new work must prompt before closing.
            var result = AutomationResult(); result["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()); return result;
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
        var targetTab = AutomationTab(args, true); var before = doc.Snapshot(); var candidate = doc.Snapshot();
        void Recheck() { RequireAutomationIdle(token); _ = AutomationTab(args, true); }
        if (command is "undo" or "redo")
        {
            Recheck(); if (command == "undo") Undo(); else Redo(); return AutomationResult();
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
            string staging = Path.Combine(Path.GetDirectoryName(path)!, $".morupixel-{Guid.NewGuid():N}{extension}");
            int detached = 0;
            try
            {
                await CompatibilityImport.OnSta(() =>
                {
                    if (command == "save_project") ProjectStore.Save(candidate, staging);
                    else
                    {
                        Raster image;
                        if (selected.HasValue)
                        {
                            var rendered = SelectedLayerExport.Render(candidate, [selected.Value], true, token);
                            image = rendered.Image; detached = rendered.IndependentClippingCount;
                        }
                        else image = Imaging.Render(candidate, token);
                        using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        ImportExport.Write(image, stream, extension, (int)ANumber(args, "quality", 95), candidate.Dpi);
                    }
                    return true;
                }, token);
                Recheck();
                if (command == "save_project") EnsureSavePathAvailable(path);
                File.Move(staging, path, overwrite);
                if (command == "save_project") { projectPath = path; history.MarkSaved(doc); StoreTab(); Refresh(false); }
                var result = AutomationResult(); result["path"] = path; result["bytes"] = new FileInfo(path).Length;
                result["independentClippingCount"] = detached; return result;
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        var affected = await ApplyAutomationEditAsync(candidate, command, args, token);
        candidate.Validate(); Recheck();
        CommitAutomationCandidate("AI · " + command, before, candidate, affected);
        return AutomationResult(affected);
    }

    static async Task<Guid?> ApplyAutomationEditAsync(Document candidate, string command, JsonObject args, CancellationToken token)
    {
        Guid? affected = null;
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
                var mapped = await CompatibilityImport.OnSta(() => MaterialEditing.Apply(candidate, Guid.Parse(AString(args, "materialId")), Guid.Parse(AString(args, "regionId")),
                    ANumber(args, "tileWidth"), ANumber(args, "tileHeight"), ANumber(args, "angle"), ANumber(args, "offsetX"), ANumber(args, "offsetY")), token);
                mapped.Opacity = ANumber(args, "opacity", 1);
                if (args.ContainsKey("blend")) mapped.Blend = Enum.Parse<BlendMode>(AString(args, "blend"));
                Add(mapped); break;
            case "update_material":
                var materialLayer = Target();
                if (materialLayer.Material is not { } original) throw new AutomationFault("wrong_layer_kind", "재료 맵핑 레이어를 선택하세요.");
                var material = args.ContainsKey("materialId") ? MaterialEditing.Assets(candidate).SingleOrDefault(a => a.Id == Guid.Parse(AString(args, "materialId")))
                    ?? throw new AutomationFault("material_not_found", "등록된 재료가 없습니다.") : original.Asset;
                var replacement = original with { Asset = material, TileWidth = ANumber(args, "tileWidth", original.TileWidth), TileHeight = ANumber(args, "tileHeight", original.TileHeight),
                    Angle = ANumber(args, "angle", original.Angle), OffsetX = ANumber(args, "offsetX", original.OffsetX), OffsetY = ANumber(args, "offsetY", original.OffsetY) };
                if (replacement != original)
                {
                    MaterialEditing.ValidateFill(replacement, materialLayer.Pixels);
                    materialLayer.Pixels = await CompatibilityImport.OnSta(() => MaterialRenderer.Render(replacement), token); materialLayer.Material = replacement;
                }
                if (args.ContainsKey("name")) materialLayer.Name = AString(args, "name"); break;
            case "add_image":
                string source = AutomationPath(args);
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
            case "remove_background":
                var masked = Target();
                if (masked.Kind is LayerKind.Group or LayerKind.Adjustment) throw new AutomationFault("wrong_layer_kind", "이미지·텍스트·도형 레이어를 선택하세요.");
                masked.Mask = await Task.Run(() => BackgroundRemoval.CreateMask(masked.Pixels, cancellationToken: token), token); break;
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
    static double ANumber(JsonObject args, string name, double fallback = 0) => args[name] is { } value ? JsonSerializer.Deserialize<double>(value.ToJsonString()) : fallback;
    static bool ABool(JsonObject args, string name, bool fallback = false) => args[name]?.GetValue<bool>() ?? fallback;
    static Color AColor(JsonObject args, string name, Color fallback) => args[name] is { } value
        ? value.GetValue<string>() == "transparent" ? Colors.Transparent : (Color)ColorConverter.ConvertFromString(value.GetValue<string>()) : fallback;
    static string AutomationPath(JsonObject args)
    {
        string path = AString(args, "path");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("절대 파일 경로를 지정하세요.");
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException("대상 폴더가 없습니다.");
        return full;
    }
    static TextSpec AutomationText(JsonObject args, TextSpec current) => current with
    {
        Content = args.ContainsKey("text") ? AString(args, "text") : current.Content,
        FontFamily = args.ContainsKey("fontFamily") ? AString(args, "fontFamily") : current.FontFamily,
        FontSize = ANumber(args, "fontSize", current.FontSize), ColorArgb = VectorShapes.Argb(AColor(args, "color", VectorShapes.Color(current.ColorArgb))),
        Bold = ABool(args, "bold", current.Bold), Italic = ABool(args, "italic", current.Italic),
        Alignment = args.ContainsKey("alignment") ? Enum.Parse<TextAlignment>(AString(args, "alignment")) : current.Alignment,
        LineHeight = ANumber(args, "lineHeight", current.LineHeight), Tracking = ANumber(args, "tracking", current.Tracking)
    };
    static AdjustmentSpec AutomationAdjustment(JsonObject args) => new()
    {
        Kind = AString(args, "kind") switch { "exposure" => AdjustmentKind.Exposure, "levels" => AdjustmentKind.Levels, "hue_saturation" => AdjustmentKind.HueSaturation, _ => AdjustmentKind.PhotoDevelop },
        Exposure = ANumber(args, "exposure"), Offset = ANumber(args, "offset"), ExposureGamma = ANumber(args, "gamma", 1),
        Black = ANumber(args, "black"), White = ANumber(args, "white", 255), Gamma = ANumber(args, "gamma", 1),
        Hue = ANumber(args, "hue"), Saturation = ANumber(args, "saturation"), Lightness = ANumber(args, "lightness"),
        PhotoDevelop = new PhotoDevelopSpec { Temperature = ANumber(args, "temperature"), Tint = ANumber(args, "tint"), Exposure = AString(args, "kind") == "photo_develop" ? ANumber(args, "exposure") : 0,
            Contrast = ANumber(args, "contrast"), Highlights = ANumber(args, "highlights"), Shadows = ANumber(args, "shadows"), Whites = ANumber(args, "whites"), Blacks = ANumber(args, "blacks"),
            Texture = ANumber(args, "texture"), Clarity = ANumber(args, "clarity"), Dehaze = ANumber(args, "dehaze"), Vibrance = ANumber(args, "vibrance"), Saturation = ANumber(args, "saturation") }
    };
}
