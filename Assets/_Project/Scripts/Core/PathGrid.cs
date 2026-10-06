using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime-baked walkability grid + A* for the blockout. At Awake the grid
/// probes each cell with a waist-height overlap — walls/props block, the flat
/// floor and low steps stay walkable — and rejects cells with no ground under
/// them. EnemyAI asks for FindPath only when direct line-of-sight chase fails;
/// the returned waypoints are line-of-sight smoothed so paths don't zig-zag.
/// Cells containing the player or an enemy are walkable (bodies don't block
/// the nav grid — the CharacterController handles collision between agents).
/// </summary>
[DefaultExecutionOrder(-50)]
public sealed class PathGrid : MonoBehaviour
{
    [Header("Area (set by the setup tool, or size by hand)")]
    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector2 size = new Vector2(24f, 24f);
    [SerializeField, Min(0.2f)] private float cellSize = 0.5f;

    [Header("Bake")]
    [Tooltip("A cell is blocked if this box at probeHeight overlaps an obstacle collider.")]
    [SerializeField, Min(0.2f)] private float probeHeight = 0.55f;
    [SerializeField, Min(0.5f)] private float groundProbeTop = 3f;
    [SerializeField] private LayerMask obstacleMask = ~0;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos;

    private int cols, rows;
    private bool[] blocked;
    private float[] groundY;
    private Vector3 origin; // world position of cell (0,0) corner

    public static PathGrid Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
        Bake();
    }

    /// <summary>Sizes the grid around a world-space bounds (ground plane).</summary>
    public void Configure(Vector3 c, Vector2 s)
    {
        center = c;
        size = s;
    }

    public void Bake()
    {
        cols = Mathf.Max(1, Mathf.CeilToInt(size.x / cellSize));
        rows = Mathf.Max(1, Mathf.CeilToInt(size.y / cellSize));
        origin = center + new Vector3(-size.x * 0.5f, 0f, -size.y * 0.5f);
        blocked = new bool[cols * rows];
        groundY = new float[cols * rows];
        var half = new Vector3(cellSize * 0.45f, 0.3f, cellSize * 0.45f);
        var hits = new Collider[8];

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var i = r * cols + c;
            var wc = CellCenter(c, r);
            // No floor below → hole, not walkable.
            if (!Physics.Raycast(wc + Vector3.up * groundProbeTop, Vector3.down, out var gh,
                                 groundProbeTop + 3f, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                blocked[i] = true;
                continue;
            }
            groundY[i] = gh.point.y;
            // Waist-height probe: walls and tall props block; floors, steps and
            // low debris pass under it.
            var n = Physics.OverlapBoxNonAlloc(wc + Vector3.up * probeHeight, half, hits,
                                               Quaternion.identity, obstacleMask, QueryTriggerInteraction.Ignore);
            for (var h = 0; h < n; h++)
            {
                var hit = hits[h];
                if (hit == null || hit.isTrigger) continue;
                if (hit.GetComponentInParent<EnemyAI>() != null) continue;   // agents don't block nav
                if (hit.GetComponentInParent<PlayerLocomotion>() != null) continue;
                if (hit.GetComponentInParent<CharacterController>() != null) continue;
                blocked[i] = true;
                break;
            }
        }
    }

    /// <summary>Straight-line visibility at chest height — used for aggro sight and chase-direct.</summary>
    public bool HasLine(Vector3 a, Vector3 b)
    {
        // Lift to at least chest height — never above the height callers passed.
        a.y = Mathf.Max(a.y, CellGroundAt(a) + 1.2f);
        b.y = Mathf.Max(b.y, CellGroundAt(b) + 1.2f);
        var d = b - a;
        // Agents' own capsules aren't obstacles — a ray ending inside the
        // target's capsule hits it early, so treat agent hits as clear.
        return !Physics.Raycast(a, d.normalized, out var hit, d.magnitude, obstacleMask, QueryTriggerInteraction.Ignore)
               || hit.collider.GetComponentInParent<CharacterController>() != null;
    }

    /// <summary>Visibility test that counts "first hit is the player" as seen.</summary>
    public bool CanSee(Vector3 eye, Transform target)
    {
        var chest = target.position + Vector3.up * 1.2f;
        var d = chest - eye;
        if (!Physics.Raycast(eye, d.normalized, out var hit, d.magnitude, obstacleMask, QueryTriggerInteraction.Ignore))
            return true; // nothing between
        return hit.collider.GetComponentInParent<PlayerLocomotion>() != null;
    }

    /// <summary>Fills result with smoothed world waypoints; false if no path.</summary>
    public bool FindPath(Vector3 from, Vector3 to, List<Vector3> result)
    {
        result.Clear();
        if (blocked == null) Bake();
        var sc = WorldToCell(from, out var sx, out var sr);
        var dc = WorldToCell(to, out var dx, out var dr);
        if (!sc || !dc) return false;
        if (!NearestWalkable(ref sx, ref sr) || !NearestWalkable(ref dx, ref dr)) return false;

        var start = sr * cols + sx;
        var dest = dr * cols + dx;
        if (start == dest)
        {
            result.Add(new Vector3(to.x, groundY[dest], to.z));
            return true;
        }

        var count = cols * rows;
        var g = _g; var parent = _parent; var closed = _closed;
        if (g == null || g.Length < count)
        {
            g = _g = new float[count];
            parent = _parent = new int[count];
            closed = _closed = new bool[count];
        }
        System.Array.Clear(closed, 0, count);
        for (var i = 0; i < count; i++)
        {
            g[i] = float.MaxValue;
            parent[i] = -1; // stale parents from a previous call corrupt reconstruction
        }
        g[start] = 0f;
        parent[start] = -1;
        _open.Clear();
        _open.Add(start);

        while (_open.Count > 0)
        {
            // Linear-scan min — grids here are small and pops are few.
            var bi = 0;
            var bf = float.MaxValue;
            for (var i = 0; i < _open.Count; i++)
            {
                var n = _open[i];
                var f = g[n] + Heuristic(n % cols, n / cols, dx, dr);
                if (f < bf) { bf = f; bi = i; }
            }
            var cur = _open[bi];
            _open.RemoveAt(bi);
            if (cur == dest) break;
            if (closed[cur]) continue;
            closed[cur] = true;

            var cx = cur % cols;
            var cr = cur / cols;
            for (var dy = -1; dy <= 1; dy++)
            for (var dxc = -1; dxc <= 1; dxc++)
            {
                if (dxc == 0 && dy == 0) continue;
                var nx = cx + dxc; var nr = cr + dy;
                if (nx < 0 || nr < 0 || nx >= cols || nr >= rows) continue;
                var ni = nr * cols + nx;
                if (blocked[ni] || closed[ni]) continue;
                // No diagonal corner-cutting through blocked orthogonals.
                if (dxc != 0 && dy != 0 &&
                    (blocked[cr * cols + nx] || blocked[nr * cols + cx])) continue;
                var step = (dxc != 0 && dy != 0) ? 1.41421356f : 1f;
                var ng = g[cur] + step;
                if (ng >= g[ni]) continue;
                g[ni] = ng;
                parent[ni] = cur;
                if (!closed[ni] && !_open.Contains(ni)) _open.Add(ni);
            }
        }

        if (parent[dest] == -1 && dest != start) return false;

        // Reconstruct cell path then smooth: keep a waypoint only when the
        // straight run to the next one can't stay on walkable, clear cells.
        _cells.Clear();
        for (var n = dest; n != -1; n = parent[n]) _cells.Add(n);
        _cells.Reverse();
        var a = 0;
        while (a < _cells.Count - 1)
        {
            var b = a + 1;
            while (b < _cells.Count && WalkableLine(_cells[a], _cells[b])) b++;
            result.Add(CellCenter(_cells[b - 1] % cols, _cells[b - 1] / cols));
            a = b - 1;
        }
        if (result.Count > 0)
            result[result.Count - 1] = new Vector3(to.x, result[result.Count - 1].y, to.z);
        return result.Count > 0;
    }

    // scratch buffers shared across calls (single-threaded game code)
    private readonly List<int> _open = new();
    private readonly List<int> _cells = new();
    private float[] _g;
    private int[] _parent;
    private bool[] _closed;

    private static float Heuristic(int x, int r, int dx, int dr)
    {
        var ax = Mathf.Abs(x - dx); var ar = Mathf.Abs(r - dr);
        return Mathf.Max(ax, ar) + 0.41421356f * Mathf.Min(ax, ar);
    }

    private bool WalkableLine(int a, int b)
    {
        // Sample the segment at half-cell steps — every cell crossed must be clear.
        var ax = a % cols; var ar = a / cols;
        var bx = b % cols; var br = b / cols;
        var steps = Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(br - ar)) * 2;
        for (var s = 1; s < steps; s++)
        {
            var t = (float)s / steps;
            var x = Mathf.RoundToInt(Mathf.Lerp(ax, bx, t));
            var r = Mathf.RoundToInt(Mathf.Lerp(ar, br, t));
            if (blocked[r * cols + x]) return false;
        }
        var pa = CellCenter(ax, ar); var pb = CellCenter(bx, br);
        pa.y += probeHeight; pb.y += probeHeight;
        var d = pb - pa;
        return !Physics.Raycast(pa, d.normalized, out var hit, d.magnitude, obstacleMask, QueryTriggerInteraction.Ignore)
               || hit.collider.GetComponentInParent<CharacterController>() != null;
    }

    private bool WorldToCell(Vector3 w, out int x, out int r)
    {
        x = Mathf.FloorToInt((w.x - origin.x) / cellSize);
        r = Mathf.FloorToInt((w.z - origin.z) / cellSize);
        return x >= 0 && r >= 0 && x < cols && r < rows;
    }

    private bool NearestWalkable(ref int x, ref int r)
    {
        if (!blocked[r * cols + x]) return true;
        for (var ring = 1; ring <= 4; ring++)
            for (var dy = -ring; dy <= ring; dy++)
            for (var dx = -ring; dx <= ring; dx++)
            {
                var nx = x + dx; var nr = r + dy;
                if (nx < 0 || nr < 0 || nx >= cols || nr >= rows) continue;
                if (!blocked[nr * cols + nx]) { x = nx; r = nr; return true; }
            }
        return false;
    }

    private Vector3 CellCenter(int x, int r)
    {
        var i = r * cols + x;
        var p = origin + new Vector3((x + 0.5f) * cellSize, 0f, (r + 0.5f) * cellSize);
        p.y = groundY != null && i < groundY.Length ? groundY[i] : center.y;
        return p;
    }

    private float CellGroundAt(Vector3 w)
    {
        return WorldToCell(w, out var x, out var r) ? groundY[r * cols + x] : w.y;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos || blocked == null) return;
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            if (!blocked[r * cols + c]) continue;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.35f);
            Gizmos.DrawCube(CellCenter(c, r) + Vector3.up * 0.3f,
                            new Vector3(cellSize * 0.9f, 0.6f, cellSize * 0.9f));
        }
    }
}
