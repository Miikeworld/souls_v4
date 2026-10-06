using UnityEngine;

/// <summary>
/// Minimal health pool for test dummies. TakeDamage flashes the renderer; at
/// zero the dummy tips over, drops out of lock-on (Targetable disabled), and
/// respawns at its spawn transform after respawnDelay so the test loop keeps
/// going. Uses a runtime material instance so the shared test material is
/// never dirtied.
/// </summary>
public sealed class Health : MonoBehaviour
{
    [SerializeField, Min(1f)] private float maxHealth = 200f;
    [SerializeField, Min(0f)] private float respawnDelay = 3f;
    [Tooltip("Souls rule: corpses stay down until a checkpoint rest or player death resets them. Unchecked keeps the old test-loop auto-respawn.")]
    [SerializeField] private bool stayDead = true;
    [Tooltip("Souls awarded to the player on kill.")]
    [SerializeField, Min(0)] private int soulsReward = 25;
    [Tooltip("Mana restored to the player on a player-owned kill (elites override in setup; bosses carry no EnemyAI so they never pay this).")]
    [SerializeField, Min(0)] private int manaReward = 6;
    [Tooltip("Seconds a player hit stays credited — covers environmental kills like a punt into a KillZone.")]
    [SerializeField, Min(0.5f)] private float killCreditWindow = 4f;
    [Tooltip("Hit flash length (real time — it plays through the hit freeze).")]
    [SerializeField, Min(0.01f)] private float hitFlashTime = 0.16f;
    [SerializeField, Min(0.05f)] private float fallDuration = 0.45f;
    [Tooltip("Blood-splat prefab burst on the victim when a hit lands (PolygonParticles).")]
    [SerializeField] private GameObject bloodFx;

    public float Current { get; private set; }
    public float Max => maxHealth;
    public bool IsDead { get; private set; }
    /// <summary>(amount, hitFrom, poiseDamage, reaction) — AI hooks this for stagger reactions.</summary>
    public event System.Action<float, Vector3, float, ReactionProfile> Damaged;
    /// <summary>Fired once on death — souls payout hooks this.</summary>
    public event System.Action Died;
    public int SoulsReward => soulsReward;
    public int ManaReward => manaReward;
    /// <summary>Set when a skeletal death animation replaces the capsule tip-over.</summary>
    public bool SuppressDeathMotion { get; set; }
    /// <summary>Scripted boss beats (phase transitions, sealed finales) — hits are
    /// ignored outright: no damage, no numerals, no Damaged event.</summary>
    public bool Invulnerable { get; set; }

    private Renderer rend;
    private Color baseColor;
    private float flashT;
    // Visible meshes (skeleton body + weapons) flash via a property block —
    // the first child renderer is the hidden physics capsule, so flashing
    // only that showed nothing.
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private Renderer[] flashRends;
    private Color[] flashBase;
    private MaterialPropertyBlock mpb;
    private Targetable targetable;
    private Collider col;
    private Vector3 homePos;
    private Quaternion homeRot;
    private float respawnT = -1f;
    private float fallT;

    private void Awake()
    {
        rend = GetComponentInChildren<Renderer>();
        if (rend != null) baseColor = rend.material.color; // instanced copy — shared asset untouched
        targetable = GetComponent<Targetable>();
        col = GetComponent<Collider>();
        homePos = transform.position;
        homeRot = transform.rotation;
        Current = maxHealth;
    }

    private void Start()
    {
        // After Awake so dressed skeleton visuals are in place.
        var list = new System.Collections.Generic.List<Renderer>();
        foreach (var r in GetComponentsInChildren<Renderer>())
            if (r.enabled && r.gameObject.activeInHierarchy && r is not ParticleSystemRenderer && r is not TrailRenderer
                && r.sharedMaterial != null && r.sharedMaterial.HasProperty(BaseColorId))
                list.Add(r);
        if (list.Count == 0) return;
        flashRends = list.ToArray();
        flashBase = new Color[flashRends.Length];
        for (var i = 0; i < flashRends.Length; i++) flashBase[i] = flashRends[i].sharedMaterial.GetColor(BaseColorId);
        mpb = new MaterialPropertyBlock();
    }

    private void ApplyFlash(float k)
    {
        if (flashRends != null)
        {
            for (var i = 0; i < flashRends.Length; i++)
            {
                if (flashRends[i] == null) continue;
                flashRends[i].GetPropertyBlock(mpb);
                // HDR multiply washes the texture toward white — reads as a hit flash.
                mpb.SetColor(BaseColorId, Color.LerpUnclamped(flashBase[i], flashBase[i] * 4f, k));
                flashRends[i].SetPropertyBlock(mpb);
            }
        }
        else if (rend != null)
            rend.material.color = Color.Lerp(baseColor, Color.white, k);
    }

    // Player-owned kill credit: the last player-attributed hit stays live for
    // killCreditWindow so punts into KillZones still count. Cleared on respawn
    // — a genuine reset starts a new life eligible for a later reward.
    private PlayerMana lastPlayerHit;
    private float lastPlayerHitAt = -999f;
    /// <summary>The ActionRevision of the crediting attack — ultimate-meter
    /// gain is capped per originating action, so the kill must name its source.</summary>
    private int lastActionId = -1;

    /// <summary>True when the killing blow (or a recent player hit inside the
    /// credit window) was player-owned; hands back the pool to pay and the
    /// originating action id for per-action meter caps.</summary>
    public bool TryKillCredit(out PlayerMana pool, out int originAction)
    {
        originAction = lastActionId;
        pool = lastPlayerHit != null && Time.time - lastPlayerHitAt <= killCreditWindow ? lastPlayerHit : null;
        return pool != null;
    }

    public void TakeDamage(float amount, Vector3 hitFrom, float poiseDamage = 10f, DamageKind kind = DamageKind.Normal,
        Component attacker = null, ReactionProfile reaction = ReactionProfile.Normal)
    {
        if (IsDead || amount <= 0f || Invulnerable) return;
        var fromPlayer = false;
        if (attacker != null && attacker.GetComponentInParent<PlayerMana>() is { } owner)
        {
            fromPlayer = true;
            lastPlayerHit = owner;
            lastPlayerHitAt = Time.time;
            lastActionId = attacker.GetComponentInParent<AttackController>() is { } ac ? ac.ActionRevision : -1;
        }
        Current = Mathf.Max(0f, Current - amount);
        flashT = hitFlashTime;
        // Player hits read through the weapon's own impact (HitFx/HitFlash, one
        // palette per weapon) — the blood splat on top crowded the screen.
        if (bloodFx != null && !fromPlayer)
        {
            // Pivot sits at capsule CENTER — chest is ~0.4 up, not 1.1 (which put
            // the splat at head-top). Spawn on the attacker-facing surface so it
            // reads as the wound, spraying away through the body.
            var away = transform.position - hitFrom;
            away.y = 0f;
            var dir = away.sqrMagnitude > 0.001f ? away.normalized : -transform.forward;
            ArtFx.SpawnAt(bloodFx, transform.position + Vector3.up * 0.4f - dir * 0.35f,
                          Quaternion.LookRotation(dir));
        }
        DamageNumberSpawner.Hit(this, amount, kind, attacker);
        Damaged?.Invoke(amount, hitFrom, poiseDamage, reaction);
        if (Current <= 0f) Die();
    }

    /// <summary>Restores full health without respawn bookkeeping (editor/tests).</summary>
    public void ResetHealth()
    {
        Current = maxHealth;
        IsDead = false;
        respawnT = -1f;
        lastPlayerHit = null;
        transform.SetPositionAndRotation(homePos, homeRot);
        if (targetable != null) targetable.enabled = true;
        if (col != null) col.enabled = true;
        flashT = 0f;
        ApplyFlash(0f);
    }

    /// <summary>Multi-phase-boss revive: comes back to life where it fell with
    /// a FRACTION of max — unlike ResetHealth there is no home-position snap.
    /// The Died event already fired for the fake death; callers gate payout.</summary>
    public void Revive(float fraction)
    {
        Current = maxHealth * Mathf.Clamp01(fraction);
        IsDead = false;
        fallT = 0f;
        respawnT = -1f;
        lastPlayerHit = null;
        if (targetable != null) targetable.enabled = true;
        if (col != null) col.enabled = true;
        flashT = 0f;
        ApplyFlash(0f);
    }

    private void Die()
    {
        IsDead = true;
        fallT = 0f;
        respawnT = stayDead ? -1f : respawnDelay;
        if (targetable != null) targetable.enabled = false; // IsTargetable drops → lock-on releases
        if (col != null) col.enabled = false;
        Died?.Invoke();
    }

    private void Update()
    {
        var dt = Time.deltaTime;
        if (flashT > 0f)
        {
            flashT = Mathf.Max(0f, flashT - Time.unscaledDeltaTime);
            ApplyFlash(flashT / hitFlashTime);
        }

        if (!IsDead) return;

        // Tip forward over ~0.45s and sink so the capsule rests on its side —
        // skipped when a skeletal Animator owns the corpse.
        if (fallT < 1f && !SuppressDeathMotion)
        {
            fallT = Mathf.Min(1f, fallT + dt / fallDuration);
            transform.rotation = homeRot * Quaternion.AngleAxis(fallT * 90f, Vector3.right);
            transform.position = homePos + Vector3.down * (fallT * 0.5f);
        }

        if (respawnT < 0f) return;
        respawnT -= dt;
        if (respawnT <= 0f) ResetHealth();
    }
}

/// <summary>Flavour of a landed hit — drives the damage numeral's colour/size.
/// Poison = the toxic art's ticks; Crit = backstabs and other heavy hits.</summary>
public enum DamageKind { Normal, Crit, Poison }
