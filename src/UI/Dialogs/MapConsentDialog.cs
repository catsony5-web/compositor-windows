using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

/// <summary>The user's answer to one online map request.</summary>
public sealed record MapConsentAnswer(bool Agreed, bool AllowAutomation);

/// <summary>
/// Asked before every online map request: names the public server, the exact data sent (the
/// latitude/longitude box or the search words), what travels with it and what does not, and the
/// licence of what comes back. Nothing is sent unless the user presses the primary button.
/// </summary>
public sealed class MapConsentDialog : Window
{
    public MapRequest Request { get; }
    public MapConsentAnswer Answer { get; private set; } = new(false, false);
    internal CheckBox AutomationOption { get; }
    internal IReadOnlyList<(string Caption, string Value)> Rows { get; }

    public MapConsentDialog(Window? owner, MapRequest request, bool automationAllowed)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        bool search = request.Kind == MapRequestKind.PlaceSearch;
        string caption = search ? "인터넷에서 장소 찾기" : "인터넷에서 지도 데이터 받기";
        DialogShell.Prepare(this, owner, caption);
        Width = 620; SizeToContent = SizeToContent.Height; MinWidth = 480; ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        var head = new DockPanel();
        var icon = Theme.Glyph(Theme.Glyphs.Globe, 28, Theme.Accent); icon.Margin = new Thickness(0, 2, 12, 0); icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left); head.Children.Add(icon);
        var titles = new StackPanel(); titles.Children.Add(DialogShell.Title(caption));
        var subtitle = DialogShell.Subtitle(search
            ? "아래 내용을 OpenStreetMap의 공개 서버로 보냅니다. ‘보내고 찾기’를 누르기 전에는 아무것도 보내지 않습니다."
            : "아래 내용을 OpenStreetMap의 공개 서버로 보냅니다. ‘보내고 받기’를 누르기 전에는 아무것도 보내지 않습니다.");
        subtitle.TextWrapping = TextWrapping.Wrap; titles.Children.Add(subtitle); head.Children.Add(titles);
        panel.Children.Add(head);

        string area = "";
        if (request.Area is { } box)
        {
            var (w, h) = box.Meters;
            area = string.Format(CultureInfo.InvariantCulture, "{0:0.#####}, {1:0.#####} – {2:0.#####}, {3:0.#####}", box.South, box.West, box.North, box.East);
            area = Loc.Format("영역 {0} (남서–북동, 약 {1} × {2} km)", area, (w / 1000).ToString("0.0", CultureInfo.InvariantCulture), (h / 1000).ToString("0.0", CultureInfo.InvariantCulture));
        }
        Rows =
        [
            ("보내는 곳", search ? request.Endpoint.Host + " · " + Loc.T("OpenStreetMap 장소 검색(Nominatim)") : request.Endpoint.Host + " · " + Loc.T("OpenStreetMap 데이터 조회(Overpass API)")),
            ("보내는 내용", search ? Loc.Format("검색어 “{0}”", request.Query!) : area + "\n" + Loc.T(request.Buildings ? "그릴 항목 목록: 길·철도·물·녹지·건물" : "그릴 항목 목록: 길·철도·물·녹지")),
            ("함께 전달되는 것", Loc.Format("프로그램 이름과 버전({0}) · 이 컴퓨터의 인터넷 주소(IP)는 서버에 보입니다.", MapDownload.UserAgent.Split(' ')[0])),
            ("보내지 않는 것", "열린 문서, 파일, 이미지, 레이어 이름, 계정 정보"),
            ("받는 것", search ? "장소 이름과 좌표 최대 6개" : "이 영역의 OpenStreetMap 데이터(ODbL 사용권). 지도에 ‘© OpenStreetMap contributors’를 표시합니다."),
            ("이용 규칙", "공개 서버의 이용 규칙에 따라 1초에 한 번만, 작은 영역만 요청합니다. 받은 내용은 이번 실행 동안만 메모리에 두고 같은 요청에 다시 씁니다.")
        ];
        var table = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 96 });
        table.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (name, value) in Rows)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Theme.Label(name, Theme.CaptionSize, Theme.Muted); label.Margin = new Thickness(0, 4, 14, 4); label.TextWrapping = TextWrapping.Wrap; label.MaxWidth = 150;
            var text = Theme.Label(value, Theme.BodySize); text.Margin = new Thickness(0, 3, 0, 5); text.TextWrapping = TextWrapping.Wrap;
            if (name == "보내는 내용") Loc.Keep(text);
            Grid.SetRow(label, table.RowDefinitions.Count - 1); Grid.SetRow(text, table.RowDefinitions.Count - 1); Grid.SetColumn(text, 1);
            table.Children.Add(label); table.Children.Add(text);
        }
        var card = DialogShell.Card(table); card.Margin = new Thickness(0, 16, 0, 0); panel.Children.Add(card);

        AutomationOption = new CheckBox
        {
            Content = new TextBlock { Text = "AI 연결이 보내는 지도 요청도 이번 실행 동안 묻지 않고 허용", TextWrapping = TextWrapping.Wrap },
            IsChecked = automationAllowed, Margin = new Thickness(1, 14, 1, 0), Foreground = Theme.Text,
            ToolTip = "켜면 AI 연결(create_map)이 같은 공개 서버에 작은 영역의 지도 데이터를 요청할 수 있습니다. 앱을 끄면 다시 꺼집니다."
        };
        panel.Children.Add(AutomationOption);
        panel.Children.Add(DialogShell.Note("인터넷을 쓰지 않으려면 openstreetmap.org의 ‘내보내기’로 받은 .osm 파일로 지도를 만드세요."));

        var cancel = DialogShell.Secondary("취소", Close); cancel.IsCancel = true;
        var send = DialogShell.Primary(search ? "보내고 찾기" : "보내고 받기", Agree); send.IsDefault = false;
        panel.Children.Add(DialogShell.Footer(cancel, send));
        Loaded += (_, _) => cancel.Focus();
    }

    internal void Agree()
    {
        Answer = new MapConsentAnswer(true, AutomationOption.IsChecked == true);
        if (IsVisible) DialogResult = true;
    }
}
