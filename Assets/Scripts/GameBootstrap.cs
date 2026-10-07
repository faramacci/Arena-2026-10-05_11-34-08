using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using TMPro;
using Arena.Core;
using Arena.Gameplay;
using Arena.Presentation;

namespace Arena
{
    /// <summary>
    /// Boots the whole game from an empty scene: EventSystem, MOBA camera rig,
    /// generated arena, main menu and touch HUD, then runs the menu → match
    /// flow.
    ///
    /// Self-installs on scene load and owns the LAN lifetime. Only the host
    /// simulates a network match; the guest renders host snapshots.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GameBootstrap : MonoBehaviour
    {
        private enum Phase
        {
            Menu,
            Countdown,
            Playing
        }

        [Header("Feel (world units)")]
        [SerializeField] private float countdownSeconds = GameConfig.CountdownSeconds;

        private MobaCameraRig rig;
        private ArenaBuilder arena;
        private MainMenuView menu;
        private TouchHudView hud;
        private TextMeshProUGUI countdownLabel;
        private UiPulse countdownPulse;
        private int lastCountShown = -1;

        private CharacterView localView;
        private CharacterView foeView;
        private CharacterDef localDef;

        private Phase phase = Phase.Menu;
        private float timer;
        private float goTimer = -1f;
        private LanSession network;
        private bool handshake;
        private CharacterId networkCharacter;
        private ZoneShape networkZone;
        private float sendTimer;
        private Vector3 remoteInput;
        private readonly System.Collections.Generic.Queue<int> remoteCasts = new System.Collections.Generic.Queue<int>();
        private bool IsGuest => network != null && !network.IsHost;

        /// <summary>Whole match state: physics, spells, zone and the bot brain.</summary>
        private MatchSim sim;

        /// <summary>Negative when no end card is up, otherwise seconds left.</summary>
        private float endTimer = -1f;

        // Spawns sit inside the enlarged zone so nobody starts taking damage.
        private static readonly Vector3 LocalSpawn = new Vector3(-10f, 0f, 0f);
        private static readonly Vector3 FoeSpawn = new Vector3(10f, 0f, 0f);

        // ------------------------------------------------------------------ boot

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindFirstObjectByType<GameBootstrap>() != null) return;
            var go = new GameObject("GameBootstrap");
            go.AddComponent<GameBootstrap>();
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

            menu = MainMenuView.Build(canvas.transform);
            menu.StartRequested += OnStartRequested;
            menu.CancelRequested += ReturnToMenu;

            BuildCountdownLabel(canvas.transform);

            ShowMenu();
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

        // ------------------------------------------------------------------ flow

        private void OnStartRequested(GameMode mode, ZoneShape zone,
            CharacterId localId, CharacterId foeId)
        {
            if (mode == GameMode.Bot)
            {
                StartMatch(mode, zone, localId, foeId);
                return;
            }
            try
            {
                networkCharacter = localId;
                networkZone = zone;
                handshake = false;
                remoteInput = Vector3.zero;
                remoteCasts.Clear();
                sendTimer = 0f;
                network = mode == GameMode.LanHost ? LanSession.Host() : LanSession.Join(menu.HostAddress);
                network.Received += OnNetworkMessage;
                network.ConnectedToPeer += () =>
                {
                    if (network != null && !network.IsHost)
                        network.Send(new LanMessage { kind = "hello", character = networkCharacter });
                };
                menu.SetNetworkStatus(mode == GameMode.LanHost ?
                    "Host IP: " + LanSession.LocalAddresses() + "\nWaiting for player (TCP " + LanSession.Port + ")" :
                    "Connecting to " + menu.HostAddress + "...", true);
            }
            catch (System.Exception ex) when (ex is System.Net.Sockets.SocketException || ex is System.ArgumentException ||
                                              ex is System.Net.NetworkInformation.NetworkInformationException)
            {
                ReturnToMenu();
                menu.SetNetworkStatus(ex.Message, false);
            }
        }

        private void StartMatch(GameMode mode, ZoneShape zone,
            CharacterId localId, CharacterId foeId)
        {
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            menu.Hide();

            arena.SetShape(zone);

            localDef = GameConfig.Get(localId);
            CharacterDef foeDef = GameConfig.Get(foeId);

            if (localView != null) Destroy(localView.gameObject);
            if (foeView != null) Destroy(foeView.gameObject);

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
            sim = new MatchSim(localDef, foeDef, mode == GameMode.Bot,
                localView, foeView, zone);

            hud.ResetInput();
            hud.Show();

            timer = countdownSeconds;
            phase = Phase.Countdown;
            countdownLabel.alpha = 1f;
            countdownLabel.text = Mathf.CeilToInt(timer).ToString();
            countdownLabel.gameObject.SetActive(true);
        }

        private void ShowMenu()
        {
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            phase = Phase.Menu;
            endTimer = -1f;
            hud.Hide();
            countdownLabel.gameObject.SetActive(false);
            menu.Show();
            if (rig != null) rig.SetTarget(null);
        }

        private void ReturnToMenu()
        {
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            network?.Dispose();
            network = null;
            handshake = false;
            remoteCasts.Clear();
            if (sim != null)
            {
                sim.Clear();
                sim = null;
            }
            if (localView != null) Destroy(localView.gameObject);
            if (foeView != null) Destroy(foeView.gameObject);
            localView = null;
            foeView = null;
            ShowMenu();
            menu.SetNetworkStatus("Choose Solo, Host or Join.", false);
        }

        private void OnDestroy()
        {
            CancelInvoke();
            network?.Dispose();
            sim?.Clear();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && network != null)
            {
                ReturnToMenu();
                menu.SetNetworkStatus("LAN session ended while the app was in background. Reconnect.", false);
            }
        }

        private void OnNetworkMessage(LanMessage message)
        {
            if (network == null) return;
            if (network.IsHost)
            {
                if (!handshake && message.kind == "hello" && ValidCharacter(message.character))
                {
                    handshake = true;
                    // Read the live choice, not the one captured at Start:
                    // the host may still switch the zone while waiting.
                    networkZone = menu.Zone;
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

        private static bool ValidCharacter(CharacterId id) => id == CharacterId.Vanguard || id == CharacterId.Storm;
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool ValidSnapshot(LanMessage message)
        {
            bool Fighter(FighterSnapshot f) => f != null && Finite(f.position) && Finite(f.forward) &&
                Finite(f.health) && f.health >= 0f && f.health <= GameConfig.MaxHealth &&
                f.cooldowns != null && f.cooldowns.Length == 3 &&
                System.Array.TrueForAll(f.cooldowns, c => Finite(c) && c >= 0f);
            var s = message.snapshot;
            if (message.phase < 0 || message.phase > 2 || !Finite(message.timer) || s == null ||
                !Fighter(s.host) || !Fighter(s.guest) || s.effects == null || s.effects.Length > 32) return false;
            foreach (var fx in s.effects)
                if (fx == null || !Finite(fx.position) || !Finite(fx.scale) ||
                    (fx.primitive != (int)PrimitiveType.Sphere && fx.primitive != (int)PrimitiveType.Cylinder) ||
                    !Finite(fx.color.r) || !Finite(fx.color.g) || !Finite(fx.color.b) || !Finite(fx.color.a)) return false;
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
                ReturnToMenu();
                menu.SetNetworkStatus(error, false);
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

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            // Legacy Input throws when the project runs on Input System only.
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && (phase != Phase.Menu || network != null))
            {
                ReturnToMenu();
                return;
            }

            StepNetwork();
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

            if (sim.Local.Dead && sim.Foe.Dead) ShowEndCard("DRAW!");
            else if (sim.Local.Dead) ShowEndCard("KO!");
            else if (sim.Foe.Dead) ShowEndCard("WIN!");
        }

        /// <summary>Freezes the match behind an end card, then returns to the menu.</summary>
        private void ShowEndCard(string text)
        {
            CancelInvoke(nameof(HideCountdown));
            goTimer = -1f;
            endTimer = GameConfig.MatchEndDelay + 0.8f;
            countdownLabel.text = text;
            countdownLabel.alpha = 1f;
            countdownLabel.gameObject.SetActive(true);
            if (countdownPulse != null) countdownPulse.PlayPop(0.22f);
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
