using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Solid-colour uGUI shape: parallelogram (the Persona slab), triangle,
/// diamond or N-point star. Rendered into the pixel canvas, its edges come out
/// as crisp pixel stair-steps.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class PolyGraphic : MaskableGraphic
{
    public enum Shape { Parallelogram, Triangle, Diamond, Star }

    [SerializeField] private Shape shape = Shape.Parallelogram;
    [Tooltip("Parallelogram lean: top edge shifts right by skew × height (negative leans left).")]
    [SerializeField] private float skew = 0.35f;
    [SerializeField, Range(3, 12)] private int starPoints = 4;
    [SerializeField, Range(0.05f, 0.95f)] private float starInner = 0.3f;

    public Shape ShapeType { get => shape; set { shape = value; SetVerticesDirty(); } }
    public float Skew { get => skew; set { skew = value; SetVerticesDirty(); } }
    public int StarPoints { get => starPoints; set { starPoints = Mathf.Clamp(value, 3, 12); SetVerticesDirty(); } }
    public float StarInner { get => starInner; set { starInner = Mathf.Clamp(value, 0.05f, 0.95f); SetVerticesDirty(); } }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        var r = GetPixelAdjustedRect();
        var c = r.center;
        switch (shape)
        {
            case Shape.Parallelogram:
            {
                var off = skew * r.height * 0.5f;
                Quad(vh, new Vector2(r.xMin - off, r.yMin), new Vector2(r.xMin + off, r.yMax),
                         new Vector2(r.xMax + off, r.yMax), new Vector2(r.xMax - off, r.yMin));
                break;
            }
            case Shape.Triangle:
                V(vh, new Vector2(r.xMin, r.yMin));
                V(vh, new Vector2(c.x, r.yMax));
                V(vh, new Vector2(r.xMax, r.yMin));
                vh.AddTriangle(0, 1, 2);
                break;
            case Shape.Diamond:
                Quad(vh, new Vector2(c.x, r.yMin), new Vector2(r.xMin, c.y),
                         new Vector2(c.x, r.yMax), new Vector2(r.xMax, c.y));
                break;
            case Shape.Star:
            {
                var outer = Mathf.Min(r.width, r.height) * 0.5f;
                var inner = outer * starInner;
                V(vh, c);
                var n = starPoints * 2;
                for (var i = 0; i < n; i++)
                {
                    var a = Mathf.PI * 0.5f + i * Mathf.PI * 2f / n;
                    var rad = (i % 2 == 0) ? outer : inner;
                    V(vh, c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad);
                }
                for (var i = 0; i < n; i++)
                    vh.AddTriangle(0, 1 + i, 1 + (i + 1) % n);
                break;
            }
        }
    }

    private void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 cc, Vector2 d)
    {
        var start = vh.currentVertCount;
        V(vh, a); V(vh, b); V(vh, cc); V(vh, d);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start + 2, start + 3, start);
    }

    private void V(VertexHelper vh, Vector2 p)
    {
        var v = UIVertex.simpleVert;
        v.position = p;
        v.color = color;
        vh.AddVert(v);
    }
}
