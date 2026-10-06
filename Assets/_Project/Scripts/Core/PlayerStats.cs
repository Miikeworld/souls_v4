using UnityEngine;

/// <summary>
/// Checkpoint level-ups: souls buy +1 to vigor/endurance/mind — the same
/// point-buy stats character creation uses, so <c>ConfigureMax</c> and the
/// character.json save stay the single source of truth. Levels derive lazily
/// from the live pools (Awake order against PlayerCustomizer's Start apply is
/// undefined) and persist back into the build file when one exists — bare
/// test scenes level session-only.
/// </summary>
public sealed class PlayerStats : MonoBehaviour
{
    public enum Stat { Vigor, Endurance, Mind }
    private static readonly string[] Names = { "VIGOR", "ENDURANCE", "MIND" };

    [Tooltip("Souls price of the first level point.")]
    [SerializeField, Min(0)] private int baseCost = 50;
    [Tooltip("Each level raises the price by this much — souls-style rising curve.")]
    [SerializeField, Min(0)] private int costPerLevel = 8;

    private readonly int[] level = { -1, -1, -1 };
    private PlayerHealth hp;
    private PlayerStamina sp;
    private PlayerMana mp;

    public static string Name(Stat s) => Names[(int)s];
    public int Level(Stat s) { EnsureInit(); return level[(int)s]; }

    /// <summary>Souls price of the next point — rises with the stat level.</summary>
    public int Cost(Stat s) => baseCost + costPerLevel * Level(s);
    public bool Capped(Stat s) => Level(s) >= CharacterCatalog.StatRules.Cap;
    /// <summary>The pool this stat feeds — the menu's "100 » 105" preview.</summary>
    public float PoolAt(Stat s) => s == Stat.Vigor ? CharacterCatalog.StatRules.Hp(Level(s))
                                 : s == Stat.Endurance ? CharacterCatalog.StatRules.Stamina(Level(s))
                                 : CharacterCatalog.StatRules.Mana(Level(s));
    /// <summary>Pool value at the next level — for the menu's preview.</summary>
    public float PoolNext(Stat s) => Capped(s) ? PoolAt(s)
        : s == Stat.Vigor ? CharacterCatalog.StatRules.Hp(Level(s) + 1)
        : s == Stat.Endurance ? CharacterCatalog.StatRules.Stamina(Level(s) + 1)
        : CharacterCatalog.StatRules.Mana(Level(s) + 1);

    private void Awake() => Bind();

    private void Bind()
    {
        hp = GetComponent<PlayerHealth>();
        sp = GetComponent<PlayerStamina>();
        mp = GetComponent<PlayerMana>();
    }

    /// <summary>Spend souls for +1 stat. False when capped or the purse is short.</summary>
    public bool TryUpgrade(Stat s)
    {
        EnsureInit();
        if (Capped(s)) return false;
        var cost = Cost(s);
        if (SoulsWallet.Souls < cost) return false;
        SoulsWallet.Add(-cost);
        level[(int)s]++;
        Apply(s);
        Persist();
        return true;
    }

    private void EnsureInit()
    {
        if (level[0] >= 0) return;
        if (hp == null && sp == null && mp == null) Bind(); // edit-mode AddComponent skips Awake
        // The build file is authoritative when present; otherwise invert the
        // pools through the same formulas so a serialized max still reads as
        // a level, not a hardcoded 10.
        var build = CharacterBuildData.HasSave ? CharacterBuildData.Load() : null;
        level[0] = build != null ? build.vigor     : Invert(hp != null ? hp.Max : 100f, 50f, 5f);
        level[1] = build != null ? build.endurance : Invert(sp != null ? sp.Max : 100f, 50f, 5f);
        level[2] = build != null ? build.mind      : Invert(mp != null ? mp.Max : 50f, 20f, 4f);
    }

    private static int Invert(float pool, float baseValue, float perLevel)
        => Mathf.Max(0, Mathf.RoundToInt((pool - baseValue) / perLevel));

    /// <summary>ConfigureMax refills the pool too — a level-up doubles as a small heal.</summary>
    private void Apply(Stat s)
    {
        var v = level[(int)s];
        switch (s)
        {
            case Stat.Vigor:     if (hp != null) hp.ConfigureMax(CharacterCatalog.StatRules.Hp(v)); break;
            case Stat.Endurance: if (sp != null) sp.ConfigureMax(CharacterCatalog.StatRules.Stamina(v)); break;
            case Stat.Mind:      if (mp != null) mp.ConfigureMax(CharacterCatalog.StatRules.Mana(v)); break;
        }
    }

    private void Persist()
    {
        if (!CharacterBuildData.HasSave) return;
        var build = CharacterBuildData.Load();
        if (build == null) return;
        build.vigor = level[0];
        build.endurance = level[1];
        build.mind = level[2];
        build.Save();
    }
}
