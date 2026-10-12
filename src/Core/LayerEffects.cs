using System.IO;
using System.Text.Json.Serialization;

namespace Compositor.Windows;

// Stored by number in projects: append new kinds at the end.
public enum LayerEffectKind { Stroke, DropShadow, OuterGlow, InnerGlow, ColorOverlay, GradientOverlay }
// Outside: the stroke surrounds the shape. Center: it straddles the edge. Inside: it lines the edge within the shape.
public enum StrokePosition { Outside, Center, Inside }

/// <summary>
/// One live effect of a layer (레이어 효과). A layer keeps an ordered list of them (<see cref="Layer.Effects"/>);
/// they are drawn from the layer's own coverage on every render, so they follow edits, moves and
/// transforms and stay editable. Lengths are pixels of the layer's parent space (the document for
/// top-level layers), like <see cref="ShadowSpec"/>. Projects store the list as an optional field:
/// older versions ignore it and show the plain layer.
/// </summary>
public sealed record LayerEffect
{
    public const int MaxCount = 10;
    public const double MaxSize = 250, MaxDistance = 1000;
    public LayerEffectKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public uint ColorArgb { get; init; } = 0xFFFFFFFF;
    // Gradient overlay: the color where the direction ends (ColorArgb is where it starts).
    public uint EndColorArgb { get; init; } = 0xFF000000;
    public double Opacity { get; init; } = 1;
    // Stroke width; how far a glow or shadow reaches.
    public double Size { get; init; } = 8;
    // Glows and shadows: the share of Size that stays solid before it softens (0–1).
    public double Spread { get; init; }
    public StrokePosition Position { get; init; }
    // Stroke corners follow the shape round (true) or stay square.
    public bool Rounded { get; init; } = true;
    public BlendMode Blend { get; init; }
    // Gradient direction and the direction a shadow falls: 0° right, 90° down, clockwise.
    public double Angle { get; init; } = 90;
    // Shadow offset from the layer.
    public double Distance { get; init; }

    // Drop shadows and outer glows sit behind the layer and paint onto what is below it; the other
    // kinds paint on the layer itself.
    [JsonIgnore] public bool Behind => Kind is LayerEffectKind.DropShadow or LayerEffectKind.OuterGlow;
    // Kinds computed from the layer's coverage (distance or blur); overlays only recolor its pixels.
    [JsonIgnore] public bool UsesCoverage => Kind is LayerEffectKind.Stroke or LayerEffectKind.DropShadow or LayerEffectKind.OuterGlow or LayerEffectKind.InnerGlow;

    /// <summary>Blend modes the editor offers for a kind (the first is its default).</summary>
    public static BlendMode[] BlendsFor(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => [BlendMode.Normal],
        LayerEffectKind.DropShadow => [BlendMode.Normal, BlendMode.Multiply],
        LayerEffectKind.OuterGlow or LayerEffectKind.InnerGlow => [BlendMode.Normal, BlendMode.Screen],
        _ => [BlendMode.Normal, BlendMode.Multiply, BlendMode.Screen, BlendMode.Color]
    };

    /// <summary>Starting values for a new effect of a kind.</summary>
    public static LayerEffect Default(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => new() { Kind = kind, ColorArgb = 0xFFFFFFFF, Size = 8, Position = StrokePosition.Outside },
        LayerEffectKind.DropShadow => new() { Kind = kind, ColorArgb = 0xFF1E232B, Opacity = .45, Size = 18, Angle = 45, Distance = 10 },
        LayerEffectKind.OuterGlow => new() { Kind = kind, ColorArgb = 0xFFFFE6A0, Opacity = .85, Size = 20, Spread = .1 },
        LayerEffectKind.InnerGlow => new() { Kind = kind, ColorArgb = 0xFFFFFFFF, Opacity = .75, Size = 14 },
        LayerEffectKind.ColorOverlay => new() { Kind = kind, ColorArgb = 0xFF3A6FDB },
        _ => new() { Kind = LayerEffectKind.GradientOverlay, ColorArgb = 0xFF8EC5FF, EndColorArgb = 0xFF1E3A70, Angle = 90 }
    };

    // Solid and soft reach of glows and shadows (parent px): the solid part grows the shape, the soft
    // part blurs it. LayerEffectRenderer uses the same split.
    [JsonIgnore] internal double Hard => Spread * Size;
    [JsonIgnore] internal double Soft => (1 - Spread) * Size;

    /// <summary>How far beyond the layer's edges the effect can paint, in parent pixels.</summary>
    [JsonIgnore] public double Reach => Kind switch
    {
        LayerEffectKind.Stroke => Position == StrokePosition.Outside ? Size : Position == StrokePosition.Center ? Size / 2 : 0,
        LayerEffectKind.OuterGlow => Hard + 1.4 * Soft,
        LayerEffectKind.DropShadow => Distance + Hard + 1.2 * Soft,
        _ => 0
    };

    public void Validate()
    {
        static bool In(double n, double low, double high) => double.IsFinite(n) && n >= low && n <= high;
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Position) || !Enum.IsDefined(Blend) || !BlendsFor(Kind).Contains(Blend) ||
            !In(Opacity, 0, 1) || !In(Size, Kind == LayerEffectKind.Stroke ? .5 : 0, MaxSize) || !In(Spread, 0, 1) ||
            !In(Angle, -360, 360) || !In(Distance, 0, MaxDistance))
            throw new InvalidDataException("레이어 효과 속성이 올바르지 않습니다.");
    }
}

/// <summary>Operations on a layer's effect list. Lists are immutable: every edit returns a new array.</summary>
public static class LayerEffects
{
    // Paint order of new effects from the bottom (first) to the top: a new effect is placed after the
    // last one of its own rank or below, so strokes stay on top and shadows at the back.
    static int Rank(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.DropShadow => 0, LayerEffectKind.OuterGlow => 1, LayerEffectKind.GradientOverlay => 2,
        LayerEffectKind.ColorOverlay => 3, LayerEffectKind.InnerGlow => 4, _ => 5
    };

    /// <summary>Kinds the editor lists in its add menu, in menu order.</summary>
    public static readonly LayerEffectKind[] MenuOrder = [LayerEffectKind.Stroke, LayerEffectKind.DropShadow, LayerEffectKind.OuterGlow, LayerEffectKind.InnerGlow, LayerEffectKind.ColorOverlay, LayerEffectKind.GradientOverlay];

    /// <summary>Leaves only: groups and adjustment layers have no coverage of their own.</summary>
    public static bool Supports(Layer layer) => layer.Kind is not (LayerKind.Group or LayerKind.Adjustment);
    public static bool Supports(LayerKind kind) => kind is not (LayerKind.Group or LayerKind.Adjustment);

    /// <summary>True when the layer has at least one effect turned on.</summary>
    public static bool Any(Layer layer) => layer.Effects is { } list && Array.Exists(list, e => e.Enabled) && Supports(layer);

    /// <summary>The effects turned on, in paint order.</summary>
    public static LayerEffect[] Active(Layer layer) => layer.Effects is { } list && Supports(layer) ? Array.FindAll(list, e => e.Enabled) : [];

    internal static void ValidateLayer(Layer layer)
    {
        if (layer.Effects is not { } list) return;
        if (!Supports(layer)) throw new InvalidDataException("레이어 효과는 이미지·텍스트·도형 레이어에만 둘 수 있습니다.");
        if (list.Length is 0 or > LayerEffect.MaxCount) throw new InvalidDataException($"레이어 효과는 레이어마다 {LayerEffect.MaxCount}개까지 둘 수 있습니다.");
        foreach (var effect in list) (effect ?? throw new InvalidDataException("레이어 효과 정보가 없습니다.")).Validate();
    }

    public static bool Same(LayerEffect[]? a, LayerEffect[]? b) => ReferenceEquals(a, b) || a != null && b != null && a.AsSpan().SequenceEqual(b);

    public static int Hash(LayerEffect[]? list)
    {
        if (list == null) return 0;
        var hash = new HashCode(); hash.Add(list.Length);
        foreach (var effect in list) hash.Add(effect);
        return hash.ToHashCode();
    }

    /// <summary>How far the layer's effects reach beyond its edges, in parent pixels.</summary>
    public static double Reach(Layer layer)
    {
        double reach = 0;
        if (layer.Effects is { } list && Supports(layer))
            foreach (var effect in list) if (effect.Enabled) reach = Math.Max(reach, effect.Reach);
        return reach;
    }

    // An empty list is stored as no list.
    public static LayerEffect[]? Normalize(IEnumerable<LayerEffect>? effects) => effects?.ToArray() is { Length: > 0 } array ? array : null;

    /// <summary>A new effect in its usual place (see <see cref="Rank"/>).</summary>
    public static LayerEffect[] Add(LayerEffect[]? list, LayerEffect effect)
    {
        list ??= [];
        if (list.Length >= LayerEffect.MaxCount) throw new InvalidOperationException($"레이어 효과는 레이어마다 {LayerEffect.MaxCount}개까지 둘 수 있습니다.");
        int at = 0;
        for (int i = 0; i < list.Length; i++) if (Rank(list[i].Kind) <= Rank(effect.Kind)) at = i + 1;
        return [.. list[..at], effect, .. list[at..]];
    }

    public static LayerEffect[] Replace(LayerEffect[] list, int index, LayerEffect effect)
    {
        var copy = list.ToArray(); copy[index] = effect; return copy;
    }

    public static LayerEffect[]? Remove(LayerEffect[] list, int index) => Normalize(list.Where((_, i) => i != index));

    /// <summary>Moves an effect up (+1, painted later) or down (−1) in the paint order.</summary>
    public static LayerEffect[] Move(LayerEffect[] list, int index, int delta)
    {
        int next = index + delta;
        if (next < 0 || next >= list.Length) return list;
        var copy = list.ToArray(); (copy[index], copy[next]) = (copy[next], copy[index]); return copy;
    }

    public static string Name(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => "획", LayerEffectKind.DropShadow => "그림자", LayerEffectKind.OuterGlow => "외부 광선",
        LayerEffectKind.InnerGlow => "내부 광선", LayerEffectKind.ColorOverlay => "색 오버레이", _ => "그라데이션 오버레이"
    };

    public static string PositionName(StrokePosition position) => position switch
    {
        StrokePosition.Outside => "바깥쪽", StrokePosition.Center => "가운데", _ => "안쪽"
    };

    /// <summary>A short description of an effect's main settings, for its row.</summary>
    public static string Summary(LayerEffect effect) => effect.Kind switch
    {
        LayerEffectKind.Stroke => $"{PositionName(effect.Position)} · {effect.Size:0.#}px",
        LayerEffectKind.DropShadow => $"{effect.Distance:0.#}px · {effect.Angle:0}° · {effect.Opacity * 100:0}%",
        LayerEffectKind.OuterGlow or LayerEffectKind.InnerGlow => $"{effect.Size:0.#}px · {effect.Opacity * 100:0}%",
        LayerEffectKind.ColorOverlay => $"#{effect.ColorArgb & 0xFFFFFF:X6} · {effect.Opacity * 100:0}%",
        _ => $"{effect.Angle:0}° · {effect.Opacity * 100:0}%"
    };

    /// <summary>What a shadow silhouette of the layer includes: the strokes that widen its outline.</summary>
    internal static LayerEffect[]? ForSilhouette(LayerEffect[]? list) =>
        Normalize(list?.Where(e => e.Enabled && e.Kind == LayerEffectKind.Stroke && e.Position != StrokePosition.Inside));
}
