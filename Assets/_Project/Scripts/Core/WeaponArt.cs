using UnityEngine;

/// <summary>
/// One weapon art: a mana-fuelled skill move played through the same
/// sweep/hit/freeze plumbing as combo swings. Assets live in
/// Assets/_Project/Combat/Arts/ (authored by Setup Combat Locomotion).
///
/// Timing model: `hitWindows`, `fxCues` and the projectile's `spawnTime` are
/// normalized (0..1) fractions of `duration` so everything stays glued to the
/// attack clock when the cinematic hold slows it.
/// </summary>
[CreateAssetMenu(menuName = "Project Restart/Weapon Art", fileName = "WeaponArt")]
public sealed class WeaponArt : ScriptableObject
{
    public bool contactMotionReviewed;
    public GameObject bladeEdgeFx;
    [Min(0.02f)] public float ribbonLife = 0.12f;
    public ArtVisualTheme visualTheme;
    public ArtContactMode contactMode;
    [Min(0f)] public float recoveryTransitionDelay = 0.06f;
    public Vector2[] trailWindows;
    [Min(0.1f)] public float contactHeight = 1.6f;
    public GameObject contactFx;
    public string artName = "Art";
    [Tooltip("≤7 chars — fits the HUD slot.")]
    public string shortName = "ART";
    [Tooltip("Base Layer state that plays the art.")]
    public string stateName = "SkillMK1";
    [Tooltip("HudArt skill icon id for the HUD slot (ArcBlade, RadiantRush, StarfallSlam, SolarSigil, PhantomDaggers, ShadowstepCut, CycloneKick, DragonPalm). Unknown ids resolve by artName.")]
    public string icon = "Shard";
    [Min(0f)] public float manaCost = 20f;
    [Tooltip("Seconds the art owns the player (tool sets clip length ÷ state speed).")]
    [Min(0.1f)] public float duration = 1.4f;
    [Tooltip("Normalized (0–1) hit windows; each new window can hit the same target again.")]
    public Vector2[] hitWindows = { new Vector2(0.3f, 0.55f) };
    [Tooltip("Seconds between blade passes inside a window — a continuous spin re-arms the victim set this often so every rotation can connect. 0 = one hit per window.")]
    [Min(0f)] public float rehitInterval;
    [Min(0f)] public float damagePerHit = 28f;
    [Tooltip("Poise damage per hit — breaks the boss/heavier enemies' posture.")]
    [Min(0f)] public float poiseDamage = 25f;
    [Min(0.5f)] public float range = 3f;
    [Range(30f, 360f)] public float arc = 160f;
    [Tooltip("Forward push spread across the first hit window (in-place clips only).")]
    [Min(0f)] public float advance = 1.2f;
    [Tooltip("Clip root motion drives the capsule — the standard for the MagicKnight/Ninja kit.")]
    public bool useRootMotion = true;
    [Tooltip("The clip's authored root-Y reaches the capsule — only for launch/leap arts; code gravity owns vertical otherwise.")]
    public bool rootMotionY;
    [Min(0f)] public float cooldown = 4f;

    [Header("Channelled arts (MK3-style): loop state while the cast key is held")]
    [Tooltip("Optional loop state after stateName ends. Empty = single-shot.")]
    public string loopState;
    [Tooltip("Exit/pose when the channel ends.")]
    public string loopEndState;
    [Tooltip("Seconds the loopEnd flourish plays before control returns (tool sets it to the clip's length).")]
    [Min(0.1f)] public float loopEndDuration = 0.45f;
    [Tooltip("Max channel seconds while held.")]
    [Min(0.1f)] public float loopMaxTime = 1.6f;
    [Tooltip("Extra mana drained per second while channelling.")]
    [Min(0f)] public float manaPerSecond = 12f;
    [Tooltip("Seconds between damage ticks while the loop state plays.")]
    [Min(0.05f)] public float loopHitInterval = 0.35f;

    [Header("Effects")]
    public FxCue[] fxCues;
    public ProjectileSpec projectile;
    [Tooltip("Audio ids into SfxBank, played at their cue times.")]
    public SfxCue[] sfxCues;
    [Tooltip("Damage flavour for the numerals — Poison ticks green.")]
    public DamageKind damageKind = DamageKind.Normal;
    [Tooltip("Bloom intensity punched at the first hit window (PostPulse). 0 = none.")]
    [Min(0f)] public float bloomPunch;

    [Header("Feel")]
    public float hitstop;
    public float shake = 0.2f;

    [Header("Classification — detail.md §5: gameplay identity is explicit data, not theme-derived")]
    [Tooltip("Which combat family owns the move — art slots / technique archetype / launcher / aerial step / ultimate.")]
    public ActionFamily family = ActionFamily.Art;
    [Tooltip("Entry facing policy — movement-led for crowd sweeps, target-led for executes/projectiles, launch-session for the aerial chain.")]
    public AimPolicy aim = AimPolicy.MovementLed;
    [Tooltip("Victim displacement/stagger profile applied to contacts.")]
    public ReactionProfile reaction = ReactionProfile.Sweep;
    [Tooltip("Stamina cost for attack-family moves (aerial steps use this, not mana). 0 = none.")]
    [Min(0f)] public float staminaCost;

    [Header("Technique — right-click big-sword archetype")]
    [Tooltip("Big-sword skills summon the oversized spectral blade. Explicit family data — every technique/launcher/aerial/ultimate is one.")]
    public bool BigSwordSkill => family != ActionFamily.Art
                              || (visualTheme == ArtVisualTheme.DarkCrimson && contactMode != ArtContactMode.ProjectileOnly);
    [Tooltip("Spectral blade scale while this skill runs — big-sword skills read huge.")]
    [Range(0.5f, 3f)] public float summonScale = 1f;
    [Tooltip("Next technique when the technique input is pressed while this art is running — queued like any art (G1→G2→G_ALL).")]
    public WeaponArt followUp;
    [Tooltip("Hits launch poise-vulnerable enemies airborne — the dedicated launcher move.")]
    public bool launch;
    [Tooltip("Max enemies a single hit wave can launch (0 = no cap) — the uppercut pops its three nearest victims.")]
    [Min(0)] public int launchMax;
    [Tooltip("Hits spike an already-airborne enemy back down into a hard landing.")]
    public bool spike;
    [Tooltip("Aerial-capable art: may start airborne and, when an enemy is launched nearby, the player rises to meet it before the falling smash.")]
    public bool airChase;

    [Header("Burst — Q+RMB ult")]
    public BurstSpec burst;
}

/// <summary>Genshin-style burst presentation: the world freezes while the hero
/// winds up at full speed — a pixel name banner and screen flash sell the
/// cut-in — then time snaps back as the first hit window opens. I-frames span
/// the cast; on end the weapon swaps to the rage set for `rageSeconds`.</summary>
[System.Serializable]
public sealed class BurstSpec
{
    public bool enabled;
    [Tooltip("World timeScale during the cinematic windup — the hero keeps full speed.")]
    [Range(0.01f, 0.3f)] public float timeScale = 0.05f;
    [Min(0f)] public float flashTime = 0.07f;
    [Tooltip("Pixel banner shows the art name on cast.")]
    public bool banner = true;
    [Tooltip("SfxBank id for the charge sting — silent until a clip is wired.")]
    public string introSfx = "art.burst";
    [Tooltip("Seconds of rage mode when the cast ends — the weapon swaps to the controller's rage set (the 7-hit big-sword chain). 0 = no swap.")]
    [Min(0f)] public float rageSeconds = 15f;
}

/// <summary>A timed VFX spawn during an art. `time` is normalized 0..1.</summary>
[System.Serializable]
public sealed class FxCue
{
    [Range(0f, 1f)] public float time;
    public GameObject prefab;
    [Tooltip("'handR'/'handL'/'body'/'root'/'target'/'world'.")]
    public string attach = "handR";
    public Vector3 offset;
    public Vector3 euler;
    [Tooltip("Follow the bone — off leaves the burst where it spawned.")]
    public bool follow;
    [Min(0.1f)] public float scale = 1f;
    [Tooltip("Palette slot into FxTint (0 = art default).")]
    public int palette;
    [Tooltip("Seconds before auto-despawn; 0 = play once and release.")]
    public float life;
    [Tooltip("Camera shake on cue.")]
    public float shake;
    [Tooltip("True freeze-frame on cue (seconds, real time).")]
    public float hitstop;
}

/// <summary>A projectile (wave, dagger, missile) launched mid-clip.</summary>
[System.Serializable]
public sealed class ProjectileSpec
{
    public bool wave;
    public Vector2 waveSize = new Vector2(2.4f, 1.6f);
    [Min(0f)] public float maxTravel;

    public GameObject prefab;
    [Tooltip("Normalized cast time inside the clip.")]
    [Range(0f, 1f)] public float spawnTime = 0.45f;
    [Min(1f)] public float speed = 18f;
    [Min(0.05f)] public float radius = 0.35f;
    [Min(0.2f)] public float life = 2f;
    [Min(0f)] public float damage = 20f;
    public int count = 1;
    [Tooltip("Fan spacing degrees for count > 1.")]
    public float spreadDeg = 12f;
    [Tooltip("Curve toward the locked target.")]
    public bool homing;
    [Tooltip("Keep flying through victims instead of dying on first hit.")]
    public bool pierce;
}

/// <summary>Audio hook — id resolved through SfxBank; missing clip = silent.</summary>
[System.Serializable]
public sealed class SfxCue
{
    [Range(0f, 1f)] public float time;
    public string id = "skill.cast";
}

public enum ArtVisualTheme { Original, DarkCrimson }
public enum ArtContactMode { Frontal, Spin, ProjectileOnly }

/// <summary>Combat identity of a move — routes inputs, mana-vs-stamina cost,
/// the aerial session and the spectral-blade summon. detail.md §5.</summary>
public enum ActionFamily { Art, Technique, Launcher, Aerial, Ultimate }

/// <summary>Entry facing: crowd sweeps follow the stick (lock is only a
/// fallback), executes/projectiles prefer the lock bearing, aerial steps
/// track the session primary. detail.md §171-176.</summary>
public enum AimPolicy { MovementLed, TargetLed, LaunchSession }

/// <summary>How victims react — displacement/stagger per hit, detail.md §184.
/// Spike is applied by the session spike path, not a sweep reaction.</summary>
public enum ReactionProfile { Normal, Sweep, Spin, Finisher }
