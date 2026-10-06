using UnityEngine;

/// <summary>
/// Stat point-buy rules, attire presets and color palettes for
/// character creation. Attire indices are Chr_* part-name suffixes — they are
/// clamped to whatever variants the rig actually contains when applied.
/// </summary>
public static class CharacterCatalog
{
    /// <summary>Point-buy: three stats start at <see cref="Base"/>, cap at
    /// <see cref="Cap"/>, and share <see cref="Pool"/> points. Each maps to one
    /// resource pool.</summary>
    public static class StatRules
    {
        public const int Base = 10, Cap = 30, Pool = 20;
        public static float Hp(int vigor) => 50f + 5f * vigor;        // 10→100, 30→200
        public static float Stamina(int endurance) => 50f + 5f * endurance;
        public static float Mana(int mind) => 20f + 4f * mind;        // 10→60,  30→140
        public static int Spent(int v, int e, int m) => (v - Base) + (e - Base) + (m - Base);
        public static int Remaining(int v, int e, int m) => Pool - Spent(v, e, m);
        public static bool CanRaise(int stat, int v, int e, int m) => stat < Cap && Remaining(v, e, m) > 0;
        public static bool CanLower(int stat) => stat > Base;
    }

    /// <summary>A coordinated outfit — ORDINALS into each gendered body-part
    /// category's sorted variant list (HeroLibrary picks list[ordinal]).
    /// -1 disables an attachment slot.</summary>
    public sealed class Attire
    {
        public string name;
        public int torso, hips, legL, legR, armUpL, armUpR, armLowL, armLowR, handL, handR;
        public int back = -1, shoulderL = -1, shoulderR = -1, elbowL = -1, elbowR = -1;
        public int kneeL = -1, kneeR = -1, hipsAttach = -1, headCovering = -1;
    }

    // Indices are guesses across the pack's mix-and-match ranges — tune freely.
    public static readonly Attire[] Attires =
    {
        new Attire { name = "Heritage",
            torso = 4, hips = 1, legL = 15, legR = 15, armUpL = 6, armUpR = 6,
            armLowL = 16, armLowR = 16, handL = 4, handR = 13,
            back = 4, shoulderL = 14, shoulderR = 14, elbowR = 5, kneeR = 7, hipsAttach = 8 },
        new Attire { name = "Leather",
            torso = 0, hips = 0, legL = 0, legR = 0, armUpL = 0, armUpR = 0,
            armLowL = 0, armLowR = 0, handL = 0, handR = 0 },
        new Attire { name = "Plate",
            torso = 10, hips = 10, legL = 10, legR = 10, armUpL = 10, armUpR = 10,
            armLowL = 10, armLowR = 10, handL = 10, handR = 10,
            shoulderL = 10, shoulderR = 10 },
        new Attire { name = "Shadow",
            torso = 5, hips = 5, legL = 5, legR = 5, armUpL = 5, armUpR = 5,
            armLowL = 5, armLowR = 5, handL = 5, handR = 5 },
        new Attire { name = "Rags",
            torso = 1, hips = 1, legL = 1, legR = 1, armUpL = 1, armUpR = 1,
            armLowL = 1, armLowR = 1, handL = 1, handR = 1 },
    };

    public static readonly Color[] SkinTones =
    {
        new Color(0.96f, 0.80f, 0.66f), new Color(0.90f, 0.72f, 0.58f),
        new Color(0.80f, 0.60f, 0.45f), new Color(0.65f, 0.45f, 0.32f),
        new Color(0.48f, 0.32f, 0.22f), new Color(0.34f, 0.22f, 0.15f),
        new Color(0.25f, 0.16f, 0.11f), new Color(0.85f, 0.85f, 0.80f),
    };

    public static readonly Color[] HairColors =
    {
        new Color(0.08f, 0.06f, 0.05f), new Color(0.20f, 0.13f, 0.08f),
        new Color(0.38f, 0.24f, 0.12f), new Color(0.55f, 0.35f, 0.15f),
        new Color(0.75f, 0.60f, 0.35f), new Color(0.62f, 0.25f, 0.12f),
        new Color(0.85f, 0.85f, 0.87f), new Color(0.50f, 0.50f, 0.52f),
        new Color(0.30f, 0.10f, 0.30f), new Color(0.15f, 0.25f, 0.45f),
    };

    public static readonly Color[] EyeColors =
    {
        new Color(0.30f, 0.20f, 0.12f), new Color(0.35f, 0.55f, 0.25f),
        new Color(0.25f, 0.45f, 0.65f), new Color(0.45f, 0.40f, 0.30f),
        new Color(0.55f, 0.15f, 0.12f), new Color(0.85f, 0.80f, 0.75f),
    };

    public static readonly Color[] TrimColors =
    {
        new Color(0.82f, 0.64f, 0.30f), new Color(0.75f, 0.75f, 0.78f),
        new Color(0.55f, 0.12f, 0.10f), new Color(0.12f, 0.12f, 0.14f),
        new Color(0.85f, 0.82f, 0.74f), new Color(0.20f, 0.35f, 0.60f),
        new Color(0.30f, 0.45f, 0.25f), new Color(0.45f, 0.25f, 0.50f),
    };

    public static readonly Color[] LeatherColors =
    {
        new Color(0.28f, 0.21f, 0.16f), new Color(0.40f, 0.28f, 0.18f),
        new Color(0.18f, 0.13f, 0.10f), new Color(0.52f, 0.38f, 0.24f),
        new Color(0.12f, 0.11f, 0.11f), new Color(0.35f, 0.18f, 0.14f),
    };

    public static readonly Color[] MetalColors =
    {
        new Color(0.60f, 0.61f, 0.63f), new Color(0.35f, 0.37f, 0.40f),
        new Color(0.72f, 0.58f, 0.32f), new Color(0.55f, 0.36f, 0.25f),
        new Color(0.16f, 0.16f, 0.18f), new Color(0.80f, 0.80f, 0.84f),
    };

    public static readonly Color[] PaintColors =
    {
        new Color(0.23f, 0.58f, 0.76f), new Color(0.60f, 0.10f, 0.08f),
        new Color(0.10f, 0.10f, 0.10f), new Color(0.85f, 0.85f, 0.85f),
        new Color(0.30f, 0.55f, 0.25f), new Color(0.55f, 0.30f, 0.65f),
    };

    public static readonly Color[] ClothColors =
    {
        new Color(0.45f, 0.12f, 0.10f), new Color(0.12f, 0.20f, 0.38f),
        new Color(0.15f, 0.35f, 0.18f), new Color(0.55f, 0.45f, 0.20f),
        new Color(0.30f, 0.15f, 0.40f), new Color(0.50f, 0.50f, 0.52f),
        new Color(0.20f, 0.20f, 0.22f), new Color(0.68f, 0.44f, 0.22f),
    };
}
