using System.Collections.Generic;
using UnityEngine;

/// <summary>Swept skill contacts: victim owners deduplicated, first solid obstacle clips travel.</summary>
public sealed class ArtProjectile : MonoBehaviour
{
    private ProjectileSpec spec;
    private Vector3 direction;
    private Transform owner, target;
    private float poise, distance;
    private readonly HashSet<Health> hit = new();
    public static void Launch(ProjectileSpec spec, Vector3 origin, Vector3 forward, Transform target, Transform owner, float poiseDamage, Color? tint = null)
    {
        for (var i = 0; i < Mathf.Max(1, spec.count); i++)
        {
            var dir = Quaternion.AngleAxis((i - (Mathf.Max(1, spec.count) - 1) * 0.5f) * spec.spreadDeg, Vector3.up) * forward.normalized;
            var go = spec.prefab != null ? Instantiate(spec.prefab, origin, Quaternion.LookRotation(dir)) : new GameObject("ArtProjectile");
            if (tint.HasValue) ArtFx.ForceTint(go, tint.Value);
            go.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(dir));
            var p = go.AddComponent<ArtProjectile>(); p.spec = spec; p.direction = dir; p.owner = owner; p.target = target; p.poise = poiseDamage;
            Destroy(go, spec.life);
            if (spec.wave && spec.prefab == null)
            {
                var visual = new GameObject("Dark blade wave"); visual.transform.SetParent(go.transform, false);
                var vertices = new List<Vector3>(); var colors = new List<Color>(); var triangles = new List<int>();
                for (var column = 0; column < 17; column++)
                {
                    var x = column / 16f * 2f - 1f;
                    for (var row = 0; row < 5; row++)
                    {
                        var y = (row / 4f * 2f - 1f) * (1f - 0.45f * Mathf.Abs(x));
                        vertices.Add(new Vector3(x * spec.waveSize.x * 0.5f, y * spec.waveSize.y * 0.5f, 0.15f * x * x));
                        colors.Add(row == 0 || row == 4 ? new Color(1f, 0.012f, 0.055f, 0.85f) : new Color(0.018f, 0.003f, 0.007f, 0.8f));
                        if (column > 0 && row > 0)
                        { var j = column * 5 + row; triangles.AddRange(new[] { j - 6, j - 5, j, j - 6, j, j - 1 }); }
                    }
                }
                var mesh = new Mesh(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
                visual.AddComponent<MeshFilter>().sharedMesh = mesh; p.ownedMesh = mesh;
                var mat = new Material(Shader.Find("NEA/BladeRibbon"));
                visual.AddComponent<MeshRenderer>().sharedMaterial = mat; p.ownedMaterial = mat;
            }
        }
    }
    private Material ownedMaterial;
    private Mesh ownedMesh;
    private bool Ignore(Collider c) => c.transform.IsChildOf(transform) || (owner != null && c.transform.IsChildOf(owner));
    private void Update()
    {
        if (owner == null || owner.GetComponent<PlayerState>() is { IsDead: true }) { Destroy(gameObject); return; }
        if (spec.homing && target != null)
            direction = Vector3.RotateTowards(direction, (target.position + Vector3.up - transform.position).normalized, 9.42f * Time.deltaTime, 0f);
        var travel = spec.speed * Time.deltaTime;
        if (spec.maxTravel > 0f) travel = Mathf.Min(travel, spec.maxTravel - distance);
        if (travel <= 0f) { if (spec.maxTravel > 0f && distance >= spec.maxTravel) Destroy(gameObject); return; }
        var origin = transform.position;
        var rotation = Quaternion.LookRotation(direction);
        var extent = new Vector3(spec.waveSize.x * 0.5f, spec.waveSize.y * 0.5f, 0.06f);
        var casts = spec.wave ? Physics.BoxCastAll(origin, extent, direction, rotation, travel, ~0, QueryTriggerInteraction.Ignore)
            : Physics.SphereCastAll(origin, spec.radius, direction, travel, ~0, QueryTriggerInteraction.Ignore);
        var overlaps = spec.wave ? Physics.OverlapBox(origin, extent, rotation, ~0, QueryTriggerInteraction.Ignore)
            : Physics.OverlapSphere(origin, spec.radius, ~0, QueryTriggerInteraction.Ignore);
        var stop = travel;
        var blocked = false;
        foreach (var c in overlaps) if (!Ignore(c) && c.GetComponentInParent<Health>() == null) { stop = 0f; blocked = true; }
        foreach (var c in casts) if (!Ignore(c.collider) && c.collider.GetComponentInParent<Health>() == null)
        { stop = Mathf.Min(stop, c.distance); blocked = true; }
        // Resolve obstruction before victims, independent of physics result ordering.
        var victims = new List<(Collider collider, float at)>();
        foreach (var c in overlaps) victims.Add((c, 0f));
        foreach (var c in casts) victims.Add((c.collider, c.distance));
        victims.Sort((a, b) => a.at.CompareTo(b.at));
        foreach (var v in victims)
        {
            if (v.at >= stop && blocked || Ignore(v.collider)) continue;
            var health = v.collider.GetComponentInParent<Health>();
            if (health == null || health.IsDead || !hit.Add(health)) continue;
            health.TakeDamage(spec.damage, origin + direction * v.at, poise, DamageKind.Normal, owner);
            HitFx.Spawn(health.transform.position + Vector3.up, direction, 1.2f,
                owner != null && owner.GetComponent<WeaponSocket>() is { Set: { } set } ? set.impactTint : (Color?)null);
            if (!spec.pierce) { Destroy(gameObject); return; }
        }
        transform.SetPositionAndRotation(origin + direction * stop, rotation); distance += stop;
        if (blocked || (spec.maxTravel > 0f && distance >= spec.maxTravel)) Destroy(gameObject);
    }
    private void OnDestroy() { if (ownedMaterial != null) Destroy(ownedMaterial); if (ownedMesh != null) Destroy(ownedMesh); }
}
