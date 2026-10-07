using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

// 디자인 스타일 in the editor: the gallery (이미지 menu, Ctrl+K, the 디자인 스타일 panel actions),
// applying a style as one undo step from an isolated STA, re-applying a selected style folder with
// new settings (inspector or gallery) and removing it, which also restores what it changed.
public sealed partial class MainWindow
{
    /// <summary>Services the styles use; self-tests replace the subject cut-out.</summary>
    internal StyleServices designStyleServices = StyleServices.Default;
    string? lastDesignStyle;

    Layer? SelectedStyleGroup => HasDocument ? DesignStyles.GroupOf(doc, doc.Active) : null;

    internal DesignStyleDialog CreateDesignStyleDialog(Window? owner)
    {
        var group = SelectedStyleGroup; var tag = group?.Style;
        bool editing = tag != null && DesignStyles.Find(tag.StyleId) != null;
        return new DesignStyleDialog(owner, doc, editing ? tag!.StyleId : lastDesignStyle, editing ? tag!.ValueMap : null, editing, designStyleServices);
    }

    void ShowDesignStyles()
    {
        if (!HasDocument) return;
        CommitFocusedInspectorField(); CancelGesture();
        var group = SelectedStyleGroup;
        if (group != null && IsLockedWithParents(group)) { status.Text = "잠긴 스타일 그룹입니다. 그룹과 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        var dialog = CreateDesignStyleDialog(this);
        if (dialog.ShowDialog() != true) return;
        bool editing = group != null && dialog.Editing;
        if (dialog.Choice == DesignStyleChoice.Remove && group != null) RemoveDesignStyle(group.Id);
        else if (dialog.Choice == DesignStyleChoice.Apply)
        {
            lastDesignStyle = dialog.SelectedStyle.Id;
            _ = ApplyDesignStyleAsync(dialog.SelectedStyle.Id, dialog.Values, editing ? group!.Id : null);
        }
    }

    /// <summary>
    /// Applies a style to a copy of the document on an isolated STA (Esc cancels) and commits it as
    /// one undo step if nothing changed meanwhile. A group id re-applies that style folder in place.
    /// </summary>
    internal async Task<StyleOutcome?> ApplyDesignStyleAsync(string styleId, IReadOnlyDictionary<string, double>? values, Guid? groupId = null, IReadOnlyList<Guid>? targets = null)
    {
        if (!HasDocument || DesignStyles.Find(styleId) is not { } style) return null;
        if (groupId is { } existing && doc.Layers.FirstOrDefault(l => l.Id == existing) is { } folder && IsLockedWithParents(folder))
        { status.Text = "잠긴 스타일 그룹입니다. 그룹과 부모 그룹의 잠금을 먼저 해제하세요."; return null; }
        CommitFocusedInspectorField(); CancelGesture(); jobCts?.Cancel(); var cts = jobCts = new CancellationTokenSource();
        var document = doc; var revision = doc.Revision; var historyAtStart = history; var candidate = doc.Snapshot(); var services = designStyleServices;
        status.Text = Loc.Format("디자인 스타일 적용 중… {0}  Esc: 취소", Loc.T(style.Name));
        try
        {
            var outcome = await CompatibilityImport.OnSta(() => DesignStyleEngine.Apply(candidate, new StyleRequest(styleId, values, targets, groupId), services, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) { if (ReferenceEquals(document, doc)) status.Text = "디자인 스타일 적용을 취소했습니다."; return null; }
            if (!ReferenceEquals(document, doc) || revision != doc.Revision || !ReferenceEquals(historyAtStart, history))
            {
                if (ReferenceEquals(historyAtStart, history)) status.Text = "적용하는 동안 문서가 바뀌어 스타일을 적용하지 않았습니다. 다시 실행하세요.";
                return null;
            }
            Edit(groupId == null ? "디자인 스타일 적용" : "디자인 스타일 다시 적용", () => { doc = candidate; maskEditing = false; });
            if (doc.Layers.Any(l => l.Id == outcome.GroupId))
            {
                doc.ActiveId = outcome.GroupId; selectedLayers.Clear(); selectedLayers.Add(outcome.GroupId); RevealLayerSelection(outcome.GroupId); Refresh(false);
            }
            string done = Loc.Format("디자인 스타일을 적용했습니다: {0} · 레이어 {1}개", Loc.T(style.Name), outcome.LayerCount);
            status.Text = outcome.Notes.Count == 0 ? done : done + " · " + string.Join(" · ", outcome.Notes.Select(Loc.T));
            return outcome;
        }
        catch (OperationCanceledException) { if (ReferenceEquals(document, doc)) status.Text = "디자인 스타일 적용을 취소했습니다."; return null; }
        catch (Exception error) { if (headlessTesting) throw; if (ReferenceEquals(document, doc)) MessageDialog.Show(this, error.Message, "디자인 스타일", NoticeKind.Error); return null; }
        finally { if (ReferenceEquals(jobCts, cts)) jobCts = null; cts.Dispose(); }
    }

    /// <summary>Deletes a style folder in one undo step; layers the style hid are shown again.</summary>
    internal void RemoveDesignStyle(Guid groupId)
    {
        if (doc.Layers.FirstOrDefault(l => l.Id == groupId) is not { } group || !DesignStyles.IsStyleGroup(group)) return;
        if (IsLockedWithParents(group)) { status.Text = "잠긴 스타일 그룹입니다. 그룹과 부모 그룹의 잠금을 먼저 해제하세요."; return; }
        Edit("디자인 스타일 제거", () => { DesignStyleEngine.Remove(doc, groupId); selectedLayers.Remove(groupId); });
        status.Text = "디자인 스타일을 제거했습니다. 원본 레이어는 그대로입니다.";
    }

    // Inspector section of a selected style folder: its style, settings and re-apply / remove.
    void AddStyleProperties(Layer layer)
    {
        if (layer.Kind != LayerKind.Group || layer.Style is not { } tag) return;
        properties.Children.Add(Theme.Section("디자인 스타일"));
        var style = DesignStyles.Find(tag.StyleId);
        if (style == null)
        {
            var unknown = Theme.Label("이 버전에서 알 수 없는 스타일입니다. 안의 레이어는 그대로 편집할 수 있습니다.", Theme.CaptionSize, Theme.Muted);
            properties.Children.Add(unknown); return;
        }
        bool locked = IsLockedWithParents(layer); var id = layer.Id; var boundDocument = doc;
        var identity = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
        var badge = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = Theme.Selected, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = Theme.Glyph(Theme.Glyphs.Style, 18, Theme.Accent) };
        DockPanel.SetDock(badge, Dock.Left); identity.Children.Add(badge);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Theme.Label(style.Name, Theme.BodySize); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0);
        names.Children.Add(name); var about = Theme.Label(style.Description, Theme.CaptionSize, Theme.Muted); about.Margin = new Thickness(0, 1, 0, 0); names.Children.Add(about);
        identity.Children.Add(names); properties.Children.Add(identity);
        var current = new Dictionary<string, double>(style.Values(tag.ValueMap), StringComparer.Ordinal);
        foreach (var parameter in style.Parameters)
        {
            string key = parameter.Key;
            switch (parameter.Kind)
            {
                case StyleParameterKind.Slider:
                    var slider = new ParameterSlider(parameter.Name, parameter.Min, parameter.Max, current[key], parameter.Default) { ToolTip = parameter.Description, IsEnabled = !locked };
                    slider.Changed += value => current[key] = parameter.Clamp(value);
                    properties.Children.Add(slider); break;
                case StyleParameterKind.Toggle:
                    var check = new CheckBox { Content = parameter.Name, IsChecked = current[key] >= .5, ToolTip = parameter.Description, Margin = new Thickness(2, 4, 2, 8), IsEnabled = !locked };
                    check.Click += (_, _) => current[key] = check.IsChecked == true ? 1 : 0;
                    properties.Children.Add(check); break;
                default:
                    properties.Children.Add(PropertyRows.Caption(parameter.Name));
                    var choice = new SegmentedChoice<int>(parameter.Choices.Select((c, i) => (i, c.Name)), (int)current[key]) { ToolTip = parameter.Description, Margin = new Thickness(2, 0, 2, 8), IsEnabled = !locked };
                    System.Windows.Automation.AutomationProperties.SetName(choice, parameter.Name);
                    choice.Changed += index => current[key] = index;
                    properties.Children.Add(choice); break;
            }
        }
        bool Current() => ReferenceEquals(doc, boundDocument) && doc.ActiveId == id;
        var commands = new List<Button>
        {
            InspectorCommand(Theme.Glyphs.Style, "다시 적용", "디자인 스타일 다시 적용", () => { if (Current()) _ = ApplyDesignStyleAsync(style.Id, current, id); }, "바꾼 설정으로 이 스타일 그룹을 다시 만듭니다. 한 번에 실행 취소할 수 있습니다.", layer),
            InspectorCommand(Theme.Glyphs.Sliders, "갤러리", "디자인 스타일 갤러리 열기", ShowDesignStyles, "미리보기를 보며 설정을 바꾸거나 다른 스타일로 바꿉니다.", layer),
            InspectorCommand(Theme.Glyphs.Delete, "스타일 제거", "디자인 스타일 제거", () => { if (Current()) RemoveDesignStyle(id); }, "스타일 그룹을 지우고, 스타일이 숨긴 레이어를 다시 표시합니다.", layer)
        };
        properties.Children.Add(QuickActions.Grid(2, commands, QuickActions.CommandWidth));
    }

    // Panel entry points: the design tab's feature card and the photo tab's command row share one section title.
    void AddDesignStyleFeature(StackPanel panel)
    {
        WorkspaceSection(panel, UserProfiles.DesignStyle, "도면이나 사진을 한 번에 스크린톤 평면, 어두운 단면, 청사진, 포스터, 에디토리얼로 꾸밉니다. 결과는 편집할 수 있는 레이어 묶음입니다.");
        panel.Children.Add(QuickActions.Feature(Theme.Glyphs.Style, "디자인 스타일", "스크린톤 · 단면 · 청사진 · 포스터 · 에디토리얼",
            Run(ShowDesignStyles), "미리보기를 보며 디자인 스타일을 고르고 편집할 수 있는 레이어로 적용"));
    }

    void AddDesignStyleCommands(StackPanel panel)
    {
        WorkspaceSection(panel, UserProfiles.DesignStyle, "사진이나 도면을 한 번에 꾸미고, 결과를 편집할 수 있는 레이어 묶음으로 더합니다.");
        WorkspaceCommands(panel, (Theme.Glyphs.Style, "디자인 스타일", ShowDesignStyles, "미리보기를 보며 디자인 스타일을 고르고 편집할 수 있는 레이어로 적용", null));
    }
}
