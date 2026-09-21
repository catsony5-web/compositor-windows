using System.Windows;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    // Share the actual menu-dialog definitions with offscreen previews and validation.
    internal static ParameterDialog CreateQuickAdjustmentDialog(Window? owner, string kind) => kind switch
    {
        "levels" => new(owner, "레벨 보정",
            [new("입력 검정", 0, 254, 0), new("입력 흰색", 1, 255, 255, 255), new("감마", .1, 9.99, 1, 1)],
            values => values[1] < values[0] + 1 ? "입력 흰색은 입력 검정보다 1 이상 커야 합니다." : null),
        "exposure" => new(owner, "노출 보정", [new("노출 EV", -5, 5, .5)]),
        "saturation" => new(owner, "채도 보정", [new("채도", -100, 100, 20)]),
        "blur" => new(owner, "가우시안 흐림", [new("반경 px", 1, 30, 5, 5, 1)]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    void QuickAdjustment(string kind)
    {
        var dialog = CreateQuickAdjustmentDialog(this, kind);
        if (dialog.ShowDialog() != true) return;
        var values = dialog.Values;
        if (kind == "levels") Adjust(kind, values[0], values[1], values[2]);
        else if (kind == "blur")
            RunRasterJob("가우시안 흐림", (layer, selected, token) => Imaging.Blur(layer, (int)values[0], selected));
        else Adjust(kind, values[0]);
    }
}
