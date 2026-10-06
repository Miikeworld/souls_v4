using UnityEngine;

/// <summary>
/// Marks a GameObject as a valid lock-on target. The aim point is where the
/// lock-on marker draws and where camera/marker math aims — defaults to a
/// chest-height point above the pivot when no explicit transform is set.
/// Requires a Collider on this object or a child so OverlapSphere can find it.
/// </summary>
public sealed class Targetable : MonoBehaviour
{
    [Tooltip("Transform the lock-on camera/marker aims at. Defaults to a point aimHeight above this object.")]
    [SerializeField] private Transform aimPoint;
    [Tooltip("False while the target should be ignored by lock-on (dead, inactive, cutscene, etc.).")]
    [SerializeField] private bool isTargetable = true;
    [Tooltip("Chest-height fallback used when no explicit aim point is set.")]
    [SerializeField, Min(0f)] private float aimHeight = 1.4f;

    public bool IsTargetable => isTargetable && isActiveAndEnabled;

    public Transform AimTransform => aimPoint;

    private Collider rootCollider;

    private void Awake()
    {
        // Root collider only — children can be animated hitboxes that move with limbs.
        rootCollider = GetComponent<Collider>();
    }

    /// <summary>
    /// Aim point for camera/marker math. Explicit transform wins; otherwise the
    /// root collider's centre (colliders ride the root, so this stays stable under
    /// skeletal animation — skinned-mesh bounds jitter every frame), then a
    /// chest-height point above the pivot.
    /// </summary>
    public Vector3 AimPosition
    {
        get
        {
            if (aimPoint != null) return aimPoint.position;
            if (rootCollider != null) return rootCollider.bounds.center;
            return transform.position + Vector3.up * aimHeight;
        }
    }
}
