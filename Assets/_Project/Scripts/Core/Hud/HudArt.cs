using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// In-game HUD art kit in the hand-drawn health frame's language (IMG_0040),
/// at the SAME block size (4 UI px per art pixel): black outline, a dark
/// plum/violet mottled mass, a broken brown wood highlight, an inner ink line,
/// lumpy silhouettes with a few small thorns, the curl tendril, round wood
/// knots (no diamonds) and a round medallion like the frame's heart socket.
///
/// Everything is painted procedurally (a tiny painter + auto ink outline), then
/// cached as point-filtered sprites via <see cref="PixelFrame.Make"/>. Menus
/// keep <see cref="PixelFrame"/>; this kit is the in-game HUD only.
/// </summary>
public static class HudArt
{
    public enum Kind { Panel, Toast, Socket, Plate, Key, Boss, Thin }

    /// <summary>UI pixels per art pixel — matched to the health frame on screen.</summary>
    public const float T = 4f;

    // ---------- palette (PersonaUi + a few material tones) ----------

    public static readonly Color ManaBlue = new Color(0.545f, 0.208f, 0.910f); // Core Energy violet #8B35E8
    public static readonly Color ManaCyan = new Color(0.718f, 0.424f, 1f);     // Core highlight #B76CFF
    public static readonly Color EmberOrange = new Color(0.95f, 0.42f, 0.14f);
    public static readonly Color EmberAmber = new Color(1f, 0.7f, 0.3f);
    public static readonly Color Amber = new Color(1f, 0.66f, 0.36f, 0.55f); // bar damage ghost
    public static readonly Color WoodHi = new Color(0.62f, 0.42f, 0.33f);
    public static readonly Color Steel = new Color(0.66f, 0.68f, 0.76f);
    public static readonly Color SteelDark = new Color(0.36f, 0.37f, 0.45f);
    public static readonly Color Gold = new Color(0.98f, 0.8f, 0.4f);
    public static readonly Color Parchment = new Color(0.88f, 0.8f, 0.68f);
    public static readonly Color Toxic = new Color(0.45f, 0.95f, 0.4f); // poison green — numerals + skull icon
    private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);
    private static readonly Color Well = new Color(0.05f, 0.03f, 0.06f);
    private static readonly Color Bark = new Color(0.25f, 0.14f, 0.09f); // dark bark brown
    private static readonly Color Groove = new Color(0.173f, 0.094f, 0.059f); // bark stripe groove
    private static readonly Color Glass = new Color(0.737f, 0.753f, 0.776f);  // gourd headspace
    private static readonly Color GlassD = new Color(0.533f, 0.549f, 0.580f); // headspace, low

    private static readonly Dictionary<string, Sprite> cache = new();

    // ---------- painter ----------

    /// <summary>Tiny pixel painter: paint shapes in colour, then Outline()
    /// wraps them in ink — detailed sprites stay short to author.</summary>
    private sealed class Px
    {
        public readonly int w, h;
        public readonly Color[] c;
        public Px(int w, int h) { this.w = w; this.h = h; c = new Color[w * h]; }
        public bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
        public void Set(int x, int y, Color col) { if (In(x, y)) c[y * w + x] = col; }
        public Color Get(int x, int y) => In(x, y) ? c[y * w + x] : Clear;
        public bool Solid(int x, int y) => Get(x, y).a > 0f;

        /// <summary>Ink on every clear texel 4-touching paint. Rows below
        /// <paramref name="skipBelow"/> and columns left of <paramref name="skipLeft"/>
        /// stay open (the parts that sink into a frame rim).
        /// <paramref name="diagonal"/> also inks 8-touching texels — thin
        /// diagonal strokes (vines/veins) otherwise leak un-outlined gaps.</summary>
        public void Outline(int skipBelow = 0, int skipLeft = 0, bool diagonal = false)
        {
            var add = new List<int>();
            for (var y = skipBelow; y < h; y++)
            for (var x = skipLeft; x < w; x++)
            {
                if (Solid(x, y)) continue;
                if (Solid(x - 1, y) || Solid(x + 1, y) || Solid(x, y - 1) || Solid(x, y + 1)
                    || (diagonal && (Solid(x - 1, y - 1) || Solid(x + 1, y - 1)
                                  || Solid(x - 1, y + 1) || Solid(x + 1, y + 1))))
                    add.Add(y * w + x);
            }
            foreach (var i in add) c[i] = PersonaUi.Ink;
        }

        /// <summary>Stamp top-down string rows with the kit palette at (ox, oy) = bottom-left.</summary>
        public void Blit(string[] rows, int ox, int oy)
        {
            for (var r = 0; r < rows.Length; r++)
            for (var x = 0; x < rows[r].Length; x++)
            {
                var col = Char(rows[r][x], x + ox, rows.Length - 1 - r + oy);
                if (col.a > 0f) Set(x + ox, rows.Length - 1 - r + oy, col);
            }
        }
    }

    private static int Hash(int x, int y) => ((x * 73856093) ^ (y * 19349663)) & 0x7fffffff;

    /// <summary>The frame mass: Night with a violet mottle.</summary>
    private static Color Mass(int x, int y) => Hash(x, y) % 3 == 0 ? PersonaUi.Violet : PersonaUi.Night;

    private static Color Char(char ch, int x, int y) => ch switch
    {
        'K' => PersonaUi.Ink,
        'M' => Mass(x, y),
        'N' => PersonaUi.Night,
        'V' => PersonaUi.Violet,
        'P' => PersonaUi.Plum,
        'C' => PersonaUi.Copper,
        'R' => PersonaUi.Rust,
        'W' => WoodHi,
        'H' => PersonaUi.Heart,
        'L' => PersonaUi.Blood,
        'B' => PersonaUi.Bone,
        'D' => Parchment,
        'O' => EmberOrange,
        'Y' => EmberAmber,
        'G' => Gold,
        'S' => Steel,
        's' => SteelDark,
        'b' => ManaBlue,
        'c' => ManaCyan,
        'g' => Toxic,
        _ => Clear,
    };

    public static bool KnownChar(char ch) => ch == '.' || Char(ch, 0, 0).a > 0f;

    private static Sprite Store(string key, Px p, Vector4 border = default)
    {
        var sp = PixelFrame.Make(p.c, p.w, p.h, border);
        cache[key] = sp;
        sources[sp] = p; // textures are non-readable — keep the buffer for flipped variants
        return sp;
    }

    private static bool Cached(string key, out Sprite sp) => cache.TryGetValue(key, out sp) && sp != null;

    // ---------- frames ----------

    /// <summary>Slice border in texels. Bevelled kinds (Socket, Key) carry their
    /// 1-texel bevel INSIDE the border — the stretched centre must be uniform,
    /// or a bevel texel smears into a bar across the whole body.</summary>
    public static int BorderOf(Kind k) => k switch
    {
        Kind.Thin => 3,
        Kind.Socket => 6,
        Kind.Key => 6,
        _ => 5,
    };

    /// <summary>24×24 9-slice. Rings: ink / mass / mass / broken wood line /
    /// ink / body (Thin: ink / wood / ink / body). Mottle and wood breaks stretch
    /// along the slice into streaks — the hand-drawn frame's texture.</summary>
    public static Sprite Frame(Kind k)
    {
        var key = "F" + k;
        if (Cached(key, out var hit)) return hit;
        const int n = 24;
        var p = new Px(n, n);
        var border = BorderOf(k);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var ring = Mathf.Min(Mathf.Min(x, y), Mathf.Min(n - 1 - x, n - 1 - y));
            var lit = y >= n - 1 - ring || x <= ring; // top + left edges (texture y is bottom-up)
            Color col;
            if (k == Kind.Thin)
                col = ring == 0 ? PersonaUi.Ink : ring == 1 ? WoodLine(x, y, lit) : ring == 2 ? PersonaUi.Ink : Body(k, ring, lit);
            else
                col = ring == 0 ? PersonaUi.Ink
                    : ring <= 2 ? (lit && Hash(x, y) % 2 == 0 ? PersonaUi.Violet : Mass(x, y))
                    : ring == 3 ? WoodLine(x, y, lit)
                    : ring == 4 ? PersonaUi.Ink
                    : Body(k, ring, lit);
            var cx = Mathf.Min(x, n - 1 - x);
            var cy = Mathf.Min(y, n - 1 - y);
            if (cx + cy < 3) col = Clear;
            else if (cx + cy == 3) col = PersonaUi.Ink;
            p.Set(x, y, col);
        }
        return Store(key, p, new Vector4(border, border, border, border));
    }

    private static Color WoodLine(int x, int y, bool lit)
    {
        var hsh = Hash(x, y);
        if (hsh % 6 == 0) return PersonaUi.Plum; // broken highlight
        if (!lit) return PersonaUi.Rust;
        return hsh % 7 == 0 ? WoodHi : PersonaUi.Copper;
    }

    private static Color Body(Kind k, int ring, bool lit)
    {
        var bevel = ring == 5 && BorderOf(k) == 6; // last border ring of bevelled kinds
        return k switch
        {
            Kind.Socket => bevel && lit ? PersonaUi.Violet : Well,
            Kind.Key => bevel && !lit ? PersonaUi.Copper : PersonaUi.Bone,
            Kind.Toast => PersonaUi.Blood,
            Kind.Thin => PersonaUi.Ink,
            _ => PersonaUi.Night,
        };
    }

    /// <summary>Fixed 21×21 skill sockets — one unique frame per slot so each
    /// reads differently (Warden / Duelist / Thorn). Painted whole, NOT
    /// 9-sliced: the ornament never smears, the well stays opaque so the icon
    /// above can never show a decoration through its transparent texels, and
    /// all baked decor lives in the outer 2-texel strips the icon can't reach.
    /// <paramref name="slot"/> = 0, 1 or 2.</summary>
    public static Sprite SocketFrame(int slot)
    {
        var key = "SockF" + slot;
        if (Cached(key, out var hit)) return hit;
        const int n = 21;
        var p = new Px(n, n);
        var chamfer = slot == 1 ? 7 : 3; // Duelist cuts its corners deep
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var cx = Mathf.Min(x, n - 1 - x);
            var cy = Mathf.Min(y, n - 1 - y);
            if (cx + cy < chamfer) continue;
            var ring = Mathf.Min(cx, cy);
            var lit = y >= n - 1 - ring || x <= ring;
            Color col;
            if (cx + cy == chamfer) col = PersonaUi.Ink;
            else if (ring == 0) col = PersonaUi.Ink;
            else if (slot == 1 && ring <= 1) col = WoodLine(x, y, lit); // Duelist: wood outside
            else if (ring <= 2) col = lit && Hash(x, y) % 2 == 0 ? PersonaUi.Violet : Mass(x, y);
            else if (ring == 3) col = WoodLine(x, y, lit);
            else if (ring == 4) col = PersonaUi.Ink;
            else if (slot == 2 && ring == 5) col = Mass(x, y);  // Thorn: extra rim ring
            else if (slot == 2 && ring == 6) col = PersonaUi.Ink;
            else col = ring == 5 && lit ? PersonaUi.Violet : Well;
            p.Set(x, y, col);
        }
        switch (slot)
        {
            case 0: // Warden: knot dots on all corners + a vine up the left rim
                KnotDot(p, 1, 1); KnotDot(p, 19, 1); KnotDot(p, 1, 19); KnotDot(p, 19, 19);
                p.Set(1, 1, PersonaUi.Copper); p.Set(19, 1, PersonaUi.Copper);
                p.Set(1, 19, PersonaUi.Copper); p.Set(19, 19, PersonaUi.Copper);
                for (var i = 0; i <= 7; i++) // climbing stem, wood tones
                {
                    p.Set(i % 3 == 0 ? 1 : 2, 3 + i, i % 2 == 0 ? PersonaUi.Rust : PersonaUi.Copper);
                }
                p.Set(1, 5, PersonaUi.Violet); p.Set(2, 8, PersonaUi.Violet); // leaf nubs
                p.Set(1, 10, PersonaUi.Violet); // curling tip
                p.Set(3, 1, PersonaUi.Rust); p.Set(5, 1, PersonaUi.Copper); p.Set(7, 1, PersonaUi.Rust);
                p.Set(4, 2, PersonaUi.Violet); // bottom-right vein nub
                break;
            case 1: // Duelist: deep chamfer, copper studs on top, vein crack top-right, bottom barb
                Stud(p, 7, 19); Stud(p, 12, 19);
                p.Set(19, 16, PersonaUi.Violet); p.Set(18, 16, PersonaUi.Violet);
                p.Set(19, 17, PersonaUi.Violet); p.Set(18, 17, PersonaUi.Violet);
                p.Set(19, 18, PersonaUi.Violet); p.Set(18, 18, PersonaUi.Plum);
                p.Set(19, 19, PersonaUi.Violet); p.Set(18, 19, PersonaUi.Violet);
                p.Set(10, 0, PersonaUi.Copper); p.Set(9, 1, PersonaUi.Rust);
                p.Set(10, 1, PersonaUi.Rust); p.Set(11, 1, PersonaUi.Rust);
                break;
            default: // Thorn: double inner rim + vine up the right edge, hanging barb
                for (var i = 0; i <= 8; i++)
                    p.Set(19 - (i % 2), 2 + i, i % 2 == 0 ? PersonaUi.Rust : PersonaUi.Copper);
                p.Set(18, 4, PersonaUi.Violet); p.Set(18, 7, PersonaUi.Violet);
                p.Set(19, 11, PersonaUi.Violet); p.Set(18, 10, PersonaUi.Plum); // tip curl
                p.Set(10, 1, PersonaUi.Plum); p.Set(9, 2, PersonaUi.Rust); p.Set(11, 2, PersonaUi.Rust);
                break;
        }
        p.Outline();
        return Store(key, p);
    }

    private static void KnotDot(Px p, int x, int y)
    {
        p.Set(x, y, PersonaUi.Copper); p.Set(x + 1, y, PersonaUi.Rust);
        p.Set(x, y + 1, PersonaUi.Rust); p.Set(x + 1, y + 1, PersonaUi.Plum);
    }

    private static void Stud(Px p, int x, int y)
    {
        p.Set(x, y, PersonaUi.Copper); p.Set(x + 1, y, PersonaUi.Copper);
        p.Set(x, y - 1, PersonaUi.Rust); p.Set(x + 1, y - 1, PersonaUi.Rust);
        p.Set(x, y + 1, PersonaUi.Ink); p.Set(x + 1, y + 1, PersonaUi.Ink);
    }

    /// <summary>Selected-socket glow: 12×12 9-slice, a 4-texel stepped ember
    /// falloff around a clear centre. Drawn 4 texels outside the socket frame.</summary>
    public static Sprite GlowRim()
    {
        if (Cached("Glow", out var hit)) return hit;
        const int n = 12;
        var p = new Px(n, n);
        var alpha = new[] { 0.12f, 0.26f, 0.45f, 0.7f };
        var cols = new[] { EmberAmber, EmberOrange, EmberOrange, PersonaUi.Heart };
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var ring = Mathf.Min(Mathf.Min(x, y), Mathf.Min(n - 1 - x, n - 1 - y));
            if (ring > 3) continue;
            var cx = Mathf.Min(x, n - 1 - x);
            var cy = Mathf.Min(y, n - 1 - y);
            if (cx + cy < 3) continue;
            var col = cols[ring];
            col.a = alpha[ring];
            p.Set(x, y, col);
        }
        return Store("Glow", p, new Vector4(4, 4, 4, 4));
    }

    /// <summary>Tapered focus-line streak for the sprint overlay — a needle
    /// that thickens toward its root (x=0) and sharpens to a point. Painted
    /// plain white so the Image colour carries the palette tint.</summary>
    public static Sprite Streak()
    {
        if (Cached("Streak", out var hit)) return hit;
        const int w = 40, h = 7;
        var p = new Px(w, h);
        for (var x = 0; x < w; x++)
        {
            var half = (w - 1 - x) * (h - 1) * 0.5f / (w - 1);
            for (var y = 0; y < h; y++)
                if (Mathf.Abs(y - (h - 1) * 0.5f) <= half + 0.35f)
                    p.Set(x, y, Color.white);
        }
        return Store("Streak", p);
    }

    // ---------- silhouette pieces (sink 3 texels into a frame rim) ----------

    public const int Sink = 3;

    /// <summary>Half-ellipse bump of frame mass with a brown top highlight —
    /// the health frame's lumps. <paramref name="width"/> = 8/12/16.</summary>
    public static Sprite Lump(int width)
    {
        var key = "Lump" + width;
        if (Cached(key, out var hit)) return hit;
        const int sh = 6; // shape rows; sticks out sh - Sink (+ outline)
        var p = new Px(width + 2, sh + 1);
        bool Inside(int x, int y)
        {
            var dx = (x + 0.5f - (width + 2) * 0.5f) / (width * 0.5f);
            var dy = (y + 0.5f) / sh;
            return y >= 0 && y < sh && dx * dx + dy * dy <= 1f;
        }
        for (var y = 0; y < sh; y++)
        for (var x = 1; x <= width; x++)
        {
            if (!Inside(x, y)) continue;
            var top = !Inside(x, y + 1) && y >= Sink;
            p.Set(x, y, top ? (x < (width + 2) / 2 ? PersonaUi.Copper : PersonaUi.Rust) : Mass(x, y));
        }
        p.Outline(skipBelow: Sink);
        return Store(key, p);
    }

    /// <summary>Small curved thorn spur (mass, brown lit edge, 1-texel tip).</summary>
    public static Sprite Thorn()
    {
        if (Cached("Thorn", out var hit)) return hit;
        var p = new Px(7, 9);
        // (x from, x to) per row, bottom-up; rows < Sink sink into the rim.
        var rows = new[] { (2, 4), (2, 4), (2, 4), (2, 4), (3, 4), (3, 4), (4, 4), (5, 5) };
        for (var y = 0; y < rows.Length; y++)
            for (var x = rows[y].Item1; x <= rows[y].Item2; x++)
                p.Set(x, y, x == rows[y].Item1 && y >= Sink ? PersonaUi.Copper : Mass(x, y));
        p.Outline(skipBelow: Sink);
        return Store("Thorn", p);
    }

    public static readonly string[] CurlRows =
    {
        "...MMMM...",
        "..MCCCCM..",
        ".MC....CM.",
        ".MC.MM..M.",
        ".MC.MC..M.",
        "..MM.MCCM.",
        "......MM..",
        ".....MMM..",
        "....MMMMM.",
        "...MMMMMM.",
        "...MMMMMM.",
    };

    /// <summary>The curl tendril from the top of the health frame.</summary>
    public static Sprite Curl()
    {
        if (Cached("Curl", out var hit)) return hit;
        var p = new Px(12, CurlRows.Length + 1);
        p.Blit(CurlRows, 1, 0);
        p.Outline(skipBelow: Sink);
        return Store("Curl", p);
    }

    /// <summary>Vine tendril + vein branches that creep over a frame's bottom
    /// edge: a waving violet stem that climbs left, two vein offshoots, leaf
    /// nubs and a tip that curls inward like the health frame's tendril.
    /// Bottom rows stay un-outlined so the vine reads as growing out of the
    /// rim it overlaps. <paramref name="right"/> mirrors it for the other side.</summary>
    public static Sprite Vine(bool right = false)
    {
        var key = right ? "VineR" : "VineL";
        if (Cached(key, out var hit)) return hit;
        var p = new Px(12, 16);
        // Stem: enters at the bottom-right, winds up-left — wood tones like
        // the frame's tendrils (violet on dark reads as nothing). Two texels
        // thick near the root, tapering to one as it climbs.
        for (var i = 0; i <= 80; i++)
        {
            var t = i / 80f;
            var x = 10.6f - 8.6f * t - Mathf.Sin(t * Mathf.PI) * 1.4f;
            var y = 0.6f + 12.8f * t;
            var xi = Mathf.RoundToInt(x);
            var yi = Mathf.RoundToInt(y);
            p.Set(xi, yi, PersonaUi.Rust);
            if (t < 0.45f) p.Set(xi + 1, yi, PersonaUi.Rust);
            if (t < 0.18f) p.Set(xi, yi - 1, PersonaUi.Rust);
        }
        // Curl tip: a 3/4 spiral at the top end, curling inward (clockwise).
        for (var i = 0; i <= 44; i++)
        {
            var u = i / 44f;
            var a = 2.79f - u * 4.7f;
            var r = 2.24f * (1f - u * 0.6f);
            p.Set(Mathf.RoundToInt(4.2f + Mathf.Cos(a) * r),
                  Mathf.RoundToInt(12.6f + Mathf.Sin(a) * r * 0.9f), PersonaUi.Copper);
        }
        // Vein offshoots — jagged violet splits off the stem.
        Line(p, 5.4f, 6.4f, 8.6f, 8.2f);
        Line(p, 3.4f, 9.6f, 6.6f, 11.6f);
        Line(p, 7.0f, 4.8f, 9.4f, 4.0f);
        // Leaf nubs — little plum/violet diamonds off the stem.
        Leaf(p, 7, 4); Leaf(p, 5, 11); Leaf(p, 4, 8);
        p.Outline(skipBelow: 2, diagonal: true);
        if (right) Mirror(p);
        return Store(key, p);
    }

    /// <summary>Vine hanging downward from a top edge — the vertical flip of
    /// <see cref="Vine"/>: the open root end merges into the rim above.</summary>
    public static Sprite VineDown(bool right = false)
    {
        var key = "VineD" + (right ? 'R' : 'L');
        if (Cached(key, out var hit)) return hit;
        var p = PxOf(Vine(right));
        FlipY(p);
        return Store(key, p);
    }

    /// <summary>Short jagged vein crack — a meandering violet split with
    /// branches and leaf nubs, for frame edges and banners.
    /// Descends top-left → bottom-right.</summary>
    public static Sprite Vein(bool right = false)
    {
        var key = right ? "VeinR" : "VeinL";
        if (Cached(key, out var hit)) return hit;
        var p = new Px(10, 12);
        for (var i = 0; i <= 50; i++)
        {
            var t = i / 50f;
            var x = 2.0f + 5.4f * t + Mathf.Sin(t * 7f) * 0.9f;
            var y = 11.0f - 9.4f * t;
            var xi = Mathf.RoundToInt(x);
            var yi = Mathf.RoundToInt(y);
            p.Set(xi, yi, PersonaUi.Violet);
            if (t < 0.32f) p.Set(xi - 1, yi, PersonaUi.Violet); // thicker at the entry edge
        }
        Line(p, 4.6f, 6.8f, 7.8f, 8.4f);  // upper split
        Line(p, 5.4f, 5.2f, 8.4f, 3.8f);  // lower split
        Line(p, 2.4f, 9.8f, 4.6f, 11.4f); // entry fork
        p.Set(8, 4, PersonaUi.Plum); p.Set(8, 8, PersonaUi.Plum); // tip nubs
        p.Outline(diagonal: true);
        if (right) Mirror(p);
        return Store(key, p);
    }

    private static void Line(Px p, float ax, float ay, float bx, float by)
    {
        var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) * 3f));
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            p.Set(Mathf.RoundToInt(ax + (bx - ax) * t), Mathf.RoundToInt(ay + (by - ay) * t), PersonaUi.Violet);
        }
    }

    private static void Leaf(Px p, int x, int y)
    {
        p.Set(x, y, PersonaUi.Violet);
        p.Set(x + 1, y, PersonaUi.Violet);
        p.Set(x, y + 1, PersonaUi.Plum);
        p.Set(x + 1, y + 1, PersonaUi.Violet);
    }

    /// <summary>Round wood-knot end cap with concentric end grain — replaces
    /// every diamond. Right-facing; the left column attaches to the frame.</summary>
    public static Sprite Knot(bool right)
    {
        var key = right ? "KnotR" : "KnotL";
        if (Cached(key, out var hit)) return hit;
        const int w = 8, h = 13;
        var p = new Px(w, h);
        const float cy = h * 0.5f;
        for (var y = 1; y < h - 1; y++)
        for (var x = 0; x < w - 1; x++)
        {
            var dx = (x + 0.5f) / (w - 1.5f);
            var dy = (y + 0.5f - cy) / (cy - 1f);
            if (dx * dx + dy * dy > 1f) continue;
            var ring = Mathf.Sqrt(((x + 0.5f - 1.2f) * (x + 0.5f - 1.2f)) + (y + 0.5f - cy) * (y + 0.5f - cy) * 0.55f);
            Color col = ring < 1.3f ? PersonaUi.Plum
                      : ((int)(ring / 1.4f)) % 2 == 0 ? PersonaUi.Rust : PersonaUi.Copper;
            if (y + 0.5f > cy + 3f && x > 1 && Hash(x, y) % 3 == 0) col = WoodHi; // lit top
            p.Set(x, y, col);
        }
        p.Outline(skipLeft: 1);
        if (!right) Mirror(p);
        return Store(key, p);
    }

    private static void Mirror(Px p)
    {
        for (var y = 0; y < p.h; y++)
        for (var x = 0; x < p.w / 2; x++)
        {
            var a = p.Get(x, y);
            p.Set(x, y, p.Get(p.w - 1 - x, y));
            p.Set(p.w - 1 - x, y, a);
        }
    }

    private static void FlipY(Px p)
    {
        for (var y = 0; y < p.h / 2; y++)
        for (var x = 0; x < p.w; x++)
        {
            var a = p.Get(x, y);
            p.Set(x, y, p.Get(x, p.h - 1 - y));
            p.Set(x, p.h - 1 - y, a);
        }
    }

    private static Sprite Flipped(string key, System.Func<Px> make)
    {
        if (Cached(key, out var hit)) return hit;
        var p = make();
        FlipY(p);
        return Store(key, p);
    }

    /// <summary>Round ring socket like the health frame's heart: ink / mass with
    /// a brown top-left highlight / ink, hollow Night centre for an icon.</summary>
    public static Sprite Medallion()
    {
        if (Cached("Medal", out var hit)) return hit;
        const int n = 22; // inner well Ø ≈ 14 texels — fits the 12×14 ember
        const float c0 = n * 0.5f;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = x + 0.5f - c0;
            var dy = y + 0.5f - c0;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d > 10.6f) continue;
            var lit = (-dx + dy) > 1.5f;
            Color col = d > 9.7f ? PersonaUi.Ink
                      : d > 8.9f ? (lit ? (Hash(x, y) % 5 == 0 ? PersonaUi.Plum : PersonaUi.Copper) : PersonaUi.Rust)
                      : d > 7.9f ? Mass(x, y)
                      : d > 7.1f ? PersonaUi.Ink
                      : PersonaUi.Night;
            p.Set(x, y, col);
        }
        return Store("Medal", p);
    }

    // ---------- small bitmaps ----------

    public static readonly string[] HeartRows =
    {
        ".HH.HH.",
        "HBHHHHH",
        "HHHHHHL",
        ".HHHHL.",
        "..HHL..",
        "...L...",
    };

    public static readonly string[] EyeRows =
    {
        ".bbb.",
        "bbccb",
        "bcBcb",
        "bccbb",
        ".bbb.",
    };

    public static readonly string[] SwirlRows =
    {
        "..ccccc..",
        ".c.....c.",
        "c..ccc..c",
        "c.c...c.c",
        "c.c.c.c.c",
        "c.c..cc.c",
        "c..c....c",
        ".c..cccc.",
        "..c......",
    };

    /// <summary>Crimson ember — the souls icon.</summary>
    public static readonly string[] EmberRows =
    {
        "....H.....",
        "....HH....",
        "...HOH..H.",
        "...HOH.HH.",
        "..HOOHHOH.",
        "..HOYOOOH.",
        ".HOYYYOOH.",
        ".HOYBYYOH.",
        ".HOYYYYOH.",
        ".HOOYYOOH.",
        "..LHOOHL..",
        "...LLLL...",
    };

    /// <summary>Heart gem on a twine loop — hangs above the selected skill.</summary>
    public static readonly string[] GemRows =
    {
        "...R...",
        "..R.R..",
        "...R...",
        ".HH.HH.",
        "HBHHHHH",
        "HHHHHHL",
        ".HHHHL.",
        "..HHL..",
        "...L...",
    };

    public static readonly string[][] DigitRows =
    {
        new[] { "DDD", "D.D", "D.D", "D.D", "DDD" },
        new[] { ".D.", "DD.", ".D.", ".D.", "DDD" },
        new[] { "DDD", "..D", "DDD", "D..", "DDD" },
        new[] { "DDD", "..D", ".DD", "..D", "DDD" },
        new[] { "D.D", "D.D", "DDD", "..D", "..D" },
        new[] { "DDD", "D..", "DDD", "..D", "DDD" },
        new[] { "DDD", "D..", "DDD", "D.D", "DDD" },
        new[] { "DDD", "..D", ".D.", ".D.", ".D." },
        new[] { "DDD", "D.D", "DDD", "D.D", "DDD" },
        new[] { "DDD", "D.D", "DDD", "..D", "DDD" },
    };

    private static Sprite Outlined(string key, string[] rows, bool outline = true)
    {
        if (Cached(key, out var hit)) return hit;
        var m = outline ? 1 : 0;
        var p = new Px(rows[0].Length + 2 * m, rows.Length + 2 * m);
        p.Blit(rows, m, m);
        if (outline) p.Outline();
        return Store(key, p);
    }

    public static Sprite Heart() => Outlined("Heart", HeartRows);
    public static Sprite Eye() => Outlined("Eye", EyeRows);
    public static Sprite Swirl() => Outlined("Swirl", SwirlRows, outline: false);
    public static Sprite Ember() => Outlined("Ember", EmberRows);
    public static Sprite Gem() => Outlined("Gem", GemRows);

    /// <summary>Carved digit 0–9: parchment fill, rust groove down-right.
    /// Outlined (chunky, ink-bordered) is 6×8; carved is 4×6 and reads thin.</summary>
    public static Sprite Digit(int n, bool outlined = true)
    {
        n = Mathf.Clamp(n, 0, 9);
        var key = "Dig" + n + (outlined ? "" : "c");
        if (Cached(key, out var hit)) return hit;
        var p = outlined ? new Px(6, 8) : new Px(4, 6);
        p.Blit(DigitRows[n], outlined ? 1 : 0, outlined ? 2 : 1);
        if (outlined) p.Outline();
        // Groove shadow: rust wherever a solid texel's down-right neighbour is open.
        var shade = new List<(int, int)>();
        for (var y = 0; y < p.h; y++)
        for (var x = 0; x < p.w; x++)
            if (p.Solid(x, y) && p.Get(x + 1, y - 1).a == 0f) shade.Add((x + 1, y - 1));
        foreach (var (x, y) in shade) p.Set(x, y, PersonaUi.Rust);
        return Store(key, p);
    }

    // ---------- skill icons (14×14 painted, outlined to 16×16) ----------

    private static readonly Dictionary<string, string[]> SkillRows = new()
    {
        ["ArcBlade"] = new[]
        {
            "......SSSS....",
            "....SSBBBsS...",
            "...SBs....sS..",
            "..SBs......s..",
            ".SBs..........",
            ".SBs..........",
            "SBs.......Y...",
            "SBs......YO...",
            "SBs....YYO....",
            ".SBs.YYO......",
            ".SBsYO........",
            "..SYO.........",
            "..YO..........",
            ".YO...........",
        },
        ["RadiantRush"] = new[]
        {
            "...........GG.",
            "..........GBBG",
            ".........GBBG.",
            "........YGGG..",
            ".......YYGG...",
            "......YOY.....",
            ".....YOY......",
            "....OOY.......",
            "...OO.Y.......",
            "..OO..........",
            ".O..O.........",
            "O..O..........",
            "..O...........",
            "O.............",
        },
        ["StarfallSlam"] = new[]
        {
            "..O...........",
            "...O..O.......",
            "....OO.O......",
            ".....OYO......",
            "......YYO.....",
            "......YGYO....",
            ".......GBGO...",
            ".......GGGH...",
            "........HHL...",
            "..............",
            "..s..s..s..s..",
            "RRRRRRPRRRRRRR",
            "RRPRRRRRRPRRRR",
            ".RRRRR.RRRRRR.",
        },
        ["SolarSigil"] = new[]
        {
            "......GG......",
            "..G........G..",
            "....YYYYYY....",
            "...YGGGGGGY...",
            "..YGGYYYYGGY..",
            "..YGYOOOOYGY..",
            "G.YGYOBBOYGY.G",
            "G.YGYOBBOYGY.G",
            "..YGYOOOOYGY..",
            "..YGGYYYYGGY..",
            "...YGGGGGGY...",
            "....YYYYYY....",
            "..G........G..",
            "......GG......",
        },
        ["PhantomDaggers"] = new[]
        {
            "......B.......",
            "..B...S...B...",
            "..S...S...S...",
            "..S...S...S...",
            "..S...S...S...",
            "..s...s...s...",
            ".CCC.CCC.CCC..",
            "..R...R...R...",
            "..R...R...R...",
            "..V...V...V...",
            ".V.V.V.V.V.V..",
            "V...V...V...V.",
            ".V.....V.....V",
            "..............",
        },
        ["ShadowstepCut"] = new[]
        {
            "............B.",
            "...VV......S..",
            "..VPPV....S...",
            ".VP..PV..S....",
            ".VP...PVS.....",
            "..VP...SV.....",
            "...VVPS.PV....",
            ".....S.VPPV...",
            "....S..VP.PV..",
            "...S....VP.V..",
            "..S......VVV..",
            ".s............",
            "..VV...VV.....",
            ".V..V.V..V....",
        },
        ["CycloneKick"] = new[]
        {
            "....ccccc.....",
            "..cc.....cc...",
            ".c...SSS...c..",
            "c...S...S...c.",
            "c..S..cc.S..c.",
            "c..S.c..c.S.c.",
            "c..S.c.Bc.S.c.",
            ".c.S..cc..S.c.",
            ".c..S....S..c.",
            "..c..SSSS..c..",
            "...cc....cc...",
            ".....cccc.....",
            ".s..........s.",
            "..ss......ss..",
        },
        ["DragonPalm"] = new[]
        {
            "..O...Y....O..",
            "...O..Y...O...",
            "....DD.DD.....",
            "..D.DD.DD.D...",
            "..DDDD.DDDD...",
            "..DDDDDDDDD...",
            "DD.DDDDDDDD...",
            "DDDDDDDDDDD...",
            ".DDDDDDDDDD...",
            "..DDDDDDDDD...",
            "...DDDDDDD....",
            "...DDDDDDD....",
            "..HOYYYYOH....",
            ".HOO....OOH...",
        },
        ["ToxicSkull"] = new[]
        {
            ".g..........g.",
            "..g..g....g...",
            "....BBBBBB....",
            "..BBBBBBBBBB..",
            "..BBKKBBKKBB..",
            "..BBKKBBKKBB..",
            "..BBBBKKBBBB..",
            "...BBBBBBBB...",
            "...BB.BB.BB...",
            "...BBBBBBBB...",
            "....B.BB.B....",
            "g..........g..",
            "....g....g....",
            "..g........g..",
        },
        ["GraveRend"] = new[] // fat spectral blade slammed tip-down into cracked ground
        {
            ".....HH.......",
            "....HHHH......",
            "...SSSSSSS....",
            "....SGYGS.....",
            "....SGYGS.....",
            "....SGYGS.....",
            "....SGYGS.....",
            "....SGYGS.....",
            "....SGS.......",
            ".....S........",
            "..GGG.GGG.....",
            "RRRPRRRRPRRRRR",
            "RRPRRRPRRRRPRR",
            ".s..s..s..s...",
        },
        ["IronGale"] = new[] // steel slash streaks over a cyan gale ring
        {
            "..............",
            "...SSS...SSS..",
            "..SSS...SSS...",
            ".SSS...SSS....",
            "SSS...........",
            "..............",
            ".....cccc.....",
            "...cc...cc....",
            "..cc.....cc...",
            "..cc.SSG.cc...",
            "..cc.....cc...",
            "...cc...cc....",
            ".....cccc.....",
            "..............",
        },
        ["Mooncleaver"] = new[] // gold crescent over a falling spectral blade
        {
            "...GGG........",
            "..GG.GG.......",
            "..G...GG......",
            "..GG.GG.......",
            "...GGG........",
            "..............",
            ".........SS...",
            "........SGS...",
            "........SGS...",
            "........SGS...",
            "........SGS...",
            ".........HH...",
            "..........H...",
            "..............",
        },
    };

    public static IEnumerable<string> SkillIds => SkillRows.Keys;

    /// <summary>True when <paramref name="id"/> or the art's name (spaces
    /// stripped) names a drawn skill icon.</summary>
    public static bool TryResolveSkill(string id, string fullName, out string key)
    {
        if (!string.IsNullOrEmpty(id) && SkillRows.ContainsKey(id)) { key = id; return true; }
        var flat = (fullName ?? "").Replace(" ", "");
        foreach (var k in SkillRows.Keys)
            if (string.Equals(k, flat, System.StringComparison.OrdinalIgnoreCase)) { key = k; return true; }
        key = null;
        return false;
    }

    /// <summary>Skill icon by id, falling back to the art's name, then the ember.</summary>
    public static Sprite SkillIcon(string id, string fullName)
        => TryResolveSkill(id, fullName, out var key) ? Outlined("Skill" + key, SkillRows[key]) : Ember();

    // ---------- item + weapon icons (14×14 painted, outlined to 16×16) ----------

    private static readonly Dictionary<string, string[]> ItemRows = new()
    {
        ["Vial"] = new[] // corked emberwine vial, ember glint
        {
            ".....RCCR.....",
            ".....RRRR.....",
            "......SS......",
            "......sS......",
            ".....SssS.....",
            "....SsssBS....",
            "...SHHHHHHS...",
            "..SHYOHHHHHS..",
            "..SHOHHHHHHS..",
            "..SHHHHHHHLS..",
            "..SHHHHHHLLS..",
            "..SLHHHHHLLS..",
            "...SLLLLLLS...",
            "....SSSSSS....",
        },
        ["Starwater"] = new[] // round blue flask with stars caught in it
        {
            ".....RCCR.....",
            ".....RRRR.....",
            "......SS......",
            "......SS......",
            ".....SssS.....",
            "...SSsssBSS...",
            "..SbbbbbbbbS..",
            ".SbbcbbbbGbbS.",
            ".SbcbbbbGBGbS.",
            ".SbbbbbbbGbbS.",
            ".SbbbbGbbbbbS.",
            "..SbbbbbbbbS..",
            "...SSbbbbSS...",
            ".....SSSS.....",
        },
        ["Feather"] = new[] // green-vaned feather riding wind streaks
        {
            "...........gg.",
            "..........gBg.",
            ".........gBgg.",
            "........gBgg..",
            ".c.....gBgg...",
            "..cc..gBgg....",
            ".....gBgg..cc.",
            "....gBgg......",
            "...gBgg...ccc.",
            "..gBgg........",
            "..Bgg..cc.....",
            ".RD...........",
            "R.............",
            "..............",
        },
        ["Bomb"] = new[] // wax-capped black bomb, lit fuse
        {
            "..........Y...",
            "........YOY...",
            ".........O....",
            "........R.....",
            ".......R......",
            "....DDDCD.....",
            "...NDVDDNN....",
            "..NVBVNDNNN...",
            "..NVVNNNNNN...",
            "..NNNNNNNNN...",
            "..NNNNNNNPN...",
            "...NNNNNPN....",
            "....NNNNN.....",
            "..............",
        },
        ["Cask"] = new[] // banded barrel leaking crimson soul-wisps
        {
            ".....H..H.....",
            "....HOH.HO....",
            ".....YOOY.....",
            "...RRRRRRRR...",
            "..RCWCCCCCCR..",
            "..sSSSSSSSSs..",
            "..RCCRCCRCCR..",
            ".RCWCRCCRCCCR.",
            ".RCWCRCCRCCCR.",
            "..RCCRCCRCCR..",
            "..sSSSSSSSSs..",
            "..RCCRCCRCCR..",
            "...RRRRRRRR...",
            "..............",
        },
        ["Medal"] = new[] // half a rusted token on a chain
        {
            "..s........s..",
            "...s......s...",
            "....s....s....",
            ".....s..s.....",
            "......ss......",
            "....RCCCR.....",
            "...RCWCCRR....",
            "..RCWRRRCR....",
            "..RCRLLRCRR...",
            "..RCRLLRCR....",
            "..RCCRRRCRR...",
            "...RCCCCR.....",
            "....RRRR......",
            "..............",
        },
        ["Katana"] = new[] // diagonal blade, gold tsuba, red-wrapped hilt
        {
            ".............B",
            "............SB",
            "...........SB.",
            "..........SB..",
            ".........SB...",
            "........SB....",
            ".......SB.....",
            "....G.SB......",
            ".....GsG......",
            ".....HGG......",
            "....HLH.G.....",
            "...HLH........",
            "..HLH.........",
            ".GG...........",
        },
        ["BigSword"] = new[] // broad crimson-fullered greatsword
        {
            "......BB......",
            ".....SBBS.....",
            ".....SBSs.....",
            ".....SBSs.....",
            ".....SBSs.....",
            ".....SBSs.....",
            ".....SBSs.....",
            ".....SHSs.....",
            ".....SHSs.....",
            "..GGGGGGGGG...",
            "......LH......",
            "......LH......",
            "......LH......",
            ".....GGGG.....",
        },
    };

    /// <summary>The ult button's emblem — a black sun in a crimson/gold corona.</summary>
    public static readonly string[] EclipseRows =
    {
        "......HH......",
        "...H..OO..H...",
        "....HYYYYH....",
        "..HYGKKKKGYH..",
        "...YKKKKKHY...",
        ".HYKKKKKKKHYH.",
        "HOYKKKKKKKKYOH",
        "HOYKKKKKKKKYOH",
        ".HYKKKKKKKKYH.",
        "...YKKKKKKY...",
        "..HYGKKKKGYH..",
        "....HYYYYH....",
        "...H..OO..H...",
        "......HH......",
    };

    public static Sprite Eclipse() => Outlined("Eclipse", EclipseRows);

    /// <summary>True when the id or the flattened name names a drawn item icon.</summary>
    private static bool TryResolveItem(string id, string fullName, out string key)
    {
        if (!string.IsNullOrEmpty(id) && ItemRows.ContainsKey(id)) { key = id; return true; }
        var flat = (fullName ?? "").Replace(" ", "");
        foreach (var k in ItemRows.Keys)
            if (string.Equals(k, flat, System.StringComparison.OrdinalIgnoreCase)) { key = k; return true; }
        key = null;
        return false;
    }

    // ---------- ult button ----------

    /// <summary>Solid white disc, Ø <paramref name="n"/> texels — the ult
    /// fill/well/flash, tinted by the Image colour.</summary>
    public static Sprite Disc(int n)
    {
        var key = "Disc" + n;
        if (Cached(key, out var hit)) return hit;
        var p = new Px(n, n);
        var c = n * 0.5f;
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = x + 0.5f - c; var dy = y + 0.5f - c;
            if (dx * dx + dy * dy <= c * c) p.Set(x, y, Color.white);
        }
        return Store(key, p);
    }

    /// <summary>Soft white halo (alpha falls off squared) — glows tinted by colour.</summary>
    public static Sprite Halo()
    {
        if (Cached("Halo", out var hit)) return hit;
        const int n = 32;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = (x + 0.5f - n * 0.5f) / (n * 0.5f);
            var dy = (y + 0.5f - n * 0.5f) / (n * 0.5f);
            var a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
            p.Set(x, y, new Color(1f, 1f, 1f, a * a));
        }
        return Store("Halo", p);
    }

    /// <summary>30-texel ult ring in the health frame's language: ink / lit
    /// copper-rust wood / violet mass / ink, hollow centre for the fill disc,
    /// four copper studs on the diagonals.</summary>
    public static Sprite UltRing()
    {
        if (Cached("UltRing", out var hit)) return hit;
        const int n = 30;
        const float c = n * 0.5f;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = x + 0.5f - c; var dy = y + 0.5f - c;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d > 14.6f || d <= 11.2f) continue;
            var lit = (-dx + dy) > 1.5f;
            var col = d > 13.8f ? PersonaUi.Ink
                    : d > 12.9f ? (lit ? (Hash(x, y) % 5 == 0 ? WoodHi : PersonaUi.Copper) : PersonaUi.Rust)
                    : d > 12f ? Mass(x, y)
                    : PersonaUi.Ink;
            p.Set(x, y, col);
        }
        foreach (var (sx, sy) in new[] { (4, 4), (25, 4), (4, 25), (25, 25) })
            KnotDot(p, sx, sy);
        p.Outline();
        return Store("UltRing", p);
    }

    // ---------- gourd (flask) ----------

    public const int GourdW = 30, GourdH = 38;
    // Teardrop/comma GLASS flask (reference pair): a round fat belly low-left
    // whose mass tapers up-right into a curved stem that hooks over and ends
    // in a pale cut cap. The thorned wood frames the edge, heavy left/bottom.
    public const float WinCx = 13.2f, WinCy = 10.8f;    // big round glass face, low-centre
    private const float GlassRx = 6.8f, GlassRy = 6.8f; // circular glass
    public const float WinBotY = 4.2f;    // liquid surface at level 0
    public const float WinTopY = 18.0f;   // liquid surface at level 1 (snaps to row 18 = glass top)
    /// <summary>Count badge centre (texels) — a round chit pinned on the
    /// bottom-right rim; half on the wood, half hanging off.</summary>
    public const float TagCx = 22.5f, TagCy = 4.2f;
    /// <summary>Emblem centre (heart/eye) on the thick left wood wrap at the
    /// glass's left edge, like the reference flask.</summary>
    public const float EmbCx = 7.0f, EmbCy = 16.5f;

    /// <summary>The whole silhouette: round belly + left lumps + upper-right
    /// tail + stem + cap + thorn spikes. Used by the ring/self checks; the
    /// painter classifies cap/stem/glass/wood per region.</summary>
    public static bool InBody(float x, float y)
    {
        var dx = (x - 13.5f) / 10.6f; var dy = (y - 11f) / 10.4f;
        if (dx * dx + dy * dy <= 1f) return true;                  // round belly
        dx = (x - 4.2f) / 2.6f; dy = (y - 15f) / 3.2f;
        if (dx * dx + dy * dy <= 1f) return true;                  // left hip lump
        dx = (x - 8f) / 4f; dy = (y - 20.2f) / 3f;
        if (dx * dx + dy * dy <= 1f) return true;                  // top-left hump
        if (Seg(x, y, 16.5f, 17f, 20.5f, 23.5f, out var t) < Mathf.Lerp(4.2f, 2.4f, t)) return true; // upper tail
        var sd = StemDist(x, y, out var st);
        if (sd < StemHalf(st)) return true;                       // stem hook
        var cd = CapDist(x, y, out var ct);
        if (cd < CapHalf(ct)) return true;                        // pale cap tip
        if (Seg(x, y, 4.2f, 15.5f, 1.4f, 17f, out t) < Mathf.Lerp(1.2f, 0.4f, t)) return true;   // thorn L
        if (Seg(x, y, 4.5f, 6.5f, 2.2f, 4.6f, out t) < Mathf.Lerp(1.2f, 0.4f, t)) return true;   // thorn L-low
        return Seg(x, y, 23.6f, 7f, 26.2f, 6f, out t) < Mathf.Lerp(1.0f, 0.3f, t);               // thorn R-low
    }

    /// <summary>Signed distance to the glass edge: negative inside. The belly
    /// ellipse shrunk by the wood frame — no carved window, the whole face
    /// is the bottle's glass.</summary>
    public static float WinDist(float x, float y)
    {
        var dx = (x - WinCx) / GlassRx; var dy = (y - WinCy) / GlassRy;
        return (Mathf.Sqrt(dx * dx + dy * dy) - 1f) * GlassRx;
    }

    public static bool InWin(float x, float y) => WinDist(x, y) <= 0f;

    /// <summary>Half-width of the glass chord at texel height y.</summary>
    public static float WinHalf(float y)
    {
        var dy = (y - WinCy) / GlassRy;
        var s = 1f - dy * dy;
        return s > 0f ? GlassRx * Mathf.Sqrt(s) : 0f;
    }

    /// <summary>Image.fillAmount (vertical, from the sprite's bottom) that fills
    /// the glass window to <paramref name="level"/> (0..1).</summary>
    public static float LiquidFill(float level)
        => (WinBotY + Mathf.Clamp01(level) * (WinTopY - WinBotY)) / GourdH;

    /// <summary>Liquid surface height (texels) and half the window chord there.</summary>
    public static float SurfaceY(float level) => WinBotY + Mathf.Clamp01(level) * (WinTopY - WinBotY);

    /// <summary>Surface snapped to a whole texel row — the HUD's liquid cut.</summary>
    public static float SnappedSurface(float level) => Mathf.Floor(SurfaceY(level) + 0.5f);
    public static float HalfChord(float y) => WinHalf(y);

    private static float Seg(float px, float py, float ax, float ay, float bx, float by, out float t)
    {
        var abx = bx - ax;
        var aby = by - ay;
        t = Mathf.Clamp01(((px - ax) * abx + (py - ay) * aby) / (abx * abx + aby * aby));
        var cx = ax + abx * t - px;
        var cy = ay + aby * t - py;
        return Mathf.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>The reference flask: a teardrop GLASS bottle — round belly,
    /// narrow tilted neck, pale cork stopper — framed by asymmetric thorned
    /// dark-brown wood (heavier on the left/bottom). Grey headspace with a
    /// diagonal parchment reflection on the glass's upper-right; the liquid
    /// sprite fills the glass bottom-up. Heart emblem rides upper-left.</summary>
    public static Sprite Gourd()
    {
        if (Cached("Gourd", out var hit)) return hit;
        var p = new Px(GourdW, GourdH);
        for (var y = 0; y < GourdH; y++)
        for (var x = 0; x < GourdW; x++)
        {
            var fx = x + 0.5f;
            var fy = y + 0.5f;
            Color? col = null;

            var capD = CapDist(fx, fy, out var ct);
            var stemD = StemDist(fx, fy, out var st);
            if (fy > 28f && capD < CapHalf(ct))
            {
                // Cap tip — pale cut end: parchment body, flat top face,
                // dark right edge.
                col = ct > 0.72f ? WoodHi : Parchment;
                if (capD > CapHalf(ct) - 0.8f) col = PersonaUi.Rust;
            }
            else if (fy > 22.6f && stemD < StemHalf(st))
            {
                // Stem — dark wood like the frame: stripes carry over.
                var sband = Mathf.Floor((fy + fx * 0.4f) / 3f);
                col = (int)sband % 2 == 0 ? PersonaUi.Rust : Groove;
                if (stemD > StemHalf(st) - 0.8f) col = PersonaUi.Ink;
            }
            else if (InBody(fx, fy))
            {
                var wd = WinDist(fx, fy);
                if (wd <= 0f)
                {
                    // Glass — flat grey headspace (bright band at the top edge)
                    // + diagonal parchment reflection on the upper-right.
                    col = fy > WinTopY - 1.8f ? Glass : GlassD;
                    var sd = Seg(fx, fy, 16.5f, 15f, 19f, 11f, out _);
                    if (sd < 0.9f) col = Parchment;
                    else if (sd < 1.4f) col = Color.Lerp(col.Value, Parchment, 0.35f);
                }
                else if (wd <= 1.1f) col = PersonaUi.Ink;                   // glass edge
                else
                {
                    // Thorned wood frame — dark brown, bold vertical stripes,
                    // upper-left sheen, deep groove bottom-right.
                    var band = Mathf.Floor((fx + Mathf.Sin(fy * 0.2f) * 1.8f) / 3f);
                    col = (int)band % 2 == 0 ? PersonaUi.Rust : Groove;
                    var nx = (fx - 13.5f) / 10.6f; var ny = (fy - 11f) / 10.4f;
                    var light = -nx * 0.6f + ny * 0.45f;
                    if (light > 0.45f) col = WoodHi;
                    else if (light < -0.5f) col = Groove;
                    if (Hash(x / 2, y / 3) % 23 == 0) col = col == Groove ? PersonaUi.Copper : Groove;
                }
            }

            if (col.HasValue) p.Set(x, y, col.Value);
        }
        p.Outline();
        // Stepped bite + thorn tips — hand-placed carved notches.
        p.Set(3, 12, Clear); p.Set(4, 12, PersonaUi.Night); p.Set(4, 11, PersonaUi.Rust);
        p.Set(1, 17, Parchment); p.Set(2, 4, Parchment); p.Set(26, 6, Parchment);
        // Left rim light — the first solid texel each row goes pale.
        for (var y = 4; y < 34; y++)
        for (var x = 0; x < GourdW; x++)
        {
            if (!p.Solid(x, y)) continue;
            if (p.Get(x, y) != PersonaUi.Ink && Hash(x, y) % 4 != 0) p.Set(x, y, WoodHi);
            break;
        }
        return Store("Gourd", p);
    }

    /// <summary>The stem — seg (20.5,23.5)->(23,28.5), the comma's hook
    /// curving up-right off the tail; half-width tapers 2.4->1.6.</summary>
    private static float StemDist(float x, float y, out float t)
        => Seg(x, y, 20.5f, 23.5f, 23f, 28.5f, out t);

    private static float StemHalf(float t) => Mathf.Lerp(2.4f, 1.6f, t);

    /// <summary>The cap — seg (23,28.5)->(25.2,31.8), the pale cut tip at
    /// the hook's end; half-width 2.3->1.9.</summary>
    private static float CapDist(float x, float y, out float t)
        => Seg(x, y, 23f, 28.5f, 25.2f, 31.8f, out t);

    private static float CapHalf(float t) => Mathf.Lerp(2.3f, 1.9f, t);

    /// <summary>The window region in greys (tinted by Image.color): darker
    /// toward the pointed bottom and a depth rim hugging the glass edge.</summary>
    public static Sprite GourdLiquid()
    {
        if (Cached("Liquid", out var hit)) return hit;
        var p = new Px(GourdW, GourdH);
        for (var y = 0; y < GourdH; y++)
        for (var x = 0; x < GourdW; x++)
        {
            var wd = WinDist(x + 0.5f, y + 0.5f);
            if (wd > 0f) continue;
            var rel = (y + 0.5f - WinBotY) / (WinTopY - WinBotY);
            var g = rel < 0.2f ? 0.45f : rel < 0.78f ? 0.8f : 1f; // dark point, hot band at top
            if (wd > -1f) g *= 0.8f;
            p.Set(x, y, new Color(g, g, g, 1f));
        }
        return Store("Liquid", p);
    }

    /// <summary>The YOU DIED ribbon — a torn black banner sized for the death
    /// root (275×48 texels = 1100×192 UI px): jagged sawteeth on both edges,
    /// blood drips hanging off the bottom, a broken blood seam inside, heart
    /// studs at the top corners, faint grain so the mass reads as cloth/wood
    /// not flat fill.</summary>
    public static Sprite DeathRibbon()
    {
        if (Cached("DeathRibbon", out var hit)) return hit;
        const int w = 275, h = 48;
        var p = new Px(w, h);
        var drip = 0;
        for (var x = 0; x < w; x++)
        {
            // Drips start on rare hash hits and taper over their tail — every
            // ~40 texels one hangs 5–9 rows, wide enough to read.
            if (drip <= 0 && Hash(x, 5) % 41 == 0) drip = 5 + Hash(x, 11) % 5;
            else if (drip > 0) drip--;
            var top = h - 1 - (x % 11 < 2 ? 3 : x % 19 < 3 ? 2 : x % 7 < 2 ? 1 : 0);
            var bot = x % 13 < 2 ? 3 : x % 17 < 3 ? 2 : x % 9 < 2 ? 1 : 0;
            for (var y = bot; y <= top; y++)
            {
                var dyTop = top - y;
                var dyBot = y - bot;
                var d = Mathf.Min(Mathf.Min(dyTop, dyBot), Mathf.Min(x, w - 1 - x));
                Color col;
                if (d <= 0) col = PersonaUi.Ink;
                else if (d == 1) col = dyTop < dyBot ? PersonaUi.Rust : PersonaUi.Blood;
                else if (d == 2) col = PersonaUi.Ink;
                else if (d == 3) col = dyTop < 3 ? PersonaUi.Copper : PersonaUi.Blood;
                else
                {
                    var mid = 1f - Mathf.Abs(y - (bot + top) * 0.5f) / Mathf.Max(1f, (top - bot) * 0.5f);
                    col = Color.Lerp(PersonaUi.Night, new Color(0.28f, 0.05f, 0.08f), mid * 0.55f);
                    if (y % 5 == 0 && Hash(x, y) % 2 == 0) col = Color.Lerp(col, PersonaUi.Blood, 0.35f);
                    if (Hash(x, y) % 11 == 0) col = Color.Lerp(col, PersonaUi.Plum, 0.3f);
                }
                // Broken blood seam, a few texels above the bottom rim.
                if (dyBot == 6 && Hash(x, 2) % 7 != 0) col = PersonaUi.Blood;
                if (dyBot == 7 && Hash(x, 2) % 9 == 0) col = PersonaUi.Blood;
                if ((x >= 5 && x <= 7 || x >= w - 8 && x <= w - 6) && dyTop is >= 3 and <= 5)
                    col = PersonaUi.Heart; // studs
                p.Set(x, y, col);
            }
            for (var dy = 1; dy <= drip && bot - dy >= 0; dy++)
            {
                var tip = dy == drip;
                p.Set(x, bot - dy, tip ? PersonaUi.Ink : PersonaUi.Heart);
                p.Set(x - 1, bot - dy, tip || dy > drip - 2 ? PersonaUi.Ink : PersonaUi.Blood);
            }
        }
        p.Outline();
        return Store("DeathRibbon", p);
    }

    /// <summary>A jagged fracture for the death letters — dark crack wandering
    /// down with side splinters; dark ink on clear so it carves INTO the
    /// bone letters it overlays.</summary>
    public static Sprite Crack()
    {
        if (Cached("Crack", out var hit)) return hit;
        const int w = 14, h = 20;
        var p = new Px(w, h);
        var cx = w * 0.5f;
        for (var y = h - 1; y >= 0; y--)
        {
            cx += (Hash(y, 1) % 3 - 1) * 0.9f;
            var xi = Mathf.Clamp(Mathf.RoundToInt(cx), 1, w - 3);
            p.Set(xi, y, PersonaUi.Ink);
            if (Hash(y, 4) % 3 == 0) p.Set(xi + 1, y, PersonaUi.Ink);
            if (Hash(y, 8) % 4 == 0)
            {
                var dir = Hash(y, 12) % 2 == 0 ? 1 : -1;
                p.Set(xi + dir, y - 1, PersonaUi.Ink);
                p.Set(xi + dir * 2, y - 2, PersonaUi.Ink);
            }
        }
        return Store("Crack", p);
    }

    /// <summary>Soft elliptical red glow — the burst behind the death letters.</summary>
    public static Sprite Radial()
    {
        if (Cached("Radial", out var hit)) return hit;
        const int n = 48;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = (x + 0.5f - n * 0.5f) / (n * 0.5f);
            var dy = (y + 0.5f - n * 0.5f) / (n * 0.5f) * 1.5f;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            var a = Mathf.Clamp01(1f - d); a *= a * 0.55f;
            p.Set(x, y, new Color(0.6f, 0.03f, 0.07f, a));
        }
        return Store("Radial", p);
    }

    /// <summary>The broken-heart medallion pinned to the ribbon's top edge —
    /// copper ring, dark disc, the cracked heart in blood red inside.</summary>
    public static Sprite DeathMedal()
    {
        if (Cached("DeathMedal", out var hit)) return hit;
        const int n = 18;
        var p = new Px(n, n);
        var c = (n - 1) * 0.5f;
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            if (d > 8.2f) continue;
            var lit = y > c || x < c;
            var col = d > 7.4f ? PersonaUi.Ink
                    : d > 6.2f ? lit ? PersonaUi.Copper : PersonaUi.Rust
                    : d > 5.2f ? PersonaUi.Ink
                    : PersonaUi.Night;
            p.Set(x, y, col);
        }
        // Cracked heart, stamped inside the disc.
        var rows = new[]
        {
            ".KK...KK.",
            "KHHK.KHHK",
            "KHHBKBHHK",
            "KHHBHHBHK",
            ".KHBHBHK.",
            "..KHBHK..",
            "...KHK...",
            "....K....",
        };
        p.Blit(rows, 4, 5);
        p.Outline();
        return Store("DeathMedal", p);
    }

    /// <summary>The count badge pinned on the gourd's bottom-right rim — a
    /// round chit: ink outer ring, copper/rust lit ring, dark well. The
    /// digits paint on top (Wukong-style circular count over the flask).</summary>
    public static Sprite Tag()
    {
        if (Cached("Tag", out var hit)) return hit;
        const int n = 11;
        var p = new Px(n, n);
        var c = (n - 1) * 0.5f;
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            if (d > 5f) continue;
            var lit = y > c || x < c;
            p.Set(x, y, d > 4.1f ? PersonaUi.Ink
                    : d > 3.2f ? lit ? PersonaUi.Copper : PersonaUi.Rust
                    : Hash(x, y) % 6 == 0 ? PersonaUi.Plum : PersonaUi.Night);
        }
        p.Outline();
        return Store("Tag", p);
    }

    // Souls plate layout (texels, origin bottom-left) — GameHud places the
    // ember and count against these.
    public const int SoulsW = 80, SoulsH = 24;
    public const float SoulsSocketX = 65f, SoulsSocketY = 12.5f;
    public static readonly RectInt SoulsWell = new RectInt(14, 10, 41, 6); // count interior

    /// <summary>The top-right souls plate — the health frame mirrored for the
    /// right edge at one-slot height: ember socket on the screen-edge end
    /// with a spike tip past it, a black count well, a hollow diamond tip
    /// pointing inward, lumps + curl on top and a drip below, all in the
    /// frame's mass / copper-rim / ink language.</summary>
    public static Sprite SoulsPlate()
    {
        if (Cached("SoulsPlate", out var hit)) return hit;
        const int w = SoulsW, h = SoulsH;
        var p = new Px(w, h);
        var curl = new HashSet<int> { 31 + 19 * w, 31 + 20 * w, 32 + 21 * w, 33 + 21 * w, 34 + 20 * w, 33 + 19 * w };
        var drip = new HashSet<int> { 38 + 6 * w, 39 + 6 * w, 40 + 6 * w, 38 + 5 * w, 39 + 5 * w, 39 + 4 * w, 39 + 3 * w };
        float Ell(int x, int y, float cx, float cy, float a, float b)
        { var dx = (x + 0.5f - cx) / a; var dy = (y + 0.5f - cy) / b; return dx * dx + dy * dy; }
        float Socket(int x, int y)
        { var dx = x + 0.5f - SoulsSocketX; var dy = y + 0.5f - SoulsSocketY; return Mathf.Sqrt(dx * dx + dy * dy); }
        float Diamond(int x, int y, float cx) => Mathf.Abs(x + 0.5f - cx) + Mathf.Abs(y + 0.5f - SoulsSocketY);
        bool Solid(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            if (Diamond(x, y, 7.5f) <= 2.5f) return false; // hollow inward tip
            return x >= 9 && x <= 61 && y >= 7 && y <= 18
                || Socket(x, y) <= 9f
                || Diamond(x, y, 75.5f) <= 3f
                || Diamond(x, y, 7.5f) <= 6.5f
                || y >= 18 && (Ell(x, y, 24.5f, 18.5f, 6f, 3f) <= 1f || Ell(x, y, 44.5f, 18.5f, 4f, 2f) <= 1f)
                || y <= 7 && Ell(x, y, 19.5f, 7.5f, 5f, 2.5f) <= 1f
                || curl.Contains(x + y * w) || drip.Contains(x + y * w);
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!Solid(x, y)) continue;
            var d = Socket(x, y);
            Color col;
            if (d <= 9f)
            {
                var lit = (-(x + 0.5f - SoulsSocketX) + (y + 0.5f - SoulsSocketY)) > 1.5f;
                col = d > 8.2f ? PersonaUi.Ink
                    : d > 7.3f ? (lit ? (Hash(x, y) % 5 == 0 ? PersonaUi.Plum : PersonaUi.Copper) : PersonaUi.Rust)
                    : d > 6.3f ? Mass(x, y)
                    : d > 5.6f ? PersonaUi.Ink
                    : PersonaUi.Night;
            }
            else if (x >= SoulsWell.xMin - 1 && x <= SoulsWell.xMax && y >= SoulsWell.yMin - 1 && y <= SoulsWell.yMax)
            {
                var edge = x == SoulsWell.xMin - 1 || x == SoulsWell.xMax || y == SoulsWell.yMin - 1 || y == SoulsWell.yMax;
                col = edge ? PersonaUi.Ink : Hash(x, y) % 9 == 0 ? PersonaUi.Night : Well;
            }
            else if (!Solid(x, y + 1))
                col = Hash(x, y) % 4 == 0 ? WoodHi : Hash(x, y) % 5 == 0 ? PersonaUi.Rust : PersonaUi.Copper;
            else if (!Solid(x, y - 1))
                col = Hash(x, y) % 3 == 0 ? Mass(x, y) : PersonaUi.Rust;
            else col = Mass(x, y);
            p.Set(x, y, col);
        }
        p.Outline();
        return Store("SoulsPlate", p);
    }

    /// <summary>The lock-on marker: a plain blood-red dot with an ink rim —
    /// no ring, no thorns.</summary>
    public static Sprite Dot()
    {
        if (Cached("Dot", out var hit)) return hit;
        const int n = 8;
        var p = new Px(n, n);
        var c = (n - 1) * 0.5f;
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
            if (d > 3.2f) continue;
            p.Set(x, y, d > 2.4f ? PersonaUi.Ink : d > 1.6f ? PersonaUi.Blood : PersonaUi.Heart);
        }
        if (p.Solid(2, 5)) p.Set(2, 5, PersonaUi.Bone); // specular fleck
        return Store("Dot", p);
    }

    /// <summary>Lock-on marker v2: a small X — two diagonal strokes, ink
    /// edge, blood-red core, bone fleck at the crossing. 90°-symmetric so a
    /// slow rotation reads as a turn.</summary>
    public static Sprite LockX()
    {
        if (Cached("LockX", out var hit)) return hit;
        const int n = 9;
        const float c = (n - 1) * 0.5f;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = x - c; var dy = y - c;
            // Distance to each diagonal stroke's centreline (|x|=|y| axes).
            var w = Mathf.Min(Mathf.Abs(dx - dy), Mathf.Abs(dx + dy)) * 0.7071f;
            var tip = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) > 3.4f;
            if (tip) continue;                       // strokes stop before the corners
            Color col = Clear;
            if (w <= 0.8f) col = PersonaUi.Heart;
            else if (w <= 1.7f) col = PersonaUi.Ink;
            if (col.a > 0f) p.Set(x, y, col);
        }
        if (p.Solid(4, 4)) p.Set(4, 4, PersonaUi.Bone); // crossing fleck
        p.Outline();
        return Store("LockX", p);
    }

    /// <summary>Low-HP screen vignette — a sliced deep-red edge glow, alpha
    /// ramping up toward the frame and fully clear in the middle.</summary>
    public static Sprite Vignette()
    {
        if (Cached("Vign", out var hit)) return hit;
        const int n = 24;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var d = Mathf.Min(x, Mathf.Min(y, n - 1 - x, n - 1 - y));
            var a = Mathf.Clamp01(1f - d / 7f);
            p.Set(x, y, new Color(0.45f, 0.02f, 0.06f, a * a * 0.9f));
        }
        return Store("Vign", p, new Vector4(8, 8, 8, 8));
    }

    // ---------- reticle ----------

    /// <summary>Lock-on: a Heart ring broken into four arcs, ink-edged, four
    /// rounded violet nubs on the diagonals, a Heart pip — nothing points in.</summary>
    public static Sprite Reticle()
    {
        if (Cached("Reticle", out var hit)) return hit;
        const int n = 17;
        const float c0 = n * 0.5f;
        var p = new Px(n, n);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = x + 0.5f - c0;
            var dy = y + 0.5f - c0;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            var gap = Mathf.Abs(dx) < 1.3f || Mathf.Abs(dy) < 1.3f;
            Color col = Clear;
            if (!gap && d >= 5.2f && d < 6.4f) col = PersonaUi.Heart;
            else if (!gap && ((d >= 6.4f && d < 7.3f) || (d >= 4.3f && d < 5.2f))) col = PersonaUi.Ink;
            else if (d < 1.3f) col = dx < 0f && dy > 0f ? EmberAmber : PersonaUi.Heart;
            else if (d < 2.2f) col = PersonaUi.Ink;
            foreach (var (sx, sy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
            {
                var ex = dx - sx * 5.6f;
                var ey = dy - sy * 5.6f;
                var e = Mathf.Sqrt(ex * ex + ey * ey);
                if (e < 0.9f) col = PersonaUi.Violet;
                else if (e < 1.6f && col.a == 0f) col = PersonaUi.Ink;
            }
            p.Set(x, y, col);
        }
        return Store("Reticle", p);
    }

    // ---------- self-check hooks ----------

    public static IEnumerable<(string name, string[] rows)> AllBitmaps()
    {
        yield return ("Curl", CurlRows);
        yield return ("Heart", HeartRows);
        yield return ("Eye", EyeRows);
        yield return ("Swirl", SwirlRows);
        yield return ("Ember", EmberRows);
        yield return ("Gem", GemRows);
        for (var i = 0; i < DigitRows.Length; i++) yield return ("Digit" + i, DigitRows[i]);
        foreach (var kv in SkillRows) yield return ("Skill" + kv.Key, kv.Value);
        foreach (var kv in ItemRows) yield return ("Item" + kv.Key, kv.Value);
        yield return ("Eclipse", EclipseRows);
    }

    /// <summary>Self-check hook: every shipped item/weapon resolves to a drawn icon.</summary>
    public static bool HasItemIcon(string id, string fullName) => TryResolveItem(id, fullName, out _);

    /// <summary>Outline smoke test: one painted texel gains a 4-texel ink plus.</summary>
    public static int OutlineProbe()
    {
        var p = new Px(3, 3);
        p.Set(1, 1, PersonaUi.Heart);
        p.Outline();
        var ink = 0;
        foreach (var c in p.c) if (c == PersonaUi.Ink) ink++;
        return ink;
    }

    // ---------- builders ----------

    /// <summary>Stretched 9-slice frame filling <paramref name="parent"/>.</summary>
    public static Image Build(Transform parent, string name, Kind k)
    {
        var img = PersonaUi.Stretch(parent, name).gameObject.AddComponent<Image>();
        img.sprite = Frame(k);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 1f / T;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>Item/weapon icon: a drawn item bitmap by id or flattened name
    /// (Vial, Starwater, Feather, Bomb, Cask, Medal, Katana, BigSword), then a
    /// skill icon, then the ember.</summary>
    public static Sprite ItemIcon(string id, string fullName = null)
        => TryResolveItem(id, fullName, out var key) ? Outlined("Item" + key, ItemRows[key]) : SkillIcon(id, fullName ?? "ITEM");

    /// <summary>Sprite at texel scale, centred on an anchor.</summary>
    public static Image Icon(Transform parent, string name, Sprite sp, Vector2 anchor, Vector2 pos, float scale = T)
    {
        var rt = PersonaUi.Box(parent, name, anchor, pos, new Vector2(sp.rect.width, sp.rect.height) * scale);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sp;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>Plain texel-sized quad (bubbles, glints, surface line).</summary>
    public static Image Quad(Transform parent, string name, Color col, Vector2 texels)
    {
        var rt = PersonaUi.Box(parent, name, new Vector2(0.5f, 0.5f), Vector2.zero, texels * T);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = col;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>Round wood knot glued to the middle of the left/right edge; its
    /// open attach column overlaps the frame's outline so they read as one piece.</summary>
    public static Image KnotCap(Transform parent, bool right)
    {
        var sp = Knot(right);
        var w = sp.rect.width * T;
        return Icon(parent, right ? "KnotR" : "KnotL", sp, new Vector2(right ? 1f : 0f, 0.5f),
            new Vector2(right ? w * 0.5f - T : -w * 0.5f + T, 0f));
    }

    /// <summary>Health-frame silhouette along a frame's rim: lumps on top and
    /// bottom, a couple of small thorns, the curl tendril, a bottom drip.
    /// Deterministic per seed; children of <paramref name="host"/> so they ride
    /// its animation.</summary>
    public static void Dress(RectTransform host, int seed, int lumps = 2, int thorns = 1, bool curl = false, bool drip = true)
    {
        var rng = new System.Random(seed);
        var widths = new[] { 8, 12, 16 };
        for (var i = 0; i < lumps; i++)
        {
            var f = 0.12f + 0.76f * (i + 0.5f) / lumps + (float)(rng.NextDouble() - 0.5) * 0.1f;
            Edge(host, "LumpT" + i, Lump(widths[rng.Next(widths.Length)]), f, true);
            var g = 0.12f + 0.76f * (i + 0.2f + (float)rng.NextDouble() * 0.6f) / lumps;
            var w = widths[rng.Next(widths.Length)];
            Edge(host, "LumpB" + i, Flipped("LumpD" + w, () => PxOf(Lump(w))), g, false);
        }
        for (var i = 0; i < thorns; i++)
            Edge(host, "Thorn" + i, Thorn(), 0.3f + (float)rng.NextDouble() * 0.45f, true);
        if (curl) Edge(host, "Curl", Curl(), 0.16f + (float)rng.NextDouble() * 0.12f, true);
        if (drip) Edge(host, "Drip", Flipped("ThornD", () => PxOf(Thorn())), 0.46f + (float)rng.NextDouble() * 0.08f, false);
    }

    // Source buffers of stored sprites (textures are non-readable) — flipped
    // variants copy from here.
    private static readonly Dictionary<Sprite, Px> sources = new();
    private static Px PxOf(Sprite sp)
    {
        if (sources.TryGetValue(sp, out var p)) return Copy(p);
        throw new System.InvalidOperationException("HudArt: no source buffer for " + sp.name);
    }

    private static Px Copy(Px src)
    {
        var p = new Px(src.w, src.h);
        System.Array.Copy(src.c, p.c, src.c.Length);
        return p;
    }

    private static void Edge(RectTransform host, string name, Sprite sp, float f, bool top)
    {
        var size = new Vector2(sp.rect.width, sp.rect.height) * T;
        var rt = PersonaUi.Rect(host, name, new Vector2(f, top ? 1f : 0f), new Vector2(f, top ? 1f : 0f), Vector2.zero, Vector2.zero);
        rt.pivot = new Vector2(0.5f, top ? 0f : 1f);
        rt.sizeDelta = size;
        rt.anchoredPosition = new Vector2(0f, top ? -Sink * T : Sink * T); // sink into the rim
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sp;
        img.raycastTarget = false;
    }
}
