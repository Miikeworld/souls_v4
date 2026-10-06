using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Procedural pixel-art kit in the hand-drawn health bar's language (IMG_0040):
/// ink outline, copper/rust rim lit from the top-left, an inner ink line, a
/// bevel, then the body — with notched corners. Frames are 9-sliced sprites
/// (each texel = 3 UI px); icons are tiny string bitmaps. Everything is
/// point-filtered and cached, so the HUD needs no imported art.
/// </summary>
public static class PixelFrame
{
    public enum Style { Panel, Slot, Banner, Key, Gold }
    public enum IconId { Heart, HeartCracked, Soul, Flask, FlaskEmpty, FlaskMana, Shard, Sigil }

    /// <summary>UI pixels per texel — matched to the health bar's ~4px art blocks.</summary>
    public const float Texel = 4f;

    private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);
    private static readonly Color SoulGreen = new Color(0.36f, 1f, 0.54f);
    private static readonly Color SoulDark = new Color(0.12f, 0.55f, 0.3f);
    private static readonly Dictionary<Style, Sprite> frames = new();
    private static readonly Dictionary<int, Sprite> spikes = new();
    private static readonly Dictionary<IconId, Sprite> icons = new();

    private static (Color body, Color hi, Color lo) Palette(Style s) => s switch
    {
        Style.Slot   => (new Color(0.05f, 0.03f, 0.06f), PersonaUi.Plum, PersonaUi.Ink),
        Style.Banner => (PersonaUi.Heart, new Color(1f, 0.38f, 0.3f), PersonaUi.Blood),
        Style.Key    => (PersonaUi.Bone, Color.white, PersonaUi.Copper),
        Style.Gold   => (PersonaUi.Copper, new Color(0.93f, 0.78f, 0.62f), PersonaUi.Rust),
        _            => (PersonaUi.Night, PersonaUi.Violet, PersonaUi.Ink),
    };

    // ---------- frames ----------

    public static Sprite Frame(Style s)
    {
        if (frames.TryGetValue(s, out var cached) && cached != null) return cached;
        const int n = 24;
        var (body, hi, lo) = Palette(s);
        var px = new Color[n * n];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var ring = Mathf.Min(Mathf.Min(x, y), Mathf.Min(n - 1 - x, n - 1 - y));
            // Texture y is bottom-up: the top edge (high y) and left edge are lit.
            var lit = y == n - 1 - ring || (x == ring && y != ring);
            Color c = ring switch
            {
                0 => PersonaUi.Ink,
                1 => lit ? PersonaUi.Copper : PersonaUi.Rust,
                2 => PersonaUi.Ink,
                3 => lit ? hi : lo,
                _ => body,
            };
            // Notched corners — the thorny silhouette of the hand-drawn frame.
            var cx = Mathf.Min(x, n - 1 - x);
            var cy = Mathf.Min(y, n - 1 - y);
            if (cx + cy < 2) c = Clear;
            else if (cx + cy == 2 && ring == 1) c = PersonaUi.Ink;
            px[y * n + x] = c;
        }
        var sp = Make(px, n, n, new Vector4(6f, 6f, 6f, 6f));
        frames[s] = sp;
        return sp;
    }

    /// <summary>Diamond end-cap (like the health frame's right tip), 12×24 texels.</summary>
    public static Sprite Spike(bool right, Style s = Style.Panel)
    {
        var key = (right ? 1 : 0) | ((int)s << 1);
        if (spikes.TryGetValue(key, out var cached) && cached != null) return cached;
        const int w = 12, h = 24;
        var (body, _, _) = Palette(s);
        var px = new Color[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var dx = right ? x : w - 1 - x;           // 0 = frame side
            var half = (w - dx) * 1f;                // narrows toward the tip
            var dy = Mathf.Abs(y - (h - 1) * 0.5f);
            var edge = half - dy;
            Color c = edge < 0f ? Clear
                    : edge < 1f ? PersonaUi.Ink
                    : edge < 2f ? (y > h / 2 ? PersonaUi.Copper : PersonaUi.Rust)
                    : edge < 3f ? PersonaUi.Ink
                    : body;
            px[y * w + x] = c;
        }
        var sp = Make(px, w, h, Vector4.zero);
        spikes[key] = sp;
        return sp;
    }

    // ---------- icons ----------

    public static Sprite Icon(IconId id)
    {
        if (icons.TryGetValue(id, out var cached) && cached != null) return cached;
        var rows = id switch
        {
            IconId.Heart => new[]
            {
                ".KK...KK.",
                "KHHK.KHHK",
                "KHBHKHHHK",
                "KHHHHHHHK",
                ".KHHHHHK.",
                "..KHHHK..",
                "...KHK...",
                "....K....",
            },
            IconId.HeartCracked => new[]
            {
                ".KK...KK.",
                "KNNK.KNNK",
                "KNNNKBNNK",
                "KNNBNNNNK",
                ".KNNBNNK.",
                "..KNBNK..",
                "...KNK...",
                "....K....",
            },
            IconId.Soul => new[]
            {
                "....K....",
                "...KGK...",
                "...KGK...",
                "..KGGgK..",
                "..KGBGK..",
                ".KGBBGgK.",
                ".KGBBBGK.",
                ".KGGBGGK.",
                "..KGGGK..",
                "...KKK...",
            },
            IconId.Flask => new[]
            {
                "..KKKK..",
                "..KCCK..",
                "...KK...",
                "..KBBK..",
                ".KBBBBK.",
                "KBHHHHBK",
                "KHHHHHHK",
                "KHBHHHHK",
                "KHHHHHHK",
                "KLHHHHLK",
                ".KLLLLK.",
                "..KKKK..",
            },
            IconId.FlaskMana => new[]
            {
                "..KKKK..",
                "..KCCK..",
                "...KK...",
                "..KBBK..",
                ".KBBBBK.",
                "KBVVVVBK",
                "KVVVVVVK",
                "KVPVVVVK",
                "KVVVVVVK",
                "KPVVVVPK",
                ".KPPPPK.",
                "..KKKK..",
            },
            IconId.FlaskEmpty => new[]
            {
                "..KKKK..",
                "..KCCK..",
                "...KK...",
                "..KBBK..",
                ".KBNNBK.",
                "KBNNNNBK",
                "KNNNNNNK",
                "KNBNNNNK",
                "KNNNNNNK",
                "KNNNNNNK",
                ".KNNNNK.",
                "..KKKK..",
            },
            IconId.Sigil => new[] // lock-on reticle: ring + pip, no arrowhead
            {
                "...KKK...",
                "..K...K..",
                ".K..K..K.",
                "K..KHK..K",
                "K..KHK..K",
                ".K..K..K.",
                "..K...K..",
                "...KKK...",
            },
            _ => new[] { "HH", "HH" },
        };
        var h = rows.Length;
        var w = rows[0].Length;
        var px = new Color[w * h];
        for (var r = 0; r < h; r++)
        for (var x = 0; x < w; x++)
            px[(h - 1 - r) * w + x] = Char(rows[r][x]);
        var sp = Make(px, w, h, Vector4.zero);
        icons[id] = sp;
        return sp;
    }

    private static Color Char(char c) => c switch
    {
        'K' => PersonaUi.Ink,
        'H' => PersonaUi.Heart,
        'L' => PersonaUi.Blood,
        'B' => PersonaUi.Bone,
        'C' => PersonaUi.Copper,
        'R' => PersonaUi.Rust,
        'P' => PersonaUi.Plum,
        'N' => PersonaUi.Night,
        'V' => PersonaUi.Violet,
        'G' => SoulGreen,
        'g' => SoulDark,
        _ => Clear,
    };

    internal static Sprite Make(Color[] px, int w, int h, Vector4 border)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "PixelFrame",
        };
        tex.SetPixels(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, border);
    }

    // ---------- builders ----------

    /// <summary>Stretched, 9-sliced pixel frame filling <paramref name="parent"/>.</summary>
    public static Image Build(Transform parent, string name, Style s)
    {
        var img = PersonaUi.Stretch(parent, name).gameObject.AddComponent<Image>();
        img.sprite = Frame(s);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 1f / Texel;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>Icon at native texel scale (×<see cref="Texel"/>) centred on an anchor.</summary>
    public static Image IconImage(Transform parent, string name, IconId id, Vector2 anchor, Vector2 pos, float scale = Texel)
    {
        var sp = Icon(id);
        var rt = PersonaUi.Box(parent, name, anchor, pos, new Vector2(sp.rect.width, sp.rect.height) * scale);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sp;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>End-cap spike glued to the left or right edge of <paramref name="parent"/>.</summary>
    public static Image SpikeImage(Transform parent, bool right, Style s = Style.Panel)
    {
        var sp = Spike(right, s);
        var rt = PersonaUi.Rect(parent, right ? "SpikeR" : "SpikeL",
            new Vector2(right ? 1f : 0f, 0f), new Vector2(right ? 1f : 0f, 1f),
            new Vector2(right ? -Texel : -sp.rect.width * Texel + Texel, 0f),
            new Vector2(right ? sp.rect.width * Texel - Texel : Texel, 0f));
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sp;
        img.raycastTarget = false;
        return img;
    }
}
