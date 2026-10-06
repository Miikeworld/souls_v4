using UnityEngine;

/// <summary>
/// Crimson Instability — the greatsword's contained Crimson Core. Builds
/// whenever a <see cref="WeaponSet.crimsonCore"/> blade does the work: the
/// equipped greatsword, or the big sword summoned by a big-sword skill (RMB
/// techniques) even while the katana is equipped. Each successful action
/// builds it once (light swing / heavier art, plunge or chain finisher), and
/// it bleeds away after a quiet spell. At full it fires Overdrive by itself:
/// damage/poise scale up while the unleashed Violet Core runs flat out —
/// Core Energy regenerates fast instead of draining, so the pool is no
/// concern while it lasts. Duration expiry is a clean shutdown; if the pool
/// ever does hit zero it's a discharge: the blade vents, goes dark for a
/// lockout, and the instability resets. No self-damage.
/// Feeds the HUD as an <see cref="IHudGauge"/> (the screen-edge arcs in
/// <see cref="OverdriveEdge"/>) and drives the blade's own emissive cracks,
/// glow and leaks.
/// </summary>
public sealed class CrimsonInstability : MonoBehaviour, IHudGauge
{
    [Header("Build-up")]
    [SerializeField, Min(1f)] private float maxInstability = 100f;
    [Tooltip("Per successful plain swing (once per action, however many victims).")]
    [SerializeField, Min(0f)] private float lightGain = 8f;
    [Tooltip("Per successful heavy action — arts/techniques, plunges, the chain finisher.")]
    [SerializeField, Min(0f)] private float heavyGain = 16f;
    [Tooltip("Seconds without a gain before instability starts bleeding off.")]
    [SerializeField, Min(0f)] private float decayDelay = 4f;
    [Tooltip("Instability lost per second once decay starts.")]
    [SerializeField, Min(0f)] private float decaySpeed = 6f;

    [Header("Overdrive (auto-fires at full instability)")]
    [SerializeField, Min(0.5f)] private float overdriveDuration = 12f;
    [Tooltip("Core Energy regenerated per second while Overdrive runs — the unleashed Violet Core runs flat out.")]
    [SerializeField, Min(0f)] private float coreRegenPerSecond = 20f;
    [SerializeField, Min(1f)] private float overdriveDamageScale = 1.35f;
    [SerializeField, Min(1f)] private float overdrivePoiseScale = 1.6f;

    [Header("Shutdown / discharge")]
    [Tooltip("Seconds the blade stays vented (no build-up) after Core Energy ran out mid-Overdrive.")]
    [SerializeField, Min(0f)] private float dischargeLockout = 3f;
    [Tooltip("Instability left (fraction of max) after Overdrive runs its full duration.")]
    [SerializeField, Range(0f, 1f)] private float instabilityAfterOverdrive;
    [Tooltip("Instability left (fraction of max) after a Core Energy discharge.")]
    [SerializeField, Range(0f, 1f)] private float instabilityAfterDischarge;

    [Header("Blade feedback")]
    [SerializeField] private Color crimson = new Color(1f, 0.05f, 0.07f);
    [SerializeField, Min(0f)] private float glowRange = 2.4f;
    [Tooltip("Blade size multiplier while Overdrive runs — the sword itself grows instead of the FX getting bigger. Visual and reach.")]
    [SerializeField, Range(1f, 1.6f)] private float overdriveBladeScale = 1.25f;
    [Tooltip("HDR multiplier on ember/mote colour so bloom picks them up.")]
    [SerializeField, Min(1f)] private float particleGlow = 3f;

    public float Instability { get; private set; }
    public float Normalized => Instability / maxInstability;
    public bool Overdrive { get; private set; }
    public bool Ready => !Overdrive && Instability >= maxInstability - 0.01f;
    public bool Discharging => lockoutT > 0f;
    /// <summary>Overdrive empowers crimson contacts only — katana normals stay 1×.</summary>
    public float DamageScale => Overdrive && CrimsonInHand ? overdriveDamageScale : 1f;
    public float PoiseScale => Overdrive && CrimsonInHand ? overdrivePoiseScale : 1f;
    /// <summary>The greatsword is the equipped weapon.</summary>
    public bool Equipped => testEquipped || (socket != null && socket.Set != null && socket.Set.crimsonCore);
    /// <summary>A crimson blade is doing the work right now: the equipped
    /// greatsword, the summoned big sword, or any running big-sword skill.</summary>
    public bool CrimsonInHand => Equipped
        || (socket != null && socket.ActiveBladeSet != null && socket.ActiveBladeSet.crimsonCore)
        || (attack != null && attack.ActiveArt != null && attack.ActiveArt.BigSwordSkill);
    /// <summary>Self-check hook: treat the crimson set as equipped without a WeaponSocket.</summary>
    [System.NonSerialized] public bool testEquipped;

    // HUD contract — the red containment layer reads these. With the katana
    // it shows while big-sword skills have left instability in the core.
    public bool GaugeVisible => Equipped || CrimsonInHand || Instability > 0f || Overdrive || Discharging;
    public float Gauge01 => Overdrive ? overdriveT / overdriveDuration : Normalized;
    public bool GaugeReady => Ready;
    public bool GaugeActive => Overdrive;
    public bool GaugeFault => Discharging;
    public float GaugeFlare => flare;

    private static readonly int EmissionStrength = Shader.PropertyToID("_EmissionStrength");

    private AttackController attack;
    private WeaponSocket socket;
    private PlayerMana mana;
    private PlayerHealth health;
    private float sinceGain, overdriveT, lockoutT, flare, leakT;
    private int creditedAction = -1;
    private bool bound;

    private GameObject bladeFor;
    private Renderer[] bladeRenderers;
    private float[] bladeBase;
    private Light bladeLight;
    private MaterialPropertyBlock mpb;

    private void Bind()
    {
        if (bound) return;
        bound = true;
        attack = GetComponent<AttackController>();
        socket = GetComponent<WeaponSocket>();
        mana = GetComponent<PlayerMana>();
        health = GetComponent<PlayerHealth>();
    }

    private void Awake() => Bind();

    private void OnEnable()
    {
        Bind();
        if (attack != null) attack.ContactLanded += OnContact;
        if (health != null) health.Died += OnDied;
    }

    private void OnDisable()
    {
        if (attack != null) attack.ContactLanded -= OnContact;
        if (health != null) health.Died -= OnDied;
        if (socket != null) socket.CrimsonScale = 1f;
        CacheBlade(null);
    }

    private void Update() => Tick(Time.deltaTime);

    private void OnContact(Health victim, DamageKind kind, bool dive)
    {
        if (!CrimsonInHand || Overdrive || Discharging || attack == null) return;
        if (attack.ActionRevision == creditedAction) return; // once per action — a crowd spin can't farm it
        creditedAction = attack.ActionRevision;
        var set = attack.ActionWeapon;
        var finisher = set != null && set.attacks != null && set.attacks.Length > 0
                       && attack.ActiveArt == null && attack.ComboBranch >= set.attacks.Length;
        Add(dive || attack.ActiveArt != null || finisher ? heavyGain : lightGain);
    }

    /// <summary>Raise instability; crossing full flares the HUD/blade once.</summary>
    public void Add(float amount)
    {
        if (amount <= 0f || Overdrive || Discharging) return;
        var wasReady = Ready;
        Instability = Mathf.Min(maxInstability, Instability + amount);
        sinceGain = 0f;
        if (wasReady || !Ready) return;
        flare = 1f;
        Burst(1.6f);
    }

    /// <summary>Ignite Overdrive — Tick fires it the moment the gauge fills
    /// (self-checks also call it directly). Needs full instability, a live
    /// wielder and a spark of Core Energy to light the fuse.</summary>
    public bool TryActivate()
    {
        Bind();
        if (!Ready || mana == null || mana.Current <= 0f || (health != null && health.IsDead))
        {
            mana?.Deny();
            return false;
        }
        Overdrive = true;
        overdriveT = overdriveDuration;
        flare = 1f;
        Burst(2.2f);
        if (Application.isPlaying) SfxBank.Play("crimson.overdrive", BladePoint(0.5f));
        return true;
    }

    public void Tick(float dt)
    {
        Bind();
        flare = Mathf.Max(0f, flare - dt * 2.5f);
        if (lockoutT > 0f) lockoutT -= dt;
        if (Overdrive)
        {
            // Survives a weapon swap: with the katana it keeps empowering
            // big-sword skills. Core Energy regenerates the whole time.
            overdriveT -= dt;
            if (mana == null || mana.Current <= 0f) Discharge();
            else
            {
                mana.Restore(coreRegenPerSecond * dt);
                if (overdriveT <= 0f) EndOverdrive();
            }
        }
        else
        {
            // A full gauge ignites itself — an empty pool just can't light the fuse.
            if (Ready && !Discharging && mana != null && mana.Current > 0f) TryActivate();
            if (!Overdrive && Instability > 0f)
            {
                sinceGain += dt;
                if (sinceGain >= decayDelay) Instability = Mathf.Max(0f, Instability - decaySpeed * dt);
            }
        }
        if (Application.isPlaying) { TickBlade(dt); TickAura(dt); TickGrowth(dt); }
    }

    // ---------- blade growth (Overdrive) ----------

    private float growK, growPop;
    private bool growWas;

    private void TickGrowth(float dt)
    {
        if (socket == null) return;
        if (Overdrive && !growWas) growPop = 1f; // ignition: a quick overshoot, like the blade surging
        growWas = Overdrive;
        growK = Mathf.MoveTowards(growK, Overdrive ? 1f : 0f, dt * (Overdrive ? 4f : 3f));
        growPop = Mathf.Max(0f, growPop - dt * 3f);
        var breath = Overdrive ? 0.015f * Mathf.Sin(Time.time * 6f) : 0f;
        socket.CrimsonScale = 1f + (overdriveBladeScale - 1f) * growK + 0.12f * growPop * Mathf.Sin(growPop * Mathf.PI) + breath;
    }

    // ---------- embers (small, glowing; replaces HitFx's big smeared sparks) ----------

    private ParticleSystem embers;

    private void BuildEmbers()
    {
        var go = new GameObject("CrimsonEmbers");
        go.transform.SetParent(transform, false);
        embers = go.AddComponent<ParticleSystem>();
        embers.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = embers.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 400;
        main.gravityModifier = 0.15f;
        var em = embers.emission;
        em.enabled = false;
        var shape = embers.shape;
        shape.enabled = false;
        var col = embers.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var size = embers.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));
        go.GetComponent<ParticleSystemRenderer>().material = GlowMaterial();
        embers.Play();
    }

    /// <summary>Sprites/Default (unlit, vertex colour) with an HDR tint so
    /// the colour lands above PostPulse's bloom threshold — the glow.</summary>
    private Material GlowMaterial()
    {
        var m = new Material(Shader.Find("Sprites/Default"));
        m.SetColor("_Color", Color.white * particleGlow);
        return m;
    }

    private void Ember(Vector3 p, Vector3 v, float size, float life, bool hot)
    {
        if (embers == null) BuildEmbers();
        embers.Emit(new ParticleSystem.EmitParams
        {
            position = p,
            velocity = v,
            startSize = size,
            startLifetime = life,
            startColor = hot ? Color.Lerp(crimson, Color.white, 0.45f) : crimson,
            applyShapeToPosition = false,
        }, 1);
    }

    // ---------- body aura (Overdrive only) ----------
    // The suit stays purple: a violet key light in front (the Core working
    // flat out), a crimson rim light behind (the blade's force pressing in),
    // and mixed violet/crimson motes rising off the body. Nothing recolours
    // the character's own materials.

    private static readonly Vector3 CoreLightOffset = new Vector3(0f, 1.25f, 0.55f);
    private static readonly Vector3 RimLightOffset = new Vector3(0f, 1.45f, -0.6f);
    private Light auraCore, auraRim;
    private Transform auraFacing;
    private ParticleSystem auraMotes;
    private float auraK, auraFlash;
    private bool auraWas;

    private void TickAura(float dt)
    {
        if (auraCore == null) BuildAura();
        if (Overdrive && !auraWas) { auraFlash = 1f; auraMotes.Emit(40); }
        if (!Overdrive && auraWas && Discharging) { auraFlash = 1f; auraMotes.Emit(25); }
        auraWas = Overdrive;
        auraK = Mathf.MoveTowards(auraK, Overdrive ? 1f : 0f, dt * (Overdrive ? 4f : 2.5f));
        auraFlash = Mathf.Max(0f, auraFlash - dt * 3f);

        var t = Time.time;
        var breath = 0.75f + 0.25f * Mathf.Sin(t * 5.5f);
        var flick = Mathf.PerlinNoise(t * 10f, 0.81f);
        auraCore.intensity = auraK * 1.8f * breath + auraFlash * 2.5f;
        auraRim.intensity = auraK * (1.6f + 1.2f * flick) + auraFlash * (Discharging ? 4f : 1.5f);
        auraCore.enabled = auraCore.intensity > 0.01f;
        auraRim.enabled = auraRim.intensity > 0.01f;
        // Placed by the visual's facing in world metres (rig scale can differ).
        var face = auraFacing != null ? auraFacing.rotation : transform.rotation;
        auraCore.transform.position = transform.position + face * CoreLightOffset;
        auraRim.transform.position = transform.position + face * RimLightOffset;
        var em = auraMotes.emission;
        em.rateOverTime = 30f * auraK;
    }

    private void BuildAura()
    {
        var anim = GetComponentInChildren<Animator>();
        auraFacing = anim != null ? anim.transform : transform;
        auraCore = AuraLight(transform, "OverdriveCoreLight", CoreConduit.Main, 3f);
        auraRim = AuraLight(transform, "OverdriveRimLight", crimson, 3.2f);

        var go = new GameObject("OverdriveMotes");
        go.transform.SetParent(transform, false);
        auraMotes = go.AddComponent<ParticleSystem>();
        auraMotes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = auraMotes.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.45f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.035f);
        main.gravityModifier = -0.25f; // motes rise off the suit
        main.maxParticles = 200;
        main.startColor = new ParticleSystem.MinMaxGradient(crimson, CoreConduit.Bright);
        var shape = auraMotes.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.position = new Vector3(0f, 0.95f, 0f);
        shape.scale = new Vector3(0.55f, 1.5f, 0.55f);
        var col = auraMotes.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var em = auraMotes.emission;
        em.rateOverTime = 0f;
        go.GetComponent<ParticleSystemRenderer>().material = GlowMaterial();
        auraMotes.Play();
    }

    private static Light AuraLight(Transform parent, string name, Color color, float range)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.range = range;
        l.intensity = 0f;
        l.shadows = LightShadows.None;
        l.enabled = false;
        return l;
    }

    private void EndOverdrive()
    {
        Overdrive = false;
        Instability = maxInstability * instabilityAfterOverdrive;
        sinceGain = 0f;
    }

    /// <summary>Core Energy ran dry: the suit can't hold the blade — vent, go dark, reset.</summary>
    private void Discharge()
    {
        Overdrive = false;
        Instability = maxInstability * instabilityAfterDischarge;
        sinceGain = 0f;
        lockoutT = dischargeLockout;
        flare = 1f;
        Burst(3f);
        if (Application.isPlaying) SfxBank.Play("crimson.discharge", BladePoint(0.5f));
    }

    private void OnDied()
    {
        Overdrive = false;
        Instability = 0f;
        lockoutT = 0f;
    }

    // ---------- blade feedback ----------

    private void TickBlade(float dt)
    {
        var w = CrimsonBlade();
        if (w != bladeFor) CacheBlade(w);
        if (w == null) return;

        var t = Time.time;
        var k = Overdrive ? 1f : Normalized;
        // Emission gain over the painted crack mask: faint → building → hot
        // pulse when ready → ragged flicker in Overdrive → near-dark after a
        // discharge, warming back up as the lockout ends.
        float gain, glow, leaks;
        if (Discharging)
        {
            var back = 1f - lockoutT / Mathf.Max(0.01f, dischargeLockout);
            gain = Mathf.Lerp(0.1f, 0.7f, back); glow = 0f; leaks = 0f;
        }
        else if (Overdrive)
        {
            var n = Mathf.PerlinNoise(t * 9f, 0.37f);
            gain = 6.5f + 1.8f * n; glow = 5.5f + 2f * n; leaks = 22f; // small embers, so more of them

        }
        else if (Ready)
        {
            var p = 0.5f + 0.5f * Mathf.Sin(t * 7f);
            gain = 2.6f + 0.5f * p; glow = 1.2f + 0.4f * p; leaks = 4f;
        }
        else
        {
            gain = 0.7f + 1.7f * k * k;
            glow = k > 0.3f ? (k - 0.3f) / 0.7f : 0f;
            leaks = k > 0.7f ? 2.5f : k > 0.3f ? 1f : 0f;
        }
        gain += flare * 1.5f;
        glow += flare * 2f;

        for (var i = 0; i < bladeRenderers.Length; i++)
        {
            var r = bladeRenderers[i];
            if (r == null || bladeBase[i] < 0f) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetFloat(EmissionStrength, bladeBase[i] * gain);
            r.SetPropertyBlock(mpb);
        }
        if (bladeLight != null)
        {
            bladeLight.intensity = glow;
            bladeLight.range = glowRange * (Overdrive ? 1.6f : 1f);
        }

        leakT += dt * leaks;
        for (; leakT >= 1f; leakT -= 1f)
            Ember(BladePoint(Random.value), Vector3.up * Random.Range(0.2f, 0.7f) + Random.insideUnitSphere * 0.35f,
                Random.Range(0.015f, 0.03f), Random.Range(0.3f, 0.6f), Random.value < 0.3f);
    }

    private void CacheBlade(GameObject w)
    {
        if (bladeFor != null && bladeRenderers != null)
            foreach (var r in bladeRenderers) if (r != null) r.SetPropertyBlock(null);
        if (bladeLight != null) Destroy(bladeLight.gameObject);
        bladeLight = null;
        bladeRenderers = null;
        bladeFor = w;
        if (w == null) return;

        mpb ??= new MaterialPropertyBlock();
        var list = new System.Collections.Generic.List<Renderer>();
        foreach (var r in w.GetComponentsInChildren<Renderer>(true))
            if (r is MeshRenderer || r is SkinnedMeshRenderer) list.Add(r);
        bladeRenderers = list.ToArray();
        bladeBase = new float[bladeRenderers.Length];
        for (var i = 0; i < bladeRenderers.Length; i++)
        {
            var m = bladeRenderers[i].sharedMaterial;
            bladeBase[i] = m != null && m.HasProperty(EmissionStrength) ? m.GetFloat(EmissionStrength) : -1f;
        }

        var go = new GameObject("CrimsonGlow");
        go.transform.SetParent(w.transform, false);
        go.transform.position = BladePoint(0.55f);
        bladeLight = go.AddComponent<Light>();
        bladeLight.type = LightType.Point;
        bladeLight.color = crimson;
        bladeLight.range = glowRange;
        bladeLight.intensity = 0f;
        bladeLight.shadows = LightShadows.None;
    }

    /// <summary>The crimson blade in the hand — the summoned big sword during a
    /// skill (it outranks the equipped weapon), else the equipped greatsword.</summary>
    private GameObject CrimsonBlade()
    {
        if (socket == null || socket.ActiveBlade == null || socket.ActiveBladeSet == null
            || !socket.ActiveBladeSet.crimsonCore) return null;
        return socket.ActiveBlade.gameObject;
    }

    /// <summary>World point along the blade, 0 = base, 1 = tip.</summary>
    private Vector3 BladePoint(float f)
    {
        var w = CrimsonBlade();
        if (w == null) return transform.position + Vector3.up;
        return BladeGeometry.TryGet(w.transform, socket.ActiveBladeSet, out var b, out var tip)
            ? w.transform.TransformPoint(Vector3.Lerp(b, tip, f))
            : w.transform.position;
    }

    private void Burst(float strength)
    {
        if (!Application.isPlaying || CrimsonBlade() == null) return;
        var count = Mathf.RoundToInt(12f * strength);
        for (var i = 0; i < count; i++)
            Ember(BladePoint(Random.value), Random.onUnitSphere * Random.Range(1f, 2.5f) * Mathf.Sqrt(strength),
                Random.Range(0.02f, 0.04f), Random.Range(0.25f, 0.45f), Random.value < 0.4f);
    }
}
