using UnityEngine;

/// <summary>
/// Pure grounding math, separated from FootGrounding so the edit-mode
/// Grounding Self-Test can run it without an Animator update.
///
/// Model: the body gets a single pelvis offset and each foot can be pulled to
/// its local floor by foot IK. The pelvis correction is computed by the caller
/// from the deeper foot's calibrated sole bottom (ankle − MeasureSoleOffset)
/// vs the floor under it — the whole-mesh bottom is only a fallback, since a
/// coat tail or cape can dip below the feet and planting THAT floats them.
/// </summary>
public static class GroundingSolver
{
    /// <summary>Ground probe: sphere-cast straight down from just above the
    /// foot bone to find the floor under this foot. Ignores anything under the
    /// owner's physics root so the capsule/body never self-hits. Returns the
    /// floor Y, plus the hit normal and distance for foot planting.</summary>
    public static float ProbeFootGround(Vector3 footPos, Transform physicsRoot, Transform visualRoot,
                                        out float distance, out Vector3 normal)
    {
        distance = float.MaxValue;
        normal = Vector3.up;
        var origin = footPos + Vector3.up * 0.6f;
        var hits = Physics.SphereCastAll(origin, 0.12f, Vector3.down, 1.6f, ~0, QueryTriggerInteraction.Ignore);
        // No floor found = no floor info: MaxValue keeps the miss out of the
        // min() and out of foot IK. Returning footPos.y fakes a floor at
        // ANKLE height, which parks the body sole-offset too high = float.
        var best = float.MaxValue;
        foreach (var hit in hits)
        {
            var c = hit.collider;
            if (c == null) continue;
            var root = c.transform.root;
            if (root == physicsRoot || root == visualRoot) continue;
            // Actors aren't floor: a foot probe landing on a nearby enemy's
            // capsule reads its dome as ground a metre up and lifts the pelvis —
            // the attack-lunge float.
            if (c.GetComponentInParent<Health>() != null) continue;
            if (hit.distance < distance)
            {
                distance = hit.distance;
                normal = hit.normal;
                best = hit.point.y;
            }
        }
        return best;
    }

    /// <summary>Ankle-to-sole distance measured off the baked skinned mesh:
    /// the lowest vertex inside a 12.5cm vertical column around the ankle.
    /// Spatial, not weight-driven — dominant-bone tests under-read (blended
    /// verts skipped), total-influence over-reads (cuff hems counted), and an
    /// authored avatar value can be wrong for the retargeted rig. A trouser
    /// hem has to hang through the ankle column to lie, and if it does it is
    /// genuinely at sole level anyway. Returns -1 when nothing qualifies.</summary>
    public static float MeasureSoleOffset(SkinnedMeshRenderer[] smrs, Transform ankle)
    {
        if (ankle == null) return -1f;
        var o = ankle.position;
        var low = float.MaxValue;
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
            var mesh = new Mesh();
            smr.BakeMesh(mesh);
            var t = smr.transform;
            foreach (var v in mesh.vertices)
            {
                var w = t.TransformPoint(v);
                var dx = w.x - o.x; var dz = w.z - o.z;
                if (dx * dx + dz * dz < 0.015625f && w.y < low) low = w.y; // r = 0.125m
            }
            Object.DestroyImmediate(mesh);
        }
        return low < float.MaxValue ? o.y - low : -1f;
    }

    /// <summary>Lowest vertex inside a vertical column around a foot — the
    /// mesh that must not sink through the floor. A swept kick leg or coat
    /// tail far from the ankles doesn't count. MaxValue when nothing is in
    /// the column.</summary>
    public static float MeasureFootLowest(SkinnedMeshRenderer[] smrs, Transform foot, float radius)
    {
        if (foot == null) return float.MaxValue;
        var o = foot.position;
        var r2 = radius * radius;
        var low = float.MaxValue;
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
            var mesh = new Mesh();
            smr.BakeMesh(mesh);
            var t = smr.transform;
            foreach (var v in mesh.vertices)
            {
                var w = t.TransformPoint(v);
                var dx = w.x - o.x; var dz = w.z - o.z;
                if (dx * dx + dz * dz < r2 && w.y < low) low = w.y;
            }
            Object.DestroyImmediate(mesh);
        }
        return low;
    }

    /// <summary>Lowest vertex of the whole skinned body (corpse grounding).</summary>
    public static float MeasureBodyLowest(SkinnedMeshRenderer[] smrs)
    {
        var low = float.MaxValue;
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
            var mesh = new Mesh();
            smr.BakeMesh(mesh);
            var t = smr.transform;
            foreach (var v in mesh.vertices)
            {
                var wy = t.TransformPoint(v).y;
                if (wy < low) low = wy;
            }
            Object.DestroyImmediate(mesh);
        }
        return low;
    }
}
