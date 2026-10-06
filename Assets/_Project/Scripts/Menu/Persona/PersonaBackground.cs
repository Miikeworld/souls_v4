using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The living menu backdrop: a big tilted violet field edged in heart-red and
/// ink with scrolling stripes inside it, drifting/spinning palette shapes, and
/// rising pixel embers — the whole thing sways a hair so nothing is ever
/// static. The right side stays open so the 3D hero shows through.
/// </summary>
public sealed class PersonaBackground : MonoBehaviour
{
    private const float W = 1920f, H = 1080f;

    private RectTransform sway;
    private RectTransform[] stripes;
    private float[] stripeSpeed;
    private RectTransform[] shapes;
    private Vector2[] shapeVel;
    private float[] shapeSpin;
    private RectTransform[] embers;
    private Graphic[] emberG;
    private float[] emberSpeed, emberPhase, emberBaseA;
    private float fieldHeight;

    /// <summary><paramref name="compact"/> = the in-game variant (checkpoint,
    /// satchel, shop): a narrower field so the world stays visible on the
    /// right, no accent slabs or drifting shapes, fewer embers.</summary>
    public static PersonaBackground Build(Transform parent, bool compact = false)
    {
        var rt = PersonaUi.Stretch(parent, "PersonaBackground");
        var bg = rt.gameObject.AddComponent<PersonaBackground>();
        bg.Construct(rt, compact);
        return bg;
    }

    private void Construct(RectTransform root, bool compact)
    {
        sway = PersonaUi.Stretch(root, "Sway");

        // Tilted field — anchored left, oversized so its rotated edges never show.
        var field = PersonaUi.Box(sway, "Field", new Vector2(0f, 0.5f),
            new Vector2(compact ? 300f : 380f, 0f), new Vector2(compact ? 1100f : 1500f, 2200f));
        field.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt);
        MenuParallax.Attach(field, 0.25f); // deepest layer
        fieldHeight = 2200f;

        var inkEdge = PersonaUi.Rect(field, "InkEdge", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(70f, 0f));
        inkEdge.gameObject.AddComponent<Image>().color = PersonaUi.Ink;
        inkEdge.GetComponent<Image>().raycastTarget = false;
        var redEdge = PersonaUi.Rect(field, "HeartEdge", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-10f, 0f), new Vector2(46f, 0f));
        var redImg = redEdge.gameObject.AddComponent<Image>();
        redImg.color = PersonaUi.Heart;
        redImg.raycastTarget = false;
        var fill = PersonaUi.Stretch(field, "Fill").gameObject.AddComponent<Image>();
        fill.color = PersonaUi.WithAlpha(PersonaUi.Violet, 0.93f);
        fill.raycastTarget = false;

        // Stripes scroll upward inside the field (horizontal in field space =
        // tilted on screen).
        var stripeRoot = PersonaUi.Stretch(field, "Stripes");
        MenuParallax.Attach(stripeRoot, 0.1f); // stacks on the field's 0.25
        var n = 12;
        stripes = new RectTransform[n];
        stripeSpeed = new float[n];
        for (var i = 0; i < n; i++)
        {
            var h = Random.Range(10f, 46f);
            var s = PersonaUi.Rect(stripeRoot, "Stripe" + i, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, -h * 0.5f), new Vector2(0f, h * 0.5f));
            s.anchoredPosition = new Vector2(0f, Random.Range(-fieldHeight * 0.5f, fieldHeight * 0.5f));
            var img = s.gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = i % 3 == 0 ? PersonaUi.WithAlpha(PersonaUi.Plum, 0.8f)
                      : i % 3 == 1 ? PersonaUi.WithAlpha(PersonaUi.Night, 0.55f)
                      : PersonaUi.WithAlpha(PersonaUi.Rust, 0.35f);
            stripes[i] = s;
            stripeSpeed[i] = Random.Range(18f, 70f);
        }

        // A few big accent slabs cutting across the right side.
        if (!compact)
        {
            Accent(sway, new Vector2(0.78f, 0.1f), new Vector2(900f, 60f), PersonaUi.WithAlpha(PersonaUi.Heart, 0.85f));
            Accent(sway, new Vector2(0.84f, 0.16f), new Vector2(700f, 22f), PersonaUi.Ink);
            Accent(sway, new Vector2(0.9f, 0.93f), new Vector2(620f, 34f), PersonaUi.WithAlpha(PersonaUi.Copper, 0.8f));
        }

        // Drifting shapes.
        var shapeRoot = PersonaUi.Stretch(sway, "Shapes");
        MenuParallax.Attach(shapeRoot, 0.5f);
        var shapeCount = compact ? 0 : 18;
        shapes = new RectTransform[shapeCount];
        shapeVel = new Vector2[shapeCount];
        shapeSpin = new float[shapeCount];
        var palette = new[]
        {
            PersonaUi.WithAlpha(PersonaUi.Copper, 0.45f), PersonaUi.WithAlpha(PersonaUi.Heart, 0.55f),
            PersonaUi.WithAlpha(PersonaUi.Bone, 0.22f), PersonaUi.WithAlpha(PersonaUi.Plum, 0.8f),
            PersonaUi.WithAlpha(PersonaUi.Rust, 0.6f),
        };
        for (var i = 0; i < shapeCount; i++)
        {
            var size = Random.Range(28f, 150f);
            var rt = PersonaUi.Rect(shapeRoot, "Shape" + i, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            rt.sizeDelta = new Vector2(size * Random.Range(0.8f, 1.8f), size);
            rt.anchoredPosition = new Vector2(Random.Range(0f, W), Random.Range(0f, H));
            rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            var g = rt.gameObject.AddComponent<PolyGraphic>();
            g.raycastTarget = false;
            g.ShapeType = (PolyGraphic.Shape)(i % 4);
            g.Skew = 0.6f;
            g.StarPoints = 4;
            g.color = palette[i % palette.Length];
            shapes[i] = rt;
            shapeVel[i] = new Vector2(Random.Range(-26f, 26f), Random.Range(8f, 34f));
            shapeSpin[i] = Random.Range(-45f, 45f);
        }

        // Rising pixel embers.
        var emberRoot = PersonaUi.Stretch(sway, "Embers");
        MenuParallax.Attach(emberRoot, 0.8f); // embers read as closest particles
        var emberCount = compact ? 24 : 48;
        embers = new RectTransform[emberCount];
        emberG = new Graphic[emberCount];
        emberSpeed = new float[emberCount];
        emberPhase = new float[emberCount];
        emberBaseA = new float[emberCount];
        for (var i = 0; i < emberCount; i++)
        {
            var rt = PersonaUi.Rect(emberRoot, "Ember" + i, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            var px = Random.value < 0.7f ? 6f : 10f;
            rt.sizeDelta = new Vector2(px, px);
            rt.anchoredPosition = new Vector2(Random.Range(0f, W), Random.Range(0f, H));
            var img = rt.gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            var c = i % 3 == 0 ? PersonaUi.Heart : i % 3 == 1 ? PersonaUi.Bone : PersonaUi.Copper;
            emberBaseA[i] = Random.Range(0.35f, 0.9f);
            img.color = PersonaUi.WithAlpha(c, emberBaseA[i]);
            embers[i] = rt;
            emberG[i] = img;
            emberSpeed[i] = Random.Range(28f, 95f);
            emberPhase[i] = Random.Range(0f, 10f);
        }
    }

    private static void Accent(Transform parent, Vector2 anchor, Vector2 size, Color color)
    {
        var rt = PersonaUi.Box(parent, "Accent", anchor, Vector2.zero, size);
        rt.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt);
        var g = PersonaUi.Poly(rt, "Slab", PolyGraphic.Shape.Parallelogram, color, 1.2f);
        g.gameObject.AddComponent<UiFloat>().Configure(14f, 3f, 0.4f, Random.value * 10f, 0.6f);
    }

    private void Update()
    {
        var dt = Time.unscaledDeltaTime;
        var t = Time.unscaledTime;

        sway.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 0.35f) * 0.6f);
        sway.anchoredPosition = new Vector2(Mathf.Sin(t * 0.27f) * 6f, Mathf.Sin(t * 0.41f) * 4f);

        var half = fieldHeight * 0.5f;
        for (var i = 0; i < stripes.Length; i++)
        {
            var p = stripes[i].anchoredPosition;
            p.y += stripeSpeed[i] * dt;
            if (p.y > half) p.y -= fieldHeight;
            stripes[i].anchoredPosition = p;
        }

        for (var i = 0; i < shapes.Length; i++)
        {
            var p = shapes[i].anchoredPosition + shapeVel[i] * dt;
            if (p.y > H + 160f) { p.y = -160f; p.x = Random.Range(0f, W); }
            if (p.x < -160f) p.x = W + 160f;
            else if (p.x > W + 160f) p.x = -160f;
            shapes[i].anchoredPosition = p;
            shapes[i].localRotation *= Quaternion.Euler(0f, 0f, shapeSpin[i] * dt);
        }

        for (var i = 0; i < embers.Length; i++)
        {
            var p = embers[i].anchoredPosition;
            p.y += emberSpeed[i] * dt;
            p.x += Mathf.Sin(t * 1.7f + emberPhase[i]) * 14f * dt;
            if (p.y > H + 20f) { p.y = -20f; p.x = Random.Range(0f, W); }
            embers[i].anchoredPosition = p;
            var c = emberG[i].color;
            c.a = emberBaseA[i] * (0.55f + 0.45f * Mathf.Sin(t * 5f + emberPhase[i]));
            emberG[i].color = c;
        }
    }
}
