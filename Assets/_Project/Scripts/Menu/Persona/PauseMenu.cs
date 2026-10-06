using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// In-game Esc menu in the same Persona pixel style as the front end, but
/// lighter: opening plays a quick thin-stripe streak instead of a full wipe,
/// and the slabs enter faster. Pauses via timeScale; hosts the control-scheme
/// switch that used to be the OnGUI box.
/// </summary>
public sealed class PauseMenu : MonoBehaviour
{
    public static bool IsPaused { get; private set; }

    [SerializeField] private string mainMenuScene = "00_MainMenu";

    private PlayerInputSettings settings;
    private PixelCanvas pixelCanvas;
    private PersonaButton first, schemeButton;
    private RectTransform[] stripes;
    private RectTransform content;
    private CanvasGroup fade;
    private bool leaving;
    private float animT = -1f; // <0 = settled; 0..1 menu visibility
    private int animDir = 1;
    private const float AnimIn = 0.26f, AnimOut = 0.2f;

    public static PauseMenu Attach(PlayerInputSettings owner)
    {
        var existing = FindFirstObjectByType<PauseMenu>();
        if (existing != null) return existing;
        var go = new GameObject("PauseMenu");
        var pm = go.AddComponent<PauseMenu>();
        pm.settings = owner;
        pm.Build();
        return pm;
    }

    private void Build()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        pixelCanvas = PixelCanvas.Create("PausePixelUi", 60, 1.25f, transform);
        var root = pixelCanvas.Root;
        // Entrance slides this wrapper — MenuParallax owns anchoredPosition on
        // the slabs inside, so animating them directly would fight it.
        content = PersonaUi.Stretch(root, "Content");

        var dim = PersonaUi.Stretch(content, "Dim").gameObject.AddComponent<Image>();
        dim.color = PersonaUi.WithAlpha(PersonaUi.Ink, 0.55f);

        // Slim tilted field with scrolling stripes — a lighter cousin of the
        // front-end backdrop.
        var field = PersonaUi.Box(content, "Field", new Vector2(0f, 0.5f), new Vector2(330f, 0f), new Vector2(1100f, 2200f));
        field.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt);
        MenuParallax.Attach(field, 0.35f);
        var edge = PersonaUi.Rect(field, "Edge", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-8f, 0f), new Vector2(34f, 0f));
        var edgeImg = edge.gameObject.AddComponent<Image>();
        edgeImg.color = PersonaUi.Heart;
        edgeImg.raycastTarget = false;
        var fill = PersonaUi.Stretch(field, "Fill").gameObject.AddComponent<Image>();
        fill.color = PersonaUi.WithAlpha(PersonaUi.Violet, 0.94f);
        fill.raycastTarget = false;
        stripes = new RectTransform[6];
        for (var i = 0; i < stripes.Length; i++)
        {
            var h = 14f + i % 3 * 12f;
            var s = PersonaUi.Rect(field, "Stripe" + i, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, -h * 0.5f), new Vector2(0f, h * 0.5f));
            s.anchoredPosition = new Vector2(0f, -1100f + i * 380f);
            var img = s.gameObject.AddComponent<Image>();
            img.color = PersonaUi.WithAlpha(i % 2 == 0 ? PersonaUi.Plum : PersonaUi.Night, 0.7f);
            img.raycastTarget = false;
            stripes[i] = s;
        }

        PersonaUi.Heading(content, "Header", "PAUSED", 84, new Vector2(0.19f, 0.82f), Vector2.zero,
            new Vector2(500f, 120f), PersonaUi.Heart);

        var list = PersonaUi.Box(content, "List", new Vector2(0.24f, 0.42f), Vector2.zero, new Vector2(760f, 520f));
        list.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt);
        MenuParallax.Attach(list, 0.7f);
        var entries = new (string label, UnityEngine.Events.UnityAction act)[]
        {
            ("RESUME", Close),
            ("ITEMS", InventoryMenu.Toggle),
            (SchemeLabel(), CycleScheme),
            ("MAIN MENU", ToMainMenu),
            ("QUIT", Quit),
        };
        const float rowH = 96f, gap = 22f, cascade = 34f;
        for (var i = 0; i < entries.Length; i++)
        {
            var y = -i * (rowH + gap);
            var rowRt = PersonaUi.Rect(list, "Row" + i, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(i * cascade, y - rowH), new Vector2(i * cascade + 640f - i * 20f, y));
            var b = PersonaUi.Button(rowRt, "Btn", entries[i].label, 50, entries[i].act, i);
            b.EntranceSpeed = 1.9f;
            if (i == 0) first = b;
            if (i == 2) schemeButton = b;
        }
        list.sizeDelta = new Vector2(760f, entries.Length * (rowH + gap));

        PersonaCursor.Build(pixelCanvas);
        // Menu fade rides a CanvasGroup on the pixel canvas root — every child
        // graphic's alpha multiplies into the RT, so the whole UI fades crisp.
        fade = root.gameObject.AddComponent<CanvasGroup>();
        SetShown(false);
    }

    private string SchemeLabel() =>
        settings != null && settings.Scheme == PlayerInputSettings.ControlScheme.Alt ? "CONTROLS: ALT" : "CONTROLS: DEFAULT";

    private void SetShown(bool shown)
    {
        pixelCanvas.Canvas.gameObject.SetActive(shown);
        pixelCanvas.SetVisible(shown);
    }

    private void Update()
    {
        var kb = Keyboard.current;
        var gp = Gamepad.current;
        var toggle = (kb != null && kb.escapeKey.wasPressedThisFrame) || (gp != null && gp.startButton.wasPressedThisFrame);
        // Other menus own Esc while open (and on the frame they closed).
        if (CheckpointMenu.IsOpen || CheckpointMenu.ClosedFrame == Time.frameCount
            || InventoryMenu.IsOpen || InventoryMenu.ClosedFrame == Time.frameCount
            || ShopMenu.IsOpen || ShopMenu.ClosedFrame == Time.frameCount) toggle = false;
        if (toggle && !leaving && animDir > 0)
        {
            if (IsPaused) Close();
            else Open();
        }

        // Entrance/exit: overshoot scale-pop on the slabs + whole-menu fade.
        // Scale, not position — MenuParallax owns anchoredPosition on these.
        if (animT >= 0f)
        {
            animT += animDir * Time.unscaledDeltaTime / (animDir > 0 ? AnimIn : AnimOut);
            ApplyAnim(Mathf.Clamp01(animT));
            if (animDir > 0 ? animT >= 1f : animT <= 0f)
            {
                animT = -1f;
                if (animDir < 0) { FinishClose(); animDir = 1; }
                else fade.alpha = 1f;
            }
        }

        if (!IsPaused) return;
        var dt = Time.unscaledDeltaTime;
        foreach (var s in stripes)
        {
            var p = s.anchoredPosition;
            p.y += 40f * dt;
            if (p.y > 1100f) p.y -= 2200f;
            s.anchoredPosition = p;
        }
    }

    private void Open()
    {
        IsPaused = true;
        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        if (schemeButton != null) schemeButton.SetLabel(SchemeLabel());
        SetShown(true);
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(first.gameObject);
        }
        fade.blocksRaycasts = true;
        animDir = 1;
        animT = 0f;
        ApplyAnim(0f);
    }

    private void Close()
    {
        if (animDir < 0) return; // already exiting
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        fade.blocksRaycasts = false;
        animDir = -1;
        animT = animT < 0f ? 1f : Mathf.Clamp01(animT); // reverse mid-entrance
        // IsPaused + timeScale release when the exit anim finishes.
    }

    private void FinishClose()
    {
        IsPaused = false;
        Time.timeScale = 1f;
        SetShown(false); // disables PersonaCursor → it re-shows the OS cursor…
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false; // …so hide it again for gameplay
    }

    private void ApplyAnim(float t)
    {
        fade.alpha = t;
        // Same punchy slide as the buttons: in from the left with EaseOutBack
        // overshoot (their EnterDistance 900 / overshoot 2.2); exit accelerates out.
        var x = animDir > 0
            ? -900f * (1f - PersonaUi.EaseOutBack(t, 2.2f))
            : -900f * Mathf.Pow(1f - t, 3f);
        content.anchoredPosition = new Vector2(x, 0f);
    }

    private void CycleScheme()
    {
        if (settings == null) return;
        var next = settings.Scheme == PlayerInputSettings.ControlScheme.Default
            ? PlayerInputSettings.ControlScheme.Alt
            : PlayerInputSettings.ControlScheme.Default;
        settings.SetScheme(next);
        schemeButton.SetLabel(SchemeLabel());
    }

    private void ToMainMenu()
    {
        leaving = true;
        IsPaused = false;
        Time.timeScale = 1f;
        PersonaTransition.LoadScene(mainMenuScene);
    }

    private void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.ExitPlaymode();
#else
        Application.Quit();
#endif
    }

    private void OnDestroy()
    {
        if (!IsPaused) return;
        IsPaused = false;
        Time.timeScale = 1f;
    }
}
