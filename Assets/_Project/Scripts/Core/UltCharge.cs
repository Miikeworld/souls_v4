using UnityEngine;

/// <summary>The earned ultimate meter (detail.md §223): 100 capacity, filled
/// only by fighting — +3 per successful normal action, +4 per technique/art,
/// +4 per trash kill and +8 per elite kill. At most 12 from any single
/// originating action so a multi-window spin or projectile wave can't farm it.
/// No passive gain, no decay; death/rest/load reset it. The dedicated ult
/// input spends the full 100 — Last Eclipse never touches mana.</summary>
public sealed class UltCharge : MonoBehaviour
{
    public const float Max = 100f;
    /// <summary>Charge ceiling for one originating action (swing, art, projectile volley).</summary>
    public const float PerActionCap = 12f;

    public float Current { get; private set; }
    public bool Full => Current >= Max;
    /// <summary>(current, max) — the HUD's meter fill.</summary>
    public event System.Action<float, float> Changed;
    /// <summary>The ult was cast (the meter spent) — the HUD's orb burst + impact frames.</summary>
    public event System.Action Spent;

    private PlayerState state;
    private int originAction = -1;
    private float originGain;

    private void Awake()
    {
        state = GetComponent<PlayerState>();
        if (state == null) state = GetComponentInParent<PlayerState>();
    }

    /// <summary>Credit charge attributed to one action. `origin` is the
    /// AttackController's ActionRevision (or -1 for unattributed sources) —
    /// a new action resets the per-action cap bookkeeping.</summary>
    public void Credit(int origin, float amount)
    {
        if (amount <= 0f || Full) return;
        if (origin != originAction) { originAction = origin; originGain = 0f; }
        var take = Mathf.Min(amount, PerActionCap - originGain);
        if (take <= 0f) return;
        originGain += take;
        Current = Mathf.Min(Max, Current + take);
        Changed?.Invoke(Current, Max);
    }

    /// <summary>The dedicated-input spend: all-or-nothing, exactly once.</summary>
    public bool TrySpend()
    {
        if (!Full) return false;
        Current = 0f;
        originAction = -1;
        originGain = 0f;
        Changed?.Invoke(Current, Max);
        Spent?.Invoke();
        return true;
    }

    /// <summary>Deny flash reuses the mana pool's refused-spend channel.</summary>
    public void Deny() => GetComponent<PlayerMana>()?.Deny();

    /// <summary>Death and checkpoint rests zero the meter (detail §223).</summary>
    public void Reset()
    {
        if (Current <= 0f) return;
        Current = 0f;
        Changed?.Invoke(Current, Max);
    }

    private void Update()
    {
        if ((state != null && state.IsDead) || GameLoop.IsResting) Reset();
    }
}
