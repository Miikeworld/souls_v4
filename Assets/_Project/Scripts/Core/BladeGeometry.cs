using UnityEngine;

/// <summary>Blade endpoints in weapon-root coordinates, shared by ribbons and pose diagnostics.</summary>
public static class BladeGeometry
{
    public static bool TryGet(Transform blade, WeaponSet set, out Vector3 bladeBase, out Vector3 tip)
    {
        bladeBase = tip = Vector3.zero;
        if (blade == null) return false;
        if (set != null && set.bladeGeometryCalibrated && (set.bladeTipLocal - set.bladeBaseLocal).sqrMagnitude > 0.001f)
        { bladeBase = set.bladeBaseLocal; tip = set.bladeTipLocal; return true; }
        return TryMeasure(blade, out bladeBase, out tip, out _);
    }

    /// <summary>Uses explicit markers when present; otherwise measures the imported weapon's long axis.</summary>
    public static bool TryMeasure(Transform blade, out Vector3 bladeBase, out Vector3 tip, out Vector3 axis)
    {
        bladeBase = tip = Vector3.zero; axis = Vector3.forward;
        if (blade == null) return false;
        Transform baseMarker = null, tipMarker = null;
        foreach (var child in blade.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "BladeBase") baseMarker = child;
            else if (child.name == "BladeTip") tipMarker = child;
        }
        if (baseMarker != null && tipMarker != null)
        {
            bladeBase = blade.InverseTransformPoint(baseMarker.position);
            tip = blade.InverseTransformPoint(tipMarker.position);
            axis = (tip - bladeBase).normalized;
            return (tip - bladeBase).sqrMagnitude > 0.001f;
        }

        var bounds = new Bounds();
        var found = false;
        foreach (var filter in blade.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            var localBounds = filter.sharedMesh.bounds;
            if (localBounds.size.sqrMagnitude < 0.000001f) continue;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = localBounds.center + Vector3.Scale(localBounds.extents,
                    new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                point = blade.InverseTransformPoint(filter.transform.TransformPoint(point));
                if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                else bounds.Encapsulate(point);
            }
        }
        if (!found) return false;
        var size = bounds.size;
        axis = size.y > size.x && size.y > size.z ? Vector3.up : size.x > size.z ? Vector3.right : Vector3.forward;
        var minimum = Vector3.Dot(bounds.min, axis);
        var maximum = Vector3.Dot(bounds.max, axis);
        var tipDistance = Mathf.Abs(maximum) >= Mathf.Abs(minimum) ? maximum : minimum;
        var gripDistance = Mathf.Clamp(0f, minimum, maximum);
        // The origin is the grip. Exclude the handle and guard from the ribbon's inner edge.
        var centre = bounds.center - axis * Vector3.Dot(bounds.center, axis);
        bladeBase = centre + axis * Mathf.Lerp(gripDistance, tipDistance, 0.18f);
        tip = centre + axis * Mathf.Lerp(gripDistance, tipDistance, 0.98f);
        axis = (tip - bladeBase).normalized;
        return (tip - bladeBase).sqrMagnitude > 0.001f;
    }
}
