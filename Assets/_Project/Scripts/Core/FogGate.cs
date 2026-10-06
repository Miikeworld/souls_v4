using UnityEngine;

/// <summary>
/// Souls fog-gate: a trigger volume at the arena mouth. The player crossing
/// it Engages the boss and seals the arena behind them (blocker collider +
/// fog visual on). Boss death opens the gate permanently; a dormant boss
/// (reset by death, rest or leash) reopens it — OnTriggerStay re-engages if
/// the player is still inside, so no manual re-arm bookkeeping is needed.
/// Setup: tool places a BoxCollider(isTrigger) + this, blocker = wall GO,
/// fog = optional particle/quad GO (may be null — gate still seals).
/// </summary>
/// <summary>Anything a fog gate can engage — BossGolem and BossLord both
/// implement it; the field is typed MonoBehaviour so either serializes in.</summary>
public interface IBossEngage
{
    void Engage();
    bool Engaged { get; }
}

public sealed class FogGate : MonoBehaviour
{
    [SerializeField] private MonoBehaviour boss;
    [Tooltip("Wall/collider that seals the arena while the fight is on.")]
    [SerializeField] private GameObject blocker;
    [Tooltip("Optional fog-wall visual — off once the boss is dead.")]
    [SerializeField] private GameObject fog;

    private IBossEngage Boss => boss as IBossEngage;
    private Health bossHealth;
    private void Awake() { if (boss != null) bossHealth = boss.GetComponent<Health>(); }

    private void Update()
    {
        var b = Boss;
        var sealedIn = b != null && b.Engaged;
        if (blocker != null && blocker.activeSelf != sealedIn) blocker.SetActive(sealedIn);
        if (fog != null)
        {
            // Fog shows while the boss lives — the wall is the "no way back" read.
            var show = bossHealth == null || !bossHealth.IsDead;
            if (fog.activeSelf != show) fog.SetActive(show);
        }
    }

    private void OnTriggerStay(Collider other)
    {
        var b = Boss;
        if (b == null) return;
        if (other.GetComponentInParent<PlayerLocomotion>() == null) return;
        b.Engage(); // no-ops unless dormant — Stay covers re-entry and rest-inside
    }
}
