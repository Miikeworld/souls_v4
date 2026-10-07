using UnityEngine;

/// <summary>
/// The Warden's armoury — REAL weapon prefabs the fight summons (Resources/WardenArmory):
/// his own sword (the BossSword the custom rig carries) plus Synty PolygonDarkFantasy,
/// PolygonDungeon and PolygonDungeonRealms swords, greatswords, axes, spears,
/// halberds and a scythe. <see cref="SpectralBlade"/> instantiates these with their
/// own meshes and materials (legacy Standard materials are re-shaded to URP at
/// runtime) and adds only the fight's crimson energy on top: a hot spine, a trail,
/// an emission swell before it fires. Written by Tools > Project Restart > Warden >
/// Bake Spectral Arsenal (and shipped pre-filled), so a fresh checkout needs no step.
/// </summary>
[CreateAssetMenu(menuName = "Souls/Warden Armory", fileName = "WardenArmory")]
public sealed class WardenArmory : ScriptableObject
{
    public const string ResourceName = "WardenArmory";

    [Tooltip("His own blade — Crown of Blades, Judgment and Grave of Kings summon copies of it.")]
    public GameObject ownSword;
    public GameObject[] swords = System.Array.Empty<GameObject>();
    public GameObject[] greatswords = System.Array.Empty<GameObject>();
    public GameObject[] axes = System.Array.Empty<GameObject>();
    public GameObject[] spears = System.Array.Empty<GameObject>();
    public GameObject[] halberds = System.Array.Empty<GameObject>();
    public GameObject[] scythes = System.Array.Empty<GameObject>();

    private static WardenArmory cached;
    private static bool tried;

    public static WardenArmory Active
    {
        get
        {
            if (cached != null || tried) return cached;
            tried = true;
            cached = Resources.Load<WardenArmory>(ResourceName);
            return cached;
        }
    }

    /// <summary>The prefabs of one family (empty when none are assigned).</summary>
    public GameObject[] For(ArsenalKind kind) => kind switch
    {
        ArsenalKind.Own => ownSword != null ? new[] { ownSword } : System.Array.Empty<GameObject>(),
        ArsenalKind.Sword => swords,
        ArsenalKind.Greatsword => greatswords,
        ArsenalKind.Axe => axes,
        ArsenalKind.Spear => spears,
        ArsenalKind.Halberd => halberds,
        ArsenalKind.Scythe => scythes,
        _ => System.Array.Empty<GameObject>(),
    };
}
