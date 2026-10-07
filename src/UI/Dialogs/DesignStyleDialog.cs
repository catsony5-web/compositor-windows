using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

/// <summary>What the gallery asks the editor to do when it closes.</summary>
public enum DesignStyleChoice { None, Apply, Remove }

// 디자인 스타일 gallery: one card per style with a small live render of the current document in that
// style, and the chosen style's larger preview and parameters. Renders run on an STA thread from a
// miniature of the document (StylePreview), one at a time, newest settings first; closing cancels them.
public sealed class DesignStyleDialog : Window
{
    sealed class Card(DesignStyle style, Button button, Image image, TextBlock pending)
    {
        public DesignStyle Style { get; } = style;
        public Button Button { get; } = button;
        public Image Image { get; } = image;
        public TextBlock Pending { get; } = pending;
        public long Rendered = -1;
    }

    readonly Document document;
    readonly StyleServices services;
    readonly bool editing;
    readonly List<Card> cards = [];
    readonly Dictionary<string, Dictionary<string, double>> values = [];
    readonly Image preview = new() { Stretch = Stretch.Uniform };
    readonly TextBlock previewState = DialogShell.Note("미리보기 준비 중…");
    readonly TextBlock title = Theme.Label("", Theme.HeadingSize), description = Theme.Label("", Theme.BodySize, Theme.Muted), kindNote = DialogShell.Note("");
    readonly StackPanel parameters = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Button apply;
    StylePreview? small, large;
    Task? sources;
    CancellationTokenSource? largeCts;
    System.Windows.Threading.DispatcherTimer? debounce;
    long version;
    int pending;
    bool started, closed;

    public DesignStyle SelectedStyle { get; private set; }
    public DesignStyleChoice Choice { get; private set; }
    /// <summary>Values of the selected style's parameters.</summary>
    public IReadOnlyDictionary<string, double> Values => values[SelectedStyle.Id];
    internal int ThumbnailsRendered { get; private set; }
    internal int PreviewsRendered { get; private set; }
    internal bool Busy => pending > 0 || cardsRunning || sources is { IsCompleted: false } || debounce?.IsEnabled == true;
    internal Image LargePreview => preview;
    internal IReadOnlyList<Button> CardButtons => cards.Select(c => c.Button).ToArray();
    internal IReadOnlyList<Image> CardImages => cards.Select(c => c.Image).ToArray();
    internal StackPanel ParameterPanel => parameters;
    internal Button ApplyButton => apply;
    /// <summary>The gallery re-applies (or removes) the selected style folder instead of adding one.</summary>
    internal bool Editing => editing;
    internal CancellationToken Lifetime => lifetime.Token;

    public DesignStyleDialog(Window? owner, Document document, string? styleId = null, IReadOnlyDictionary<string, double>? initial = null, bool editing = false, StyleServices? services = null)
    {
        this.document = document.Snapshot(); this.editing = editing; this.services = (services ?? StyleServices.Default).Memoized();
        foreach (var style in DesignStyles.All) values[style.Id] = new(style.Values(style.Id == styleId ? initial : null), StringComparer.Ordinal);
        bool drawing = DesignStyleEngine.IsDrawing(document, null);
        SelectedStyle = DesignStyles.Find(styleId) ?? DesignStyles.All.First(s => s.Target == (drawing ? StyleTarget.Drawing : StyleTarget.Photo));
        DialogShell.Prepare(this, owner, "디자인 스타일");
        Width = 1040; Height = 720; MinWidth = 860; MinHeight = 600; ResizeMode = ResizeMode.CanResizeWithGrip;
        var root = new Grid { Margin = new Thickness(22, 20, 22, 18) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(DialogShell.Title("디자인 스타일"));
        head.Children.Add(DialogShell.Subtitle("도면이나 사진에 한 번에 디자인을 입힙니다. 결과는 원본 위에 편집할 수 있는 레이어 묶음으로 더해집니다."));
        root.Children.Add(head);

        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 420 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
        var grid = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
        grid.SizeChanged += (_, e) => { if (e.WidthChanged) grid.Columns = Math.Clamp((int)(e.NewSize.Width / 210), 1, 2); };
        foreach (var style in DesignStyles.All) { var card = CreateCard(style, drawing); cards.Add(card); grid.Children.Add(card.Button); }
        var gallery = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(-3, 0, 12, 0) };
        AutomationProperties.SetName(gallery, "디자인 스타일 목록");
        body.Children.Add(gallery);

        var details = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        var stage = new Grid { MinHeight = 180, MaxHeight = 220 };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        stage.Children.Add(preview);
        previewState.HorizontalAlignment = HorizontalAlignment.Center; previewState.VerticalAlignment = VerticalAlignment.Center; stage.Children.Add(previewState);
        details.Children.Add(DialogShell.Card(new Border { Padding = new Thickness(8), Child = stage }));
        title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 14, 0, 0); details.Children.Add(title);
        description.Margin = new Thickness(0, 2, 0, 0); details.Children.Add(description);
        details.Children.Add(kindNote);
        details.Children.Add(parameters);
        details.Children.Add(DialogShell.Note(editing ? "다시 적용하면 선택한 스타일 그룹을 이 설정으로 새로 만듭니다. 실행 취소 한 번으로 되돌릴 수 있습니다."
            : "적용하면 ‘스타일 · 이름’ 그룹이 추가됩니다. 원본 레이어는 그대로이고, 그룹을 고르면 설정을 바꿔 다시 적용할 수 있습니다."));
        var side = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(side, 1); body.Children.Add(side);

        var close = DialogShell.Secondary("닫기", () => { Choice = DesignStyleChoice.None; Close(); }); close.IsCancel = true;
        apply = DialogShell.Primary(editing ? "다시 적용" : "적용", () => { Choice = DesignStyleChoice.Apply; if (IsLoaded) DialogResult = true; else Close(); });
        apply.IsDefault = true;
        var buttons = new List<Button>();
        if (editing)
        {
            var remove = DialogShell.Secondary("스타일 제거", () => { Choice = DesignStyleChoice.Remove; if (IsLoaded) DialogResult = true; else Close(); });
            remove.ToolTip = "스타일 그룹을 지우고 스타일이 바꾼 레이어 표시를 되돌립니다."; buttons.Add(remove);
        }
        buttons.Add(close); buttons.Add(apply);
        var footer = DialogShell.Footer([.. buttons]); footer.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(footer, 2); root.Children.Add(footer);

        ShowSelection();
        Loaded += (_, _) => StartPreviews();
        Closed += (_, _) => { closed = true; version++; lifetime.Cancel(); largeCts?.Cancel(); debounce?.Stop(); };
    }

    Card CreateCard(DesignStyle style, bool drawing)
    {
        var image = new Image { Stretch = Stretch.Uniform, Height = 104 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var pendingText = DialogShell.Note("미리보기 준비 중…"); pendingText.HorizontalAlignment = HorizontalAlignment.Center; pendingText.VerticalAlignment = VerticalAlignment.Center; pendingText.Margin = new Thickness(6);
        var frame = new Grid(); frame.Children.Add(image); frame.Children.Add(pendingText);
        var thumb = new Border { Background = Theme.Stage, CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = frame, MinHeight = 112 };
        var content = new StackPanel();
        content.Children.Add(thumb);
        var heading = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        var chip = new Border { Background = Theme.Input, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = DesignStyles.TargetName(style.Target), FontSize = Theme.CaptionSize, Foreground = Theme.Muted } };
        DockPanel.SetDock(chip, Dock.Right); heading.Children.Add(chip);
        heading.Children.Add(new KeepWordsTextBlock { Text = style.Name, FontSize = Theme.BodySize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(heading);
        content.Children.Add(new KeepWordsTextBlock { Text = style.Description, FontSize = Theme.CaptionSize, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 2) });
        var button = new Button { Content = content, Padding = new Thickness(8), Margin = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1.5) };
        button.ToolTip = $"{style.Name} · {style.Description}";
        AutomationProperties.SetName(button, style.Name);
        button.Click += (_, _) => Select(style.Id);
        return new Card(style, button, image, pendingText);
    }

    internal void Select(string styleId)
    {
        var style = DesignStyles.Find(styleId) ?? throw new ArgumentException("알 수 없는 디자인 스타일입니다.");
        if (ReferenceEquals(style, SelectedStyle)) return;
        SelectedStyle = style; ShowSelection(); ScheduleLarge(0);
    }

    /// <summary>Changes a parameter of the selected style the way its control does.</summary>
    internal void SetValue(string key, double value)
    {
        var parameter = SelectedStyle.Parameter(key) ?? throw new ArgumentException("이 스타일에 없는 설정입니다.");
        values[SelectedStyle.Id][key] = parameter.Clamp(value); BuildParameters(); Changed();
    }

    void Changed()
    {
        // The selected card and the large preview follow the settings once they settle.
        var card = cards.First(c => ReferenceEquals(c.Style, SelectedStyle)); card.Rendered = -1;
        ScheduleLarge(180);
    }

    void ShowSelection()
    {
        foreach (var card in cards)
        {
            bool on = ReferenceEquals(card.Style, SelectedStyle);
            card.Button.Background = on ? Theme.Selected : Theme.Surface; card.Button.BorderBrush = on ? Theme.Accent : Brushes.Transparent;
        }
        title.Text = SelectedStyle.Name; description.Text = SelectedStyle.Description;
        bool drawing = DesignStyleEngine.IsDrawing(document, null);
        (kindNote.Text, kindNote.Foreground) = SelectedStyle.Target switch
        {
            StyleTarget.Drawing when !drawing => ("도면용 스타일입니다. 사진에도 적용되지만 어두운 부분을 선으로 읽습니다.", Theme.Warning),
            StyleTarget.Photo when drawing => ("사진용 스타일입니다. 도면에도 적용되지만 사진에서 더 잘 어울립니다.", Theme.Warning),
            StyleTarget.Drawing => ("닫힌 영역과 선을 읽어 도면을 꾸밉니다.", Theme.Subtle),
            StyleTarget.Photo => ("사진 전체의 색과 질감, 글자 배치를 바꿉니다.", Theme.Subtle),
            _ => (drawing ? "도면은 흰 선의 청사진으로, 사진은 푸른 인화로 바뀝니다." : "사진은 푸른 인화로, 도면은 흰 선의 청사진으로 바뀝니다.", Theme.Subtle)
        };
        BuildParameters();
    }

    void BuildParameters()
    {
        parameters.Children.Clear();
        parameters.Children.Add(Theme.Section("설정"));
        var current = values[SelectedStyle.Id];
        foreach (var parameter in SelectedStyle.Parameters)
        {
            string key = parameter.Key;
            switch (parameter.Kind)
            {
                case StyleParameterKind.Slider:
                    var slider = new ParameterSlider(parameter.Name, parameter.Min, parameter.Max, current[key], parameter.Default) { ToolTip = parameter.Description };
                    slider.Changed += value => { values[SelectedStyle.Id][key] = parameter.Clamp(value); Changed(); };
                    parameters.Children.Add(slider); break;
                case StyleParameterKind.Toggle:
                    var check = new CheckBox { Content = parameter.Name, IsChecked = current[key] >= .5, ToolTip = parameter.Description, Margin = new Thickness(1, 6, 1, 8) };
                    check.Click += (_, _) => { values[SelectedStyle.Id][key] = check.IsChecked == true ? 1 : 0; Changed(); };
                    parameters.Children.Add(check); break;
                default:
                    parameters.Children.Add(DialogShell.FieldLabel(parameter.Name));
                    var choice = new SegmentedChoice<int>(parameter.Choices.Select((c, i) => (i, c.Name)), (int)current[key]) { ToolTip = parameter.Description, Margin = new Thickness(0, 0, 0, 8) };
                    AutomationProperties.SetName(choice, parameter.Name);
                    choice.Changed += index => { values[SelectedStyle.Id][key] = index; Changed(); };
                    parameters.Children.Add(choice); break;
            }
        }
    }

    /// <summary>Starts the miniature and the card renders (also for offscreen use without Loaded).</summary>
    internal void StartPreviews()
    {
        if (started || closed) return;
        started = true;
        var token = lifetime.Token;
        sources = PrepareAsync(token);
    }

    async Task PrepareAsync(CancellationToken token)
    {
        try
        {
            var snapshot = document;
            // Both miniatures share the memoized subject cut-out of each image.
            (small, large) = await CompatibilityImport.OnSta(() => (StylePreview.Create(snapshot, 300, services, token), StylePreview.Create(snapshot, 820, services, token)), token);
            if (closed) return;
            ScheduleLarge(0);
            EnsureCards();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) previewState.Text = Loc.T("미리보기를 만들지 못했습니다: ") + error.Message; }
    }

    // One loop renders the cards that need it; settings changes mark their card again.
    async void EnsureCards()
    {
        if (cardsRunning || small == null || closed) return;
        cardsRunning = true;
        try { await RenderCardsAsync(lifetime.Token); } finally { cardsRunning = false; }
    }

    async Task RenderCardsAsync(CancellationToken token)
    {
        while (!closed && small != null)
        {
            // The selected card first, then the others in order.
            var card = cards.OrderBy(c => ReferenceEquals(c.Style, SelectedStyle) ? 0 : 1).FirstOrDefault(c => c.Rendered < 0);
            if (card == null) return;
            long at = ++cardGeneration; card.Rendered = at;
            var style = card.Style; var settings = new Dictionary<string, double>(values[style.Id]); var source = small;
            pending++;
            try
            {
                var image = await CompatibilityImport.OnSta(() => source.Render(style.Id, settings, token), token);
                if (closed) return;
                // A newer change to this card's settings asked for another render.
                if (card.Rendered == at) { card.Image.Source = image.Bitmap(); card.Pending.Visibility = Visibility.Collapsed; ThumbnailsRendered++; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { card.Pending.Text = error.Message; card.Rendered = long.MaxValue; }
            finally { pending--; }
        }
    }
    long cardGeneration;
    bool cardsRunning;

    void ScheduleLarge(int delay)
    {
        if (closed) return;
        debounce ??= CreateDebounce();
        debounce.Stop(); debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)); debounce.Start();
    }

    System.Windows.Threading.DispatcherTimer CreateDebounce()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
        timer.Tick += (_, _) => { timer.Stop(); _ = RenderLargeAsync(); EnsureCards(); };
        return timer;
    }

    async Task RenderLargeAsync()
    {
        if (closed || large == null) return;
        largeCts?.Cancel(); var cts = largeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        long at = ++version; var style = SelectedStyle; var settings = new Dictionary<string, double>(values[style.Id]); var source = large;
        previewState.Text = Loc.T("미리보기 계산 중…"); previewState.Visibility = Visibility.Visible;
        pending++;
        try
        {
            var image = await CompatibilityImport.OnSta(() => source.Render(style.Id, settings, cts.Token), cts.Token);
            if (closed || at != version) return;
            preview.Source = image.Bitmap(); previewState.Visibility = Visibility.Collapsed; PreviewsRendered++;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && at == version) { previewState.Text = Loc.T("미리보기를 만들지 못했습니다: ") + error.Message; previewState.Visibility = Visibility.Visible; } }
        finally { pending--; if (ReferenceEquals(largeCts, cts)) largeCts = null; cts.Dispose(); }
    }

    /// <summary>Cancels every render, as closing the window does.</summary>
    internal void CancelPreviews() { version++; lifetime.Cancel(); largeCts?.Cancel(); debounce?.Stop(); closed = true; }

    /// <summary>Starts the previews and pumps the dispatcher until they are idle (offscreen captures and self-tests).</summary>
    internal bool WaitForPreviews(TimeSpan timeout)
    {
        var previous = SynchronizationContext.Current;
        if (previous is not System.Windows.Threading.DispatcherSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        try
        {
            StartPreviews();
            var frame = new System.Windows.Threading.DispatcherFrame(); var deadline = DateTime.UtcNow + timeout;
            var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            bool Done() => closed || !Busy && cards.All(c => c.Rendered >= 0) && small != null;
            poll.Tick += (_, _) => { if (Done() || DateTime.UtcNow > deadline) frame.Continue = false; };
            poll.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); } finally { poll.Stop(); }
            return Done();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
