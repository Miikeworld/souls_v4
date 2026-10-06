using UnityEngine;

/// <summary>
/// Souls stamina economy: dodge and swings spend fixed chunks, sprint drains
/// continuously, and the pool refills after a short delay once you stop
/// spending. Actions gate on <see cref="TrySpend"/> — empty bar means no
/// dodge, no swing, no sprint until it recovers.
/// </summary>
public sealed class PlayerStamina : MonoBehaviour
{
    // Field names changed on purpose: the scene serialized the old harsher
    // costs, and new names drop them so the cheaper defaults apply.
    [SerializeField, Min(1f)] private float maxStamina = 100f;
    [SerializeField, Min(0f)] private float dodgeStamina = 12f;
    [SerializeField, Min(0f)] private float attackStamina = 8f;
    [SerializeField, Min(0f)] private float sprintDrain = 7f;
    [SerializeField, Min(0f)] private float regenRate = 26f;
    [SerializeField, Min(0f)] private float regenHold = 0.55f;
    [Tooltip("Once the bar empties mid-sprint, sprint stays locked until this fraction of the pool has refilled — stops sprint flickering at the empty boundary.")]
    [SerializeField, Range(0f, 1f)] private float sprintResumeFraction = 0.2f;

    public float Current { get; private set; }
    public float Max => maxStamina;
    /// <summary>True while the bar is empty — gates any spend.</summary>
    public bool Exhausted => Current <= 0.001f;
    /// <summary>Sprint gate with hysteresis: latched off at empty, released once the pool refills past <see cref="sprintResumeFraction"/>.</summary>
    public bool CanSprint => FreeOutOfCombat || !sprintBlocked;
    /// <summary>Fired whenever the pool changes — HUD hooks this.</summary>
    public event System.Action<float, float> Changed;
    /// <summary>Fired when a spend is refused while armed — HUD flashes the bar.</summary>
    public event System.Action Denied;

    private float regenTimer;
    private bool sprintBlocked;
    private WeaponSocket weapon;

    private void Awake()
    {
        Current = maxStamina;
        weapon = GetComponent<WeaponSocket>();
    }

    /// <summary>Character creation: set the pool size, refill, ping the HUD.</summary>
    public void ConfigureMax(float value)
    {
        maxStamina = Mathf.Max(1f, value);
        Current = maxStamina;
        Changed?.Invoke(Current, maxStamina);
    }

    /// <summary>Drawing/previewing the sword alone must not charge traversal or practice.</summary>
    private bool FreeOutOfCombat => weapon == null || !weapon.ResourceCombat;

    /// <summary>Spends a flat action cost — false when the bar can't cover it. Free out of combat.</summary>
    public bool TrySpend(float cost)
    {
        if (FreeOutOfCombat) return true;
        if (cost > 0f && Current < cost) { Denied?.Invoke(); return false; }
        Spend(cost);
        return true;
    }

    public bool TrySpendDodge() => TrySpend(dodgeStamina);
    public bool TrySpendAttack() => TrySpend(attackStamina);
    /// <summary>Step-specific stamina cost (aerial strikes) — 0 falls back to the flat attack cost.</summary>
    public bool TrySpendAttack(float cost) => TrySpend(cost > 0f ? cost : attackStamina);

    /// <summary>Respawn/bonfire refill.</summary>
    public void Refill()
    {
        Current = maxStamina;
        regenTimer = 0f;
        sprintBlocked = false;
        Changed?.Invoke(Current, maxStamina);
    }

    /// <summary>Continuous drain while sprinting — keeps ticking the regen delay. Free out of combat.</summary>
    public void ConsumeSprint(float dt)
    {
        if (!FreeOutOfCombat) Spend(sprintDrain * dt);
    }

    private void Spend(float amount)
    {
        if (amount <= 0f) return;
        Current = Mathf.Max(0f, Current - amount);
        regenTimer = regenHold;
        Changed?.Invoke(Current, maxStamina);
    }

    private void Update()
    {
        // Sprint latch: hitting empty locks sprint until the pool recovers past
        // the resume threshold — without it the sprint state flaps on/off as the
        // bar hovers around zero, which reads as the run animation bugging out.
        if (Current <= 0.001f) sprintBlocked = true;
        else if (sprintBlocked && Current >= maxStamina * sprintResumeFraction) sprintBlocked = false;

        if (regenTimer > 0f)
        {
            regenTimer -= Time.deltaTime;
            return;
        }
        if (Current >= maxStamina) return;
        Current = Mathf.Min(maxStamina, Current + regenRate * Time.deltaTime);
        Changed?.Invoke(Current, maxStamina);
    }
}
