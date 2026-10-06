using UnityEngine;

/// <summary>
/// Foundry lessons use one changing input hint and a world-space practice marker.
/// Legacy volumes retain their one-shot toast behaviour.
/// </summary>
public sealed class TutorialTrigger : MonoBehaviour
{
    public enum Lesson { None, Jump, Momentum, WallRun, Combat, Backstab, Technique, Aerial, Plunge, Assessment, Checkpoint }
    [SerializeField] private string message = "MOVE — WASD";
    [SerializeField] private bool banner;
    [Header("Foundry tutorial (None preserves legacy one-shot prompts)")]
    [SerializeField] private Lesson lesson;
    [SerializeField] private string lessonTitle;
    [SerializeField] private TutorialTrigger prerequisite;
    [SerializeField] private GameObject exitGate;
    [SerializeField] private Health[] targets;
    [SerializeField] private Transform landing;
    [SerializeField] private bool practiceResources;
    public bool Completed { get; private set; }
    private bool fired;
    private PlayerLocomotion player;
    private AttackController attack;
    private TraversalEffects fx;
    private bool performed, dodged, slid, wallSeen, doubleSeen;
    private int jumpStart, doubleStart;
    private int wallJumpStart;
    private bool rested, drank;
    private int flaskStart;
    private float refreshAt, retryAt;
    private UnityEngine.UI.Text cardText;
    private RectTransform card;
    private bool finished;
    private CanvasGroup hintFade;
    private float hintChangedAt, beganAt;
    private string currentHint;
    private LineRenderer guide;
    private Material guideMaterial;

    private void OnTriggerEnter(Collider other)
    {
        if (lesson != Lesson.None) { Begin(other); return; }
        if (fired || other.GetComponentInParent<PlayerLocomotion>() == null) return;
        fired = true;
        if (banner) GameHud.Banner(message, 2.4f);
        else GameHud.Toast(message);
    }

    private void OnTriggerStay(Collider other)
    {
        if (lesson != Lesson.None && player == null && !Completed) Begin(other);
    }
    private void Begin(Collider other)
    {
        var p = other.GetComponentInParent<PlayerLocomotion>();
        if (!p || Completed || player || (prerequisite && !prerequisite.Completed)) return;
        player = p; attack = p.GetComponent<AttackController>(); fx = p.GetComponent<TraversalEffects>();
        jumpStart = fx ? fx.JumpBursts : 0; doubleStart = fx ? fx.DoubleJumpBursts : 0;
        wallJumpStart=fx?fx.WallJumpBursts:0;
        var flask=player.GetComponent<EstusFlask>();flaskStart=flask?flask.HpCharges+flask.ManaCharges:0;
        if(attack) attack.ContactLanded += Contact;
        beganAt=Time.unscaledTime;
        card = PersonaUi.Card(GameHud.WorldLayer, "LessonHint", new Vector2(0.5f,1), new Vector2(0,-98), new Vector2(570,48));
        hintFade=card.gameObject.AddComponent<CanvasGroup>();hintFade.blocksRaycasts=false;
        cardText = PersonaUi.Label(card,"Instruction","",18,PersonaUi.Bone);
        cardText.rectTransform.offsetMin = new Vector2(18,4); cardText.rectTransform.offsetMax = new Vector2(-18,-4);
        cardText.alignment = TextAnchor.MiddleCenter;
        CreateGuide();
        UpdateText();
    }
    private void UpdateText()
    {
        if(!cardText || !player) return;
        bool alt = player.GetComponent<PlayerInputSettings>()?.Scheme == PlayerInputSettings.ControlScheme.Alt;
        string jump=alt?"F / SOUTH":"SPACE / SOUTH";
        string slide=alt?"SPACE / EAST":"SHIFT / EAST";
        string sprint=alt?"HOLD SHIFT / L3":"CTRL / L3";
        string body=lesson switch {
            Lesson.Jump => Time.unscaledTime-beganAt<4 ? "WASD / STICK — FOLLOW THE EMBER" : fx && fx.JumpBursts>jumpStart ? "REACH THE EMBER" : jump+" — JUMP THE SILL",
            Lesson.Momentum => !slid ? sprint+" → "+slide+" — SLIDE" : !doubleSeen ? jump+" → "+jump+" — DOUBLE JUMP" : "REACH THE EMBER",
            Lesson.WallRun => !wallSeen ? jump+" — RUN ALONG THE VIOLET WALL" : fx && fx.WallJumpBursts<=wallJumpStart ? jump+" — PUSH OFF" : "REACH THE EMBER",
            Lesson.Combat => !dodged ? slide+" — DODGE" : "LMB / RT — DEFEAT THE GUARD",
            Lesson.Backstab => "LMB / RT — STRIKE FROM BEHIND",
            Lesson.Technique => "RMB / RB — SUMMON THE BLADE",
            Lesson.Aerial => attack && attack.InAirSession ? "LMB / RT — AIR STRIKE" : attack && attack.ComboBranch==2 ? "RMB / RB — LAUNCH" : "LMB / RT × 2 → RMB / RB",
            Lesson.Plunge => !player.GetComponent<CharacterController>().isGrounded ? "LMB / RT — PLUNGE" : player.transform.position.y>1.5f ? "STEP OFF TOWARD THE GUARD" : "CLIMB THE STEPS",
            Lesson.Checkpoint => !drank ? "R / WEST — DRINK" : !rested ? "E / NORTH — LIGHT & REST" : "REACH THE EMBER",
            Lesson.Assessment => targets!=null && System.Array.TrueForAll(targets,h=>h && h.IsDead) ? "REACH THE EXIT" : "THE YARD IS YOURS",
            _ => message
        };
        if(body==currentHint)return;
        currentHint=body;hintChangedAt=Time.unscaledTime;cardText.text=body;
    }
    private void CreateGuide()
    {
        var shader=Shader.Find("Universal Render Pipeline/Unlit");if(!shader)return;
        guideMaterial=new Material(shader);guideMaterial.SetColor("_BaseColor",new Color(1,.48f,.13f));
        var go=new GameObject("Practice Ember");guide=go.AddComponent<LineRenderer>();
        guide.sharedMaterial=guideMaterial;guide.loop=true;guide.positionCount=32;
        guide.useWorldSpace=true;guide.widthMultiplier=.045f;guide.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
    }
    private void TickGuide()
    {
        if(!guide || !player)return;
        var cc=player.GetComponent<CharacterController>();
        bool quiet=PauseMenu.IsPaused || GameLoop.IsResting || player.GetComponent<PlayerState>() is {IsDead:true};
        guide.enabled=!quiet;if(hintFade) {
            float age=Time.unscaledTime-hintChangedAt;
            // Brief first cue, then a quiet reminder rather than a permanent instruction board.
            float visible=age<5 ? Mathf.Clamp01(5-age) : age%14<2.5f ? 1 : 0;
            hintFade.alpha=quiet?0:Mathf.MoveTowards(hintFade.alpha,visible,Time.unscaledDeltaTime*5);
        }
        Vector3 goal=landing?landing.position:transform.position;
        Health target=null;if(targets!=null)foreach(var h in targets)if(h&&!h.IsDead){target=h;break;}
        if(target)goal=new Vector3(target.transform.position.x,0,target.transform.position.z);
        if(lesson==Lesson.Backstab && target)goal-=target.transform.forward*1.3f;
        if(lesson==Lesson.Momentum && !slid)goal=new Vector3(0,0,transform.position.z-3);
        if(lesson==Lesson.Jump && fx && fx.JumpBursts==jumpStart)goal=new Vector3(0,0,transform.position.z-3);
        if(lesson==Lesson.WallRun && !wallSeen)goal=new Vector3(-2.5f,0,transform.position.z-7);
        if(lesson==Lesson.Plunge && cc.isGrounded && player.transform.position.y<1.5f && !performed)goal=new Vector3(-4.5f,0,transform.position.z-11);
        if(lesson==Lesson.Checkpoint && !rested)goal=new Vector3(-3,0,transform.position.z-1);
        float radius=target?1.05f:.8f;
        float pulse=1+.09f*Mathf.Sin(Time.unscaledTime*3);
        for(int n=0;n<32;n++){float a=n*Mathf.PI*2/32;guide.SetPosition(n,goal+new Vector3(Mathf.Cos(a)*radius*pulse,.07f,Mathf.Sin(a)*radius*pulse));}
    }
    private void Contact(Health victim, DamageKind kind, bool plunge)
    {
        if(!player || Completed || targets == null || System.Array.IndexOf(targets,victim)<0) return;
        if(lesson==Lesson.Backstab && kind==DamageKind.Crit) performed=true;
        if(lesson==Lesson.Technique && attack.ActiveArt && attack.ActiveArt.BigSwordSkill) performed=true;
        if(lesson==Lesson.Aerial && attack.InAirSession && attack.ActiveArt==null) performed=true;
        if(lesson==Lesson.Plunge && plunge) performed=true;
    }
    private void Update()
    {
        if(finished && !PauseMenu.IsPaused) {
            if(UnityEngine.InputSystem.Keyboard.current?.enterKey.wasPressedThisFrame==true || UnityEngine.InputSystem.Gamepad.current?.buttonNorth.wasPressedThisFrame==true)
                PersonaTransition.LoadScene("00_MainMenu");
            return;
        }
        if(!player || Completed || lesson==Lesson.None) return;
        TickGuide();
        if(!fx) fx=player.GetComponent<TraversalEffects>();
        if(lesson==Lesson.Checkpoint) {
            rested |= GameLoop.IsResting;
            var flask=player.GetComponent<EstusFlask>();
            drank |= flask && flask.HpCharges+flask.ManaCharges<flaskStart;
        }
        if(player.GetComponent<PlayerState>() is {IsDead:true} || PauseMenu.IsPaused || GameLoop.IsResting) return;
        if(Time.unscaledTime>=refreshAt) { refreshAt=Time.unscaledTime+0.5f; UpdateText(); }
        slid |= player.GetComponent<SlideController>() is {IsSliding:true};
        dodged |= player.GetComponent<DodgeController>() is {IsDodging:true};
        wallSeen |= player.GetComponent<WallRunController>() is {AttachedToWall:true};
        doubleSeen |= fx && fx.DoubleJumpBursts>doubleStart;
        bool arrived = !landing || (Vector3.Distance(player.transform.position,landing.position)<2.5f && player.GetComponent<CharacterController>().isGrounded);
        bool dead = targets!=null && targets.Length>0 && System.Array.TrueForAll(targets,h=>h && h.IsDead);
        bool done = lesson switch {
            Lesson.Jump => fx && fx.JumpBursts>jumpStart && arrived,
            Lesson.Momentum => slid && doubleSeen && arrived,
            Lesson.WallRun => wallSeen && fx && fx.WallJumpBursts>wallJumpStart && arrived,
            Lesson.Combat => dodged && dead,
            Lesson.Backstab or Lesson.Technique or Lesson.Aerial or Lesson.Plunge => performed,
            Lesson.Assessment => dead && arrived,
            Lesson.Checkpoint => rested && drank && arrived,
            _ => false
        };
        if(done) {
            Completed=true; if(exitGate)exitGate.SetActive(false);
            // The opening gate is the reward; avoid a completion banner after every action.
            Release();
            if(lesson==Lesson.Assessment) {
                finished=true;
                card=PersonaUi.Card(GameHud.WorldLayer,"TrainingComplete",new Vector2(.5f,1),new Vector2(0,-98),new Vector2(570,48));
                cardText=PersonaUi.Label(card,"Result","ENTER / NORTH — LEAVE THE FOUNDRY",18,PersonaUi.Bone);
            }
            return;
        }
        if(lesson==Lesson.Combat && dead && !dodged && Time.time>=retryAt)
        {
            retryAt=Time.time+3f;
            foreach(var h in targets) h.GetComponent<EnemyAI>()?.Respawn();
            hintChangedAt=Time.unscaledTime;
        }
        // Practice is renewable, not a change to global costs or permanent saves.
        if(practiceResources && attack && !attack.IsAttacking && !attack.InAirSession && Time.time>=retryAt)
        {
            retryAt=Time.time+3f; player.GetComponent<PlayerMana>()?.Refill();
            if(targets!=null) foreach(var h in targets) if(h && h.IsDead) h.GetComponent<EnemyAI>()?.Respawn();
        }
    }
    private void OnTriggerExit(Collider other)
    {
        if(player && other.GetComponentInParent<PlayerLocomotion>()==player) Release();
    }
    private void OnDisable() => Release();
    private void Release()
    {
        if(attack)attack.ContactLanded-=Contact;
        if(card)Destroy(card.gameObject);
        card=null;cardText=null;player=null;attack=null;fx=null;
        hintFade=null;currentHint=null;
        if(guide)Destroy(guide.gameObject);if(guideMaterial)Destroy(guideMaterial);
        guide=null;guideMaterial=null;
    }
}
