using System.Collections.Generic;
using UnityEngine;

/// <summary>Samples the visible blade after its final socket pose. Each swing gate owns one ribbon.</summary>
[DefaultExecutionOrder(100)]
public sealed class BladeRibbon : MonoBehaviour
{
    private struct Sample
    {
        public Vector3 bladeBase, tip;
        public float born;
        public Sample(Vector3 bladeBase, Vector3 tip, float born)
        { this.bladeBase = bladeBase; this.tip = tip; this.born = born; }
    }

    private AttackController attack;
    private WeaponSocket socket;
    private Transform blade;
    private WeaponArt sampledArt;
    private WeaponSet sampledWeapon;
    private GameObject edge, ribbon;
    private Mesh mesh;
    private Material material;
    private readonly List<Sample> samples = new();
    private readonly List<Vector3> vertices = new();
    private readonly List<Color> colors = new();
    private readonly List<Vector2> uv = new();
    private readonly List<int> triangles = new();
    private readonly Vector2[] swingGate = new Vector2[1];
    private int gate = -1, actionRevision = -1, bladeRevision = -1;
    private Vector3 baseLocal, tipLocal, previousRoot, previousBase, previousTip;
    private float previousAge;
    private bool havePreviousPose;

    private void Awake() { attack = GetComponent<AttackController>(); socket = GetComponent<WeaponSocket>(); }

    public void Clear()
    {
        samples.Clear(); gate = -1; blade = null; sampledArt = null; sampledWeapon = null; havePreviousPose = false;
        actionRevision = bladeRevision = -1;
        if (mesh != null) mesh.Clear();
        ClearEdge();
    }

    private void ClearEdge()
    {
        if (edge == null) return;
        edge.SetActive(false); Destroy(edge); edge = null;
    }

    private void LateUpdate()
    {
        var art = attack != null && attack.IsAttacking ? attack.ActiveArt : null;
        var weapon = art == null && attack != null && attack.IsAttacking ? attack.ActionWeapon : null;
        // Arts carry their own trail windows; a flagged weapon set (the big
        // sword) gets the ribbon + edge on its plain swing's hit window.
        var active = (art != null && art.visualTheme == ArtVisualTheme.DarkCrimson &&
                      art.contactMode != ArtContactMode.ProjectileOnly ||
                      weapon != null && weapon.swingRibbon)
                     && socket != null && socket.ActiveBlade != null;
        if (!active) { Clear(); return; }
        var age = attack.NormalizedAge;
        var changed = blade != socket.ActiveBlade || sampledArt != art || sampledWeapon != weapon ||
                      actionRevision != attack.ActionRevision ||
                      bladeRevision != socket.BladePoseRevision || age + 0.0001f < previousAge ||
                      havePreviousPose && Vector3.Distance(previousRoot, transform.position) > 2.5f;
        if (changed)
        {
            Clear(); blade = socket.ActiveBlade; sampledArt = art; sampledWeapon = weapon;
            actionRevision = attack.ActionRevision; bladeRevision = socket.BladePoseRevision;
            if (!BladeGeometry.TryGet(blade, socket.ActiveBladeSet, out baseLocal, out tipLocal)) { Clear(); return; }
        }

        var bladeBase = blade.TransformPoint(baseLocal);
        var tip = blade.TransformPoint(tipLocal);
        Vector2[] gates;
        if (art != null)
            gates = art.trailWindows != null && art.trailWindows.Length > 0 ? art.trailWindows : art.hitWindows;
        else { swingGate[0] = attack.SwingWindow; gates = swingGate; }
        var edgePrefab = art != null ? art.bladeEdgeFx : weapon.edgeFx;
        var openGate = -1;
        if (gates != null)
        {
            for (var i = 0; i < gates.Length; i++)
            {
                var interval = gates[i];
                if (interval.y <= interval.x) continue;
                var inWindow = age >= interval.x && age <= interval.y;
                var crossed = havePreviousPose && previousAge < interval.y && age >= interval.x && age > previousAge;
                if (!inWindow && !crossed) continue;
                openGate = i;
                if (gate != i)
                {
                    samples.Clear(); ClearEdge(); gate = i;
                    EnsureMesh();
                    if (edgePrefab != null && inWindow)
                    {
                        edge = Instantiate(edgePrefab, tip, blade.rotation);
                        edge.transform.SetParent(blade, true);
                    }
                }
                if (havePreviousPose && age > previousAge)
                {
                    // Clip low-frame-rate segments to the actual opening and closing.
                    var begin = Mathf.Clamp01((interval.x - previousAge) / (age - previousAge));
                    var end = Mathf.Clamp01((interval.y - previousAge) / (age - previousAge));
                    if (samples.Count == 0)
                        AddSample(Vector3.Lerp(previousBase, bladeBase, begin), Vector3.Lerp(previousTip, tip, begin));
                    AddSample(Vector3.Lerp(previousBase, bladeBase, end), Vector3.Lerp(previousTip, tip, end));
                }
                else if (inWindow) AddSample(bladeBase, tip);
            }
        }
        if (openGate < 0 || gates != null && age > gates[openGate].y) ClearEdge();
        previousRoot = transform.position; previousAge = age;
        previousBase = bladeBase; previousTip = tip; havePreviousPose = true;

        var lifetime = art != null ? Mathf.Clamp(art.ribbonLife, 0.02f, art.artName == "Last Eclipse" ? 0.14f : 0.10f)
                                  : 0.16f; // plain swing — 0.09 was ~5 frames, invisible through the retro downscale
        while (samples.Count > 0 && (Time.time - samples[0].born > lifetime || samples.Count > 24)) samples.RemoveAt(0);
        RebuildMesh(lifetime);
    }

    private void AddSample(Vector3 bladeBase, Vector3 tip)
    {
        if (samples.Count > 0)
        {
            var prior = samples[samples.Count - 1];
            if ((prior.bladeBase - bladeBase).sqrMagnitude + (prior.tip - tip).sqrMagnitude < 0.00001f) return;
            // A discontinuity must never paint a screen-wide connecting triangle.
            if (Vector3.Distance(prior.tip, tip) > 2.5f) samples.Clear();
        }
        samples.Add(new Sample(bladeBase, tip, Time.time));
    }

    private void RebuildMesh(float lifetime)
    {
        if (mesh == null) return;
        mesh.Clear();
        if (samples.Count < 2) return;
        vertices.Clear(); colors.Clear(); uv.Clear(); triangles.Clear();
        // Arts keep the crimson theme; plain swings take the weapon's tint
        // (katana purple/white, big sword crimson).
        var crimson = sampledWeapon == null;
        var body = crimson ? new Color(0.22f, 0.006f, 0.02f) : sampledWeapon.ribbonBody;
        var tipColor = crimson ? new Color(0.88f, 0.018f, 0.055f) : sampledWeapon.ribbonTint;
        // Plain swings carry no art FX, so their smear does all the work — run it hotter.
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var fade = Mathf.Clamp01(1f - (Time.time - sample.born) / lifetime);
            // ×1.5 peak (tip alpha .55 → .82), held, then falling off.
            if (!crimson) fade = Mathf.Min(1.5f, fade * 2.4f) * Mathf.Sqrt(fade);
            // Fourth row: a thin ink band just past the tip — the swing reads as
            // a drawn stroke with an inked leading edge, like the traversal FX.
            var edge = sample.tip + (sample.tip - sample.bladeBase).normalized * 0.035f;
            vertices.Add(sample.bladeBase); vertices.Add(Vector3.Lerp(sample.bladeBase, sample.tip, 0.90f)); vertices.Add(sample.tip); vertices.Add(edge);
            colors.Add(new Color(body.r * 0.16f, body.g * 0.16f, body.b * 0.16f, fade * 0.16f));
            colors.Add(new Color(body.r, body.g, body.b, fade * 0.28f));
            colors.Add(new Color(tipColor.r, tipColor.g, tipColor.b, fade * 0.55f));
            colors.Add(new Color(0.05f, 0.02f, 0.09f, fade * 0.4f));
            uv.Add(new Vector2(0f, 0f)); uv.Add(new Vector2(0.9f, 0f)); uv.Add(new Vector2(1f, 0f)); uv.Add(new Vector2(1f, 0f));
            if (i == 0) continue;
            var row = i * 4;
            for (var strip = 0; strip < 3; strip++)
            {
                triangles.Add(row - 4 + strip); triangles.Add(row - 3 + strip); triangles.Add(row + strip);
                triangles.Add(row + strip); triangles.Add(row - 3 + strip); triangles.Add(row + 1 + strip);
            }
        }
        mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uv);
        mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
    }

    private void EnsureMesh()
    {
        if (ribbon != null) return;
        var shader = Shader.Find("NEA/BladeRibbon");
        if (shader == null) return;
        ribbon = new GameObject("Dark blade ribbon");
        mesh = new Mesh { name = "Blade ribbon" }; mesh.MarkDynamic();
        ribbon.AddComponent<MeshFilter>().sharedMesh = mesh;
        material = new Material(shader);
        ribbon.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private void OnDisable() => Clear();
    private void OnDestroy()
    { Clear(); if (ribbon != null) Destroy(ribbon); if (mesh != null) Destroy(mesh); if (material != null) Destroy(material); }
}

