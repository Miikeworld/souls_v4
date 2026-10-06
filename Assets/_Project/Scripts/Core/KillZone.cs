using UnityEngine;

/// <summary>
/// Bottomless-pit volume — the player falling in takes lethal damage and the
/// normal death/respawn flow owns the rest (souls drop where they fell).
/// </summary>
public sealed class KillZone : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        var hp = other.GetComponentInParent<PlayerHealth>();
        if (hp == null || hp.IsDead) return;
        hp.TakeDamage(9999f, transform.position);
    }
}
