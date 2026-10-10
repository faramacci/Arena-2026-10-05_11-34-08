using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using TMPro;
using Arena.Core;
using Arena.Gameplay;
using Arena.Presentation;

namespace Arena
{
    /// <summary>
    /// Boots the whole game from an empty scene: EventSystem, MOBA camera rig,
    /// generated arena, the two screens and the touch HUD, then runs the
    /// flow — <b>mode choice → fighter picker → match</b>.
    ///
    /// Neither screen simulates anything: the picker only collects the fighter,
    /// the zone, the opponent count (solo) and the host/join role (LAN), and the
    /// match builds its own fighters and countdown when START is pressed.
    ///
    /// Self-installs on scene load and owns the LAN lifetime. Only the host
    /// simulates a network match; the guest renders host snapshots.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GameBootstrap : MonoBehaviour
    {
        private enum Phase
        {
            /// <summary>The mode tiles are up.</summary>
            Modes,
            /// <summary>Picking a fighter and the line-up for the chosen mode.</summary>
            Select,
            Countdown,
            Playing
        }

        [Header("Feel (world units)")]
        [SerializeField] private float countdownSeconds = GameConfig.CountdownSeconds;

        private MobaCameraRig rig;
        private ArenaBuilder arena;
        private ModeSelectView modes;
        private CharacterSelectView signup;
        private TouchHudView hud;
        private TextMeshProUGUI countdownLabel;
        private UiPulse countdownPulse;
        private int lastCountShown = -1;

        private CharacterView localView;
        private CharacterView foeView;
        /// <summary>Free-for-all fighters past the first two slots, destroyed with the match.</summary>
        private readonly List<CharacterView> extraViews = new List<CharacterView>();
        private CharacterDef localDef;

        private Phase phase = Phase.Modes;
        private float timer;
        private float goTimer = -1f;
        private LanSession network;
        /// <summary>Shouts this phone's port while it hosts, so a guest can find it.</summary>
        private LanBeacon beacon;
        /// <summary>Listens for a host while the picker is set to join.</summary>
        private LanFinder finder;
        /// <summary>The address discovery last offered, used when the field is empty.</summary>
        private string discoveredAddress;
        /// <summary>The last network failure, kept on the picker until the next attempt.</summary>
        private string pickerError;
        /// <summary>The picker role the bootstrap last saw, so a switch retires an old failure.</summary>
        private GameMode pickerModeSeen = GameMode.Bot;
        private bool handshake;
        private CharacterId networkCharacter;
        private ZoneShape networkZone;
        private float sendTimer;
        private Vector3 remoteInput;
        private readonly Queue<int> remoteCasts = new Queue<int>();
        private bool IsGuest => network != null && !network.IsHost;

        /// <summary>Whole match state: physics, spells, zone and the bot brain.</summary>
        private MatchSim sim;

        /// <summary>Negative when no end card is up, otherwise seconds left.</summary>
        private float endTimer = -1f;

        // ---- what the player chose on the way in -------------------------------
        private GameMode mode = GameMode.Bot;
        /// <summary>Who fights whom: slot 0 is this phone, the rest are bots.</summary>
        private readonly LobbyRoster roster =
            new LobbyRoster(LobbyRoster.MaxSlots, CharacterId.Vanguard);
        private CharacterId chosenCharacter = CharacterId.Vanguard;
        private ZoneShape zone = ZoneShape.Circle;
        private int botCount = 1;
        private int campaignStage = 1;
        private bool campaignCleared;

        /// <summary>The class a solo opponent takes: always the player's opposite.</summary>
        private CharacterId OpponentClass => LobbyRoster.CycleCharacter(chosenCharacter, 1);

        // Spawns sit inside the enlarged zone so nobody starts taking damage.
        private static readonly Vector3 LocalSpawn = new Vector3(-10f, 0f, 0f);
        private static readonly Vector3 FoeSpawn = new Vector3(10f, 0f, 0f);

        // ------------------------------------------------------------------ boot

        /// <summary>
        /// The root objects this game builds at runtime. None of them is authored in a
        /// scene, so one found at boot - or left after Play Mode stops - belongs to a
        /// session that is already over.
        /// </summary>
        public static readonly string[] RuntimeRoots =
            { "GameBootstrap", "Arena", "GameCanvas", "MatchFx", "EventSystem" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // This project enters Play Mode with both reloads disabled, so the world a
            // previous session built is still in the scene when this one starts - and
            // whatever it was doing when it stopped, spent knives and shields lying on
            // the sand included, would be sitting under the new match. Sweep first.
            int swept = ClearLeftoverSceneObjects();
            if (swept > 0)
                Debug.Log("[Bootstrap] cleared " + swept +
                          " object(s) left in the scene by an earlier session.");

            if (FindFirstObjectByType<GameBootstrap>() != null) return;
            var go = new GameObject("GameBootstrap");
            go.AddComponent<GameBootstrap>();
        }

        /// <summary>
        /// Destroys what a finished session left in the open scene and returns how many
        /// roots went. A root counts when it carries a fighter view, or when its name is
        /// one of <see cref="RuntimeRoots"/> - the scene's authored objects (the camera,
        /// the light, the volume) are never in either list. Extra names are for the
        /// editor's own scratch scenes; nothing in the game passes any.
        /// </summary>
        public static int ClearLeftoverSceneObjects(params string[] alsoNames)
        {
            var roots = new List<GameObject>();
            SceneManager.GetActiveScene().GetRootGameObjects(roots);
            int gone = 0;
            foreach (var root in roots)
            {
                if (root == null || !IsRuntimeRoot(root, alsoNames)) continue;
                Object.DestroyImmediate(root);
                gone++;
            }
            return gone;
        }

        private static bool IsRuntimeRoot(GameObject root, string[] alsoNames)
        {
            // A fighter view is the surest sign: the props a throw is drawn with hang
            // off the same root, so sweeping the view sweeps the knives with it.
            if (root.GetComponentInChildren<CharacterView>(true) != null) return true;
            for (int i = 0; i < RuntimeRoots.Length; i++)
                if (root.name == RuntimeRoots[i]) return true;
            if (alsoNames == null) return false;
            for (int i = 0; i < alsoNames.Length; i++)
                if (root.name == alsoNames[i]) return true;
            return false;
        }

        private void Awake()
        {
            // Lock orientation strictly to landscape on mobile devices.
            // Portrait stays disabled here *and* in Player Settings, so the
            // Android manifest is sensorLandscape: the game opens landscape
            // even when the phone's auto-rotate lock is off.
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;

            // Phones: lock the frame rate so the ice-feel stays consistent.
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            EnsureEventSystem();
            EnsureCamera();

            var arenaGo = new GameObject("Arena");
            arena = arenaGo.AddComponent<ArenaBuilder>();

            var canvas = UiFactory.CreateCanvas("GameCanvas", 0);

            hud = TouchHudView.Build(canvas.transform);
            hud.AbilityPressed += OnAbilityPressed;
            hud.MenuRequested += ReturnToMenu;

            modes = ModeSelectView.Build(canvas.transform);
            modes.ModeChosen += OnModeChosen;

            signup = CharacterSelectView.Build(canvas.transform);
            signup.CharacterChosen += OnCharacterChosen;
            signup.ZoneChosen += OnZoneChosen;
            signup.BotCountChosen += OnBotCountChosen;
            signup.StartRequested += BeginMatch;
            signup.CancelRequested += ReturnToMenu;

            BuildCountdownLabel(canvas.transform);
            BuildLogPathLabel(canvas.transform);

            roster.SetBotCount(botCount, OpponentClass);
            ShowModeSelect();

            Debug.Log("[Bootstrap] boot complete (" +
                      (Application.isEditor ? "editor" : "build") + ").");
        }

        /// <summary>The scene ships without one, and pointer UI needs it.</summary>
        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            // Input System only (activeInputHandler == 1), so the legacy
            // StandaloneInputModule would silently ignore every touch.
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        private void EnsureCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) cam = FindFirstObjectByType<Camera>();
            if (cam == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                go.tag = "MainCamera";
                cam = go.GetComponent<Camera>();
            }

            rig = cam.GetComponent<MobaCameraRig>();
            if (rig == null) rig = cam.gameObject.AddComponent<MobaCameraRig>();

            // Arcane night backdrop so the colosseum reads as a lit bowl of
            // sand inside the dark, instead of the flat grey a skybox-less
            // camera renders with.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.034f, 0.028f, 0.070f, 1f);

            if (FindFirstObjectByType<Light>() == null)
            {
                var sun = new GameObject("Directional Light", typeof(Light));
                var light = sun.GetComponent<Light>();
                light.type = LightType.Directional;
                // Cool, low moon-ish key light; the braziers and ward supply the
                // warm and violet accents from inside the arena.
                light.color = new Color(0.74f, 0.79f, 1f);
                light.intensity = 1.15f;
                sun.transform.rotation = Quaternion.Euler(52f, -38f, 0f);
            }
        }

        private void BuildCountdownLabel(Transform canvas)
        {
            countdownLabel = UiFactory.CreateLabel("Countdown", canvas, string.Empty,
                150f, UiFactory.TextPrimary, TextAlignmentOptions.Center);
            countdownLabel.fontStyle = FontStyles.Bold;
            countdownLabel.characterSpacing = 4f;
            // Ice-to-blue vertex gradient so the big readout matches the menu's
            // wordmark instead of a flat white block.
            countdownLabel.enableVertexGradient = true;
            countdownLabel.colorGradient = new VertexGradient(
                UiFactory.Frost, UiFactory.Frost, UiFactory.AccentDeep, UiFactory.AccentDeep);
            countdownLabel.color = Color.white;
            UiFactory.AddOutline(countdownLabel, new Color(0.01f, 0.04f, 0.10f, 0.9f), 3f);
            UiFactory.AddShadow(countdownLabel, new Color(0f, 0.08f, 0.18f, 0.55f), new Vector2(0f, -7f));
            countdownPulse = countdownLabel.gameObject.AddComponent<UiPulse>();

            var rt = (RectTransform)countdownLabel.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(600f, 220f);
            countdownLabel.gameObject.SetActive(false);
        }

        /// <summary>
        /// Builds only: a small footer showing where this run's runtime log is
        /// written, so it can be pulled off the phone without a USB cable. The
        /// editor skips it entirely.
        /// </summary>
        private void BuildLogPathLabel(Transform canvas)
        {
            if (Application.isEditor) return;

            RuntimeLogger.EnsureRunning(false);
            string path = RuntimeLogger.CurrentFilePath ?? RuntimeLogger.LogDirectory;
            if (string.IsNullOrEmpty(path)) return;

            var label = UiFactory.CreateLabel("LogPath", canvas, "LOG: " + path, 18f,
                UiFactory.Muted, TextAlignmentOptions.BottomLeft,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(16f, 12f), new Vector2(1000f, 34f));
            UiFactory.AddOutline(label, new Color(0f, 0f, 0f, 0.85f), 2f);
        }

        // ------------------------------------------------------- screen one: modes

        /// <summary>Top-level screen: campaign, bots or local network.</summary>
        private void ShowModeSelect()
        {
            CancelInvoke(nameof(HideCountdown));
            CloseBeacon();
            CloseFinder();
            discoveredAddress = null;
            pickerError = null;
            goTimer = -1f;
            endTimer = -1f;
            phase = Phase.Modes;
            hud.Hide();
            countdownLabel.gameObject.SetActive(false);
            signup.Hide();
            modes.SetCampaignStage(campaignStage, GameConfig.CampaignStages, campaignCleared);
            modes.Show();
            if (rig != null) rig.SetTarget(null);
        }

        /// <summary>A mode tile was tapped: set the line-up up and go practise.</summary>
        private void OnModeChosen(GameMode picked)
        {
            mode = picked == GameMode.LanJoin ? GameMode.LanHost : picked;
            if (mode == GameMode.Campaign && campaignCleared)
            {
                campaignCleared = false;
                campaignStage = 1;
            }

            roster.SetBotCount(mode == GameMode.Campaign ? campaignStage : botCount, OpponentClass);
            Debug.Log($"[Bootstrap] mode chosen: {mode}.");
            OpenPicker();
        }

        // --------------------------------------------------- screen two: the picker

        /// <summary>
        /// Opens the fighter picker for the chosen mode. Nothing is simulated
        /// here: the match builds its own fighters and countdown on START.
        /// </summary>
        private void OpenPicker()
        {
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            endTimer = -1f;
            countdownLabel.gameObject.SetActive(false);
            modes.Hide();

            signup.Show(mode, chosenCharacter, zone, botCount, campaignStage);
            pickerModeSeen = mode;
            pickerError = null;
            phase = Phase.Select;
            Debug.Log($"[Bootstrap] picker: mode={mode}, fighter={chosenCharacter}, " +
                      $"zone={zone}, {roster.BotCount} opponent(s).");
        }

        /// <summary>The pick only has to survive until the match spawns it.</summary>
        private void OnCharacterChosen(CharacterId id)
        {
            chosenCharacter = id;
            roster.SetCharacter(LobbyRoster.LeaderIndex, id);
            roster.SetBotCount(mode == GameMode.Campaign ? campaignStage : botCount, OpponentClass);
        }

        /// <summary>
        /// The shape stays live while a host waits for its guest; the match
        /// applies it to the arena when it actually starts.
        /// </summary>
        private void OnZoneChosen(ZoneShape shape) => zone = shape;

        private void OnBotCountChosen(int bots)
        {
            botCount = bots;
            if (mode == GameMode.Campaign) return;   // the campaign owns its stage count
            roster.SetBotCount(bots, OpponentClass);
            Debug.Log($"[Bootstrap] {bots} opponent(s) for the next match.");
        }

        /// <summary>START, pressed on the picker.</summary>
        private void BeginMatch()
        {
            if (phase != Phase.Select) return;
            mode = signup.Mode;      // a LAN picker can still flip host ⇄ join
            signup.SetStatus(string.Empty, false);

            if (mode == GameMode.LanHost || mode == GameMode.LanJoin)
            {
                StartNetwork();
                return;
            }

            StartMatch(mode, zone, chosenCharacter, OpponentClass);
        }

        // ------------------------------------------------------------- networking

        /// <summary>Hosts or joins according to the address the player typed.</summary>
        private void StartNetwork()
        {
            pickerError = null;
            // A guest who never typed anything joins whoever discovery heard.
            string address = signup.HostAddress;
            if (mode == GameMode.LanJoin && string.IsNullOrEmpty(address)) address = discoveredAddress;
            if (mode == GameMode.LanJoin && string.IsNullOrEmpty(address))
            {
                // Nothing typed and nothing heard: say so, instead of handing an
                // empty address to the socket layer. This is the one press that
                // can never work, so it gets its own plain sentence.
                FailOnPicker("Please enter the Host IP address — the host's screen shows it under the " +
                    "HOST BATTLE button — or wait for the host to be found on this hotspot.");
                return;
            }
            try
            {
                networkCharacter = chosenCharacter;
                networkZone = zone;
                handshake = false;
                remoteInput = Vector3.zero;
                remoteCasts.Clear();
                sendTimer = 0f;
                network = mode == GameMode.LanHost
                    ? LanSession.Host()
                    : LanSession.Join(address);
                Debug.Log(mode == GameMode.LanHost
                    ? $"[Network] hosting on TCP {LanSession.Port}."
                    : $"[Network] joining {address}:{LanSession.Port}.");
                network.Received += OnNetworkMessage;
                network.ConnectedToPeer += () =>
                {
                    if (network != null && !network.IsHost)
                        network.Send(new LanMessage { kind = "hello", character = networkCharacter });
                };
                signup.SetStatus(mode == GameMode.LanHost
                    ? "Waiting on " + LanSession.LocalAddresses() + " (TCP " + LanSession.Port +
                      ") — a phone on this hotspot finds you by itself. CANCEL leaves the wait."
                    : "Connecting to " + address + "…", true);
            }
            catch (System.Exception ex)
            {
                // Stay on the picker: it says what went wrong, and its own
                // button starts the next attempt. The two failures a hotspot
                // player actually hits - the phone's port is still busy after a
                // previous session, or the address cannot be reached - get text
                // that says what to do about them. Everything else is caught too:
                // no press of this button may ever throw into the UI event and
                // leave the screen looking dead.
                FailOnPicker(mode == GameMode.LanHost
                    ? $"Could not open TCP {LanSession.Port} on this phone ({ex.Message}). " +
                      "Wait a moment, then press HOST BATTLE again."
                    : $"Could not reach {address}:{LanSession.Port} ({ex.Message}). " +
                      "Check both phones are on the same hotspot.");
            }
        }

        /// <summary>
        /// A network attempt failed before a match began. The message belongs on
        /// the picker the player is standing on, not on the mode screen they
        /// were bounced back to - a silent drop out of the picker is what
        /// "the host and join buttons do nothing" looks like.
        /// </summary>
        private void FailOnPicker(string reason)
        {
            Debug.LogError("[Network] " + reason);
            CloseNetwork();
            pickerError = reason;
            signup.SetStatus(reason, false, true);
        }

        private void CloseNetwork()
        {
            if (network != null) Debug.Log("[Network] session closed.");
            network?.Dispose();
            network = null;
            handshake = false;
            remoteCasts.Clear();
        }

        private void CloseBeacon()
        {
            if (beacon == null) return;
            Debug.Log("[Network] stopped shouting for a guest.");
            beacon.Dispose();
            beacon = null;
        }

        private void CloseFinder()
        {
            if (finder == null) return;
            if (discoveredAddress != null) Debug.Log("[Network] stopped listening for hosts.");
            finder.Dispose();
            finder = null;
        }

        /// <summary>
        /// Discovery, once per frame. A waiting host shouts its port on the
        /// hotspot; a joining phone listens and fills the address field with
        /// what it hears, so nobody has to read an address off one screen and
        /// type it into the other.
        ///
        /// The picker's status line has three possible owners and the quietest
        /// one wins: a session in progress (its own wait/connect text), then the
        /// last failure, then discovery.
        /// </summary>
        private void StepDiscovery()
        {
            if (network != null && network.Active && network.IsHost && !handshake)
            {
                if (beacon == null)
                {
                    beacon = new LanBeacon(network.ListeningPort);
                    if (beacon.Error != null) Debug.LogWarning("[Network] discovery beacon off: " + beacon.Error);
                    else Debug.Log("[Network] shouting for a guest on UDP " + LanDiscovery.Port + ".");
                }
                beacon.Tick();
            }
            else CloseBeacon();

            bool pickerUp = phase == Phase.Select && signup != null && signup.gameObject.activeSelf;
            if (pickerUp && signup.Mode != pickerModeSeen)
            {
                // Host ⇄ join flipped: the failure that belonged to the old
                // role is not this one's problem.
                pickerModeSeen = signup.Mode;
                pickerError = null;
                signup.SetStatus(string.Empty, false);
            }
            if (!pickerUp || signup.Mode != GameMode.LanJoin)
            {
                CloseFinder();
                discoveredAddress = null;
                return;
            }

            if (network != null || pickerError != null) return;

            if (finder == null)
            {
                finder = new LanFinder();
                if (finder.Error != null)
                {
                    // Kept as the picker's failure, not rewritten next frame: a
                    // phone that cannot listen is a real state, not a hint.
                    Debug.LogWarning("[Network] " + finder.Error);
                    pickerError = finder.Error + " — type the host's address instead.";
                    signup.SetStatus(pickerError, false, true);
                    return;
                }
                Debug.Log("[Network] listening for hosts on UDP " + LanDiscovery.Port + ".");
            }

            finder.Poll();
            LanHost host = finder.Best;
            if (host == null)
            {
                discoveredAddress = null;
                signup.SetStatus("Searching the hotspot for a host — start hosting on the other phone, " +
                    "or type its address.", false);
                return;
            }

            discoveredAddress = host.Address;
            signup.SetHostAddress(host.Address);
            signup.SetStatus("Found host " + host.Address + " — press JOIN BATTLE.", false);
        }

        private void OnNetworkMessage(LanMessage message)
        {
            if (network == null) return;
            if (network.IsHost)
            {
                if (!handshake && message.kind == "hello" && ValidCharacter(message.character))
                {
                    handshake = true;
                    Debug.Log($"[Network] guest joined as {message.character}.");
                    // Read the live choice, not the one captured at Start:
                    // the host may still switch the zone while waiting.
                    networkZone = signup.Zone;
                    StartMatch(GameMode.LanHost, networkZone, networkCharacter, message.character);
                    network.Send(new LanMessage { kind = "welcome", hostCharacter = networkCharacter,
                        character = message.character, zone = networkZone });
                }
                else if (handshake && message.kind == "input")
                {
                    if (!Finite(message.input)) { network.Reject("Invalid player input."); return; }
                    remoteInput = Vector3.ClampMagnitude(new Vector3(message.input.x, 0f, message.input.z), 1f);
                }
                else if (handshake && message.kind == "cast" && phase == Phase.Playing && endTimer < 0f)
                {
                    if (message.slot >= 0 && message.slot < 3 && remoteCasts.Count < 3) remoteCasts.Enqueue(message.slot);
                }
            }
            else
            {
                if (!handshake && message.kind == "welcome" && ValidCharacter(message.character) &&
                    ValidCharacter(message.hostCharacter) && (message.zone == ZoneShape.Circle || message.zone == ZoneShape.Square))
                {
                    handshake = true;
                    Debug.Log($"[Network] connected to host (zone={message.zone}).");
                    StartMatch(GameMode.LanJoin, message.zone, message.hostCharacter, message.character);
                }
                else if (handshake && message.kind == "state")
                {
                    if (!ValidSnapshot(message)) { network.Reject("Invalid host snapshot."); return; }
                    sim.ApplySnapshot(message.snapshot);
                    timer = message.timer;
                    if (message.phase == 0)
                    {
                        phase = Phase.Countdown;
                        countdownLabel.text = Mathf.CeilToInt(timer).ToString();
                        countdownLabel.gameObject.SetActive(true);
                    }
                    else if (message.phase == 1)
                    {
                        if (phase == Phase.Countdown) ShowGo();
                        phase = Phase.Playing;
                    }
                    else if (endTimer < 0f)
                    {
                        phase = Phase.Playing;
                        ShowEndCard(sim.Foe.Dead && sim.Local.Dead ? "DRAW!" : sim.Foe.Dead ? "KO!" : "WIN!");
                    }
                    for (int i = 0; i < 3; i++) hud.SetCooldown(i, sim.Foe.Cooldowns[i]);
                    hud.SetLocalHealth(sim.Foe.Health, GameConfig.MaxHealth);
                }
            }
        }

        private static bool ValidCharacter(CharacterId id) =>
            id == CharacterId.Vanguard || id == CharacterId.Mage || id == CharacterId.Assassin;
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool ValidSnapshot(LanMessage message)
        {
            bool Fighter(FighterSnapshot f) => f != null && Finite(f.position) && Finite(f.forward) &&
                Finite(f.health) && f.health >= 0f && f.health <= GameConfig.MaxHealth &&
                Finite(f.haste) && f.haste >= 0f && f.haste <= GameConfig.HasteMaxSeconds &&
                Finite(f.hasteSpeed) && f.hasteSpeed >= 0f && f.hasteSpeed <= GameConfig.HasteMaxSpeed &&
                Finite(f.hasteGrip) && f.hasteGrip >= 0f && f.hasteGrip <= GameConfig.HasteMaxGrip &&
                f.cooldowns != null && f.cooldowns.Length == 3 &&
                System.Array.TrueForAll(f.cooldowns, c => Finite(c) && c >= 0f);
            var s = message.snapshot;
            if (message.phase < 0 || message.phase > 2 || !Finite(message.timer) || s == null ||
                !Fighter(s.host) || !Fighter(s.guest) || s.effects == null || s.effects.Length > 32) return false;
            foreach (var fx in s.effects)
                // Every thrown weapon is a cube — blade, dagger and shield are the
                // same slab at different scales — so rejecting cubes would reject
                // any host that throws one.
                if (fx == null || !Finite(fx.position) || !Finite(fx.scale) ||
                    (fx.primitive != (int)PrimitiveType.Sphere &&
                     fx.primitive != (int)PrimitiveType.Cylinder &&
                     fx.primitive != (int)PrimitiveType.Cube) ||
                    !Finite(fx.color.r) || !Finite(fx.color.g) || !Finite(fx.color.b) ||
                    !Finite(fx.color.a)) return false;
            return true;
        }

        private void StepNetwork()
        {
            if (network == null) return;
            var session = network;
            session.Pump();
            if (session.Error != null)
            {
                string error = session.Error;
                if (phase == Phase.Select)
                {
                    // The player never left the picker, so the failure belongs
                    // there, with the button ready for another go.
                    FailOnPicker(error);
                    return;
                }
                Debug.LogError("[Network] " + error);
                ReturnToMenu();
                modes.SetNotice(error);
                return;
            }
            if (!handshake) return;
            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer > 0f) return;
            // 60Hz: the guest renders snapshots with no prediction, so a 30Hz
            // timeslot showed up as guest-only stutter next to the host.
            sendTimer = 1f / 60f;
            if (session.IsHost)
                session.Send(new LanMessage { kind = "state", snapshot = sim.CaptureSnapshot(),
                    phase = endTimer > 0f ? 2 : phase == Phase.Countdown ? 0 : 1, timer = timer });
            else
                session.Send(new LanMessage { kind = "input", input =
                    phase == Phase.Playing && endTimer < 0f ? rig.StickToWorld(hud.MoveInput) : Vector3.zero });
            session.Pump();
        }

        // -------------------------------------------------------------- the match

        private void StartMatch(GameMode mode, ZoneShape zone,
            CharacterId localId, CharacterId foeId)
        {
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            modes.Hide();
            signup.Hide();

            arena.SetShape(zone);

            localDef = GameConfig.Get(localId);
            CharacterDef foeDef = GameConfig.Get(foeId);

            if (localView != null) Destroy(localView.gameObject);
            if (foeView != null) Destroy(foeView.gameObject);

            ClearExtraViews();
            localView = CharacterView.Create(localDef, LocalSpawn, arena.transform);
            foeView = CharacterView.Create(foeDef, FoeSpawn, arena.transform);

            rig.SetTarget(IsGuest ? foeView.transform : localView.transform);

            hud.BindCharacter(IsGuest ? foeDef : localDef);
            hud.SetLocalHealth(GameConfig.MaxHealth, GameConfig.MaxHealth);
            endTimer = -1f;
            lastCountShown = -1;

            // Health and the English display name live on each fighter's
            // floating nameplate now — the HUD carries no corner plates.
            localView.SetName(localDef.DisplayName);
            foeView.SetName(foeDef.DisplayName);
            localView.SetHealth(GameConfig.MaxHealth, GameConfig.MaxHealth);
            foeView.SetHealth(GameConfig.MaxHealth, GameConfig.MaxHealth);

            // Everything that plays the match (movement, spells, zone, bot)
            // lives in MatchSim; the bootstrap only drives phases and UI.
            if (sim != null) sim.Clear();
            sim = mode == GameMode.Bot || mode == GameMode.Campaign
                ? new MatchSim(SoloSetups(localDef, foeDef), zone)
                : new MatchSim(localDef, foeDef, false, localView, foeView, zone);

            hud.ResetInput();
            hud.Show();

            Debug.Log($"[Bootstrap] match start (mode={mode}, zone={zone}, " +
                      $"local={localDef.DisplayName}, foe={foeDef.DisplayName}).");
            timer = countdownSeconds;
            phase = Phase.Countdown;
            countdownLabel.alpha = 1f;
            countdownLabel.text = Mathf.CeilToInt(timer).ToString();
            countdownLabel.gameObject.SetActive(true);
        }

        /// <summary>
        /// Solo free-for-all line-up: the player, the first opponent, then one
        /// fighter per extra bot the picker asked for. Characters cycle so a
        /// crowded arena is not six copies of the same class, and the extras are
        /// spread around the ring instead of stacking on the opening duellists.
        /// </summary>
        private List<MatchSim.Setup> SoloSetups(CharacterDef localDef, CharacterDef foeDef)
        {
            var setups = new List<MatchSim.Setup>
            {
                new MatchSim.Setup { Def = localDef, View = localView, IsBot = false },
                new MatchSim.Setup { Def = foeDef, View = foeView, IsBot = true }
            };

            int bots = roster.BotCount;      // slot 1 already is the first bot
            for (int i = 1; i < bots; i++)
            {
                CharacterDef def = GameConfig.Get(LobbyRoster.CycleCharacter(foeDef.Id, i));
                var view = CharacterView.Create(def, RingSpawn(i, bots), arena.transform);
                view.SetName(def.DisplayName);
                view.SetHealth(GameConfig.MaxHealth, GameConfig.MaxHealth);
                extraViews.Add(view);
                setups.Add(new MatchSim.Setup { Def = def, View = view, IsBot = true });
            }
            return setups;
        }

        /// <summary>Even spread on a ring clear of the two opening positions.</summary>
        private static Vector3 RingSpawn(int index, int total)
        {
            float angle = Mathf.PI * 0.5f + index / (float)Mathf.Max(1, total) * Mathf.PI * 2f;
            float radius = GameConfig.PlayReach * 0.55f;
            return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        private void ClearExtraViews()
        {
            for (int i = 0; i < extraViews.Count; i++)
                if (extraViews[i] != null) Destroy(extraViews[i].gameObject);
            extraViews.Clear();
        }

        /// <summary>Back to the mode tiles, tearing the world and any session down.</summary>
        private void ReturnToMenu()
        {
            // Logged with a stack: whatever bounces the player back to the tiles
            // is worth knowing about, and the caller is the interesting half.
            Debug.Log("[Bootstrap] back to the mode tiles.");
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            CloseNetwork();
            if (sim != null)
            {
                sim.Clear();
                sim = null;
            }
            if (localView != null) Destroy(localView.gameObject);
            if (foeView != null) Destroy(foeView.gameObject);
            localView = null;
            foeView = null;
            ClearExtraViews();
            ShowModeSelect();
        }

        private void OnDestroy()
        {
            CancelInvoke();
            network?.Dispose();
            beacon?.Dispose();
            finder?.Dispose();
            sim?.Clear();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && network != null)
            {
                ReturnToMenu();
                modes.SetNotice("LAN session ended while the app was in background. Reconnect.");
            }
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            // Legacy Input throws when the project runs on Input System only.
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && (phase != Phase.Modes || network != null))
            {
                ReturnToMenu();
                return;
            }

            StepNetwork();
            StepDiscovery();
            // Guests ease between snapshots so 60fps rendering never shows
            // the snapshot cadence; the host already ticks at 60Hz.
            if (IsGuest && sim != null) sim.AdvanceSnapshotView(Time.deltaTime);
            if (goTimer > 0f)
            {
                goTimer -= Time.deltaTime;
                if (goTimer <= 0f) HideCountdown();
            }

            switch (phase)
            {
                case Phase.Modes:
                    break;
                case Phase.Select:
                    break;
                case Phase.Countdown:
                    if (!IsGuest) StepCountdown();
                    break;
                case Phase.Playing:
                    if (endTimer > 0f)
                    {
                        // Freeze the match while the end card is on screen.
                        endTimer -= Time.deltaTime;
                        if (endTimer <= 0f)
                        {
                            endTimer = -1f;
                            ReturnToMenu();
                        }
                        break;
                    }
                    if (!IsGuest) StepMatch();
                    break;
            }
        }

        private void StepCountdown()
        {
            timer -= Time.deltaTime;
            int left = Mathf.CeilToInt(timer);

            if (timer <= 0f)
            {
                phase = Phase.Playing;
                ShowGo();
                return;
            }

            countdownLabel.text = left.ToString();
            countdownLabel.alpha = 0.75f + 0.25f * Mathf.PingPong(Time.time * 3f, 1f);

            // Pop once per new integer, not every frame.
            if (left != lastCountShown)
            {
                lastCountShown = left;
                if (countdownPulse != null) countdownPulse.PlayPop(0.18f);
            }
        }

        private void ShowGo()
        {
            countdownLabel.text = "GO!";
            countdownLabel.alpha = 1f;
            countdownLabel.gameObject.SetActive(true);
            if (countdownPulse != null) countdownPulse.PlayPop(0.28f);
            goTimer = 0.6f;
        }

        private void HideCountdown()
        {
            if (phase != Phase.Playing || endTimer > 0f) return;
            goTimer = -1f;
            countdownLabel.gameObject.SetActive(false);
            countdownLabel.alpha = 1f;
        }

        /// <summary>One simulation step: input → MatchSim → HUD cooldowns.</summary>
        private void StepMatch()
        {
            if (sim == null) return;

            Vector3 dir = rig.StickToWorld(hud.MoveInput);
            while (remoteCasts.Count > 0) sim.Cast(sim.Foe, remoteCasts.Dequeue());
            sim.Tick(Time.deltaTime, dir, remoteInput);

            for (int i = 0; i < 3; i++)
                hud.SetCooldown(i, sim.Local.Cooldowns[i]);
            hud.SetLocalHealth(sim.Local.Health, GameConfig.MaxHealth);

            // Free-for-all: the player's own death ends their match even while the
            // bots fight on, and the last fighter standing takes the arena.
            if (sim.Local.Dead) ShowEndCard(sim.AliveCount == 0 ? "DRAW!" : "DEFEAT!");
            else if (sim.MatchOver) ShowEndCard("WIN!");
        }

        /// <summary>Freezes the match behind an end card, then returns to the tiles.</summary>
        private void ShowEndCard(string text)
        {
            Debug.Log($"[Bootstrap] result: {text}");
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            endTimer = GameConfig.MatchEndDelay + 0.8f;
            countdownLabel.text = text;
            countdownLabel.alpha = 1f;
            countdownLabel.gameObject.SetActive(true);
            if (countdownPulse != null) countdownPulse.PlayPop(0.22f);

            // Beating a campaign stage promotes the next one, so the mode screen
            // keeps score between runs and a loss simply replays the stage.
            if (mode != GameMode.Campaign || text != "WIN!") return;
            if (campaignStage >= GameConfig.CampaignStages)
            {
                campaignCleared = true;
                Debug.Log("[Campaign] the arena is cleared — every stage beaten.");
            }
            else
            {
                campaignStage++;
                Debug.Log($"[Campaign] stage cleared — next up is stage {campaignStage}.");
            }
        }

        private void OnAbilityPressed(int slot)
        {
            if (phase != Phase.Playing || sim == null || endTimer > 0f) return;

            if (IsGuest)
            {
                network.Send(new LanMessage { kind = "cast", slot = slot });
                return;
            }

            // Real cast: MatchSim rejects stunned/dead/on-cooldown casts, so the
            // HUD cooldown bar only starts for spells that actually fired.
            if (sim.Cast(sim.Local, slot))
                hud.SetCooldown(slot, sim.Local.Cooldowns[slot]);
        }
    }
}
