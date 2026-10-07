using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Dresses the Core Sanctum (built by <see cref="ProjectRestartWarden"/>) in the same
/// Synty PolygonDungeon kit as the ruin level that leads to it, so the final fight
/// stops reading as grey blockout: tiled floors, kit wall modules, round pillars,
/// braziers with fire, statues and candles at the throne, banners and chains on the
/// standing walls, crimson Corestone gem clusters for the sconces and the throne
/// crystal, rubble and rock platforms for the floating debris, a tiled causeway
/// with a broken railing.
/// Gameplay is untouched: every collider, piece root and role stays procedural
/// (vendor colliders are disabled). Each piece's kit art is parented under that
/// piece's "Visual" transform, so it tears, floats, topples and falls with it; the
/// procedural "Surface" meshes are hidden (walls, pillars, stones) or lowered 4 cm
/// under the tiles as the stone rim (floors — and the flood overlay keys off them).
/// The Corestone veins and the purple wall-run marks are lifted clear of the kit so
/// the fight's colour language stays on top. A missing prefab skips that piece's
/// dressing (the procedural look remains) and is reported.
/// </summary>
internal static class ProjectRestartWardenDress
{
    private const string Kit = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs";
    private const float FloorLower = 0.04f;

    private enum Align { Centre, Bottom, Top }

    private static readonly Dictionary<string, string> paths = new Dictionary<string, string>();
    private static readonly HashSet<string> missing = new HashSet<string>();
    private static System.Random rng;
    private static int placed;
    private static Material crimsonGem;

    /// <summary>Dress every sanctum piece under <paramref name="root"/>. Returns a report line.</summary>
    public static string Dress(Transform root, Material crimson, float platformR, float outerR, float wallR, float wallH, float wallT,
                               float slabLen, float doorW, float pillarR)
    {
        paths.Clear();
        missing.Clear();
        placed = 0;
        rng = new System.Random(2027);
        crimsonGem = crimson;

        DressCentralFloor(root, platformR);
        for (var k = 0; k < 8; k++) DressWedge(root, k, platformR, outerR);
        DressWalls(root, wallH, wallT, slabLen, doorW);
        for (var k = 0; k < 8; k++) DressPillar(root, k);
        DressDebris(root);
        DressThrone(root);
        DressSconces(root);
        DressApproach(root);

        var styled = ProjectRestartMechanicalEnvironment.StyleTree(root.gameObject);
        return $"Sanctum dressed with the PolygonDungeon kit: {placed} kit pieces, {styled} renderers given the Mechanical Environment pass"
               + (missing.Count > 0 ? ". Missing prefabs (procedural kept there): " + string.Join(", ", missing) : ".");
    }

    // ------------------------------------------------------------------ floors

    private static readonly string[] Tiles = { "SM_Env_Tiles_01", "SM_Env_Tiles_02", "SM_Env_Tiles_03", "SM_Env_Tiles_04", "SM_Env_Tiles_05",
                                               "SM_Env_Tiles_06", "SM_Env_Tiles_07", "SM_Env_Tiles_08", "SM_Env_Tiles_09" };

    private static string TileFor(float r)
    {
        if (r < 2.6f) return rng.Next(2) == 0 ? "SM_Env_Tiles_Ornate_01" : "SM_Env_Tiles_Ornate_02";
        if (Mathf.Abs(r - 6.6f) < 1.3f && rng.Next(3) == 0) return "SM_Env_Tiles_Rune_0" + (1 + rng.Next(5));
        return Tiles[rng.Next(Tiles.Length)];
    }

    /// <summary>The central disc: 2.5 m tiles wherever a whole tile fits; the lowered
    /// procedural stone shows as the rim around them.</summary>
    private static void DressCentralFloor(Transform root, float platformR)
    {
        var piece = root.Find("CentralFloor");
        var vis = piece != null ? piece.Find("Visual") : null;
        if (vis == null) return;
        LowerSurface(vis);
        LiftStrips(vis, 0.02f, 0f);
        const float cell = 2.5f;
        var art = Group(vis, "Kit floor");
        for (var x = -platformR + cell * 0.5f; x < platformR; x += cell)
        for (var z = -platformR + cell * 0.5f; z < platformR; z += cell)
        {
            if (!SquareInside(x, z, cell * 0.5f, platformR - 0.15f)) continue;
            Box(art, TileFor(new Vector2(x, z).magnitude), new Vector3(x, 0f, z), new Vector3(cell, float.NaN, cell), rng.Next(4) * 90f, Align.Top, false);
        }
    }

    private static bool SquareInside(float x, float z, float half, float r)
    {
        for (var i = 0; i < 4; i++)
        {
            var cx = x + ((i & 1) == 0 ? -half : half);
            var cz = z + ((i & 2) == 0 ? -half : half);
            if (cx * cx + cz * cz > r * r) return false;
        }
        return true;
    }

    /// <summary>An outer ring section: two rows of tangential tiles, a brazier on the
    /// sections that survive (they fall or float with the section).</summary>
    private static void DressWedge(Transform root, int k, float platformR, float outerR)
    {
        var piece = root.Find("Ring_" + k);
        var vis = piece != null ? piece.Find("Visual") : null;
        if (vis == null) return;
        LowerSurface(vis);
        LiftStrips(vis, 0.02f, 0f);
        var pivot = piece.localPosition;
        var az = k * 45f;
        var art = Group(vis, "Kit floor");
        const float cell = 2.4f;
        foreach (var r in new[] { platformR + 1.35f, platformR + 3.85f })
        {
            var arcLen = 2f * Mathf.PI * r * (40f / 360f);
            var n = Mathf.Max(1, Mathf.FloorToInt(arcLen / cell));
            for (var i = 0; i < n; i++)
            {
                var a = az - 20f + 40f * (i + 0.5f) / n;
                var d = Az(a);
                var at = new Vector3(d.x * r, 0f, d.y * r) - pivot;
                if (k == 0 && r > platformR + 2f && Mathf.Abs(a - az) < 12f) continue; // under the throne dais
                Box(art, Tiles[rng.Next(Tiles.Length)], at, new Vector3(cell * 0.98f, float.NaN, cell * 0.98f), a, Align.Top, false);
            }
        }
        // A brazier near the wall on every other section; static sections keep theirs.
        if (k % 2 == 0 && k != 0)
        {
            var d = Az(az + 14f);
            var at = new Vector3(d.x * (outerR - 1.1f), 0f, d.y * (outerR - 1.1f)) - pivot;
            var brazier = Box(art, "SM_Prop_Brazier_01", at, new Vector3(float.NaN, 1.2f, float.NaN), 0f, Align.Bottom, false);
            if (brazier != null) Fire(art, at + Vector3.up * 1.15f);
        }
    }

    // ------------------------------------------------------------------ walls & pillars

    private static readonly string[] WallModules = { "SM_Env_Wall_01", "SM_Env_Wall_02", "SM_Env_Wall_03", "SM_Env_Wall_04", "SM_Env_Wall_05" };

    private static void DressWalls(Transform root, float wallH, float wallT, float slabLen, float doorW)
    {
        foreach (Transform piece in root)
        {
            var name = piece.name;
            if (!name.StartsWith("Wall")) continue;
            var vis = piece.Find("Visual");
            if (vis == null) continue;
            var box = piece.GetComponent<BoxCollider>();
            var len = box != null ? box.size.x : slabLen;
            HideSurface(vis);
            // Cracks / run marks sit on the inner face: push them clear of the kit modules.
            LiftStrips(vis, 0f, name.StartsWith("WallSlab") ? 0.09f : 0.06f);
            var art = Group(vis, "Kit wall");
            // Two tiers of modules, about four metres each, inner face flush with the collider.
            var tiers = 2;
            var tierH = wallH / tiers;
            var n = Mathf.Max(1, Mathf.RoundToInt(len / 4f));
            var mw = len / n;
            for (var t = 0; t < tiers; t++)
            for (var i = 0; i < n; i++)
            {
                var x = -len * 0.5f + mw * (i + 0.5f);
                var y = -wallH * 0.5f + tierH * t;
                var module = WallModules[rng.Next(WallModules.Length)];
                Box(art, module, new Vector3(x, y, 0f), new Vector3(mw * 1.01f, tierH * 1.01f, wallT), 0f, Align.Bottom, true);
            }
            if (name.StartsWith("Wall_Door"))
            {
                // A heavy jamb on the doorway side.
                var side = name.EndsWith("L") ? 1f : -1f;
                Box(art, "SM_Env_Pillar_Square_02", new Vector3(side * len * 0.5f, -wallH * 0.5f, 0f), new Vector3(1.5f, wallH + 0.3f, 1.5f), 0f, Align.Bottom, false);
            }
            else if (!name.StartsWith("WallSlab"))
            {
                // Standing walls: banners and chains — the old court's colours, torn.
                var face = wallT * 0.5f + 0.12f;
                Box(art, "SM_Prop_Wall_Banner_0" + (1 + rng.Next(6)), new Vector3(-len * 0.28f, wallH * 0.5f - 1.2f, face), new Vector3(float.NaN, 3.6f, float.NaN), 180f, Align.Top, false);
                Box(art, "SM_Prop_Wall_Banner_0" + (1 + rng.Next(6)), new Vector3(len * 0.28f, wallH * 0.5f - 1.2f, face), new Vector3(float.NaN, 3.6f, float.NaN), 180f, Align.Top, false);
                Box(art, "SM_Prop_Chain_0" + (1 + rng.Next(8)), new Vector3(0f, wallH * 0.5f - 0.4f, face), new Vector3(float.NaN, 2.6f, float.NaN), 0f, Align.Top, false);
            }
        }
    }

    private static void DressPillar(Transform root, int k)
    {
        var piece = root.Find("Pillar_" + k);
        var vis = piece != null ? piece.Find("Visual") : null;
        if (vis == null) return;
        var cap = piece.GetComponent<CapsuleCollider>();
        var h = cap != null ? cap.height : 8f;
        HideSurface(vis);
        // The Corestone spiral rides just outside the round kit shaft.
        foreach (var r in vis.GetComponentsInChildren<MeshRenderer>(true))
            if (r.name.StartsWith("Corestone")) r.transform.localScale = new Vector3(1.08f, 1f, 1.08f);
        var art = Group(vis, "Kit pillar");
        var broken = h < 7f;
        var name = broken ? (rng.Next(2) == 0 ? "SM_Env_Pillar_Broken_01" : "SM_Env_Pillar_Broken_02")
                          : "SM_Env_Pillar_Round_0" + (1 + rng.Next(3));
        Box(art, name, Vector3.zero, new Vector3(1.45f, h, 1.45f), rng.Next(4) * 90f, Align.Bottom, false);
        if (broken) Box(art, "SM_Env_Pillar_Broken_Pile_0" + (1 + rng.Next(2)), new Vector3(0.9f, 0f, 0.4f), new Vector3(2f, float.NaN, 2f), rng.Next(360), Align.Bottom, false);
    }

    // ------------------------------------------------------------------ debris

    private static readonly string[] Stones = { "SM_Env_Rock_Round_01", "SM_Env_Rock_Round_02", "SM_Env_Rock_Round_03", "SM_Env_Rock_Round_04",
                                                "SM_Env_Brick_Rubble_01", "SM_Env_Brick_Rubble_02", "SM_Env_Brick_Rubble_03" };

    private static void DressDebris(Transform root)
    {
        foreach (Transform piece in root)
        {
            var stone = piece.name.StartsWith("Stone_");
            var chunk = piece.name.StartsWith("Chunk_");
            if (!stone && !chunk) continue;
            var vis = piece.Find("Visual");
            if (vis == null) continue;
            HideSurface(vis);
            var art = Group(vis, "Kit stone");
            // The procedural rock is ~1 unit across in its own (scaled) frame.
            if (chunk)
                Box(art, "SM_Env_Rock_Flat_Platform_0" + (1 + rng.Next(6)), Vector3.zero, new Vector3(1.05f, 1f, 1.05f), 0f, Align.Centre, false);
            else
                Box(art, Stones[rng.Next(Stones.Length)], Vector3.up * -0.3f, new Vector3(1f, float.NaN, 1f), rng.Next(4) * 90f, Align.Bottom, false);
        }
    }

    // ------------------------------------------------------------------ throne & sconces

    private static void DressThrone(Transform root)
    {
        var wedge = root.Find("Ring_0");
        var vis = wedge != null ? wedge.Find("Visual") : null;
        if (vis == null) return;
        var crystal = vis.Find("Corestone crystal");
        var art = Group(vis, "Kit throne");
        var pivot = wedge.localPosition;
        var throne = new Vector3(0f, 0f, 13.3f) - pivot;
        // Statues of the old kings either side, candles on the dais steps.
        Box(art, "SM_Env_Statue_0" + (1 + rng.Next(4)), throne + new Vector3(-3.4f, 0f, 0.4f), new Vector3(float.NaN, 3.4f, float.NaN), 160f, Align.Bottom, false);
        Box(art, "SM_Env_Statue_0" + (1 + rng.Next(4)), throne + new Vector3(3.4f, 0f, 0.4f), new Vector3(float.NaN, 3.4f, float.NaN), 200f, Align.Bottom, false);
        for (var i = 0; i < 5; i++)
        {
            var d = Az(-60f + 30f * i);
            // On the floor just outside the dais (radius 2.4), facing the arena.
            Box(art, "SM_Prop_Candles_0" + (1 + rng.Next(4)), throne + new Vector3(d.x, 0f, -Mathf.Abs(d.y)) * 2.8f, new Vector3(float.NaN, 0.5f, float.NaN), rng.Next(360), Align.Bottom, false);
        }
        // The sanctum's Core: kit gem spikes in Corestone crimson instead of the procedural cluster.
        if (crystal != null)
        {
            var gems = Group(vis, "Kit corestone");
            var behind = throne + new Vector3(0f, 0.7f, 1.6f);
            var any = false;
            for (var i = 0; i < 5; i++)
            {
                var o = new Vector3((float)(rng.NextDouble() - 0.5) * 1.6f, 0f, (float)(rng.NextDouble() - 0.5) * 0.8f);
                var g = Box(gems, i % 2 == 0 ? "SM_Env_Gem_Large_0" + (1 + rng.Next(2)) : "SM_Env_Gem_Spike_0" + (1 + rng.Next(2)),
                            behind + o, new Vector3(float.NaN, 1.6f + (float)rng.NextDouble() * 2f, float.NaN), rng.Next(360), Align.Bottom, false);
                if (g != null) { Crimson(g); any = true; }
            }
            if (any) foreach (var r in crystal.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = false;
        }
    }

    private static void DressSconces(Transform root)
    {
        foreach (Transform t in root)
        {
            if (!t.name.StartsWith("Sconce_")) continue;
            var r = t.GetComponent<MeshRenderer>();
            var mf = t.GetComponent<MeshFilter>();
            if (r == null || mf == null || mf.sharedMesh == null) continue;
            var at = mf.sharedMesh.bounds.center;
            var g = Box(t, "SM_Env_Gem_Spike_0" + (1 + rng.Next(2)), at + Vector3.down * 0.45f, new Vector3(float.NaN, 1f, float.NaN), rng.Next(360), Align.Bottom, false);
            if (g == null) continue;
            Crimson(g);
            r.enabled = false;
        }
    }

    // ------------------------------------------------------------------ approach

    private static void DressApproach(Transform root)
    {
        var entrance = root.Find("Entrance landing");
        if (entrance != null) TileBox(entrance);
        var deck = root.Find("Causeway");
        if (deck != null) TileBox(deck);
        var court = root.Find("Forecourt");
        if (court != null)
        {
            var vis = court.Find("Visual");
            var mc = court.GetComponent<MeshCollider>();
            if (vis != null && mc != null && mc.sharedMesh != null)
            {
                LowerSurface(vis);
                LiftStrips(vis, 0.02f, 0f);
                var b = mc.sharedMesh.bounds;
                var art = Group(vis, "Kit floor");
                const float cell = 2.5f;
                for (var x = b.min.x + cell * 0.5f + 1.2f; x < b.max.x - 1.2f; x += cell)
                for (var z = b.min.z + cell * 0.5f + 1.2f; z < b.max.z - 1.2f; z += cell)
                    Box(art, Tiles[rng.Next(Tiles.Length)], new Vector3(x, 0f, z), new Vector3(cell, float.NaN, cell), rng.Next(4) * 90f, Align.Top, false);
                var rubble = vis.Find("Rubble");
                if (rubble != null)
                {
                    // Rubble heaped in the corners, clear of the walkway (visual only, as before).
                    foreach (var r in rubble.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = false;
                    var centre = new Vector3(b.center.x, 0f, b.center.z);
                    foreach (var corner in new[] { new Vector3(b.min.x, 0f, b.min.z), new Vector3(b.min.x, 0f, b.max.z), new Vector3(b.max.x, 0f, b.max.z), new Vector3(b.max.x, 0f, b.min.z) })
                    {
                        var inward = (centre - corner).normalized;
                        for (var i = 0; i < 2; i++)
                        {
                            var p = corner + inward * (2.4f + (float)rng.NextDouble() * 1.6f) + Vector3.Cross(Vector3.up, inward) * ((float)rng.NextDouble() - 0.5f) * 1.6f;
                            Box(art, Stones[rng.Next(Stones.Length)], p, new Vector3(1.1f, float.NaN, 1.1f), rng.Next(360), Align.Bottom, false);
                        }
                    }
                }
            }
        }
        // Railing: kit posts along the old balustrade line.
        var bal = root.Find("Causeway balustrade");
        if (bal != null && deck != null)
        {
            var box = deck.GetComponent<BoxCollider>();
            var mf = bal.GetComponentInChildren<MeshFilter>();
            if (box != null && mf != null)
            {
                foreach (var r in bal.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = false;
                var art = Group(bal, "Kit railing");
                var len = box.size.z;
                var halfW = box.size.x * 0.5f - 0.3f;
                var z0 = deck.localPosition.z - len * 0.5f + 1.2f;
                for (var z = z0; z < deck.localPosition.z + len * 0.5f - 0.8f; z += 3f)
                    foreach (var sgn in new[] { -1f, 1f })
                    {
                        var broken = rng.NextDouble() < 0.35;
                        var name = broken ? "SM_Env_Railing_Broken_0" + (1 + rng.Next(4)) : "SM_Env_Railing_0" + (1 + rng.Next(4));
                        Box(art, name, new Vector3(sgn * halfW, 0f, z + 1.5f), new Vector3(3f, float.NaN, 0.4f), 90f, Align.Bottom, true);
                    }
            }
        }
        var floaters = root.Find("Floating stones");
        if (floaters != null)
        {
            var mf = floaters.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                foreach (var r in floaters.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = false;
                var b = mf.sharedMesh.bounds;
                var art = Group(floaters, "Kit floaters");
                for (var i = 0; i < 10; i++)
                {
                    var p = new Vector3(Mathf.Lerp(b.min.x, b.max.x, (float)rng.NextDouble()), Mathf.Lerp(b.min.y, b.max.y, (float)rng.NextDouble()),
                                        Mathf.Lerp(b.min.z, b.max.z, (float)rng.NextDouble()));
                    var s = 0.8f + (float)rng.NextDouble() * 1.8f;
                    Box(art, "SM_Env_Rock_Flat_Large_0" + (1 + rng.Next(4)), p, new Vector3(s * 1.4f, float.NaN, s), rng.Next(360), Align.Centre, false);
                }
            }
        }
    }

    /// <summary>A box-collider slab (entrance landing, causeway): kit tiles across its top.</summary>
    private static void TileBox(Transform piece)
    {
        var box = piece.GetComponent<BoxCollider>();
        var vis = piece.Find("Visual");
        if (box == null || vis == null) return;
        LowerSurface(vis);
        LiftStrips(vis, 0.02f, 0f);
        var art = Group(vis, "Kit floor");
        var top = box.center.y + box.size.y * 0.5f;
        var nx = Mathf.Max(1, Mathf.RoundToInt(box.size.x / 3f));
        var nz = Mathf.Max(1, Mathf.RoundToInt(box.size.z / 3f));
        var cx = box.size.x / nx;
        var cz = box.size.z / nz;
        for (var i = 0; i < nx; i++)
        for (var j = 0; j < nz; j++)
        {
            var p = new Vector3(-box.size.x * 0.5f + cx * (i + 0.5f), top, -box.size.z * 0.5f + cz * (j + 0.5f));
            Box(art, Tiles[rng.Next(Tiles.Length)], p, new Vector3(cx, float.NaN, cz), rng.Next(2) * 180f, Align.Top, false);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void HideSurface(Transform vis)
    {
        var s = vis.Find("Surface");
        if (s != null && s.TryGetComponent<MeshRenderer>(out var r)) r.enabled = false;
    }

    /// <summary>The procedural floor drops a few centimetres under the kit tiles (it stays
    /// as the stone rim where no whole tile fits, and the flood overlay keys off it).</summary>
    private static void LowerSurface(Transform vis)
    {
        var s = vis.Find("Surface");
        if (s != null) s.localPosition = new Vector3(s.localPosition.x, -FloorLower, s.localPosition.z);
    }

    /// <summary>Corestone veins / run marks / cracks: lift them clear of the kit art.</summary>
    private static void LiftStrips(Transform vis, float up, float forward)
    {
        foreach (var r in vis.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!r.name.StartsWith("Corestone")) continue;
            r.transform.localPosition += new Vector3(0f, up, forward);
        }
    }

    private static Transform Group(Transform parent, string name)
    {
        var old = parent.Find(name);
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var g = new GameObject(name).transform;
        g.SetParent(parent, false);
        return g;
    }

    private static void Crimson(GameObject go)
    {
        if (crimsonGem == null) return;
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            r.sharedMaterials = Enumerable.Repeat(crimsonGem, r.sharedMaterials.Length).ToArray();
    }

    private static void Fire(Transform parent, Vector3 at)
    {
        var path = Resolve("FX_Fire");
        if (path == null) return;
        var fx = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), parent);
        fx.transform.localPosition = at;
        fx.transform.localScale = Vector3.one * 0.8f;
        var lgo = new GameObject("Brazier light");
        lgo.transform.SetParent(parent, false);
        lgo.transform.localPosition = at + Vector3.up * 0.6f;
        var l = lgo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(1f, 0.55f, 0.3f);
        l.intensity = 1.6f;
        l.range = 7f;
        l.shadows = LightShadows.None;
    }

    /// <summary>Place a kit prefab under <paramref name="parent"/>: yawed, scaled so its
    /// bounds match <paramref name="size"/> (in the yawed frame; NaN = keep proportion),
    /// and anchored at <paramref name="at"/>. <paramref name="longAlongX"/> turns a
    /// module whose long side is its Z axis so it runs along X (walls, railings).</summary>
    private static GameObject Box(Transform parent, string name, Vector3 at, Vector3 size, float yaw, Align align, bool longAlongX)
    {
        var path = Resolve(name);
        if (path == null) { missing.Add(name); return null; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), parent);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var l in go.GetComponentsInChildren<Light>(true)) l.enabled = false;
        var raw = LocalBounds(go, go.transform);
        if (raw.size == Vector3.zero) { Object.DestroyImmediate(go); missing.Add(name + " (no mesh)"); return null; }
        var swap = longAlongX && raw.size.z > raw.size.x * 1.05f;
        go.transform.localRotation = Quaternion.Euler(0f, yaw + (swap ? 90f : 0f), 0f);
        var want = swap ? new Vector3(size.z, size.y, size.x) : size;
        float S(float w, float r) => float.IsNaN(w) ? float.NaN : w / Mathf.Max(0.01f, r);
        var sx = S(want.x, raw.size.x);
        var sy = S(want.y, raw.size.y);
        var sz = S(want.z, raw.size.z);
        var known = new[] { sx, sy, sz }.Where(v => !float.IsNaN(v)).ToArray();
        var mean = known.Length > 0 ? known.Average() : 1f;
        go.transform.localScale = new Vector3(float.IsNaN(sx) ? mean : sx, float.IsNaN(sy) ? mean : sy, float.IsNaN(sz) ? mean : sz);
        var b = LocalBounds(go, parent);
        var anchor = new Vector3(b.center.x, align == Align.Bottom ? b.min.y : align == Align.Top ? b.max.y : b.center.y, b.center.z);
        go.transform.localPosition += at - anchor;
        placed++;
        return go;
    }

    /// <summary>Mesh-bounds AABB of <paramref name="go"/> expressed in <paramref name="frame"/>'s local space.</summary>
    private static Bounds LocalBounds(GameObject go, Transform frame)
    {
        var any = false;
        var min = Vector3.one * float.MaxValue;
        var max = Vector3.one * float.MinValue;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var lower = mf.name.ToLowerInvariant();
            if (lower.Contains("collision") || lower.Contains("lod1") || lower.Contains("lod2")) continue;
            var bb = mf.sharedMesh.bounds;
            for (var i = 0; i < 8; i++)
            {
                var corner = bb.center + Vector3.Scale(bb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = frame.InverseTransformPoint(mf.transform.TransformPoint(corner));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }
        if (!any) return new Bounds(Vector3.zero, Vector3.zero);
        var b = new Bounds();
        b.SetMinMax(min, max);
        return b;
    }

    private static string Resolve(string name)
    {
        if (paths.TryGetValue(name, out var p)) return p;
        p = AssetDatabase.FindAssets(name + " t:Prefab", new[] { Kit }).Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(x => System.IO.Path.GetFileNameWithoutExtension(x) == name);
        paths[name] = p;
        return p;
    }

    private static Vector2 Az(float deg)
    {
        var r = deg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }
}
