using System.IO;
using System.Text.RegularExpressions;

namespace Compositor.Windows;

// 디자인 스타일: one action turns the user's drawing or photo into a designed look, added as an
// editable stack of layers (adjustment layers, pattern fills, texture and text layers) inside one
// pass-through folder named "스타일 · <이름>". This file is the registry — stable ids, Korean names,
// what each style is meant for and its 1–3 parameters — and the tag the folder keeps so a project
// remembers the style, its parameters and the edits it made. The recipes are Engine/StyleRecipes.cs.
public enum StyleTarget { Drawing, Photo, Any }
public enum StyleParameterKind { Slider, Toggle, Choice }

public sealed record StyleChoice(string Key, string Name);

/// <summary>One user parameter. Sliders run Min–Max, toggles are 0/1, choices store the index of <see cref="Choices"/>.</summary>
public sealed record StyleParameter(string Key, string Name, StyleParameterKind Kind, double Default, string Description, double Min = 0, double Max = 100)
{
    public StyleChoice[] Choices { get; init; } = [];
    public double Clamp(double value) => !double.IsFinite(value) ? Default : Kind switch
    {
        StyleParameterKind.Toggle => value >= .5 ? 1 : 0,
        StyleParameterKind.Choice => Math.Clamp(Math.Round(value), 0, Math.Max(0, Choices.Length - 1)),
        _ => Math.Clamp(value, Min, Max)
    };
}

public sealed record DesignStyle(string Id, string Name, string Description, StyleTarget Target, int Version, StyleParameter[] Parameters)
{
    /// <summary>The recipe reads the document's layers (line work, hatch fills), not only how it looks; gallery previews keep them.</summary>
    public bool ReadsLayers { get; init; }
    public StyleParameter? Parameter(string key) => Parameters.FirstOrDefault(p => p.Key == key);
    /// <summary>Every parameter with its default, overridden by the given values (clamped); unknown keys are ignored.</summary>
    public IReadOnlyDictionary<string, double> Values(IReadOnlyDictionary<string, double>? given = null) =>
        Parameters.ToDictionary(p => p.Key, p => p.Clamp(given != null && given.TryGetValue(p.Key, out var value) ? value : p.Default), StringComparer.Ordinal);
}

/// <summary>A parameter value stored with a style folder.</summary>
public sealed record StyleValue(string Key, double Value);
/// <summary>A non-destructive change the style made to an existing layer, with the value to restore when the style is removed.</summary>
public sealed record StyleEdit(Guid LayerId, bool Visible);

/// <summary>Kept on the style folder (Layer.Style): which style, the parameters, its version, the analysed targets and edits to revert.</summary>
public sealed record StyleTag(string StyleId, int Version, StyleValue[] Values, Guid[] Targets, StyleEdit[] Edits)
{
    public const int MaxValues = 32;
    public double Get(string key, double fallback) => Values?.FirstOrDefault(v => v.Key == key)?.Value ?? fallback;
    public IReadOnlyDictionary<string, double> ValueMap => (Values ?? []).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
    public void Validate()
    {
        // Unknown ids stay loadable: a style folder from a newer version is still plain layers.
        if (StyleId == null || !DesignStyles.IsValidKey(StyleId) || Version < 1 || Version > 10_000 || Values == null || Values.Length > MaxValues ||
            Targets == null || Targets.Length > Document.MaxNodes || Edits == null || Edits.Length > Document.MaxNodes ||
            Values.Any(v => v == null || !DesignStyles.IsValidKey(v.Key) || !double.IsFinite(v.Value) || Math.Abs(v.Value) > 1_000_000) ||
            Targets.Any(id => id == Guid.Empty) || Edits.Any(e => e == null || e.LayerId == Guid.Empty))
            throw new InvalidDataException("디자인 스타일 정보가 올바르지 않습니다.");
    }
}

public static class DesignStyles
{
    public const string ScreentonePlan = "screentone-plan", DarkSection = "dark-section", Cyanotype = "cyanotype",
        NeoBrutalistPoster = "neo-brutalist-poster", TranslucentEditorial = "translucent-editorial";
    /// <summary>Folder name prefix; the folder is named "스타일 · " and the style's Korean name.</summary>
    public const string GroupPrefix = "스타일 · ";

    static StyleParameter Slider(string key, string name, double value, string description) => new(key, name, StyleParameterKind.Slider, value, description);

    public static readonly IReadOnlyList<DesignStyle> All =
    [
        new(ScreentonePlan, "흑백 스크린톤 평면", "닫힌 방을 망점·포셰·점묘로 채운 복사본 느낌의 평면", StyleTarget.Drawing, 2,
        [
            Slider("strength", "강도", 55, "망점 농도와 흑백 대비. 높을수록 어두운 망점과 포셰가 많아집니다."),
            Slider("texture", "질감", 45, "복사기 토너 입자와 줄무늬의 양")
        ]) { ReadsLayers = true },
        new(DarkSection, "어두운 단면", "검은 바탕에 흰 선, 뒤로 은은한 격자", StyleTarget.Drawing, 2,
        [
            new("grid", "격자", StyleParameterKind.Toggle, 1, "선 뒤에 옅은 사각 격자를 깝니다.", 0, 1),
            Slider("strength", "강도", 70, "선의 밝기와 바탕 대비")
        ]),
        new(Cyanotype, "청사진 (사이아노타입)", "프러시안 블루 단색 인화와 종이 결", StyleTarget.Any, 2,
        [
            Slider("strength", "강도", 70, "파랑의 깊이와 대비"),
            Slider("paper", "바탕 밝기", 60, "사진은 밝은 종이 색, 도면은 파란 바탕의 밝기. 종이 섬유와 붓 자국 가장자리도 함께 늘어납니다.")
        ]),
        new(NeoBrutalistPoster, "네오 브루탈리즘 포스터", "큰 제목 앞에 피사체, 강한 대비와 거친 인쇄 질감", StyleTarget.Photo, 2,
        [
            new("color", "색", StyleParameterKind.Choice, 0, "사진의 두 가지 색", 0, 3)
            {
                Choices = [new("mono", "흑백"), new("red", "빨강"), new("blue", "파랑"), new("orange", "주황")]
            },
            new("photo", "사진 표현", StyleParameterKind.Choice, 0, "사진을 두 색으로 찍는 방식: 인쇄 망점, 흑백 비트맵 또는 부드러운 듀오톤", 0, 2)
            {
                Choices = [new("halftone", "망점"), new("bitmap", "비트맵"), new("duotone", "듀오톤")]
            },
            new("title", "제목", StyleParameterKind.Choice, 0, "큰 제목을 채운 글자로 둘지 외곽선만 그릴지", 0, 1)
            {
                Choices = [new("fill", "채움"), new("outline", "외곽선")]
            }
        ]),
        new(TranslucentEditorial, "반투명 에디토리얼", "반투명 종이 패널 너머로 보이는 사진과 작은 글", StyleTarget.Photo, 2,
        [
            Slider("blur", "흐림", 60, "패널 아래 사진이 흐려지는 정도"),
            new("panel", "패널 위치", StyleParameterKind.Choice, 2, "반투명 패널을 둘 자리", 0, 3)
            {
                Choices = [new("left", "왼쪽"), new("center", "가운데"), new("right", "오른쪽"), new("bottom", "아래")]
            },
            Slider("glow", "빛 번짐", 35, "밝은 곳의 빛이 주변으로 번지는 정도. 0이면 빛 번짐 레이어를 만들지 않습니다.")
        ])
    ];

    public static DesignStyle? Find(string? id) => id == null ? null : All.FirstOrDefault(s => s.Id == id);
    public static string GroupName(DesignStyle style) => GroupPrefix + style.Name;
    public static bool IsStyleGroup(Layer? layer) => layer is { Kind: LayerKind.Group, Style: not null };
    public static bool IsValidKey(string? key) => key != null && Regex.IsMatch(key, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    public static string TargetName(StyleTarget target) => target switch { StyleTarget.Drawing => "도면", StyleTarget.Photo => "사진", _ => "도면 · 사진" };

    /// <summary>Restores what a style changed on other layers (those not being removed with it).</summary>
    public static void Revert(Document doc, StyleTag tag, IReadOnlySet<Guid>? removing = null)
    {
        foreach (var edit in tag.Edits)
            if ((removing == null || !removing.Contains(edit.LayerId)) && doc.Layers.FirstOrDefault(l => l.Id == edit.LayerId) is { } layer) layer.Visible = edit.Visible;
    }

    /// <summary>Ids of every style folder and everything inside one, found in one pass over the document.</summary>
    public static HashSet<Guid> StyledLayers(Document doc)
    {
        var styled = doc.Layers.Where(IsStyleGroup).Select(l => l.Id).ToHashSet();
        if (styled.Count == 0) return styled;
        var children = doc.Layers.Where(l => l.ParentId != null).ToLookup(l => l.ParentId!.Value);
        var pending = new Stack<Guid>(styled);
        while (pending.Count > 0) foreach (var child in children[pending.Pop()]) if (styled.Add(child.Id)) pending.Push(child.Id);
        return styled;
    }

    /// <summary>The style folder that contains the layer (or the layer itself), if any.</summary>
    public static Layer? GroupOf(Document doc, Layer? layer)
    {
        var index = doc.Layers.ToDictionary(l => l.Id);
        for (int depth = 0; layer != null && depth < 32; depth++)
        {
            if (IsStyleGroup(layer)) return layer;
            layer = layer.ParentId is { } parent && index.TryGetValue(parent, out var next) ? next : null;
        }
        return null;
    }
}
