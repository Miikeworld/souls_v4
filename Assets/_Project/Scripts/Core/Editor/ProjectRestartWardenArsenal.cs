using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Warden > Bake Spectral Arsenal (also run by Setup
/// Warden Fight). Turns Synty weapons — swords, greatswords, axes, spears,
/// halberds, a scythe — into the Warden's summoned "ghost" weapons:
/// project-owned meshes in Assets/_Project/Resources/WardenArsenal/&lt;Kind&gt;_&lt;n&gt;.asset
/// that <see cref="WardenArsenal"/> loads at runtime.
/// Each bake is normalised so the runtime never guesses: grip at the origin,
/// blade/haft along +Y with the tip (or head) at y = 1, flat side on X; faceted
/// (unwelded) with flat normals and the polygon-wire vertex colours the
/// Souls/Afterimage shader reads (rg = barycentric corner, b = visible-edge bits —
/// coplanar triangulation diagonals stay hidden, so the wire shows the model's
/// real polygons). Vendor prefabs, meshes and import settings are only read.
/// Re-runnable: assets are rewritten in place (GUIDs kept).
/// </summary>
public static class ProjectRestartWardenArsenal
{
    private const string OutDir = "Assets/_Project/Resources/" + WardenArsenal.Folder;
    private const string DR = "Assets/PolygonDungeonRealms/Prefabs/Weapons/";
    private const string PD = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Weapons/";
    private const string DF = "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Weapons/";

    private static readonly (ArsenalKind kind, string[] prefabs)[] Sources =
    {
        (ArsenalKind.Sword, new[] { DR + "SM_Wep_Sword_Large_01.prefab", DR + "SM_Wep_Sword_Large_04.prefab", DR + "SM_Wep_Sword_Large_07.prefab",
                                    PD + "SM_Wep_Ornate_Sword_01.prefab", PD + "SM_Wep_Straightsword_01.prefab", DF + "SM_Wep_Sword_02.prefab" }),
        (ArsenalKind.Greatsword, new[] { PD + "SM_Wep_GreatSword_01.prefab", PD + "SM_Wep_Greatsword_Straight_01.prefab",
                                         PD + "SM_Wep_Greatsword_Curved_01.prefab", DR + "SM_Wep_Sword_Large_10.prefab" }),
        (ArsenalKind.Axe, new[] { DR + "SM_Wep_Axe_Large_01.prefab", DR + "SM_Wep_Axe_Large_03.prefab", PD + "SM_Wep_Ornate_GreatAxe_01.prefab",
                                  DF + "SM_Wep_Axe_02.prefab", DR + "SM_Wep_Axe_Large_05.prefab" }),
        (ArsenalKind.Spear, new[] { DR + "SM_Wep_Spear_01.prefab", DR + "SM_Wep_Spear_04.prefab", PD + "SM_Wep_Ornate_Spear_01.prefab", DR + "SM_Wep_Spear_06.prefab" }),
        (ArsenalKind.Halberd, new[] { DF + "SM_Wep_Halberd_01.prefab", DF + "SM_Wep_Halberd_02.prefab", PD + "SM_Wep_Halberd_06.prefab" }),
        (ArsenalKind.Scythe, new[] { DF + "SM_Wep_Scythe_01.prefab" }),
    };

    /// <summary>The REAL weapons the fight summons (WardenArmory): his own sword plus the
    /// Synty dark-fantasy, dungeon and dungeon-realms arsenal — instantiated as-is at runtime.</summary>
    private const string OwnSword = "Assets/_Project/BossPolish/BossSword.prefab";
    private static readonly (ArsenalKind kind, string[] prefabs)[] ArmorySources =
    {
        (ArsenalKind.Sword, new[] { DF + "SM_Wep_Sword_01.prefab", DF + "SM_Wep_Sword_02.prefab", DF + "SM_Wep_Sword_03.prefab",
                                    DR + "SM_Wep_Sword_Large_01.prefab", DR + "SM_Wep_Sword_Large_04.prefab", DR + "SM_Wep_Sword_Large_07.prefab",
                                    PD + "SM_Wep_Ornate_Sword_01.prefab", PD + "SM_Wep_Straightsword_01.prefab" }),
        (ArsenalKind.Greatsword, new[] { PD + "SM_Wep_GreatSword_01.prefab", PD + "SM_Wep_Greatsword_Straight_01.prefab", PD + "SM_Wep_Greatsword_Curved_01.prefab",
                                         PD + "SM_Wep_Greatsword_Round_01.prefab", DR + "SM_Wep_Sword_Large_10.prefab", DR + "SM_Wep_Sword_Large_12.prefab" }),
        (ArsenalKind.Axe, new[] { DF + "SM_Wep_Axe_01.prefab", DF + "SM_Wep_Axe_02.prefab", DR + "SM_Wep_Axe_Large_01.prefab",
                                  DR + "SM_Wep_Axe_Large_03.prefab", DR + "SM_Wep_Axe_Large_05.prefab", PD + "SM_Wep_Ornate_GreatAxe_01.prefab" }),
        (ArsenalKind.Spear, new[] { DR + "SM_Wep_Spear_01.prefab", DR + "SM_Wep_Spear_04.prefab", DR + "SM_Wep_Spear_06.prefab",
                                    PD + "SM_Wep_Ornate_Spear_01.prefab", DF + "SM_Wep_Polearm_01.prefab" }),
        (ArsenalKind.Halberd, new[] { DF + "SM_Wep_Halberd_01.prefab", DF + "SM_Wep_Halberd_02.prefab", PD + "SM_Wep_Halberd_06.prefab" }),
        (ArsenalKind.Scythe, new[] { DF + "SM_Wep_Scythe_01.prefab" }),
    };

    /// <summary>Writes Resources/WardenArmory.asset (real prefab references). The repo
    /// ships it pre-filled; a re-run picks up moved or renamed vendor files.</summary>
    public static string WriteArmory()
    {
        const string path = "Assets/_Project/Resources/" + WardenArmory.ResourceName + ".asset";
        EnsureFolder("Assets/_Project/Resources");
        var armory = AssetDatabase.LoadAssetAtPath<WardenArmory>(path);
        if (armory == null)
        {
            armory = ScriptableObject.CreateInstance<WardenArmory>();
            AssetDatabase.CreateAsset(armory, path);
        }
        var missing = new List<string>();
        GameObject[] Load(string[] paths)
        {
            var list = new List<GameObject>();
            foreach (var p in paths)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null) list.Add(go); else missing.Add(p);
            }
            return list.ToArray();
        }
        armory.ownSword = AssetDatabase.LoadAssetAtPath<GameObject>(OwnSword);
        if (armory.ownSword == null) missing.Add(OwnSword);
        foreach (var (kind, prefabs) in ArmorySources)
        {
            var found = Load(prefabs);
            switch (kind)
            {
                case ArsenalKind.Sword: armory.swords = found; break;
                case ArsenalKind.Greatsword: armory.greatswords = found; break;
                case ArsenalKind.Axe: armory.axes = found; break;
                case ArsenalKind.Spear: armory.spears = found; break;
                case ArsenalKind.Halberd: armory.halberds = found; break;
                case ArsenalKind.Scythe: armory.scythes = found; break;
            }
        }
        EditorUtility.SetDirty(armory);
        AssetDatabase.SaveAssets();
        return "Warden armory (real weapons): " + armory.swords.Length + " swords, " + armory.greatswords.Length + " greatswords, "
               + armory.axes.Length + " axes, " + armory.spears.Length + " spears, " + armory.halberds.Length + " halberds, "
               + armory.scythes.Length + " scythes, own sword " + (armory.ownSword != null ? "ok" : "MISSING")
               + (missing.Count > 0 ? ". Missing: " + string.Join(", ", missing) : ".");
    }

    [MenuItem("Tools/Project Restart/Warden/Bake Spectral Arsenal")]
    public static void BakeMenu()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[Warden] Arsenal bake deferred: exit Play Mode and wait for compilation/import to finish.");
            return;
        }
        Debug.Log("[Warden] " + Bake());
    }

    /// <summary>Bakes every kind; returns a one-paragraph report.</summary>
    public static string Bake()
    {
        EnsureFolder(OutDir);
        var lines = new List<string>();
        var total = 0;
        foreach (var (kind, prefabs) in Sources)
        {
            var n = 0;
            foreach (var path in prefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { lines.Add($"  missing {path}"); continue; }
                var mesh = BakeOne(prefab, kind, out var note);
                if (mesh == null) { lines.Add($"  skipped {prefab.name}: {note}"); continue; }
                MeshAssetWriter.Write(mesh, $"{OutDir}/{kind}_{n}.asset");
                lines.Add($"  {kind}_{n} ← {prefab.name} ({note})");
                n++;
            }
            // Drop stale higher variants from an older, longer list.
            for (var stale = n; stale < 16; stale++)
            {
                var p = $"{OutDir}/{kind}_{stale}.asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(p) == null) break;
                AssetDatabase.DeleteAsset(p);
            }
            total += n;
        }
        AssetDatabase.SaveAssets();
        return WriteArmory() + "\n" +
               $"Spectral arsenal (ghost fallback): {total} weapons baked into {OutDir}.\n" + string.Join("\n", lines);
    }

    private static Mesh BakeOne(GameObject prefab, ArsenalKind kind, out string note)
    {
        note = "";
        var root = prefab.transform;
        var pts = new List<Vector3>();
        var tris = new List<int>();
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var m = mf.sharedMesh;
            if (m == null) continue;
            var lower = mf.name.ToLowerInvariant();
            if (lower.Contains("collision") || lower.Contains("convex") || lower.Contains("lod1") || lower.Contains("lod2")) continue;
            Vector3[] v;
            int[] t;
            try { v = m.vertices; t = m.triangles; }
            catch (System.Exception e) { note = "mesh not readable (" + e.Message + ")"; return null; }
            var baseIndex = pts.Count;
            foreach (var p in v) pts.Add(root.InverseTransformPoint(mf.transform.TransformPoint(p)));
            var flip = Vector3.Dot(Vector3.Cross(mf.transform.lossyScale.x * Vector3.right, mf.transform.lossyScale.y * Vector3.up),
                                   mf.transform.lossyScale.z * Vector3.forward) < 0f;
            for (var i = 0; i < t.Length; i += 3)
            {
                tris.Add(baseIndex + t[i]);
                tris.Add(baseIndex + (flip ? t[i + 2] : t[i + 1]));
                tris.Add(baseIndex + (flip ? t[i + 1] : t[i + 2]));
            }
        }
        if (pts.Count < 3 || tris.Count < 3) { note = "no mesh"; return null; }

        // Long axis and which end is the business end.
        var min = pts[0];
        var max = pts[0];
        foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var size = max - min;
        var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        var length = size[axis];
        if (length < 0.05f) { note = "degenerate"; return null; }
        var pivot = 0f;
        var nearMin = pivot - min[axis];
        var nearMax = max[axis] - pivot;
        float tipSign;
        float gripCoord;
        if (pivot >= min[axis] - 0.01f && pivot <= max[axis] + 0.01f && Mathf.Min(nearMin, nearMax) < 0.35f * length)
        {
            // Synty hand-held weapons pivot at the grip: the far end is the tip/head.
            tipSign = nearMax >= nearMin ? 1f : -1f;
            gripCoord = pivot;
            note = "pivot grip";
        }
        else
        {
            // Pivot elsewhere: the heavier (wider) end is the head; grip near the other end.
            var wideMax = SliceWidth(pts, axis, max[axis] - 0.2f * length, max[axis]);
            var wideMin = SliceWidth(pts, axis, min[axis], min[axis] + 0.2f * length);
            tipSign = wideMax >= wideMin ? 1f : -1f;
            gripCoord = tipSign > 0f ? min[axis] + 0.12f * length : max[axis] - 0.12f * length;
            note = "head by width";
        }
        var tipCoord = tipSign > 0f ? max[axis] : min[axis];
        var reach = Mathf.Abs(tipCoord - gripCoord);
        if (reach < 0.05f) { note = "no reach"; return null; }

        // Basis: +Y along the weapon toward the tip, +X along its wider side.
        var up = Vector3.zero; up[axis] = tipSign;
        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
        var widthAxis = size[a1] >= size[a2] ? a1 : a2;
        var right = Vector3.zero; right[widthAxis] = 1f;
        var fwd = Vector3.Cross(right, up);
        var toLocal = Quaternion.Inverse(Quaternion.LookRotation(fwd, up));
        var gripPoint = Vector3.zero;
        gripPoint[axis] = gripCoord;
        var centreAcross = (min + max) * 0.5f;
        gripPoint[a1] = centreAcross[a1];
        gripPoint[a2] = centreAcross[a2];
        for (var i = 0; i < pts.Count; i++) pts[i] = toLocal * (pts[i] - gripPoint) / reach;
        // LookRotation keeps handedness, so winding survives; verify the basis anyway.
        var mirrored = Vector3.Dot(Vector3.Cross(toLocal * right, toLocal * up), toLocal * fwd) < 0f;

        var mesh = Wire(pts, tris, mirrored);
        mesh.name = $"Warden arsenal {kind} ({prefab.name})";
        note += $", {tris.Count / 3} tris, reach {reach:F2} m";
        return mesh;
    }

    private static float SliceWidth(List<Vector3> pts, int axis, float from, float to)
    {
        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
        var lo = new Vector2(float.MaxValue, float.MaxValue);
        var hi = new Vector2(float.MinValue, float.MinValue);
        var any = false;
        foreach (var p in pts)
        {
            if (p[axis] < from || p[axis] > to) continue;
            any = true;
            lo = Vector2.Min(lo, new Vector2(p[a1], p[a2]));
            hi = Vector2.Max(hi, new Vector2(p[a1], p[a2]));
        }
        return any ? (hi - lo).magnitude : 0f;
    }

    /// <summary>Unwelded, flat-shaded, wire-coloured copy (same encoding as the boss/player wire).</summary>
    private static Mesh Wire(List<Vector3> pts, List<int> tris, bool flipWinding)
    {
        var triCount = tris.Count / 3;
        var normals = new Vector3[triCount];
        for (var t = 0; t < triCount; t++)
        {
            var a = pts[tris[t * 3]];
            var b = pts[tris[t * 3 + (flipWinding ? 2 : 1)]];
            var c = pts[tris[t * 3 + (flipWinding ? 1 : 2)]];
            var n = Vector3.Cross(b - a, c - a);
            normals[t] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
        }
        // Edge → triangles sharing it (by quantised position, so split UV seams still match).
        string K(Vector3 p) => Mathf.RoundToInt(p.x * 20000f) + "," + Mathf.RoundToInt(p.y * 20000f) + "," + Mathf.RoundToInt(p.z * 20000f);
        string E(string x, string y) => string.CompareOrdinal(x, y) < 0 ? x + "|" + y : y + "|" + x;
        var keys = new string[pts.Count];
        for (var i = 0; i < pts.Count; i++) keys[i] = K(pts[i]);
        var edgeTris = new Dictionary<string, List<int>>();
        for (var t = 0; t < triCount; t++)
            for (var e = 0; e < 3; e++)
            {
                var k = E(keys[tris[t * 3 + e]], keys[tris[t * 3 + (e + 1) % 3]]);
                if (!edgeTris.TryGetValue(k, out var list)) edgeTris[k] = list = new List<int>(2);
                list.Add(t);
            }
        bool Visible(int t, int i0, int i1)
        {
            var k = E(keys[i0], keys[i1]);
            if (!edgeTris.TryGetValue(k, out var list) || list.Count != 2) return true; // open or non-manifold edge: draw it
            var other = list[0] == t ? list[1] : list[0];
            return Vector3.Dot(normals[t], normals[other]) < 0.995f;
        }
        var v = new Vector3[triCount * 3];
        var nrm = new Vector3[triCount * 3];
        var col = new Color[triCount * 3];
        var idx = new int[triCount * 3];
        for (var t = 0; t < triCount; t++)
        {
            var i0 = tris[t * 3];
            var i1 = tris[t * 3 + (flipWinding ? 2 : 1)];
            var i2 = tris[t * 3 + (flipWinding ? 1 : 2)];
            var bits = 0;
            if (Visible(t, i1, i2)) bits |= 1;
            if (Visible(t, i2, i0)) bits |= 2;
            if (Visible(t, i0, i1)) bits |= 4;
            var corners = new[] { i0, i1, i2 };
            for (var c = 0; c < 3; c++)
            {
                var o = t * 3 + c;
                v[o] = pts[corners[c]];
                nrm[o] = normals[t];
                col[o] = new Color(c == 0 ? 1f : 0f, c == 1 ? 1f : 0f, bits, 1f);
                idx[o] = o;
            }
        }
        var mesh = new Mesh();
        if (v.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = v;
        mesh.normals = nrm;
        mesh.colors = col;
        mesh.triangles = idx;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
