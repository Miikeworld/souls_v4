using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Setup Warden Fight (Core Sanctum).
/// Builds the final boss's arena around the BossLord in the open scene (or
/// 00_TestBlockout): a circular sanctum — central platform (r 10), an outer ring
/// of eight floor sections (r 10–15.5), a ring wall with a south doorway, eight
/// pillars, a throne dais with a Corestone crystal, crimson veins in the floor and
/// loose stones — then wires <see cref="CoreSanctum"/> and the boss.
/// Phase 3 roles are baked into the pieces: the four diagonal wall segments are
/// the floating wall-run slabs (purple run marks, WallRunSurface), the four
/// diagonal floor sections collapse under them, the east/west sections lift, the
/// throne section breaks, the entrance stays.
/// All geometry is generated (faceted, unwelded, Mechanical Environment crease
/// channels) into project-owned mesh assets (MeshAssetWriter keeps GUIDs on
/// rebuild); the throne is a vendor prefab instance with its colliders off.
/// RELOCATION: the Warden no longer fights on the bare test floor. When the old
/// boss floor (BossArenaFloor2) exists it becomes a dressed stone FORECOURT (same
/// footprint, so every route that reached it still does) with a Checkpoint
/// ("Sanctum Approach") — and the sanctum itself is built 50 m further along the
/// approach, reached over an 18 m CAUSEWAY across the void (crimson-veined, broken
/// balustrade, floating stones, its own fall net back to the forecourt). The boss
/// moves to the sanctum centre; the fog gate moves to its doorway. A re-run keeps
/// the relocated site (marker child), so it never walks further away.
/// Re-runnable: the old sanctum is replaced, the scene is backed up first.
/// Also rebuilds BossLordBase.controller (Warden states) and bakes the spectral
/// arsenal (Synty weapons → Resources/WardenArsenal ghosts).
/// Manual, outside Play Mode.
/// </summary>
public static class ProjectRestartWarden
{
    private const string DefaultScene = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string MeshDir = "Assets/_Project/ArtDirection/Meshes/CoreSanctum";
    private const string MatDir = "Assets/_Project/ArtDirection/Materials/CoreSanctum";
    private const string ThronePath = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Environments/Misc/SM_Env_Stone_Throne_01.prefab";

    private const float PlatformR = 10f, OuterR = 15.5f, WallR = 16f, WallT = 1.2f, WallH = 7.5f;
    private const float FloorThick = 1.2f, PillarR = 14.2f, SlabRunR = 11.9f, SlabLen = 12.1f, DoorW = 6.8f;
    private const float EntranceLen = 4.6f, CausewayLen = 18f, CausewayW = 6.4f;
    private const string RelocatedMarker = "Relocated site (v2)";
    /// <summary>Forecourt centre → sanctum centre along the approach, for a forecourt
    /// reaching <paramref name="half"/> metres toward the sanctum.</summary>
    private static float ForecourtToCentre(float half) => OuterR + EntranceLen - 0.15f + CausewayLen + half - 0.5f;

    /// <summary>The old boss floor's top-face corners (world, clockwise from above).</summary>
    private static Vector3[] FloorCorners(GameObject floor, float top)
    {
        var t = floor.transform;
        var c = new[]
        {
            t.TransformPoint(new Vector3(-0.5f, 0.5f, -0.5f)), t.TransformPoint(new Vector3(-0.5f, 0.5f, 0.5f)),
            t.TransformPoint(new Vector3(0.5f, 0.5f, 0.5f)), t.TransformPoint(new Vector3(0.5f, 0.5f, -0.5f)),
        };
        for (var i = 0; i < c.Length; i++) c[i].y = top;
        return c;
    }

    private static float HalfAlong(Vector3[] corners, Vector3 centre, Vector3 dir)
    {
        var best = 0f;
        foreach (var c in corners) best = Mathf.Max(best, Vector3.Dot(c - centre, dir));
        return best;
    }

    private static Material stone, stoneDark, stoneLight, corestone, runMark, crystal;
    private static readonly List<string> report = new List<string>();
    private static readonly List<Renderer> floorVeins = new List<Renderer>();
    private static readonly List<PieceSpec> specs = new List<PieceSpec>();

    private sealed class PieceSpec
    {
        public Transform root, visual;
        public CoreSanctum.Role role;
        public Vector3 shatterPos, shatterEuler, ascend;
        public float delay;
        public readonly List<Renderer> veins = new List<Renderer>();
        public bool walkable;
    }

    [MenuItem("Tools/Project Restart/Setup Warden Fight (Core Sanctum)")]
    public static void Setup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[Warden] Setup deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Selection.activeObject = null;
        report.Clear();
        floorVeins.Clear();
        specs.Clear();

        ProjectRestartUrpFix.FixAll();
        ProjectRestartBossLord.BuildController();
        report.Add("BossLordBase.controller: greatsword strings + Warden Phase 2/3 states ensured.");
        report.Add(ProjectRestartWardenArsenal.Bake());

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var boss = Object.FindFirstObjectByType<BossLord>();
        if (boss == null)
        {
            scene = EditorSceneManager.OpenScene(DefaultScene, OpenSceneMode.Single);
            boss = Object.FindFirstObjectByType<BossLord>();
        }
        if (boss == null)
        {
            Debug.LogError("[Warden] No BossLord in the open scene or " + DefaultScene + " — run Setup Dark Lord Boss first.");
            return;
        }

        var backup = "Assets/_Project/ArtDirection/" + scene.name + "_BeforeWarden_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity";
        EnsureFolder("Assets/_Project/ArtDirection");
        if (!EditorSceneManager.SaveScene(scene, backup, true))
        {
            Debug.LogError("[Warden] Could not back up the scene — nothing changed.");
            return;
        }
        report.Add("Backup: " + backup);

        EnsureFolder(MeshDir);
        EnsureFolder(MatDir);
        Materials();

        // Arena frame: forward (+Z) points away from the fog gate, toward the throne.
        // The old boss floor becomes the forecourt; the sanctum sits 50 m on.
        var oldFloor = GameObject.Find("BossArenaFloor2");
        var gate = Object.FindObjectsByType<FogGate>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(g => new SerializedObject(g).FindProperty("boss").objectReferenceValue == boss);
        var old = GameObject.Find("CoreSanctum");
        var floorTop = boss.transform.position.y;
        if (oldFloor != null) floorTop = oldFloor.transform.position.y + oldFloor.transform.lossyScale.y * 0.5f;
        Vector3 centre;
        Quaternion frame;
        Vector3? forecourt = null;
        var corners = oldFloor != null ? FloorCorners(oldFloor, floorTop) : null;
        if (old != null && old.transform.Find(RelocatedMarker) != null)
        {
            // Already relocated: rebuild on the same site.
            centre = old.transform.position;
            frame = old.transform.rotation;
            if (oldFloor != null) forecourt = new Vector3(oldFloor.transform.position.x, floorTop, oldFloor.transform.position.z);
            report.Add("Relocated site kept: " + centre);
        }
        else
        {
            Vector3 fwd;
            var floorCentre = oldFloor != null ? new Vector3(oldFloor.transform.position.x, floorTop, oldFloor.transform.position.z)
                                               : new Vector3(boss.transform.position.x, floorTop, boss.transform.position.z);
            if (old != null) fwd = Vector3.ProjectOnPlane(old.transform.forward, Vector3.up);   // the old in-place sanctum's axis
            else
            {
                var toGate = gate != null ? Vector3.ProjectOnPlane(gate.transform.position - floorCentre, Vector3.up) : -boss.transform.forward;
                if (toGate.sqrMagnitude < 0.01f) toGate = -boss.transform.forward;
                fwd = -toGate;
            }
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            frame = Quaternion.LookRotation(fwd, Vector3.up);
            if (oldFloor != null)
            {
                forecourt = floorCentre;
                var along = ForecourtToCentre(HalfAlong(corners, floorCentre, fwd));
                centre = floorCentre + fwd * along;
                report.Add($"Relocated: the old boss floor at {floorCentre} is the forecourt; the sanctum is {along:F1} m on at {centre}.");
            }
            else
            {
                centre = floorCentre;
                report.Add("No BossArenaFloor2 — sanctum built in place around the boss (no causeway).");
            }
        }
        var toGateFinal = -(frame * Vector3.forward);

        if (old != null) Object.DestroyImmediate(old);
        var root = new GameObject("CoreSanctum").transform;
        root.SetPositionAndRotation(centre, frame);

        var central = BuildCentralFloor(root);
        BuildWedges(root);
        BuildWalls(root, out var doorLocal);
        BuildPillars(root);
        BuildDebris(root);
        BuildLights(root);
        BuildEntrance(root);
        BuildSconces(root);
        var fallNet = BuildFallNet(root, forecourt.HasValue ? 48f : 70f);
        if (forecourt.HasValue && corners != null)
            BuildApproach(root, root.InverseTransformPoint(forecourt.Value), corners.Select(c => root.InverseTransformPoint(c)).ToArray());
        new GameObject(RelocatedMarker).transform.SetParent(root, false);

        var sanctum = root.gameObject.AddComponent<CoreSanctum>();
        WireSanctum(sanctum, central);

        if (oldFloor != null)
        {
            // Kept (Setup Dark Lord Boss looks for it by name) but out of the way.
            if (oldFloor.TryGetComponent<Renderer>(out var r)) r.enabled = false;
            if (oldFloor.TryGetComponent<Collider>(out var c)) c.enabled = false;
            EditorUtility.SetDirty(oldFloor);
            report.Add("BossArenaFloor2: renderer + collider disabled (object kept).");
        }
        if (gate != null) PlaceGate(gate, root, doorLocal);

        boss.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(toGateFinal, Vector3.up));
        var bso = new SerializedObject(boss);
        var sp = bso.FindProperty("sanctum");
        if (sp != null) sp.objectReferenceValue = sanctum;
        bso.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(boss);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) Debug.LogError("[Warden] Scene save failed — review and save manually.");
        AssetDatabase.SaveAssets();
        report.Add($"Core Sanctum at {centre} (forward {frame * Vector3.forward}): platform r{PlatformR}, ring r{OuterR}, {specs.Count} moving pieces, fall net {(fallNet != null ? "on" : "off")}.");
        Debug.Log("[Warden] Setup Warden Fight complete.\n" + string.Join("\n", report) +
                  "\nPlay Mode checks: Tools > Project Restart > Warden > Debug … (phase skips, move cycle). " +
                  "Clip poses, tells and wall-run reach need a Play Mode pass; geometry only proves layout.");
    }

    // ------------------------------------------------------------------ pieces

    private static Transform BuildCentralFloor(Transform root)
    {
        var m = new Crease();
        var foot = new List<Vector2>();
        for (var i = 0; i < 24; i++) foot.Add(Az(i * 15f) * PlatformR);
        m.Prism(foot, -FloorThick, 0f);
        var floor = Solid(root, "CentralFloor", Save(m.Build("Sanctum central floor"), "CentralFloor"), stone, Vector3.zero, Quaternion.identity, true);

        // Corestone: two rings and eight cracked radials running out to the edge.
        var rng = new System.Random(11);
        var vein = new Ribbons();
        foreach (var r in new[] { 3.2f, 6.6f })
        {
            var ring = new List<Vector3>();
            for (var i = 0; i <= 24; i++) { var d = Az(i * 15f) * r; ring.Add(new Vector3(d.x, 0.012f, d.y)); }
            vein.Strip(ring, Vector3.up, 0.11f);
        }
        for (var k = 0; k < 8; k++)
            vein.Strip(Jagged(rng, Az(k * 45f + 22.5f), 1.2f, PlatformR - 0.2f, 0.012f), Vector3.up, 0.09f);
        floorVeins.Add(Strip(floor.Find("Visual"), "Corestone veins", Save(vein.Build("Sanctum floor veins"), "FloorVeins"), corestone, Vector3.zero));
        return floor;
    }

    private static void BuildWedges(Transform root)
    {
        var rng = new System.Random(23);
        for (var k = 0; k < 8; k++)
        {
            var az = k * 45f;
            var pivotFlat = Az(az) * ((PlatformR + OuterR) * 0.5f);
            var pivot = new Vector3(pivotFlat.x, 0f, pivotFlat.y);
            var m = new Crease();
            m.Annular(PlatformR + 0.06f, OuterR, az - 22.15f, az + 22.15f, 6, -FloorThick, 0f, 0.3f, rng, -pivot);
            var role = k == 4 ? CoreSanctum.Role.Static
                     : k == 0 ? CoreSanctum.Role.Break
                     : k % 2 == 1 ? CoreSanctum.Role.Collapse
                     : CoreSanctum.Role.Float;
            var piece = Solid(root, "Ring_" + k, Save(m.Build("Sanctum ring " + k), "Ring_" + k), stoneDark, pivot, Quaternion.identity, true);
            var arc = new List<Vector3>();
            for (var i = 0; i <= 8; i++)
            {
                var d = Az(az - 18f + 36f * i / 8f) * 12.9f;
                arc.Add(new Vector3(d.x, 0.012f, d.y) - pivot);
            }
            var veinM = new Ribbons();
            veinM.Strip(arc, Vector3.up, 0.09f);
            var vr = Strip(piece.Find("Visual"), "Corestone vein", Save(veinM.Build("Sanctum ring vein " + k), "RingVein_" + k), corestone, Vector3.zero);
            var outward = new Vector3(Az(az).x, 0f, Az(az).y);
            var tangent = Vector3.Cross(Vector3.up, outward);
            var spec = new PieceSpec { root = piece, visual = piece.Find("Visual"), role = role, walkable = true };
            spec.veins.Add(vr);
            switch (role)
            {
                case CoreSanctum.Role.Break:
                    // Sags outward: +angle about the tangent drops the outer edge.
                    spec.shatterPos = root.TransformPoint(pivot + Vector3.down * 0.75f + outward * 0.25f);
                    spec.shatterEuler = (root.rotation * Quaternion.AngleAxis(4f, tangent)).eulerAngles;
                    spec.ascend = Vector3.up * 1.2f;
                    spec.delay = 0.25f;
                    break;
                case CoreSanctum.Role.Float:
                    spec.shatterPos = root.TransformPoint(pivot + Vector3.up * 1.6f + outward * 0.6f);
                    spec.shatterEuler = (root.rotation * Quaternion.AngleAxis(2.5f, tangent)).eulerAngles;
                    spec.ascend = Vector3.up * 2.4f;
                    spec.delay = 0.4f;
                    break;
                case CoreSanctum.Role.Collapse:
                    spec.delay = 0.05f + 0.08f * (k / 2);
                    break;
            }
            specs.Add(spec); // Static too: it still floods red and glows with the veins.
            if (k == 0) BuildThrone(piece, pivot, spec);
        }
    }

    private static void BuildThrone(Transform wedge, Vector3 pivot, PieceSpec spec)
    {
        var at = new Vector3(0f, 0f, 13.3f) - pivot;
        var vis = wedge.Find("Visual");
        var dais = new Crease();
        var a = new List<Vector2>();
        var b = new List<Vector2>();
        for (var i = 0; i < 8; i++) { a.Add(Az(i * 45f + 22.5f) * 2.4f); b.Add(Az(i * 45f + 22.5f) * 1.7f); }
        dais.Prism(Offset(a, at), 0f, 0.35f);
        dais.Prism(Offset(b, at), 0.35f, 0.7f);
        Solid(vis, "Throne dais", Save(dais.Build("Sanctum throne dais"), "ThroneDais"), stoneLight, Vector3.zero, Quaternion.identity, false);
        var dc = wedge.gameObject.AddComponent<BoxCollider>();
        dc.center = at + Vector3.up * 0.35f;
        dc.size = new Vector3(3.6f, 0.7f, 3.6f);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThronePath);
        if (prefab != null)
        {
            var throne = (GameObject)PrefabUtility.InstantiatePrefab(prefab, vis);
            throne.name = "Throne";
            throne.transform.localPosition = at + Vector3.up * 0.7f;
            throne.transform.localRotation = Quaternion.LookRotation(Vector3.back);
            foreach (var c in throne.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            var bounds = RendererBounds(throne);
            if (bounds.size.y > 0.01f) throne.transform.localScale *= 3.2f / bounds.size.y;
            report.Add("Throne: vendor prefab, colliders off, scaled to 3.2 m.");
        }
        else report.Add("Throne prefab missing (" + ThronePath + ") — dais only.");

        // The sanctum's Core: a crimson crystal cluster behind the throne.
        var rng = new System.Random(5);
        var cr = new Crease();
        var behind = at + new Vector3(0f, 0.7f, 1.6f);
        for (var i = 0; i < 7; i++)
        {
            var o = new Vector3((float)(rng.NextDouble() - 0.5) * 1.6f, 0f, (float)(rng.NextDouble() - 0.5) * 0.8f);
            var h = 1.4f + (float)rng.NextDouble() * 2.4f;
            var lean = new Vector3((float)(rng.NextDouble() - 0.5) * 0.9f, 0f, (float)(rng.NextDouble() - 0.5) * 0.6f);
            cr.Spike(behind + o, 0.22f + (float)rng.NextDouble() * 0.22f, h, lean, 5);
        }
        var crystals = Solid(vis, "Corestone crystal", Save(cr.Build("Sanctum crystal"), "Crystal"), crystal, Vector3.zero, Quaternion.identity, false);
        spec.veins.Add(crystals.GetComponentInChildren<Renderer>());
        var lgo = new GameObject("Crystal light");
        lgo.transform.SetParent(vis, false);
        lgo.transform.localPosition = behind + Vector3.up * 2f;
        var l = lgo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(1f, 0.15f, 0.12f);
        l.intensity = 3f;
        l.range = 10f;
        l.shadows = LightShadows.None;
    }

    private static void BuildWalls(Transform root, out Vector3 doorLocal)
    {
        var rng = new System.Random(41);
        var full = Save(BoxMesh(new Vector3(SlabLen, WallH, WallT)).Build("Sanctum wall"), "WallSegment");
        var halfLen = (SlabLen - DoorW) * 0.5f;
        var half = Save(BoxMesh(new Vector3(halfLen, WallH, WallT)).Build("Sanctum wall half"), "WallHalf");
        doorLocal = new Vector3(0f, 0f, -WallR);
        for (var k = 0; k < 8; k++)
        {
            var az = k * 45f;
            var d = Az(az);
            var outward = new Vector3(d.x, 0f, d.y);
            var centre = outward * WallR + Vector3.up * (WallH * 0.5f - FloorThick);
            var rot = Quaternion.LookRotation(-outward, Vector3.up);
            if (k == 4)
            {
                // The doorway: two halves either side of a gap.
                var tangent = Vector3.Cross(Vector3.up, -outward);
                foreach (var s in new[] { -1f, 1f })
                {
                    var hw = Solid(root, "Wall_Door_" + (s < 0f ? "L" : "R"), half, stoneDark, centre + tangent * s * (DoorW + halfLen) * 0.5f, rot, false);
                    Box(hw, new Vector3(halfLen, WallH, WallT));
                }
                continue;
            }
            var slab = k % 2 == 1;
            var wall = Solid(root, slab ? "WallSlab_" + k : "Wall_" + k, full, stoneDark, centre, rot, false);
            Box(wall, new Vector3(SlabLen, WallH, WallT));
            var vis = wall.Find("Visual");
            if (!slab)
            {
                // Static wall: crimson Corestone cracks climbing the face.
                var cracks = new Ribbons();
                for (var c = 0; c < 2; c++)
                {
                    var x = ((float)rng.NextDouble() - 0.5f) * SlabLen * 0.7f;
                    var path = new List<Vector3>();
                    var y = -WallH * 0.5f + 0.3f;
                    while (y < WallH * 0.35f)
                    {
                        path.Add(new Vector3(x, y, WallT * 0.5f + 0.012f));
                        y += 0.5f + (float)rng.NextDouble() * 0.4f;
                        x += ((float)rng.NextDouble() - 0.5f) * 0.6f;
                    }
                    cracks.Strip(path, Vector3.forward, 0.08f);
                }
                floorVeins.Add(Strip(vis, "Corestone cracks", Save(cracks.Build("Sanctum wall cracks " + k), "WallCracks_" + k), corestone, Vector3.zero));
                continue;
            }

            // A future floating runway: purple run marks on the inner face, a torn back.
            wall.gameObject.AddComponent<WallRunSurface>();
            var marks = new Ribbons();
            foreach (var y in new[] { 0.45f, 2.05f })
                marks.Strip(new List<Vector3> { new Vector3(-SlabLen * 0.45f, y, WallT * 0.5f + 0.015f), new Vector3(SlabLen * 0.45f, y, WallT * 0.5f + 0.015f) }, Vector3.forward, 0.16f);
            for (var c = 0; c < 2; c++)
            {
                var x = (c == 0 ? -1f : 1f) * SlabLen * 0.22f;
                var path = new List<Vector3>();
                for (var yy = -0.4f; yy < 2.9f; yy += 0.55f)
                    path.Add(new Vector3(x + ((float)rng.NextDouble() - 0.5f) * 0.5f, yy, WallT * 0.5f + 0.015f));
                marks.Strip(path, Vector3.forward, 0.07f);
            }
            var markR = Strip(vis, "Corestone run marks", Save(marks.Build("Sanctum slab marks " + k), "SlabMarks_" + k), runMark, Vector3.zero);
            var torn = new Crease();
            for (var r = 0; r < 6; r++)
            {
                var c0 = new Vector3(((float)rng.NextDouble() - 0.5f) * SlabLen * 0.9f, ((float)rng.NextDouble() - 0.5f) * WallH * 0.8f, -WallT * 0.5f - 0.2f);
                torn.Rock(rng, 0.5f + (float)rng.NextDouble() * 0.6f, c0);
            }
            Solid(vis, "Torn back", Save(torn.Build("Sanctum slab back " + k), "SlabBack_" + k), stone, Vector3.zero, Quaternion.identity, false);
            var runCentre = outward * SlabRunR + Vector3.up * 0.55f;
            var spec = new PieceSpec
            {
                root = wall, visual = vis, role = CoreSanctum.Role.WallSlab, walkable = false,
                shatterPos = root.TransformPoint(runCentre),
                shatterEuler = (root.rotation * rot * Quaternion.Euler(0f, 0f, (k < 4 ? 2f : -2f))).eulerAngles,
                ascend = Vector3.up * 0.8f,
                delay = 0.15f + 0.1f * (k / 2),
            };
            spec.veins.Add(markR);
            specs.Add(spec);
        }
    }

    private static void BuildPillars(Transform root)
    {
        var rng = new System.Random(57);
        for (var k = 0; k < 8; k++)
        {
            var az = 22.5f + k * 45f;
            var entrance = k == 3 || k == 4;
            var broken = !entrance && k % 3 == 1;
            var h = broken ? 5.4f : 8.2f;
            var m = new Crease();
            var shaft = new List<Vector2>();
            var plinth = new List<Vector2>();
            var cap = new List<Vector2>();
            for (var i = 0; i < 8; i++)
            {
                shaft.Add(Az(i * 45f + 22.5f) * 0.7f);
                plinth.Add(Az(i * 45f + 22.5f) * 1.05f);
                cap.Add(Az(i * 45f + 22.5f) * 0.98f);
            }
            m.Prism(plinth, 0f, 0.45f);
            m.Prism(shaft, 0.45f, h - (broken ? 0f : 0.4f));
            if (!broken) m.Prism(cap, h - 0.4f, h);
            else for (var r = 0; r < 2; r++) m.Rock(rng, 0.35f, new Vector3(((float)rng.NextDouble() - 0.5f) * 0.6f, h - 0.1f, ((float)rng.NextDouble() - 0.5f) * 0.6f));
            var d = Az(az);
            var basePos = new Vector3(d.x, 0f, d.y) * PillarR;
            var pillar = Solid(root, "Pillar_" + k, Save(m.Build("Sanctum pillar " + k), "Pillar_" + k), stoneLight, basePos, Quaternion.Euler(0f, az, 0f), false);
            var cap2 = pillar.gameObject.AddComponent<CapsuleCollider>();
            cap2.radius = 0.75f;
            cap2.height = h;
            cap2.center = Vector3.up * h * 0.5f;
            var vein = new Ribbons();
            var path = new List<Vector3>();
            for (var y = 0.6f; y < h - 0.6f; y += 0.6f)
            {
                var ang = (y * 40f + k * 30f) * Mathf.Deg2Rad;
                path.Add(new Vector3(Mathf.Sin(ang) * 0.71f, y, Mathf.Cos(ang) * 0.71f));
            }
            floorVeins.Add(Strip(pillar.Find("Visual"), "Corestone vein", Save(vein.StripReturn(path, Vector3.zero, 0.07f).Build("Sanctum pillar vein " + k), "PillarVein_" + k), corestone, Vector3.zero));
            if (entrance) continue;
            var outward = new Vector3(d.x, 0f, d.y);
            var tilt = Quaternion.AngleAxis(10f + (float)rng.NextDouble() * 12f, new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f).normalized);
            specs.Add(new PieceSpec
            {
                root = pillar, visual = pillar.Find("Visual"), role = CoreSanctum.Role.Pillar,
                shatterPos = root.TransformPoint(basePos + Vector3.up * (2f + (float)rng.NextDouble() * 3f) + outward * (1f + (float)rng.NextDouble() * 1.5f)),
                shatterEuler = (root.rotation * Quaternion.Euler(0f, az, 0f) * tilt).eulerAngles,
                ascend = Vector3.up * (2f + (float)rng.NextDouble() * 2f),
                delay = 0.3f + 0.08f * k,
            });
        }
    }

    private static void BuildDebris(Transform root)
    {
        var rng = new System.Random(71);
        var variants = new Mesh[6];
        for (var v = 0; v < variants.Length; v++)
        {
            var c = new Crease();
            c.Rock(rng, 1f, Vector3.zero);
            variants[v] = Save(c.Build("Sanctum stone " + v), "Stone_" + v);
        }
        // Loose stones on the floor — they drift up in Phase 2, hang in Phase 3.
        for (var i = 0; i < 22; i++)
        {
            var onPlatform = i < 10;
            var r = onPlatform ? 4f + (float)rng.NextDouble() * 5.2f : 11f + (float)rng.NextDouble() * 4f;
            var az = (float)rng.NextDouble() * 360f;
            var d = Az(az);
            var size = 0.25f + (float)rng.NextDouble() * (onPlatform ? 0.35f : 0.6f);
            var pos = new Vector3(d.x * r, size * 0.35f, d.y * r);
            var go = Solid(root, "Stone_" + i, variants[i % variants.Length], stone, pos, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f), false);
            go.localScale = Vector3.one * size;
            var hang = onPlatform ? 4.2f + (float)rng.NextDouble() * 3f : 1.2f + (float)rng.NextDouble() * 4f;
            var rr = onPlatform ? r : 10.6f + (float)rng.NextDouble() * 7f;
            specs.Add(new PieceSpec
            {
                root = go, role = CoreSanctum.Role.Debris,
                shatterPos = root.TransformPoint(new Vector3(d.x * rr, hang, d.y * rr)),
                shatterEuler = new Vector3((float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f),
                ascend = Vector3.up * (2f + (float)rng.NextDouble() * 4f),
                delay = 0.1f + (float)rng.NextDouble() * 0.6f,
            });
        }
        // Torn floor chunks that only exist once the arena breaks: hidden below,
        // they rise into the void ring as floating architecture.
        for (var i = 0; i < 6; i++)
        {
            var az = 30f + i * 60f + (float)rng.NextDouble() * 20f;
            var d = Az(az);
            var go = Solid(root, "Chunk_" + i, variants[(i + 2) % variants.Length], stoneDark, new Vector3(d.x * 14f, -9f, d.y * 14f), Quaternion.identity, false);
            go.localScale = new Vector3(2.4f, 1.1f, 1.9f) * (0.8f + (float)rng.NextDouble() * 0.5f);
            go.gameObject.SetActive(false);
            var rr = 15.5f + (float)rng.NextDouble() * 3f;
            specs.Add(new PieceSpec
            {
                root = go, role = CoreSanctum.Role.Debris,
                shatterPos = root.TransformPoint(new Vector3(d.x * rr, 0.6f + (float)rng.NextDouble() * 3.5f, d.y * rr)),
                shatterEuler = new Vector3((float)rng.NextDouble() * 30f, (float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 30f),
                ascend = Vector3.up * (3f + (float)rng.NextDouble() * 3f),
                delay = 0.5f + (float)rng.NextDouble() * 0.5f,
            });
        }
    }

    private static void BuildLights(Transform root)
    {
        var go = new GameObject("Sanctum key light");
        go.transform.SetParent(root, false);
        go.transform.localPosition = Vector3.up * 9f;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(0.85f, 0.8f, 1f);
        l.intensity = 1.2f;
        l.range = 26f;
        l.shadows = LightShadows.None;
    }

    private static void BuildEntrance(Transform root)
    {
        var len = 4.6f;
        var m = BoxMesh(new Vector3(7.6f, FloorThick, len));
        var pos = new Vector3(0f, -FloorThick * 0.5f, -(OuterR + len * 0.5f - 0.15f));
        var go = Solid(root, "Entrance landing", Save(m.Build("Sanctum entrance"), "Entrance"), stoneDark, pos, Quaternion.identity, false);
        Box(go, new Vector3(7.6f, FloorThick, len));
    }

    /// <summary>Corestone sconces on the standing walls and either side of the doorway:
    /// crimson crystal clusters with a low light — they pulse with the floor veins.</summary>
    private static void BuildSconces(Transform root)
    {
        var rng = new System.Random(91);
        var spots = new List<(float az, float tangent)> { (90f, 0f), (270f, 0f), (180f, -(DoorW * 0.5f + 1.4f)), (180f, DoorW * 0.5f + 1.4f) };
        var i = 0;
        foreach (var (az, tangent) in spots)
        {
            var d = Az(az);
            var inward = -new Vector3(d.x, 0f, d.y);
            var side = Vector3.Cross(Vector3.up, inward);
            var at = -inward * (WallR - WallT * 0.5f - 0.12f) + side * tangent + Vector3.up * 3.1f;
            var m = new Crease();
            for (var k = 0; k < 4; k++)
            {
                var o = side * ((float)rng.NextDouble() - 0.5f) * 0.6f + Vector3.up * ((float)rng.NextDouble() - 0.5f) * 0.3f;
                var lean = inward * (0.25f + (float)rng.NextDouble() * 0.3f) + side * ((float)rng.NextDouble() - 0.5f) * 0.3f;
                m.Spike(at + o, 0.1f + (float)rng.NextDouble() * 0.1f, 0.45f + (float)rng.NextDouble() * 0.5f, lean, 5);
            }
            floorVeins.Add(Strip(root, "Sconce_" + i, Save(m.Build("Sanctum sconce " + i), "Sconce_" + i), crystal, Vector3.zero));
            var lgo = new GameObject("Sconce light " + i);
            lgo.transform.SetParent(root, false);
            lgo.transform.localPosition = at + inward * 0.8f + Vector3.up * 0.3f;
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.16f, 0.12f);
            l.intensity = 1.6f;
            l.range = 8f;
            l.shadows = LightShadows.None;
            i++;
        }
    }

    /// <summary>The relocated approach: the old boss floor dressed as a stone forecourt
    /// (its exact footprint, corners chamfered — every route that reached it still
    /// does — with the "Sanctum Approach" checkpoint), then a crimson-veined causeway
    /// across the void to the sanctum's entrance landing. Side guards are invisible
    /// boxes; the broken balustrade is visual only. A fall from the causeway returns
    /// to the forecourt.</summary>
    private static void BuildApproach(Transform root, Vector3 fl, Vector3[] cornersLocal)
    {
        var rng = new System.Random(131);
        fl.y = 0f;
        var apothem = 0f;
        foreach (var c in cornersLocal) apothem = Mathf.Max(apothem, c.z - fl.z);
        var minHalf = float.MaxValue;
        foreach (var c in cornersLocal) minHalf = Mathf.Min(minHalf, Mathf.Min(Mathf.Abs(c.x - fl.x), Mathf.Abs(c.z - fl.z)));

        // Forecourt slab over the old floor (same footprint, chamfered, faceted stone, a vein ring).
        var fm = new Crease();
        var foot = new List<Vector2>();
        const float chamfer = 1.2f;
        for (var k = 0; k < cornersLocal.Length; k++)
        {
            var cur = new Vector2(cornersLocal[k].x, cornersLocal[k].z);
            var prev = new Vector2(cornersLocal[(k + cornersLocal.Length - 1) % cornersLocal.Length].x, cornersLocal[(k + cornersLocal.Length - 1) % cornersLocal.Length].z);
            var next = new Vector2(cornersLocal[(k + 1) % cornersLocal.Length].x, cornersLocal[(k + 1) % cornersLocal.Length].z);
            foot.Add(cur + (prev - cur).normalized * chamfer);
            foot.Add(cur + (next - cur).normalized * chamfer);
        }
        fm.Prism(foot, -FloorThick, 0f);
        var court = Solid(root, "Forecourt", Save(fm.Build("Sanctum forecourt"), "Forecourt"), stoneDark, Vector3.zero, Quaternion.identity, true);
        var fv = new Ribbons();
        var ring = new List<Vector3>();
        var ringR = Mathf.Max(3f, minHalf * 0.55f);
        for (var k = 0; k <= 16; k++) { var d = Az(k * 22.5f) * ringR; ring.Add(new Vector3(fl.x + d.x, 0.012f, fl.z + d.y)); }
        fv.Strip(ring, Vector3.up, 0.1f);
        fv.Strip(Jagged(rng, Az(0f), ringR, apothem - 0.2f, 0.012f).Select(p => p + new Vector3(fl.x, 0f, fl.z)).ToList(), Vector3.up, 0.09f);
        floorVeins.Add(Strip(court.Find("Visual"), "Corestone veins", Save(fv.Build("Sanctum forecourt veins"), "ForecourtVeins"), corestone, Vector3.zero));
        var rocks = new Crease();
        foreach (var c in cornersLocal)
        {
            var inward = (new Vector3(fl.x, 0f, fl.z) - new Vector3(c.x, 0f, c.z)).normalized;
            for (var k = 0; k < 2; k++)
            {
                var at = new Vector3(c.x, 0.2f, c.z) + inward * (2.6f + (float)rng.NextDouble() * 2f)
                         + Vector3.Cross(Vector3.up, inward) * ((float)rng.NextDouble() - 0.5f) * 2f;
                rocks.Rock(rng, 0.6f + (float)rng.NextDouble() * 0.9f, at);
            }
        }
        Solid(court.Find("Visual"), "Rubble", Save(rocks.Build("Sanctum forecourt rubble"), "ForecourtRubble"), stone, Vector3.zero, Quaternion.identity, false);

        // The causeway: entrance landing → forecourt edge.
        var zNear = -(OuterR + EntranceLen - 0.15f) + 0.2f;
        var zFar = fl.z + apothem - 0.4f;
        var length = zNear - zFar;
        if (length < 1f) { report.Add("Approach skipped: forecourt overlaps the sanctum."); return; }
        var midZ = (zNear + zFar) * 0.5f;
        var deck = Solid(root, "Causeway", Save(BoxMesh(new Vector3(CausewayW, FloorThick, length)).Build("Sanctum causeway"), "Causeway"), stoneDark,
                         new Vector3(0f, -FloorThick * 0.5f, midZ), Quaternion.identity, false);
        Box(deck, new Vector3(CausewayW, FloorThick, length));
        foreach (var sgn in new[] { -1f, 1f })
        {
            var guard = new GameObject("Causeway guard " + (sgn < 0f ? "L" : "R"));
            guard.transform.SetParent(deck, false);
            guard.transform.localPosition = new Vector3(sgn * (CausewayW * 0.5f + 0.15f), FloorThick * 0.5f + 0.6f, 0f);
            guard.AddComponent<BoxCollider>().size = new Vector3(0.3f, 1.2f, length);
        }
        var cv = new Ribbons();
        cv.Strip(Jagged(rng, Az(180f), -zNear + 0.3f, -zFar - 0.3f, 0.012f + FloorThick * 0.5f).Select(p => p - new Vector3(0f, 0f, midZ)).ToList(), Vector3.up, 0.11f);
        floorVeins.Add(Strip(deck.Find("Visual"), "Corestone vein", Save(cv.Build("Sanctum causeway vein"), "CausewayVein"), corestone, Vector3.zero));

        // Broken balustrade (visual only): posts every ~3 m, some snapped, a few rail spans.
        var posts = new Crease();
        var sq = new List<Vector2> { new Vector2(-0.2f, -0.2f), new Vector2(-0.2f, 0.2f), new Vector2(0.2f, 0.2f), new Vector2(0.2f, -0.2f) };
        for (var z = zFar + 1.2f; z < zNear - 0.8f; z += 3f)
            foreach (var sgn in new[] { -1f, 1f })
            {
                var broken = rng.NextDouble() < 0.35;
                var h = broken ? 0.35f + (float)rng.NextDouble() * 0.4f : 1.15f;
                var at = new Vector3(sgn * (CausewayW * 0.5f - 0.3f), 0f, z);
                posts.Prism(sq.Select(q => new Vector2(q.x + at.x, q.y + at.z)).ToList(), 0f, h);
                if (!broken && rng.NextDouble() < 0.55 && z + 3f < zNear - 0.8f)
                {
                    var rail = new List<Vector2> { new Vector2(at.x - 0.12f, at.z), new Vector2(at.x - 0.12f, at.z + 3f), new Vector2(at.x + 0.12f, at.z + 3f), new Vector2(at.x + 0.12f, at.z) };
                    posts.Prism(rail, 0.8f, 1.0f);
                }
                if (broken) posts.Rock(rng, 0.35f, at + new Vector3(sgn * 0.5f, 0.1f, 0.6f));
            }
        Solid(root, "Causeway balustrade", Save(posts.Build("Sanctum causeway balustrade"), "CausewayBalustrade"), stoneLight, Vector3.zero, Quaternion.identity, false);

        // Stones hanging in the void beside it — the first sign that gravity is failing.
        var floaters = new Crease();
        for (var k = 0; k < 10; k++)
        {
            var sgn = k % 2 == 0 ? -1f : 1f;
            var at = new Vector3(sgn * (CausewayW * 0.5f + 2f + (float)rng.NextDouble() * 5f), -2.5f + (float)rng.NextDouble() * 6f,
                                 Mathf.Lerp(zFar, zNear, (float)rng.NextDouble()));
            floaters.Rock(rng, 0.7f + (float)rng.NextDouble() * 1.6f, at);
        }
        for (var k = 0; k < 3; k++)
            floaters.Rock(rng, 1.4f, new Vector3(((float)rng.NextDouble() - 0.5f) * 3f, -FloorThick - 0.8f - (float)rng.NextDouble(), Mathf.Lerp(zFar, zNear, k / 2f)));
        Solid(root, "Floating stones", Save(floaters.Build("Sanctum causeway floaters"), "CausewayFloaters"), stone, Vector3.zero, Quaternion.identity, false);

        for (var k = 0; k < 4; k++)
        {
            var lgo = new GameObject("Causeway light " + k);
            lgo.transform.SetParent(root, false);
            lgo.transform.localPosition = new Vector3((k % 2 == 0 ? -1f : 1f) * (CausewayW * 0.5f - 0.3f), 1.6f, Mathf.Lerp(zFar + 2f, zNear - 2f, k / 3f));
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.18f, 0.14f);
            l.intensity = 1.3f;
            l.range = 7f;
            l.shadows = LightShadows.None;
        }

        // A fall from the causeway goes back to the forecourt, never past the fog gate.
        var net = new GameObject("Causeway fall net");
        net.transform.SetParent(root, false);
        net.transform.localPosition = new Vector3(0f, -10f, midZ);
        var nb = net.AddComponent<BoxCollider>();
        nb.isTrigger = true;
        nb.size = new Vector3(26f, 6f, length + 6f);
        var fr = net.AddComponent<FallReturn>();
        var back = new GameObject("Causeway return point").transform;
        back.SetParent(root, false);
        back.localPosition = fl + new Vector3(0f, 0.3f, apothem - 3f);
        back.localRotation = Quaternion.identity;
        var so = new SerializedObject(fr);
        var rp = so.FindProperty("returnPoint");
        if (rp != null) rp.objectReferenceValue = back;
        so.ApplyModifiedPropertiesWithoutUndo();

        // The rest before the fight: scene-root object so a sanctum rebuild keeps its lit state.
        var cpPos = root.TransformPoint(fl + new Vector3(3.4f, 0f, apothem - 4f));
        var cp = Object.FindObjectsByType<Checkpoint>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(c => c.name == "Checkpoint Sanctum");
        if (cp == null) cp = new GameObject("Checkpoint Sanctum").AddComponent<Checkpoint>();
        cp.transform.SetPositionAndRotation(cpPos, root.rotation);
        var cso = new SerializedObject(cp);
        cso.FindProperty("displayName").stringValue = "Sanctum Approach";
        cso.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(cp.gameObject);
        report.Add($"Approach: forecourt (old floor footprint) + {length:F1} m causeway + Checkpoint 'Sanctum Approach' at {cpPos}.");
    }

    private static FallReturn BuildFallNet(Transform root, float size)
    {
        var go = new GameObject("Sanctum fall net");
        go.transform.SetParent(root, false);
        go.transform.localPosition = Vector3.down * 14f;
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(size, 6f, size);
        var fr = go.AddComponent<FallReturn>();
        var point = new GameObject("Fall return point").transform;
        point.SetParent(root, false);
        point.localPosition = new Vector3(0f, 0.3f, -6.5f);
        point.localRotation = Quaternion.identity;
        var so = new SerializedObject(fr);
        var rp = so.FindProperty("returnPoint");
        if (rp != null) rp.objectReferenceValue = point;
        so.ApplyModifiedPropertiesWithoutUndo();
        return fr;
    }

    private static void PlaceGate(FogGate gate, Transform root, Vector3 doorLocal)
    {
        gate.transform.SetPositionAndRotation(root.TransformPoint(doorLocal + Vector3.up * 1.6f), root.rotation);
        if (gate.TryGetComponent<BoxCollider>(out var trig))
        {
            // A few metres inside the doorway: the seal rises behind you, not on you.
            trig.isTrigger = true;
            trig.center = new Vector3(0f, 0f, 3.2f);
            trig.size = new Vector3(DoorW + 2f, 4.5f, 2.6f);
        }
        var bso = new SerializedObject(gate);
        var blocker = bso.FindProperty("blocker").objectReferenceValue as GameObject;
        if (blocker != null)
        {
            blocker.transform.localPosition = Vector3.up * (WallH * 0.5f - 1.6f - FloorThick);
            blocker.transform.localRotation = Quaternion.identity;
            blocker.transform.localScale = new Vector3(DoorW + 0.2f, WallH, WallT);
            EditorUtility.SetDirty(blocker);
        }
        EditorUtility.SetDirty(gate);
        report.Add("FogGate moved to the sanctum doorway.");
    }

    private static void WireSanctum(CoreSanctum sanctum, Transform central)
    {
        var so = new SerializedObject(sanctum);
        so.FindProperty("centralFloor").objectReferenceValue = central;
        so.FindProperty("platformRadius").floatValue = PlatformR;
        so.FindProperty("outerRadius").floatValue = OuterR;
        var fv = so.FindProperty("floorVeins");
        fv.arraySize = floorVeins.Count;
        for (var i = 0; i < floorVeins.Count; i++) fv.GetArrayElementAtIndex(i).objectReferenceValue = floorVeins[i];
        var pp = so.FindProperty("pieces");
        pp.arraySize = specs.Count;
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            var e = pp.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("root").objectReferenceValue = s.root;
            e.FindPropertyRelative("visual").objectReferenceValue = s.visual;
            e.FindPropertyRelative("role").enumValueIndex = (int)s.role;
            e.FindPropertyRelative("shatterPos").vector3Value = s.shatterPos;
            e.FindPropertyRelative("shatterEuler").vector3Value = s.shatterEuler;
            e.FindPropertyRelative("ascendOffset").vector3Value = s.ascend;
            e.FindPropertyRelative("delay").floatValue = s.delay;
            e.FindPropertyRelative("walkable").boolValue = s.walkable;
            var veins = e.FindPropertyRelative("veins");
            veins.arraySize = s.veins.Count;
            for (var v = 0; v < s.veins.Count; v++) veins.GetArrayElementAtIndex(v).objectReferenceValue = s.veins[v];
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(sanctum);
    }

    // ------------------------------------------------------------------ scene helpers

    /// <summary>Root (collider owner) → Visual → Surface (mesh). Moving pieces bob the Visual only.</summary>
    private static Transform Solid(Transform parent, string name, Mesh mesh, Material mat, Vector3 localPos, Quaternion localRot, bool meshCollider)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        var vis = new GameObject("Visual");
        vis.transform.SetParent(go.transform, false);
        var surf = new GameObject("Surface");
        surf.transform.SetParent(vis.transform, false);
        surf.AddComponent<MeshFilter>().sharedMesh = mesh;
        surf.AddComponent<MeshRenderer>().sharedMaterial = mat;
        if (meshCollider)
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;
        }
        return go.transform;
    }

    private static Renderer Strip(Transform parent, string name, Mesh mesh, Material mat, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return r;
    }

    private static void Box(Transform t, Vector3 size)
    {
        var b = t.gameObject.AddComponent<BoxCollider>();
        b.size = size;
        b.center = Vector3.zero;
    }

    private static Crease BoxMesh(Vector3 size)
    {
        var c = new Crease();
        var hx = size.x * 0.5f;
        var hz = size.z * 0.5f;
        c.Prism(new List<Vector2> { new Vector2(-hx, -hz), new Vector2(-hx, hz), new Vector2(hx, hz), new Vector2(hx, -hz) },
                -size.y * 0.5f, size.y * 0.5f);
        return c;
    }

    private static Bounds RendererBounds(GameObject go)
    {
        Bounds? b = null;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            b = b.HasValue ? Encap(b.Value, r.bounds) : r.bounds;
        return b ?? new Bounds(go.transform.position, Vector3.zero);
    }

    private static Bounds Encap(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

    /// <summary>Azimuth (degrees from +Z toward +X) → XZ unit vector. Increasing
    /// azimuth runs clockwise seen from above — Unity's front-face winding for tops.</summary>
    private static Vector2 Az(float deg)
    {
        var r = deg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }

    private static List<Vector2> Offset(List<Vector2> pts, Vector3 by)
        => pts.Select(p => new Vector2(p.x + by.x, p.y + by.z)).ToList();

    private static List<Vector3> Jagged(System.Random rng, Vector2 dir, float r0, float r1, float y)
    {
        var pts = new List<Vector3>();
        var side = new Vector2(dir.y, -dir.x);
        for (var r = r0; r <= r1; r += 0.6f)
        {
            var p = dir * r + side * ((float)rng.NextDouble() - 0.5f) * 0.5f;
            pts.Add(new Vector3(p.x, y, p.y));
        }
        return pts;
    }

    private static Mesh Save(Mesh mesh, string key) => MeshAssetWriter.Write(mesh, MeshDir + "/" + key + ".asset");

    private static void Materials()
    {
        var env = Shader.Find("Souls/Mechanical Environment");
        var useEnv = env != null && !ShaderUtil.ShaderHasError(env);
        stone = Lit("Stone", new Color(0.44f, 0.42f, 0.46f), useEnv ? env : null);
        stoneDark = Lit("StoneDark", new Color(0.31f, 0.29f, 0.33f), useEnv ? env : null);
        stoneLight = Lit("StoneLight", new Color(0.52f, 0.5f, 0.53f), useEnv ? env : null);
        corestone = Unlit("Corestone", new Color(1.6f, 0.06f, 0.1f));
        runMark = Unlit("RunMark", new Color(0.62f, 0.3f, 1.2f));
        crystal = Unlit("Crystal", new Color(2f, 0.12f, 0.16f));
        if (!useEnv) report.Add("Mechanical Environment shader missing — sanctum stone uses URP Lit.");
    }

    private static Material Lit(string name, Color c, Shader env)
    {
        var path = MatDir + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = env != null ? env : Shader.Find("Universal Render Pipeline/Lit");
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        else if (m.shader != shader) m.shader = shader;
        m.SetColor("_BaseColor", c);
        if (env != null)
        {
            m.SetFloat("_LineStrength", 0.75f);
            m.SetFloat("_LineWidth", 0.7f);
            m.SetFloat("_Saturation", 0.7f);
        }
        else m.SetFloat("_Smoothness", 0.1f);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static Material Unlit(string name, Color c)
    {
        var path = MatDir + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(m, path); }
        m.SetColor("_BaseColor", c);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // ------------------------------------------------------------------ mesh builders

    /// <summary>Faceted, unwelded mesh with the Mechanical Environment's channels:
    /// UV3 = barycentric corner, UV4 = crease mask (polygon edges ink, fan diagonals don't).
    /// Polygons are given clockwise as seen from their front.</summary>
    private sealed class Crease
    {
        private readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>(), bary = new List<Vector3>(), mask = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<int> t = new List<int>();

        public void Poly(IList<Vector3> p)
        {
            for (var i = 1; i < p.Count - 1; i++)
            {
                Vector3 a = p[0], b = p[i], c = p[i + 1];
                var nn = Vector3.Cross(b - a, c - a);
                if (nn.sqrMagnitude < 1e-10f) continue;
                nn.Normalize();
                var m = new Vector3(1f, i == p.Count - 2 ? 1f : 0f, i == 1 ? 1f : 0f);
                Add(a, nn, Vector3.right, m);
                Add(b, nn, Vector3.up, m);
                Add(c, nn, Vector3.forward, m);
            }
        }

        private void Add(Vector3 p, Vector3 nn, Vector3 b, Vector3 m)
        {
            t.Add(v.Count);
            v.Add(p);
            n.Add(nn);
            bary.Add(b);
            mask.Add(m);
            uv.Add(new Vector2(p.x + p.y * 0.5f, p.z + p.y * 0.5f) * 0.25f);
        }

        /// <summary>Extrude a convex footprint (XZ, clockwise from above) from y0 to y1.</summary>
        public void Prism(IList<Vector2> f, float y0, float y1)
        {
            var count = f.Count;
            var top = new Vector3[count];
            var bot = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                top[i] = new Vector3(f[i].x, y1, f[i].y);
                bot[i] = new Vector3(f[i].x, y0, f[i].y);
            }
            Poly(top);
            Poly(bot.Reverse().ToArray());
            for (var i = 0; i < count; i++)
            {
                var j = (i + 1) % count;
                Poly(new[] { bot[i], bot[j], top[j], top[i] });
            }
        }

        /// <summary>Annular sector prism (azimuth degrees), outer edge jittered (broken stone).</summary>
        public void Annular(float r0, float r1, float a0, float a1, int segs, float y0, float y1, float jitter, System.Random rng, Vector3 offset)
        {
            var outer = new Vector2[segs + 1];
            var inner = new Vector2[segs + 1];
            for (var i = 0; i <= segs; i++)
            {
                var a = Mathf.Lerp(a0, a1, i / (float)segs);
                var d = Az(a);
                var j = i == 0 || i == segs ? 0f : ((float)rng.NextDouble() - 0.5f) * 2f * jitter;
                outer[i] = d * (r1 + j);
                inner[i] = d * r0;
            }
            Vector3 P(Vector2 q, float y) => new Vector3(q.x, y, q.y) + offset;
            for (var i = 0; i < segs; i++)
            {
                Poly(new[] { P(outer[i], y1), P(outer[i + 1], y1), P(inner[i + 1], y1), P(inner[i], y1) });
                Poly(new[] { P(inner[i], y0), P(inner[i + 1], y0), P(outer[i + 1], y0), P(outer[i], y0) });
                Poly(new[] { P(outer[i], y0), P(outer[i + 1], y0), P(outer[i + 1], y1), P(outer[i], y1) });
                Poly(new[] { P(inner[i + 1], y0), P(inner[i], y0), P(inner[i], y1), P(inner[i + 1], y1) });
            }
            Poly(new[] { P(outer[segs], y0), P(inner[segs], y0), P(inner[segs], y1), P(outer[segs], y1) });
            Poly(new[] { P(inner[0], y0), P(outer[0], y0), P(outer[0], y1), P(inner[0], y1) });
        }

        /// <summary>An irregular faceted stone (~<paramref name="size"/> across) sitting on <paramref name="at"/>.</summary>
        public void Rock(System.Random rng, float size, Vector3 at)
        {
            var sides = 6 + rng.Next(2);
            var b = new Vector3[sides];
            var m = new Vector3[sides];
            var top = new Vector3[sides];
            var h = size * (0.6f + (float)rng.NextDouble() * 0.5f);
            var shift = new Vector3(((float)rng.NextDouble() - 0.5f) * 0.25f, 0f, ((float)rng.NextDouble() - 0.5f) * 0.25f) * size;
            for (var i = 0; i < sides; i++)
            {
                var a = i * 360f / sides + ((float)rng.NextDouble() - 0.5f) * 20f;
                var d = Az(a);
                var r = size * 0.5f * (0.75f + (float)rng.NextDouble() * 0.35f);
                b[i] = at + new Vector3(d.x * r * 0.8f, -h * 0.35f, d.y * r * 0.8f);
                m[i] = at + new Vector3(d.x * r, h * 0.15f, d.y * r);
                top[i] = at + shift + new Vector3(d.x * r * 0.55f, h * 0.65f, d.y * r * 0.55f);
            }
            Poly(b.Reverse().ToArray());
            Poly(top);
            for (var i = 0; i < sides; i++)
            {
                var j = (i + 1) % sides;
                Poly(new[] { b[i], b[j], m[j], m[i] });
                Poly(new[] { m[i], m[j], top[j], top[i] });
            }
        }

        /// <summary>A leaning crystal spike — five-sided pyramid.</summary>
        public void Spike(Vector3 at, float radius, float height, Vector3 lean, int sides)
        {
            var apex = at + Vector3.up * height + lean;
            var ring = new Vector3[sides];
            for (var i = 0; i < sides; i++)
            {
                var d = Az(i * 360f / sides);
                ring[i] = at + new Vector3(d.x, 0f, d.y) * radius;
            }
            for (var i = 0; i < sides; i++)
                Poly(new[] { ring[i], ring[(i + 1) % sides], apex });
            Poly(ring.Reverse().ToArray());
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name };
            if (v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(3, bary);
            mesh.SetUVs(4, mask);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>Flat glowing strips (Corestone veins, run marks) laid along a path.</summary>
    private sealed class Ribbons
    {
        private readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        private readonly List<int> t = new List<int>();

        /// <summary><paramref name="normal"/> = the surface the strip lies on (zero = a tube-ish
        /// strip facing outward from the Y axis, for pillars).</summary>
        public void Strip(IList<Vector3> path, Vector3 normal, float width)
        {
            for (var i = 0; i < path.Count - 1; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                var nn = normal != Vector3.zero ? normal : new Vector3(a.x, 0f, a.z).normalized;
                var side = Vector3.Cross(nn, (b - a).normalized) * width * 0.5f;
                var k = v.Count;
                v.Add(a - side); v.Add(a + side); v.Add(b + side); v.Add(b - side);
                n.Add(nn); n.Add(nn); n.Add(nn); n.Add(nn);
                t.Add(k); t.Add(k + 1); t.Add(k + 2);
                t.Add(k); t.Add(k + 2); t.Add(k + 3);
                // Back faces too — a strip must read from either side.
                t.Add(k); t.Add(k + 2); t.Add(k + 1);
                t.Add(k); t.Add(k + 3); t.Add(k + 2);
            }
        }

        public Ribbons StripReturn(IList<Vector3> path, Vector3 normal, float width)
        {
            Strip(path, normal, width);
            return this;
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    // ------------------------------------------------------------------ Play Mode debug

    [MenuItem("Tools/Project Restart/Warden/Debug - Skip to Phase 2 (Play Mode)")]
    private static void DebugPhase2() => DebugStage(1);

    [MenuItem("Tools/Project Restart/Warden/Debug - Skip to Phase 3 (Play Mode)")]
    private static void DebugPhase3() => DebugStage(2);

    [MenuItem("Tools/Project Restart/Warden/Debug - Skip to End of the Warden (Play Mode)")]
    private static void DebugFinale() => DebugStage(3);

    [MenuItem("Tools/Project Restart/Warden/Debug - Next Signature Move (Play Mode)")]
    private static void DebugMove()
    {
        var boss = PlayBoss();
        if (boss != null) Debug.Log("[Warden] Debug move: " + boss.DebugNextMove());
    }

    private static void DebugStage(int stage)
    {
        var boss = PlayBoss();
        if (boss != null) boss.DebugStage(stage);
    }

    private static BossLord PlayBoss()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[Warden] Debug commands run in Play Mode.");
            return null;
        }
        var boss = Object.FindFirstObjectByType<BossLord>();
        if (boss == null) Debug.LogWarning("[Warden] No BossLord in the scene.");
        return boss;
    }
}
