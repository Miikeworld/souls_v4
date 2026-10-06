using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Pooled floating damage numerals on the HUD's world layer — pixel digits from
/// HudArt, so they stay crisp like the rest of the UI (no world-space canvas).
/// Spawned by Health.TakeDamage. Juicy profile: overshoot pop, rise, gravity arc
/// down, fade; lateral drift so stacked hits fan out; size scales with damage,
/// colour by DamageKind. `skin` supports the reference's two looks (plain vs.
/// diamond-framed numerals) — Plain is the project default.
/// </summary>
public sealed class DamageNumberSpawner : MonoBehaviour
{
    public enum Skin { Plain, Diamond }

    [SerializeField] private Skin skin = Skin.Plain;
    [SerializeField, Min(4)] private int poolSize = 24;
    [SerializeField, Min(0.3f)] private float lifeTime = 0.95f;
    [Tooltip("Initial upward drift (UI px/s) — quick pop up.")]
    [SerializeField] private float riseSpeed = 90f;
    [Tooltip("Downward pull (UI px/s²) — strong, so the numeral visibly drops after the float.")]
    [SerializeField] private float gravity = 250f;
    [Tooltip("Random lateral kick range (UI px/s) so simultaneous hits spread out.")]
    [SerializeField] private float drift = 42f;

    private sealed class Entry
    {
        public RectTransform root;
        public CanvasGroup group;
        public Image[] digits = new Image[4];
        public RectTransform frame;
        public Vector3 world;
        public Vector2 vel;
        public float age;
        public float size;
        public bool active;
    }

    private static DamageNumberSpawner instance;
    private Entry[] pool;
    private int next;

    // Rapid-fire aggregation (detail §207): hits on the same victim from the
    // same originating action inside 0.12s merge into one numeral — a crowd
    // spin reads as one weighty number per pass, not a confetti of digits.
    private const float AggregateWindow = 0.12f;
    private sealed class Pending
    {
        public Health victim;
        public Component attacker;
        public int action;
        public float amount;
        public DamageKind kind;
        public Vector3 pos;
        public float deadline;
    }
    private readonly System.Collections.Generic.List<Pending> pending = new();

    /// <summary>Called from Health.TakeDamage — the victim positions the numeral.
    /// `attacker` lets a weapon tint the digits (big sword crimson, fists dull).</summary>
    public static void Hit(Health victim, float amount, DamageKind kind, Component attacker = null)
    {
        if (victim == null || amount <= 0f || !Application.isPlaying) return; // edit-mode damage (self-checks) spawns no numerals
        var t = victim.GetComponent<Targetable>();
        // Spawn above the head, not at the aim chest — numerals rise from the
        // collider's top so they never cross the enemy's face.
        var col = victim.GetComponent<Collider>();
        var aim = t != null ? t.AimPosition : victim.transform.position + Vector3.up * 1.35f;
        var pos = new Vector3(aim.x, (col != null ? col.bounds.max.y : aim.y + 0.8f) + 0.12f, aim.z);
        var ac = attacker != null ? attacker.GetComponent<AttackController>() : null;
        Ensure().Enqueue(pos, victim, attacker, ac != null ? ac.ActionRevision : -1, amount, kind);
    }

    private void Enqueue(Vector3 pos, Health victim, Component attacker, int action, float amount, DamageKind kind)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            if (p.victim != victim || p.attacker != attacker || p.action != action) continue;
            // Same victim + same action inside the window: accumulate and
            // re-position at the newest contact. Crit survives the merge.
            p.amount += amount;
            p.pos = pos;
            if (kind == DamageKind.Crit) p.kind = kind;
            return;
        }
        pending.Add(new Pending
        {
            victim = victim, attacker = attacker, action = action,
            amount = amount, kind = kind, pos = pos,
            deadline = Time.unscaledTime + AggregateWindow,
        });
    }

    private void FlushPending()
    {
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var p = pending[i];
            if (Time.unscaledTime < p.deadline) continue;
            pending.RemoveAt(i);
            var tint = p.attacker != null ? p.attacker.GetComponent<AttackController>()?.NumeralTint : null;
            Spawn(p.pos, p.amount, p.kind, tint);
        }
    }

    private static DamageNumberSpawner Ensure()
    {
        if (instance == null)
        {
            instance = FindFirstObjectByType<DamageNumberSpawner>();
            if (instance == null)
                instance = new GameObject("DamageNumbers").AddComponent<DamageNumberSpawner>();
        }
        if (instance.pool == null) instance.Build();
        return instance;
    }

    private void Build()
    {
        var host = GameHud.WorldLayer;
        pool = new Entry[poolSize];
        for (var i = 0; i < pool.Length; i++)
        {
            var e = new Entry();
            // Center anchor — ScreenToLocal returns pivot-relative points, so an
            // anchored (0,0) root lands half a layer down-left of every hit.
            e.root = PersonaUi.Box(host, "Dmg" + i, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(120f, 40f));
            e.root.pivot = new Vector2(0.5f, 0.5f);
            e.group = e.root.gameObject.AddComponent<CanvasGroup>();
            e.group.alpha = 0f;

            // Diamond skin: the existing Key frame rotated 45° — same ink/wood
            // frame family, reads as the reference's diamond-framed numerals.
            var frt = PersonaUi.Box(e.root, "Frame", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64f, 64f));
            HudArt.Build(frt, "F", HudArt.Kind.Key);
            frt.localRotation = Quaternion.Euler(0f, 0f, 45f);
            e.frame = frt;

            for (var d = 0; d < e.digits.Length; d++)
            {
                var rt = PersonaUi.Box(e.root, "d" + d, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                e.digits[d] = rt.gameObject.AddComponent<Image>();
                e.digits[d].raycastTarget = false;
            }
            e.root.gameObject.SetActive(false);
            pool[i] = e;
        }
    }

    private void Spawn(Vector3 world, float amount, DamageKind kind, Color? tint)
    {
        var e = pool[next];
        next = (next + 1) % pool.Length;
        e.world = world;
        e.age = 0f;
        e.active = true;
        e.vel = new Vector2(Random.Range(-drift, drift), riseSpeed * Random.Range(0.85f, 1.15f));
        // Damage-driven spread — a chip hit stays small, a heavy punish lands
        // chunky. ±10% jitter keeps repeats organic.
        e.size = Mathf.Clamp(0.85f + amount / 42f, 0.85f, 2.4f) * Random.Range(0.9f, 1.1f);

        // Crit/poison keep their class colour; a weapon tint only repaints
        // normal hits so a red big-sword number still can't read as a crit.
        var color = kind switch
        {
            DamageKind.Crit => HudArt.Amber,
            DamageKind.Poison => HudArt.Toxic,
            _ => tint ?? PersonaUi.Bone,
        };
        if (kind == DamageKind.Crit) e.size *= 1.25f;

        var text = Mathf.Clamp(Mathf.RoundToInt(amount), 0, 9999).ToString();
        var count = text.Length;
        // Digits are ~5×7px sprites; size them ×T like every HudArt element and
        // pitch by the real sprite width so multi-digit numbers sit as one word.
        var dw = HudArt.Digit(0, true).rect.width;
        var pitch = dw * HudArt.T * 0.82f;
        var left = -(count - 1) * pitch * 0.5f;
        for (var d = 0; d < e.digits.Length; d++)
        {
            var img = e.digits[d];
            var on = d < count;
            img.enabled = on;
            if (!on) continue;
            img.sprite = HudArt.Digit(text[d] - '0', outlined: true);
            img.color = color;
            var rt = (RectTransform)img.transform;
            rt.sizeDelta = img.sprite.rect.size * HudArt.T;
            rt.anchoredPosition = new Vector2(left + d * pitch, 0f);
        }
        var framed = skin == Skin.Diamond;
        e.frame.gameObject.SetActive(framed);
        if (framed)
            e.frame.sizeDelta = new Vector2(count * pitch + HudArt.T * 5f, HudArt.T * 14f);
        e.root.gameObject.SetActive(true);
    }

    private void Update()
    {
        if (pool == null) return;
        FlushPending();
        var dt = Time.unscaledDeltaTime;
        foreach (var e in pool)
        {
            if (!e.active) continue;
            e.age += dt;
            var k = e.age / lifeTime;
            if (k >= 1f)
            {
                e.active = false;
                e.root.gameObject.SetActive(false);
                continue;
            }
            if (!GameHud.WorldToLocal(e.world, out var local))
            {
                e.group.alpha = 0f;
                continue; // behind the camera — keep timing so it dies on schedule
            }
            // Rise then fall under gravity; pop overshoot in, fade tail out.
            var x = local.x + e.vel.x * e.age;
            var y = local.y + e.vel.y * e.age - 0.5f * gravity * e.age * e.age;
            e.root.anchoredPosition = new Vector2(x, y);
            var pop = e.age < 0.11f ? Mathf.Lerp(0.3f, 1.3f, e.age / 0.11f)
                        : 1f + 0.3f * Mathf.Exp(-(e.age - 0.11f) * 14f);
            // Half-step quantize — fractional scale on point-filtered digits makes
            // uneven texels that read as blur. The rest state lands on an integer.
            var s = Mathf.Max(1.5f, Mathf.Round(e.size * pop * 2f) / 2f);
            e.root.localScale = Vector3.one * s;
            e.group.alpha = k < 0.55f ? 1f : 1f - (k - 0.55f) / 0.45f;
        }
    }
}
