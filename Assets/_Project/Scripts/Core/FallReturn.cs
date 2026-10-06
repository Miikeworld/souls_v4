using UnityEngine;

/// <summary>
/// Traversal safety net (Titanfall-style): a missed wall-run that drops into
/// the chasm returns the player to <see cref="returnPoint"/> with a small
/// health cost instead of a death, so the parkour stays fun to retry. Lethal
/// pits elsewhere still use <see cref="KillZone"/>.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public sealed class FallReturn : MonoBehaviour
{
    [SerializeField] private Transform returnPoint;
    [SerializeField, Range(0f, 0.5f)] private float damageFraction = 0.1f;

    private void Awake() => GetComponent<BoxCollider>().isTrigger = true;

    private void OnTriggerEnter(Collider other)
    {
        var hp = other.GetComponentInParent<PlayerHealth>();
        if (hp == null || hp.IsDead || returnPoint == null) return;
        var cc = hp.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        hp.transform.SetPositionAndRotation(returnPoint.position, returnPoint.rotation);
        if (cc != null) cc.enabled = true;
        hp.GetComponent<PlayerLocomotion>()?.ResumeVerticalMotion(0f);
        GameHud.Flash(0.1f);
        if (damageFraction > 0f) hp.TakeDamage(hp.Max * damageFraction, returnPoint.position);
    }
}
