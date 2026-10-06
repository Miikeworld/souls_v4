using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Menu scene bootstrap: builds a Persona-style pixel UI at runtime and owns
/// the flow — Title ("press any key") → Main Menu → Character Creation → game
/// scene. Everything renders through a <see cref="PixelCanvas"/> (low-res,
/// point-upscaled) in the HUD frame's palette: tilted parallelogram slabs,
/// cascading entries with staggered overshoot entrances, violent selection,
/// a living background, a custom cursor and slab-wipe transitions.
/// </summary>
public sealed class MainMenuController : MonoBehaviour
{
    [SerializeField] private MenuCharacterPreview preview;
    [SerializeField] private CharacterCreatorController creator;
    [SerializeField] private string gameSceneName = "00_TestBlockout";
    /// <summary>The story level (Sunken Vault) once it is built into Build Settings;
    /// the serialized test scene otherwise.</summary>
    private const string StoryScene = "03_SunkenVault";
    private string GameScene => Application.CanStreamedLevelBeLoaded(StoryScene) ? StoryScene : gameSceneName;
    [SerializeField] private string gameTitle = "NEA";
    [SerializeField] private string subtitle = "MIKE MAU";
    [Tooltip("Screen pixels per UI pixel — higher = chunkier.")]
    [SerializeField, Min(1f)] private float pixelScale = 1.25f;

    private enum State { Title, Menu, Controls, Create }

    private PixelCanvas pixelCanvas;
    private PersonaBackground background;
    private Camera stageCam;
    private Quaternion stageCamBaseRot;
    private float createBlend;
    private RectTransform titlePanel, menuPanel, controlsPanel;
    private RectTransform pressRt;
    private Selectable menuFirst, controlsFirst;
    private State state = State.Title;

    /// <summary>The pixel canvas every menu panel (incl. the creator) parents to.</summary>
    public Canvas MenuCanvas => pixelCanvas != null ? pixelCanvas.Canvas : null;

    private void Awake()
    {
        Cursor.lockState = CursorLockMode.None;

        pixelCanvas = PixelCanvas.Create("MenuPixelUi", 10, pixelScale, transform);
        var root = pixelCanvas.Root;

        background = PersonaBackground.Build(root);
        titlePanel = BuildTitlePanel(root);
        menuPanel = BuildMenuPanel(root);
        controlsPanel = BuildControlsPanel(root);
        // Panel roots are only toggled, never moved — safe mid-depth layers.
        MenuParallax.Attach(titlePanel, 0.6f);
        MenuParallax.Attach(menuPanel, 0.6f);
        MenuParallax.Attach(controlsPanel, 0.6f);
        menuPanel.gameObject.SetActive(false);
        controlsPanel.gameObject.SetActive(false);
        PersonaCursor.Build(pixelCanvas);

        PersonaTransition.Reveal();
    }

    private RectTransform BuildTitlePanel(Transform root)
    {
        var p = PersonaUi.Stretch(root, "Title");
        // Title + subtitle share the same centre anchor — NEA sits squarely
        // over MIKE MAU instead of stepping right.
        PersonaUi.Heading(p, "GameTitle", gameTitle, 170, new Vector2(0.27f, 0.64f), Vector2.zero,
            new Vector2(760f, 230f), PersonaUi.Heart);
        PersonaUi.Heading(p, "Sub", subtitle, 38, new Vector2(0.27f, 0.47f), Vector2.zero,
            new Vector2(480f, 70f), PersonaUi.Night);

        pressRt = PersonaUi.Heading(p, "Press", "PRESS ANY KEY", 38, new Vector2(0.3f, 0.24f), Vector2.zero,
            new Vector2(600f, 80f), PersonaUi.Ink);
        return p;
    }

    private RectTransform BuildMenuPanel(Transform root)
    {
        var p = PersonaUi.Stretch(root, "MainMenu");
        PersonaUi.Heading(p, "Header", "MENU", 96, new Vector2(0.16f, 0.83f), Vector2.zero,
            new Vector2(520f, 130f), PersonaUi.Heart);

        // Tilted list; every row cascades a little further right.
        var list = PersonaUi.Box(p, "List", new Vector2(0.24f, 0.45f), Vector2.zero, new Vector2(760f, 560f));
        list.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt);

        var hasSave = CharacterBuildData.HasSave;
        var entries = new (string label, bool enabled, UnityEngine.Events.UnityAction act)[]
        {
            ("NEW GAME", true, () => PersonaTransition.Run(ShowCreate)),
            ("CONTINUE", hasSave, () => PersonaTransition.LoadScene(GameScene)),
            ("TRAINING", Application.CanStreamedLevelBeLoaded("02_FoundryTutorial"), () => PersonaTransition.LoadScene("02_FoundryTutorial")),
            ("CONTROLS", true, () => PersonaTransition.Run(() => Show(State.Controls))),
            ("QUIT", true, QuitGame),
        };

        const float rowH = 90f, gap = 18f, cascade = 38f;
        for (var i = 0; i < entries.Length; i++)
        {
            var y = -i * (rowH + gap);
            var rowRt = PersonaUi.Rect(list, entries[i].label, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(i * cascade, y - rowH), new Vector2(i * cascade + 600f - i * 24f, y));
            var b = PersonaUi.Button(rowRt, "Btn", entries[i].label, 58, entries[i].act, i);
            b.interactable = entries[i].enabled;
            if (menuFirst == null && entries[i].enabled) menuFirst = b;
        }

        PersonaUi.HintBar(p, "W/S  SELECT      ENTER  CONFIRM      ESC  BACK");
        return p;
    }

    private RectTransform BuildControlsPanel(Transform root)
    {
        var p = PersonaUi.Stretch(root, "Controls");

        var box = PersonaUi.Card(p, "Box", new Vector2(0.3f, 0.48f), Vector2.zero, new Vector2(980f, 780f), PersonaUi.Tilt * 0.5f);

        PersonaUi.Heading(p, "Header", "CONTROLS", 72, new Vector2(0.2f, 0.86f), Vector2.zero,
            new Vector2(560f, 110f), PersonaUi.Heart);

        // The real runtime bindings (detail.md §3): runtime actions + the
        // cloned input asset. Modifier = hold L Alt / LT; it consumes the
        // unmodified press — ALT+LMB launches, ALT+RMB is full Grave Wolf.
        const string lines =
            "MOVE            WASD / LEFT STICK\n" +
            "CAMERA          MOUSE / RIGHT STICK\n" +
            "ATTACK          LMB / RT\n" +
            "TECHNIQUE       RMB / RB\n" +
            "MODIFIER (HOLD) LEFT ALT / LT\n" +
            "SELECTED ART    Q / LB\n" +
            "ART SLOT        1·2·3 / MOD+DPAD L·R\n" +
            "ULTIMATE        T / DPAD UP\n" +
            "DRINK FLASK     R / WEST\n" +
            "FLASK MODE      WHEEL / MOD+WEST\n" +
            "USE ITEM        F / DPAD DOWN\n" +
            "CYCLE ITEM      V·C / DPAD L·R\n" +
            "LOCK ON         MIDDLE MOUSE / R3\n" +
            "JUMP            SPACE / SOUTH\n" +
            "SPRINT          LEFT CTRL / L3\n" +
            "DODGE·SLIDE·DUCK LEFT SHIFT / EAST\n" +
            "INTERACT        E / NORTH\n" +
            "ITEMS           TAB·I / SELECT\n" +
            "PAUSE           ESC / START";
        var body = PersonaUi.Rect(box, "Body", new Vector2(0f, 0.2f), new Vector2(1f, 0.95f), new Vector2(70f, 0f), new Vector2(-40f, 0f));
        var bt = PersonaUi.Label(body, "Text", lines, 26, PersonaUi.Bone, TextAnchor.UpperLeft, true, 3f);
        bt.lineSpacing = 1.15f;

        var backRt = PersonaUi.Rect(box, "Back", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(60f, 40f), new Vector2(400f, 130f));
        controlsFirst = PersonaUi.Button(backRt, "Btn", "BACK", 50, () => PersonaTransition.Run(() => Show(State.Menu)), 0);
        return p;
    }

    private void Start()
    {
        stageCam = Camera.main;
        if (stageCam != null) stageCamBaseRot = stageCam.transform.rotation;
    }

    /// <summary>
    /// Character creation: yaw the stage camera so the hero lands in the open
    /// right side of the screen (the creator panel owns the left 46%). The yaw
    /// is derived from the lens so the framing holds at any aspect ratio.
    /// </summary>
    private void FrameStage(float dt)
    {
        if (stageCam == null) return;
        createBlend = Mathf.MoveTowards(createBlend, state == State.Create ? 1f : 0f, dt * 2.5f);
        const float targetViewportX = 0.73f;
        var hTan = Mathf.Tan(stageCam.fieldOfView * 0.5f * Mathf.Deg2Rad) * stageCam.aspect;
        var yaw = Mathf.Atan(hTan * (2f * targetViewportX - 1f)) * Mathf.Rad2Deg;
        var k = createBlend * createBlend * (3f - 2f * createBlend);
        stageCam.transform.rotation = Quaternion.AngleAxis(-yaw * k, Vector3.up) * stageCamBaseRot;
    }

    private void Update()
    {
        FrameStage(Time.unscaledDeltaTime);
        if (state == State.Title)
        {
            if (pressRt != null)
            {
                // Looping press: squash down, spring back, hold — replaces the
                // hard alpha blink with a button-like press cycle.
                var t = Mathf.Repeat(Time.unscaledTime, 1.4f);
                float s;
                if (t < 0.10f) s = Mathf.Lerp(1f, 0.78f, t / 0.10f);
                else if (t < 0.26f) s = Mathf.Lerp(0.78f, 1.07f, (t - 0.10f) / 0.16f);
                else if (t < 0.48f) s = Mathf.Lerp(1.07f, 1f, (t - 0.26f) / 0.22f);
                else s = 1f;
                pressRt.localScale = new Vector3(1f + (1f - s) * 0.35f, s, 1f);
            }
            if (PersonaTransition.Busy) return;
            var kb = Keyboard.current;
            var ms = Mouse.current;
            var gp = Gamepad.current;
            if ((kb != null && kb.anyKey.wasPressedThisFrame) ||
                (ms != null && (ms.leftButton.wasPressedThisFrame || ms.rightButton.wasPressedThisFrame)) ||
                (gp != null && (gp.startButton.wasPressedThisFrame || gp.buttonSouth.wasPressedThisFrame)))
                PersonaTransition.Run(() => Show(State.Menu));
        }
        else if (state == State.Controls && BackPressed() && !PersonaTransition.Busy)
        {
            PersonaTransition.Run(() => Show(State.Menu));
        }
    }

    private static bool BackPressed()
    {
        var kb = Keyboard.current;
        var gp = Gamepad.current;
        return (kb != null && kb.escapeKey.wasPressedThisFrame) || (gp != null && gp.buttonEast.wasPressedThisFrame);
    }

    private void Show(State s)
    {
        state = s;
        background.gameObject.SetActive(true);
        createBlend = 0f; // swaps happen behind the wipe — snap the framing back
        FrameStage(0f);
        titlePanel.gameObject.SetActive(s == State.Title);
        menuPanel.gameObject.SetActive(s == State.Menu);
        controlsPanel.gameObject.SetActive(s == State.Controls);
        if (s == State.Menu) StartCoroutine(SelectNext(menuFirst));
        else if (s == State.Controls) StartCoroutine(SelectNext(controlsFirst));
    }

    private void ShowCreate()
    {
        state = State.Create;
        // Clear the stage: no decor between the camera and the hero.
        background.gameObject.SetActive(false);
        createBlend = 1f; // snap behind the wipe — no visible pan
        FrameStage(0f);
        titlePanel.gameObject.SetActive(false);
        menuPanel.gameObject.SetActive(false);
        controlsPanel.gameObject.SetActive(false);
        creator.Open(new CharacterBuildData(), StartGame, () => Show(State.Menu));
    }

    private void StartGame(CharacterBuildData build)
    {
        build.Save();
        PersonaTransition.LoadScene(GameScene);
    }

    private static IEnumerator SelectNext(Selectable s)
    {
        yield return null; // a frame, so the EventSystem exists and entrances started
        if (s == null || EventSystem.current == null) yield break;
        // Clear first: re-selecting the already-selected object fires no OnSelect.
        EventSystem.current.SetSelectedGameObject(null);
        EventSystem.current.SetSelectedGameObject(s.gameObject);
    }

    private void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.ExitPlaymode();
#else
        Application.Quit();
#endif
    }
}
