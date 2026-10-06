using UnityEngine;

/// <summary>
/// Core Energy — the usable output of the protagonist's Violet Core (the
/// player-facing name; the class keeps its old Mana name so scenes and saves
/// stay wired). Arts pay with <see cref="TrySpend"/>; Overdrive regenerates it
/// fast while running (see CrimsonInstability).
/// </summary>
public sealed class PlayerMana : MonoBehaviour
{
    [SerializeField, Min(1f)] private float maxMana = 50f;

    public float Current { get; private set; }
    public float Max => maxMana;
    public event System.Action<float, float> Changed; // (current, max)
    /// <summary>Refused a spend (or a denied cast) — the HUD's deny flash.</summary>
    public event System.Action Denied;
    /// <summary>A discrete paid cast — the conduit's energy-flow pulse. Drain never fires it.</summary>
    public event System.Action<float> Spent;

    private void Awake()
    {
        Current = maxMana;
    }

    /// <summary>Character creation: set the pool size, refill, ping the HUD.</summary>
    public void ConfigureMax(float value)
    {
        maxMana = Mathf.Max(1f, value);
        Current = maxMana;
        Changed?.Invoke(Current, maxMana);
    }

    /// <summary>Spend if affordable. Future spell/skill costs call this.</summary>
    public bool TrySpend(float cost)
    {
        if (cost > Current) { Denied?.Invoke(); return false; }
        Current -= cost;
        if (cost > 0f) Spent?.Invoke(cost);
        Changed?.Invoke(Current, maxMana);
        return true;
    }

    /// <summary>Continuous bleed: clamps at zero, never
    /// denies. Returns what was actually taken.</summary>
    public float Drain(float amount)
    {
        if (amount <= 0f || Current <= 0f) return 0f;
        var take = Mathf.Min(amount, Current);
        Current -= take;
        Changed?.Invoke(Current, maxMana);
        return take;
    }

    /// <summary>Deny flash for refusals that never reach a spend (cooldown, context).</summary>
    public void Deny() => Denied?.Invoke();

    /// <summary>Actual points granted (after the cap) — the HUD's kill-mana pulse.</summary>
    public event System.Action<float> Restored;

    /// <summary>Gives mana back (clamped) — refunds, flask/checkpoint recovery, kill rewards.</summary>
    public void Restore(float amount)
    {
        if (amount <= 0f) return;
        var before = Current;
        Current = Mathf.Min(maxMana, Current + amount);
        if (Current > before) Restored?.Invoke(Current - before);
        Changed?.Invoke(Current, maxMana);
    }

    /// <summary>Respawn/checkpoint refill.</summary>
    public void Refill()
    {
        Current = maxMana;
        Changed?.Invoke(Current, maxMana);
    }

}
