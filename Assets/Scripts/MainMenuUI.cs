using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Shooter-style main menu, built entirely in code.
/// Left: huge angled title + buttons. Right: your 3D heroes (MenuStage) lit by MenuCameraRig.
/// Also owns the in-game room-code pill and the Esc pause menu.
/// Put on the NetworkManager object.
/// </summary>
public class MainMenuUI : MonoBehaviour
{
    [Header("Scene objects")]
    public Camera menuCamera;
    public GameObject menuStage;      // parent of the 2 hero characters, hidden during the match

    [Header("Look")]
    public string gameTitle = "TEAM DEATHMATCH";
    public string subtitle = "5 VS 5  //  ONLINE";
    public Font uiFont;               // optional: a bold condensed font (Bebas Neue, Oswald, Teko...)
    public Sprite logoSprite;         // optional: replaces the title text

    const float Sk = 0.2f;
    static readonly Vector2 L = new Vector2(0f, 0.5f);   // left-middle anchor
    static readonly Vector2 C = new Vector2(0.5f, 0.5f);

    NetworkBootstrap boot;
    GameObject menuRoot, mainPanel, modalDim, createPanel, joinPanel, busyPanel, hudRoot, pausePanel;
    Text mainMsg, createMsg, joinMsg, busyText, playersValue, hudCode, pauseCode;
    InputField nameField, codeField;
    int players = 4;
    int killsToWin = 30;
    const int KillsStep = 5, KillsMin = 5, KillsMax = 100;   // kills-to-win selector: multiples of 5
    Text killsValue;
    bool inGame;

    // ------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (NetworkBootstrap.IsDedicatedServerMode())
        {
            if (menuStage != null) menuStage.SetActive(false);
            enabled = false;
            return;
        }

        if (uiFont != null) UIKit.Font = uiFont;
        EnsureEventSystem();
        BuildUI();
    }

    void Start()
    {
        boot = NetworkBootstrap.Instance;
        if (boot == null) { Debug.LogError("MainMenuUI: NetworkBootstrap not found on the NetworkManager object."); return; }
        boot.OnSessionStarted += EnterGame;
        boot.OnSessionEnded += ExitGame;
        ShowMain("");
        SetStage(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void OnDestroy()
    {
        if (boot == null) return;
        boot.OnSessionStarted -= EnterGame;
        boot.OnSessionEnded -= ExitGame;
    }

    void Update()
    {
        if (!inGame) return;
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SetPause(!pausePanel.activeSelf);
    }

    // ------------------------------------------------------------ flow

    void ShowMain(string msg)
    {
        menuRoot.SetActive(true);
        mainPanel.SetActive(true);
        modalDim.SetActive(false);
        createPanel.SetActive(false);
        joinPanel.SetActive(false);
        busyPanel.SetActive(false);
        hudRoot.SetActive(false);
        pausePanel.SetActive(false);
        mainMsg.text = msg;
        nameField.text = PlayerProfile.LoadName();
    }

    void ShowCreate()
    {
        modalDim.SetActive(true);
        createPanel.SetActive(true);
        joinPanel.SetActive(false);
        createMsg.text = "";
        SetPlayers(players);
        SetKills(killsToWin);
    }

    void ShowJoin()
    {
        modalDim.SetActive(true);
        joinPanel.SetActive(true);
        createPanel.SetActive(false);
        joinMsg.text = "";
        codeField.text = "";
    }

    void CloseModals()
    {
        modalDim.SetActive(false);
        createPanel.SetActive(false);
        joinPanel.SetActive(false);
    }

    void SetPlayers(int n)
    {
        players = Mathf.Clamp(n, 2, 10);
        playersValue.text = players.ToString();
    }

    void SetKills(int n)
    {
        // snap to a multiple of KillsStep, then clamp
        int snapped = Mathf.RoundToInt(n / (float)KillsStep) * KillsStep;
        killsToWin = Mathf.Clamp(snapped, KillsMin, KillsMax);
        killsValue.text = killsToWin.ToString();
    }

    async void ConfirmCreate()
    {
        if (boot == null || boot.Busy) return;
        busyText.text = "CREATING ROOM...";
        busyPanel.SetActive(true);

        var mm = FindFirstObjectByType<MatchManager>();
        if (mm != null) mm.scoreLimit = killsToWin;
        else Debug.LogWarning("MainMenuUI: no MatchManager in the scene, kills-to-win will not apply.");

        bool ok = await boot.CreateRoomAsync(players);
        busyPanel.SetActive(false);
        if (!ok) createMsg.text = boot.Status;
    }

    async void ConfirmJoin()
    {
        if (boot == null || boot.Busy) return;
        busyText.text = "JOINING ROOM...";
        busyPanel.SetActive(true);
        bool ok = await boot.JoinRoomAsync(codeField.text);
        busyPanel.SetActive(false);
        if (!ok) joinMsg.text = boot.Status;
    }

    void EnterGame()
    {
        inGame = true;
        menuRoot.SetActive(false);
        busyPanel.SetActive(false);
        pausePanel.SetActive(false);
        hudRoot.SetActive(true);
        hudCode.text = "ROOM  " + boot.RoomCode + "     ESC: MENU";
        pauseCode.text = boot.RoomCode;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        StartCoroutine(HideStageWhenPlayerExists());
    }

    void ExitGame(string reason)
    {
        inGame = false;
        StopAllCoroutines();
        SetStage(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        ShowMain(reason);
    }

    IEnumerator HideStageWhenPlayerExists()
    {
        var nm = NetworkManager.Singleton;
        float t = 0f;
        while (t < 10f && (nm.LocalClient == null || nm.LocalClient.PlayerObject == null))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        yield return null;
        SetStage(false);
    }

    void SetPause(bool on)
    {
        pausePanel.SetActive(on);
        Cursor.lockState = on ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = on;
    }

    void SetStage(bool on)
    {
        if (menuCamera != null)
        {
            menuCamera.enabled = on;
            var al = menuCamera.GetComponent<AudioListener>();
            if (al != null) al.enabled = on;
        }
        if (menuStage != null) menuStage.SetActive(on);
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ------------------------------------------------------------ UI construction

    void BuildUI()
    {
        var cgo = new GameObject("MenuCanvas");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();
        Transform root = cgo.transform;

        // ---------------- menu root
        var menuRt = UIKit.Rect("MenuRoot", root); UIKit.Fill(menuRt);
        menuRoot = menuRt.gameObject;

        var vig = UIKit.Img(menuRt, "Vignette", Color.black, UIKit.Vignette);
        UIKit.Fill(vig.rectTransform);

        var shade = UIKit.Img(menuRt, "LeftShade", new Color(0f, 0f, 0f, 0.9f), UIKit.GradientRight);
        shade.rectTransform.anchorMin = new Vector2(0f, 0f);
        shade.rectTransform.anchorMax = new Vector2(0f, 1f);
        shade.rectTransform.pivot = new Vector2(0f, 0.5f);
        shade.rectTransform.anchoredPosition = Vector2.zero;
        shade.rectTransform.sizeDelta = new Vector2(1150f, 0f);

        // ---------------- main panel
        var main = UIKit.Rect("Main", menuRt); UIKit.Fill(main);
        mainPanel = main.gameObject;

        var stripe = UIKit.Img(main, "Stripe", UIKit.Amber);
        UIKit.Place(stripe.rectTransform, L, new Vector2(70f, 250f), new Vector2(12f, 150f));
        UIKit.Skew(stripe, Sk);

        if (logoSprite != null)
        {
            var logo = UIKit.Img(main, "Logo", Color.white, logoSprite);
            logo.preserveAspect = true;
            UIKit.Place(logo.rectTransform, L, new Vector2(110f, 250f), new Vector2(900f, 260f));
        }
        else
        {
            var title = UIKit.Label(main, gameTitle, 112, TextAnchor.MiddleLeft, L, new Vector2(110f, 250f),
                                    new Vector2(1200f, 170f), FontStyle.Bold, Sk);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            title.verticalOverflow = VerticalWrapMode.Overflow;
        }

        UIKit.Label(main, subtitle, 30, TextAnchor.MiddleLeft, L, new Vector2(116f, 150f),
                    new Vector2(900f, 44f), FontStyle.Bold, Sk, UIKit.Amber);

        UIKit.MakeButton(main, "CREATE ROOM", L, new Vector2(110f, 30f), new Vector2(470f, 78f), true, ShowCreate, 34);
        UIKit.MakeButton(main, "JOIN ROOM", L, new Vector2(110f, -60f), new Vector2(470f, 78f), true, ShowJoin, 34);
        UIKit.MakeButton(main, "QUIT", L, new Vector2(110f, -150f), new Vector2(470f, 78f), false, Quit, 34);

        mainMsg = UIKit.Label(main, "", 26, TextAnchor.MiddleLeft, L, new Vector2(112f, -225f),
                              new Vector2(900f, 40f), FontStyle.Bold, Sk, UIKit.Warn);

        UIKit.Label(main, "CREATE A ROOM AND SHARE THE CODE, OR JOIN A FRIEND'S ROOM", 20, TextAnchor.MiddleLeft,
                    new Vector2(0f, 0f), new Vector2(112f, 60f), new Vector2(1000f, 30f), FontStyle.Normal, Sk, UIKit.Soft);

        var top = new Vector2(1f, 1f);
        UIKit.Label(main, "USERNAME", 20, TextAnchor.MiddleRight, top, new Vector2(-40f, -36f),
                    new Vector2(340f, 28f), FontStyle.Bold, Sk, UIKit.Amber);
        nameField = UIKit.MakeInput(main, "YOUR NAME", top, new Vector2(-40f, -70f), new Vector2(340f, 58f),
                                    28, TextAnchor.MiddleLeft, 16);
        nameField.onEndEdit.AddListener(s =>
        {
            string clean = PlayerProfile.Clean(s);
            nameField.text = clean;
            PlayerProfile.SaveName(clean);
        });

        // ---------------- modal dim (blocks clicks on the main panel)
        var dim = UIKit.Img(menuRt, "ModalDim", new Color(0f, 0f, 0f, 0.7f), null, true);
        UIKit.Fill(dim.rectTransform);
        modalDim = dim.gameObject;

        // ---------------- create room
        var cr = Modal(menuRt, "Create", new Vector2(760f, 690f), "CREATE ROOM");
        createPanel = cr.gameObject;

        // --- number of players
        UIKit.Label(cr, "NUMBER OF PLAYERS", 24, TextAnchor.MiddleCenter, C, new Vector2(0f, 180f),
                    new Vector2(660f, 36f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.MakeButton(cr, "-", C, new Vector2(-150f, 110f), new Vector2(84f, 84f), false, () => SetPlayers(players - 1), 48);
        playersValue = UIKit.Label(cr, "4", 72, TextAnchor.MiddleCenter, C, new Vector2(0f, 110f),
                                   new Vector2(160f, 90f), FontStyle.Bold, Sk);
        UIKit.MakeButton(cr, "+", C, new Vector2(150f, 110f), new Vector2(84f, 84f), false, () => SetPlayers(players + 1), 48);

        // --- kills required to win (multiples of 5)
        UIKit.Label(cr, "KILLS TO WIN", 24, TextAnchor.MiddleCenter, C, new Vector2(0f, 20f),
                    new Vector2(660f, 36f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.MakeButton(cr, "-", C, new Vector2(-150f, -50f), new Vector2(84f, 84f), false, () => SetKills(killsToWin - KillsStep), 48);
        killsValue = UIKit.Label(cr, "30", 72, TextAnchor.MiddleCenter, C, new Vector2(0f, -50f),
                                 new Vector2(160f, 90f), FontStyle.Bold, Sk);
        UIKit.MakeButton(cr, "+", C, new Vector2(150f, -50f), new Vector2(84f, 84f), false, () => SetKills(killsToWin + KillsStep), 48);

        UIKit.Label(cr, "FIRST TEAM TO REACH IT WINS  /  TEAMS ARE BALANCED AUTOMATICALLY", 18, TextAnchor.MiddleCenter, C,
                    new Vector2(0f, -120f), new Vector2(700f, 30f), FontStyle.Normal, Sk, UIKit.Soft);

        UIKit.MakeButton(cr, "CREATE", C, new Vector2(-160f, -210f), new Vector2(280f, 74f), true, ConfirmCreate, 30);
        UIKit.MakeButton(cr, "EXIT", C, new Vector2(160f, -210f), new Vector2(280f, 74f), false, CloseModals, 30);
        createMsg = UIKit.Label(cr, "", 22, TextAnchor.MiddleCenter, C, new Vector2(0f, -290f),
                                new Vector2(700f, 40f), FontStyle.Bold, Sk, UIKit.Warn);

        // ---------------- join room
        var jr = Modal(menuRt, "Join", new Vector2(760f, 500f), "JOIN ROOM");
        joinPanel = jr.gameObject;
        UIKit.Label(jr, "ENTER ROOM CODE", 24, TextAnchor.MiddleCenter, C, new Vector2(0f, 90f),
                    new Vector2(660f, 36f), FontStyle.Bold, Sk, UIKit.Amber);
        codeField = UIKit.MakeInput(jr, "ABC123", C, new Vector2(0f, 18f), new Vector2(460f, 88f),
                                    48, TextAnchor.MiddleCenter, 6);
        codeField.onValidateInput = (text, index, ch) => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '\0';
        UIKit.MakeButton(jr, "JOIN", C, new Vector2(-160f, -105f), new Vector2(280f, 74f), true, ConfirmJoin, 30);
        UIKit.MakeButton(jr, "BACK", C, new Vector2(160f, -105f), new Vector2(280f, 74f), false, CloseModals, 30);
        joinMsg = UIKit.Label(jr, "", 22, TextAnchor.MiddleCenter, C, new Vector2(0f, -190f),
                              new Vector2(700f, 40f), FontStyle.Bold, Sk, UIKit.Warn);

        // ---------------- busy overlay
        var busy = UIKit.Img(root, "Busy", new Color(0f, 0f, 0f, 0.75f), null, true);
        UIKit.Fill(busy.rectTransform);
        busyText = UIKit.Label(busy.transform, "CONNECTING...", 56, TextAnchor.MiddleCenter, C, Vector2.zero,
                               new Vector2(1000f, 100f), FontStyle.Bold, Sk);
        busyPanel = busy.gameObject;

        // ---------------- in-game room code pill (bottom centre)
        var pill = UIKit.Img(root, "RoomPill", new Color(0f, 0f, 0f, 0.55f));
        UIKit.Place(pill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(520f, 44f));
        UIKit.Skew(pill, Sk);
        var pillEdge = UIKit.Img(pill.transform, "Edge", UIKit.Amber);
        UIKit.Place(pillEdge.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(6f, 44f));
        UIKit.Skew(pillEdge, Sk);
        hudCode = UIKit.Label(pill.transform, "ROOM", 22, TextAnchor.MiddleCenter, C, Vector2.zero,
                              new Vector2(500f, 44f), FontStyle.Bold, Sk);
        hudRoot = pill.gameObject;

        // ---------------- pause panel
        var pa = Modal(root, "Pause", new Vector2(700f, 470f), "PAUSED");
        pausePanel = pa.gameObject;
        UIKit.Label(pa, "ROOM CODE", 22, TextAnchor.MiddleCenter, C, new Vector2(0f, 80f),
                    new Vector2(560f, 30f), FontStyle.Bold, Sk, UIKit.Soft);
        pauseCode = UIKit.Label(pa, "------", 64, TextAnchor.MiddleCenter, C, new Vector2(0f, 25f),
                                new Vector2(560f, 80f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.MakeButton(pa, "COPY CODE", C, new Vector2(0f, -55f), new Vector2(320f, 60f), false,
                         () => GUIUtility.systemCopyBuffer = boot.RoomCode, 24);
        UIKit.MakeButton(pa, "RESUME", C, new Vector2(-160f, -145f), new Vector2(290f, 70f), true, () => SetPause(false), 28);
        UIKit.MakeButton(pa, "LEAVE ROOM", C, new Vector2(160f, -145f), new Vector2(290f, 70f), false, () => boot.Leave(), 28);
    }

    /// <summary>Dark panel with an amber top bar and a big angled title.</summary>
    RectTransform Modal(Transform parent, string name, Vector2 size, string title)
    {
        var img = UIKit.Img(parent, name, UIKit.Ink, null, true);
        UIKit.Place(img.rectTransform, C, Vector2.zero, size);

        var bar = UIKit.Img(img.transform, "Bar", UIKit.Amber);
        UIKit.Place(bar.rectTransform, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(size.x, 6f));

        UIKit.Label(img.transform, title, 56, TextAnchor.MiddleCenter, C, new Vector2(0f, size.y / 2f - 75f),
                    new Vector2(size.x - 40f, 80f), FontStyle.Bold, Sk);
        return img.rectTransform;
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        var module = go.AddComponent<InputSystemUIInputModule>();
        module.AssignDefaultActions();
    }
}