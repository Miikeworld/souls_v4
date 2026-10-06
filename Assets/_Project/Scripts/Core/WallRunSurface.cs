using UnityEngine;

/// <summary>Marks a surface the player may wall-run along. WallRunController's
/// DetectWall skips every collider that isn't under one of these — crates,
/// parapets and pillar faces can't start a run.</summary>
public sealed class WallRunSurface : MonoBehaviour
{
    /// <summary>0..1 Core charge (the Warden arena's purple flare). A charged
    /// wall holds the runner up — the sink relaxes and the run cap stretches —
    /// so "purple = the suit can use this" is true mechanically, not just in colour.</summary>
    public float Charge { get; set; }
}
