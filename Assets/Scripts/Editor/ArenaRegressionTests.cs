using System;
using System.Collections.Generic;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using UnityEngine.UI;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using Arena.Core;
using Arena.Gameplay;
using Arena.Inputs;
using Arena.Presentation;

namespace Arena.Tests
{
    // No test assembly migration required: runs the actual components in Play Mode.
    [InitializeOnLoad]
    public static class ArenaRegressionTests
    {
        private const string Pending = "Arena.Regression.Pending";
        private const string Restore = "Arena.Regression.RestoreEditMode";
        private const string RestorePause = "Arena.Regression.RestorePause";
        private const string Owned = "Arena.Regression.OwnsSession";
        private const string Attempts = "Arena.Regression.Attempts";
        private const string Request = "Temp/arena-regression.request";
        private const string Result = "Temp/arena-regression-results.txt";
        private static readonly List<GameObject> owned = new List<GameObject>();
        private static readonly List<MatchSim> sims = new List<MatchSim>();
        private static double readyAt;
        private static double progressedAt;
        private static IEnumerator execution;
        private static int resumeAfterFrame;

        static ArenaRegressionTests() { EditorApplication.update += Poll; }

        [MenuItem("Tools/Arena/Run Regression Tests")]
        public static void Run()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for Unity to finish compiling/importing.");
            Directory.CreateDirectory("Temp");
            File.WriteAllText(Result, "RUNNING\n");
            // Always run in a session this harness booted itself. A compile that
            // lands while Play Mode is running (Enter Play Mode Options ship
            // with Reload Domain and Reload Scene both off) wipes every runtime
            // button listener, so a reused session would click dead tiles and
            // fail the UI tests for no reason in the game code.
            SessionState.SetBool(Restore, true);
            SessionState.SetBool(RestorePause, EditorApplication.isPaused);
            SessionState.SetBool(Pending, true);
            SessionState.SetBool(Owned, false);
            SessionState.SetInt(Attempts, 0);
            readyAt = EditorApplication.timeSinceStartup + 1.5d;
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        private static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (execution != null)
            {
                // The suite is driven frame by frame, so a dead session or a
                // frozen editor loop would leave this branch spinning forever.
                if (!EditorApplication.isPlaying) execution = null;
                else
                {
                    // Frames cannot be trusted here: an editor window that is not
                    // in front has its loop throttled to a crawl (measured at
                    // about one frame per second), which used to stall the suite
                    // between tests. Pause and step the loop by hand instead,
                    // which works no matter what covers the window.
                    if (!EditorApplication.isPaused)
                    {
                        SessionState.SetBool(RestorePause, true);
                        EditorApplication.isPaused = true;
                        return;
                    }
                    if (EditorApplication.timeSinceStartup - progressedAt > 120d)
                    {
                        execution = null;
                        Abort("the editor stopped advancing frames");
                        return;
                    }
                    EditorApplication.QueuePlayerLoopUpdate();
                    if (Time.frameCount < resumeAfterFrame)
                    {
                        EditorApplication.Step();
                        return;
                    }
                    progressedAt = EditorApplication.timeSinceStartup;
                    if (!execution.MoveNext())
                    {
                        execution = null;
                        SessionState.SetBool(Pending, false);
                        SessionState.SetBool(Owned, false);
                        if (EditorApplication.isPaused) EditorApplication.isPaused = false;
                    }
                    resumeAfterFrame = Time.frameCount + 2;
                    return;
                }
            }
            if (File.Exists(Request) && !SessionState.GetBool(Pending, false))
            {
                File.Delete(Request);
                Run();
            }
            if (!SessionState.GetBool(Pending, false) ||
                EditorApplication.timeSinceStartup < readyAt) return;
            if (!SessionState.GetBool(Owned, false))
            {
                // Take ownership of the session instead of reusing one: a session
                // that outlived a compile cannot be trusted with the UI tests
                // (see Run).
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPlaying = false;
                    readyAt = EditorApplication.timeSinceStartup + 1.5d;
                }
                else
                {
                    EditorApplication.isPlaying = true;
                    SessionState.SetBool(Owned, true);
                    readyAt = EditorApplication.timeSinceStartup + 2d;   // let it boot
                }
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                // A compile can take our session down mid-run. Try again, but
                // hand the poller a result after a few attempts instead of
                // leaving a file that says RUNNING forever.
                int attempt = SessionState.GetInt(Attempts, 0) + 1;
                SessionState.SetInt(Attempts, attempt);
                SessionState.SetBool(Owned, false);
                if (attempt > 3) Abort($"the play session died {attempt - 1} times mid-run");
                else readyAt = EditorApplication.timeSinceStartup + 1.5d;
                return;
            }
            if (EditorApplication.isPaused)
            {
                SessionState.SetBool(RestorePause, true);
                EditorApplication.isPaused = false;
                return;
            }
            execution = Execute();
            resumeAfterFrame = Time.frameCount;
            progressedAt = EditorApplication.timeSinceStartup;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>
        /// Leaves a readable result behind when no run could be completed, so a
        /// caller waiting on the file is never left hanging on "RUNNING".
        /// </summary>
        private static void Abort(string reason)
        {
            File.WriteAllLines(Result, new[]
            {
                "0/0 passed; failures=1",
                "FAIL run aborted: " + reason
            });
            Debug.LogError("Arena regressions aborted: " + reason);
            SessionState.SetBool(Pending, false);
            SessionState.SetBool(Owned, false);
            if (SessionState.GetBool(Restore, false)) EditorApplication.isPlaying = false;
        }

        private static IEnumerator Execute()
        {
            var results = new List<string>();
            int failed = 0;
            var random = UnityEngine.Random.state;
            var cases = new Dictionary<string, Action>
            {
                { "Pointer centering, four directions, analog strength and ownership", Joystick },
                { "Camera mapping preserves analog magnitude and screen up", CameraInput },
                { "Zoom keeps the player centred and the spawn standoff in frame", CameraZoom },
                { "The health bar clears the fighter's head on screen", NameplateClearance },
                { "30/60/120 FPS produce identical state", FrameRates },
                { "Catch-up work is bounded and backlog is retained", CatchUp },
                { "Half stick produces half acceleration", AnalogAcceleration },
                { "Shield Throw flies out, lands once each way and comes home", ShieldThrow },
                { "Dagger Throw outruns an orb and is spent on its hit", DaggerThrow },
                { "The assassin's knives drag a shorter red line than the dash", KnifeTrail },
                { "Shadowstep trades the ice for grip and speed for three seconds", Shadowstep },
                { "A cast with no target left is refused without spending the slot", NoTargetCast },
                { "Guest moves with the grip buff the host cast, not a constant", HasteReplication },
                { "Blade Wave throws a fan that charges each fighter once", BladeWave },
                { "Orb sweeps moving target and respects invulnerability", Orb },
                { "Orb cannot hit a target beyond the wall", OrbWall },
                { "Blink teleport does not become a swept attack", Blink },
                { "Earthrift rings its caster: radius gates the hit, never the Vanguard", Area },
                { "Shield soaks damage, replicates and expires", Aegis },
                { "Shield reads gold and flashes when it soaks", AegisFx },
                { "Vortex pulls and bursts", Vortex },
                { "Overlapping fighters and bot stay finite", Overlap },
                { "Reload validation safely recreates property block", Reload },
                { "Both fighters accept independent movement and casts", TwoPlayers },
                { "Lobby roster fills bots, opens slots and gates the start", Lobby },
                { "Mode tiles lead into the fighter picker and into the match", Flow },
                { "Free-for-all keeps every fighter on the sand and is decided by survival", FreeForAll },
                { "Snapshots replicate health, cooldowns, facing and effects", Snapshots },
                { "LAN loopback framing, handshake, input and disconnect", LanTransport },
                { "LAN rejects invalid addresses and oversized frames", LanValidation },
                { "LAN timeout and cancel release listener for reconnect", LanTimeout },
                { "Mode tiles + fighter picker drive the LAN match and disconnect recovery", LanMenuFlow },
                { "The LAN picker stays tappable on every phone aspect", LanPickerFits },
                { "A joining phone finds its host over the hotspot without an address", LanDiscoveryFindsHost },
                { "A failed join says why, and a waiting host can cancel", LanRetry },
                { "A guest joins the host the hotspot offered it, with nothing typed", LanAutoJoin },
                { "Guest view eases between host snapshots", SnapshotSmoothing },
                { "Landscape-only orientation for rotation-locked phones", Orientation },
                { "Countdown restart and result are immune to stale hide timers", CountdownLifecycle },
                { "Procedural materials are destroyed with their owners", Materials },
                { "Rigged fighter models load, stand, shade and pose", FighterModels },
                { "Thrown weapons are the fighter's own shield and knife", Props },
                { "Perimeter wall runs hug their edge instead of crossing the sand", Perimeter },
                { "Fighters stay on the sand instead of reaching the seating", ArenaReach }
            };
            foreach (var test in cases)
            {
                try { UnityEngine.Random.InitState(1234); test.Value(); results.Add("PASS " + test.Key); }
                catch (Exception ex) { failed++; results.Add("FAIL " + test.Key + "\n" + ex); }
                finally
                {
                    foreach (var sim in sims) sim.Clear();
                    sims.Clear();
                    foreach (var go in owned) if (go != null) UnityEngine.Object.Destroy(go);
                    owned.Clear();
                }
            }
            UnityEngine.Random.state = random;
            results.Insert(0, $"{cases.Count - failed}/{cases.Count} passed; failures={failed}");
            yield return null; // Deferred Object.Destroy, including owned runtime materials.
            foreach (var material in releasedMaterials)
                if (material != null) { failed++; results.Add("FAIL runtime material survived owner destruction"); }
            releasedMaterials.Clear();
            results[0] = $"{cases.Count - failed}/{cases.Count} passed; failures={failed}";
            File.WriteAllLines(Result, results);
            if (failed == 0) Debug.Log("Arena regressions: " + results[0]);
            else Debug.LogError("Arena regressions: " + string.Join("\n", results));
            if (EditorApplication.isPaused) EditorApplication.isPaused = false;
            if (SessionState.GetBool(Restore, false)) EditorApplication.isPlaying = false;
            else EditorApplication.isPaused = SessionState.GetBool(RestorePause, false);
            if (Application.isBatchMode) EditorApplication.Exit(failed == 0 ? 0 : 1);

        }

        private static GameObject Own(GameObject go) { owned.Add(go); return go; }
        private static void Check(bool ok, string message)
        { if (!ok) throw new Exception(message); }
        private static void Near(float actual, float expected, float tolerance = 0.0001f)
        { Check(Mathf.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}"); }
        private static FieldInfo Field(object obj, string name) => obj.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        /// <summary>Like <see cref="Field"/>, but also finds public fields (FxSpawner's records are public).</summary>
        private static FieldInfo AnyField(object obj, string name) => obj.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static void Invoke(object obj, string name) => obj.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, null);

        // Spell effects live on MatchSim's FxSpawner collaborator; reach it so the
        // orb-retention and guest-replication checks keep probing the real state.
        private static object FxSpawner(MatchSim sim) => Field(sim, "fx").GetValue(sim);
        private static System.Collections.ICollection Projectiles(MatchSim sim) =>
            (System.Collections.ICollection)Field(FxSpawner(sim), "projectiles").GetValue(FxSpawner(sim));
        private static Vector3 ProjectileVelocity(object projectile) =>
            (Vector3)AnyField(projectile, "Velocity").GetValue(projectile);
        private static System.Collections.ICollection Vortices(MatchSim sim) =>
            (System.Collections.ICollection)Field(FxSpawner(sim), "vortices").GetValue(FxSpawner(sim));
        private static object FirstVortex(MatchSim sim)
        {
            foreach (var hazard in Vortices(sim)) return hazard;
            return null;
        }
        private static System.Collections.ICollection Warnings(MatchSim sim) =>
            (System.Collections.ICollection)Field(FxSpawner(sim), "warnings").GetValue(FxSpawner(sim));
        private static object FirstWarning(MatchSim sim)
        {
            foreach (var warning in Warnings(sim)) return warning;
            return null;
        }
        private static GameObject EffectRoot(MatchSim sim) =>
            (GameObject)Field(FxSpawner(sim), "fxRoot").GetValue(FxSpawner(sim));

        private static MatchSim Match(CharacterId id = CharacterId.Vanguard, bool bot = false,
            int extraBots = 0)
        {
            CharacterView View(CharacterDef def, Vector3 pos)
            {
                var root = Own(new GameObject("RegressionFighter"));
                var part = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
                part.transform.SetParent(root.transform, false);
                var view = root.AddComponent<CharacterView>();
                view.Configure(def, pos);
                return view;
            }
            var a = GameConfig.Get(id);
            var b = GameConfig.Mage;
            var setups = new List<MatchSim.Setup>
            {
                new MatchSim.Setup { Def = a, View = View(a, Vector3.zero), IsBot = false },
                new MatchSim.Setup { Def = b, View = View(b, new Vector3(10f, 0f, 0f)), IsBot = bot }
            };
            for (int i = 0; i < extraBots; i++)
            {
                var def = GameConfig.Get(LobbyRoster.CycleCharacter(b.Id, i + 1));
                var spot = new Vector3(0f, 0f, 12f + i * 3f);
                setups.Add(new MatchSim.Setup { Def = def, View = View(def, spot), IsBot = true });
            }
            var sim = new MatchSim(setups, ZoneShape.Circle);
            sims.Add(sim);
            return sim;
        }

        private static void Joystick()
        {
            var go = Own(new GameObject("RegressionStick", typeof(RectTransform)));
            var rt = (RectTransform)go.transform;
            rt.pivot = Vector2.zero;
            rt.sizeDelta = new Vector2(200f, 200f);
            var knob = Own(new GameObject("Knob", typeof(RectTransform)));
            knob.transform.SetParent(rt, false);
            var stick = go.AddComponent<TouchStick>();
            PointerEventData Event(int id, Vector2 local) => new PointerEventData(EventSystem.current)
            { pointerId = id, position = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(local)) };
            var positions = new[] { new Vector2(100,100), new Vector2(200,100),
                new Vector2(0,100), new Vector2(100,200), new Vector2(100,0), new Vector2(150,100) };
            var expected = new[] { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down, Vector2.right * 0.5f };
            for (int i = 0; i < positions.Length; i++)
            {
                stick.ResetStick();
                stick.OnPointerDown(Event(1, positions[i]));
                Vector2 raw = (Vector2)Field(stick, "rawInput").GetValue(stick);
                Near((raw - expected[i]).magnitude, 0f);
            }
            stick.OnPointerDown(Event(2, positions[2]));
            stick.OnDrag(Event(2, positions[2]));
            stick.OnPointerUp(Event(2, positions[2]));
            Check(stick.IsDragging, "Another finger released the stick");
            Near(((Vector2)Field(stick, "rawInput").GetValue(stick)).x, 0.5f);
            stick.OnPointerUp(Event(1, positions[5]));
            Check(!stick.IsDragging, "Owner release ignored");
            Near(((Vector2)Field(stick, "rawInput").GetValue(stick)).magnitude, 0f);
            stick.ResetStick();
            stick.OnPointerDown(Event(1, new Vector2(120f, 100f)));
            for (int i = 0; i < 120; i++)
                stick.GetType().GetMethod("StepInput", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(stick, new object[] { 1f / 60f });
            Check(stick.Value.x > 0.12f && stick.Value.x < 0.25f,
                "Small analog input never converged beyond the dead zone");
            stick.OnDrag(Event(1, positions[1]));
            go.SetActive(false);
            Check(!stick.IsDragging && stick.Value == Vector2.zero, "Disabled stick retained input");
        }

        /// <summary>
        /// What the orthographic zoom costs, asserted rather than described. The rig
        /// takes its size from <see cref="GameConfig.CameraOrthoSize"/> and keeps the
        /// player in the middle of the screen, and the two bounds the size is chosen
        /// against are measured here rather than trusted:
        /// - the ground the window looks over has to reach the longest line spell in
        ///   the roster, or that telegraph cannot be read and dodged;
        /// - on the widest phone aspect the game is played at (20:9), the foe at its
        ///   20u spawn standoff has to still be inside the frame.
        /// Coming closer than that costs one of them, which is what makes 9.5 a
        /// boundary and not a preference.
        /// </summary>
        private static void CameraZoom()
        {
            var rigGo = Own(new GameObject("RegressionZoomRig", typeof(Camera)));
            var rig = rigGo.AddComponent<MobaCameraRig>();
            var cam = rigGo.GetComponent<Camera>();
            Near(cam.orthographicSize, GameConfig.CameraOrthoSize);
            Check(cam.orthographic, "The rig is not orthographic");

            var local = Own(new GameObject("RegressionZoomLocal"));
            local.transform.position = new Vector3(-10f, 0f, 0f);
            rig.SetTarget(local.transform);
            cam.aspect = 20f / 9f;      // a phone held in landscape

            Vector3 mine = cam.WorldToViewportPoint(local.transform.position + Vector3.up * 0.95f);
            Check(Mathf.Abs(mine.x - 0.5f) < 0.01f && Mathf.Abs(mine.y - 0.5f) < 0.05f,
                "The rig does not centre the player: " + mine.x.ToString("0.00") + "," +
                mine.y.ToString("0.00"));

            // A metre forward is cos(pitch) up the screen, so the top edge of the
            // window is size/cos(pitch) metres of ground ahead of the player.
            float reach = GameConfig.CameraOrthoSize /
                Mathf.Cos(GameConfig.CameraPitch * Mathf.Deg2Rad);
            float longest = 0f;
            string name = "";
            foreach (var id in new[] { CharacterId.Vanguard, CharacterId.Mage, CharacterId.Assassin })
            {
                var character = GameConfig.Get(id);
                for (int slot = 0; slot < character.Abilities.Length; slot++)
                {
                    var ab = character.Get((AbilitySlot)slot);
                    float span = Mathf.Max(ab.Range, ab.ProjectileRange);
                    if (span <= longest) continue;
                    longest = span;
                    name = character.DisplayName + "/" + ab.DisplayName;
                }
            }
            Check(reach >= longest, "The camera looks " + reach.ToString("0.0") +
                "u ahead, less than " + longest.ToString("0.0") + "u of " + name);

            Vector3 foe = cam.WorldToViewportPoint(new Vector3(10f, 0.95f, 0f));
            Check(foe.x > 0.5f && foe.x < 0.99f && foe.y > 0f && foe.y < 1f,
                "The foe at its spawn is off screen with the zoomed camera: viewport " +
                foe.x.ToString("0.00") + "," + foe.y.ToString("0.00"));
        }

        /// <summary>
        /// The nameplate turns to face the camera, so its local up is the screen's up
        /// and its height is world-height times cos(pitch) - which is why a plate that
        /// looks comfortably above a 1.9 m figure in world units can still sit on the
        /// head. The bar's bottom is read from the built hierarchy (the real rect, not a
        /// second copy of the constant) and compared with the model's own crown in
        /// screen units, so a plate that starts covering the head again fails here.
        /// </summary>
        private static void NameplateClearance()
        {
            float up = Mathf.Cos(GameConfig.CameraPitch * Mathf.Deg2Rad);
            foreach (var id in new[] { CharacterId.Vanguard, CharacterId.Mage, CharacterId.Assassin })
            {
                var host = Own(new GameObject("RegressionPlateHost"));
                var view = CharacterView.Create(GameConfig.Get(id), Vector3.zero, host.transform);
                Own(view.gameObject);

                var plate = view.transform.Find("Nameplate");
                Check(plate != null, id + ": the view built no nameplate");
                var bar = plate.Find("NameplateCanvas/HpBar");
                Check(bar != null, id + ": the nameplate has no health bar");

                // Identity rotation between frames, so the bar's own local offset from
                // the anchor is its drop; that offset is the screen's, not just Y.
                float drop = -plate.InverseTransformPoint(bar.position).y;
                float barScreen = plate.localPosition.y * up - drop;
                float crownScreen = view.Model.RestBounds.max.y * up;
                Check(barScreen - crownScreen > 0.20f,
                    id + ": the health bar's bottom is only " +
                    (barScreen - crownScreen).ToString("0.00") +
                    " m above the crown on screen (plate anchor y=" +
                    plate.localPosition.y.ToString("0.00") + ", drop " +
                    drop.ToString("0.00") + ", crown y=" +
                    view.Model.RestBounds.max.y.ToString("0.00") + ")");
            }
        }

        private static void CameraInput()
        {
            var rig = Own(new GameObject("RegressionCamera", typeof(Camera))).AddComponent<MobaCameraRig>();
            Near(rig.StickToWorld(new Vector2(0.25f, 0.5f)).magnitude, new Vector2(0.25f, 0.5f).magnitude);
            Check(rig.StickToWorld(Vector2.up).z > 0f, "Screen up must follow camera ground forward (+Z at yaw 0)");
            Near(rig.StickToWorld(Vector2.one).magnitude, 1f);
        }

        private static void FrameRates()
        {
            Vector3 baseline = Vector3.zero;
            float health = 0f, cd = 0f;
            foreach (int fps in new[] { 30, 60, 120 })
            {
                var s = Match();
                s.Foe.Position = new Vector3(-(GameConfig.ZoneCircleRadius + 5f), 0f, 0f);
                s.Local.Cooldowns[0] = 3.2f;
                for (int i = 0; i < fps * 2; i++) s.Tick(1f / fps, Vector3.right * 0.4f);
                if (fps == 30) { baseline = s.Local.Position; health = s.Foe.Health; cd = s.Local.Cooldowns[0]; }
                else { Near((baseline - s.Local.Position).magnitude, 0); Near(s.Foe.Health, health); Near(s.Local.Cooldowns[0], cd); }
                Near(s.Foe.Health, 88f, 0.002f);
            }
        }

        private static void CatchUp()
        {
            var a = Match(); var b = Match();
            a.Tick(GameConfig.FixedTimestep * 12f, Vector3.right);
            Check(a.StepsLastTick == 5 && a.PendingTime > 0, "Catch-up limit/backlog broken");
            a.Tick(0f, Vector3.right); a.Tick(0f, Vector3.right);
            for (int i = 0; i < 12; i++) b.Tick(GameConfig.FixedTimestep, Vector3.right);
            Near((a.Local.Position - b.Local.Position).magnitude, 0f);
            Check(a.PendingTime < 1e-7, "Time was lost or not drained");
        }

        private static void AnalogAcceleration()
        {
            var half = Match(); var full = Match();
            half.Tick(GameConfig.FixedTimestep, Vector3.right * 0.5f);
            full.Tick(GameConfig.FixedTimestep, Vector3.right);
            Near(half.Local.Velocity.x * 2f, full.Local.Velocity.x);
            Near(half.Local.Forward.magnitude, 1f);
        }

        /// <summary>
        /// Shield Throw is the Vanguard's ranged poke: the aegis itself flies out,
        /// scores on the way there, turns around at the end of its run and homes
        /// back to the hand it left — a second scoring chance on the way home,
        /// and nothing left in the air afterwards.
        /// </summary>
        private static void ShieldThrow()
        {
            var s = Match();
            var ab = s.Local.Def.Get(AbilitySlot.Primary);
            Check(ab.Id == "shield_throw", "The Vanguard's first spell is not Shield Throw");
            Check(ab.ProjectileRange > 0f, "Shield Throw must come back");

            s.Local.Position = Vector3.zero;
            s.Local.Forward = Vector3.right;
            s.Foe.Velocity = Vector3.zero;
            float health = s.Foe.Health;
            Check(s.Cast(s.Local, (int)AbilitySlot.Primary), "Shield Throw rejected");
            Check(!s.Cast(s.Local, (int)AbilitySlot.Primary), "Cooldown not enforced");
            Check(Projectiles(s).Count == 1, "The shield never left the hand");

            // A target that does not run is hit once going out and once coming
            // back, and never a third time on either leg.
            for (int i = 0; i < 180; i++)
            {
                s.Foe.Position = new Vector3(6f, 0f, 0f);
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(s.Foe.Health, health - 2f * ab.Damage);
            Check(Projectiles(s).Count == 0, "The thrown shield never came home");

            // Sideways is the dodge: the same throw against a target standing off
            // the lane leaves them alone.
            var aside = Match();
            aside.Local.Position = Vector3.zero;
            aside.Local.Forward = Vector3.right;
            aside.Foe.Position = new Vector3(6f, 0f, 4f);
            aside.Foe.Velocity = Vector3.zero;
            float untouched = aside.Foe.Health;
            aside.Cast(aside.Local, (int)AbilitySlot.Primary);
            for (int i = 0; i < 180; i++)
            {
                aside.Foe.Position = new Vector3(6f, 0f, 4f);
                aside.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(aside.Foe.Health, untouched);
        }

        /// <summary>
        /// Dagger Throw is the assassin's poke: faster than the orb, but spent on
        /// the fighter it hits — the knife has no way back.
        /// </summary>
        private static void DaggerThrow()
        {
            var s = Match(CharacterId.Assassin);
            var ab = s.Local.Def.Get(AbilitySlot.Primary);
            Check(ab.Id == "dagger_throw", "The assassin's first spell is not Dagger Throw");
            Check(ab.ProjectileSpeed > GameConfig.OrbSpeed, "A dagger must outrun an orb");

            s.Local.Position = Vector3.zero;
            s.Local.Forward = Vector3.right;
            s.Foe.Position = new Vector3(5f, 0f, 0f);
            s.Foe.Velocity = Vector3.zero;
            float health = s.Foe.Health;
            Check(s.Cast(s.Local, (int)AbilitySlot.Primary), "Dagger rejected");
            Check(!s.Cast(s.Local, (int)AbilitySlot.Primary), "Cooldown not enforced");
            Check(Projectiles(s).Count == 1, "The dagger never left the hand");
            for (int i = 0; i < 60; i++) s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, health - ab.Damage);
            Check(Projectiles(s).Count == 0, "The dagger should be spent on its hit");
        }

        /// <summary>
        /// The assassin's knives drag the red line the Shadowstep dash leaves on the
        /// sand, cut to knife size: the thrown dagger drops dashes behind it, the
        /// guest's replica of that knife drags the same line (the trail is dropped
        /// from the replica's own movement, so nothing rides the wire), his wave of
        /// blades shares the one line, and the orb and the aegis drag nothing. Every
        /// measure of the knife line is taken from the dash line's own constants
        /// rather than restated - the same width and thickness, the same crimson - and
        /// its shortness is pinned twice over: the marks and their life are smaller
        /// than the dash line's, and the streak those two numbers add up to (speed x
        /// life) is shorter on screen too. The line is also checked to be a line: the
        /// marks of one frame's flight have to join up rather than leave a dotted
        /// trail behind the fastest thing in the arena.
        /// </summary>
        private static void KnifeTrail()
        {
            // The dash line's numbers, read from the code that draws it.
            float dashSpan = StaticConst(typeof(CharacterView), "TrailMaxSpan");
            float dashWidth = StaticConst(typeof(CharacterView), "TrailWidth");
            float dashFade = StaticConst(typeof(CharacterView), "TrailFade");
            var dashRed = (Color)typeof(CharacterView)
                .GetField("ShadowStepRed", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(dashRed == RedTrail.LineRed, "The knife line is a different red from the dash line");

            var s = Match(CharacterId.Assassin);
            Check(s.Local.Def.Get(AbilitySlot.Primary).Id == "dagger_throw",
                "The assassin's first spell is not Dagger Throw");
            s.Local.Position = Vector3.zero;
            s.Local.Forward = Vector3.right;
            s.Foe.Position = new Vector3(60f, 0f, 0f);
            s.Foe.Velocity = Vector3.zero;
            Check(s.Cast(s.Local, (int)AbilitySlot.Primary), "Dagger rejected");

            var knife = FirstProjectile(s);
            Check(knife != null, "The dagger never left the hand");
            var trail = knife.GetComponent<RedTrail>();
            Check(trail != null, "The thrown knife carries no trail");

            // Fly it. The sim moves the knife and the trail is dropped from that
            // movement; the components are stepped by hand because this harness
            // advances the match, not the frame loop.
            var root = EffectRoot(s);
            for (int i = 0; i < 8 && knife != null; i++)
            {
                s.Foe.Position = new Vector3(60f, 0f, 0f);
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
                if (knife != null) Invoke(trail, "LateUpdate");
            }

            var line = root != null ? root.transform.Find("RedTrailLine") : null;
            Check(line != null, "The knife left no red line behind it");
            int dashes = 0;
            float longest = 0f;
            foreach (Transform dash in line)
            {
                if (!dash.gameObject.activeSelf) continue;
                dashes++;
                longest = Mathf.Max(longest, dash.localScale.z);
            }
            Check(dashes > 0, "The knife's line exists but drew nothing");
            Check(longest < dashSpan, "A knife dash is " + longest.ToString("0.00") +
                "m long, no shorter than the dash line's " + dashSpan.ToString("0.00") + "m");
            // The same ink, not a thinner copy: a knife mark is cut from the dash
            // line's own width and thickness.
            Check(Mathf.Approximately((float)StaticConst(line.gameObject, "Width"), dashWidth),
                "A knife mark is " + ((float)StaticConst(line.gameObject, "Width")).ToString("0.00") +
                "m wide, not the dash line's " + dashWidth.ToString("0.00") + "m");
            float dashThickness = StaticConst(typeof(CharacterView), "TrailThickness");
            Check(Mathf.Approximately(
                    (float)StaticConst(line.gameObject, "Thickness"), dashThickness),
                "A knife mark is a different thickness from the dash line's " +
                dashThickness.ToString("0.00") + "m");
            float knifeFade = (float)StaticConst(line.gameObject, "Fade");
            Check(knifeFade < dashFade,
                "A knife dash outlives the dash line's " + dashFade.ToString("0.00") + "s");
            // Shorter has to hold on screen and not only in the constants: what the
            // player sees is the metres the mark's owner covers before it fades, and
            // the knife is twice as fast as the dash, so a shorter life is what keeps
            // its streak the shorter of the two.
            float knifeStreak = GameConfig.Assassin.Get(AbilitySlot.Primary).ProjectileSpeed *
                knifeFade;
            float dashStreak = GameConfig.Assassin.Get(AbilitySlot.Secondary).BuffSpeed * dashFade;
            Check(knifeStreak < dashStreak, "The knife's line runs " +
                knifeStreak.ToString("0.0") + "m against the dash line's " +
                dashStreak.ToString("0.0") + "m, so it is not the shorter of the two");

            // And it has to be a line, not a dotted trail: a knife covers 0.6 m
            // between frames at 36 m/s, so the segment it crossed in one frame is
            // filled with as many marks as it takes rather than left half bare.
            var marks = new List<Vector2>();
            foreach (Transform dash in line)
            {
                if (!dash.gameObject.activeSelf) continue;
                float half = dash.localScale.z * 0.5f;
                marks.Add(new Vector2(dash.position.x - half, dash.position.x + half));
            }
            marks.Sort((a, b) => a.x.CompareTo(b.x));
            float widestGap = 0f;
            for (int i = 1; i < marks.Count; i++)
                widestGap = Mathf.Max(widestGap, marks[i].x - marks[i - 1].y);
            Check(widestGap <= 0.02f, "The knife's line is dotted: a " +
                widestGap.ToString("0.00") + "m gap sits between two of its marks");

            // The guest draws it too, from the replica's own movement.
            var snapshot = JsonUtility.FromJson<MatchSnapshot>(JsonUtility.ToJson(s.CaptureSnapshot()));
            bool carryingKnife = false;
            foreach (var effect in snapshot.effects)
                if (effect.prop == PropLibrary.WireDagger) carryingKnife = true;
            Check(carryingKnife, "The host snapshot carried no dagger for the guest");
            var guest = Match(CharacterId.Assassin);
            guest.ApplySnapshot(snapshot);
            var guestRoot = EffectRoot(guest);
            Transform replica = null;
            foreach (Transform child in guestRoot.transform)
                if (child.name.StartsWith("Replica_prop")) replica = child;
            Check(replica != null && replica.GetComponent<RedTrail>() != null,
                "The guest's replica knife carries no trail");

            // The wave of blades is the same knives: one shared line, reused.
            int built = line.childCount;
            int inTheAirAlready = Projectiles(s).Count;      // the dagger is still flying
            var ultimate = GameConfig.Assassin.Get(AbilitySlot.Ultimate);
            s.Local.Cooldowns[(int)AbilitySlot.Ultimate] = 0f;
            s.Foe.Position = new Vector3(60f, 0f, 0f);
            Check(s.Cast(s.Local, (int)AbilitySlot.Ultimate), "Blade Wave rejected");
            Check(Projectiles(s).Count - inTheAirAlready == ultimate.FanCount,
                "The wave threw the wrong number of blades");
            for (int i = 0; i < 8; i++)
            {
                s.Foe.Position = new Vector3(60f, 0f, 0f);
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
                foreach (var blade in Projectiles(s))
                {
                    var go = (GameObject)AnyField(blade, "Go").GetValue(blade);
                    var bladeTrail = go != null ? go.GetComponent<RedTrail>() : null;
                    if (bladeTrail != null) Invoke(bladeTrail, "LateUpdate");
                }
            }
            // The pool is fixed: a fan of blades fills it (lazily, one dash at a
            // time) and a further volley reuses those dashes instead of growing the
            // hierarchy, however many knives are thrown over a match.
            int segments = (int)StaticConst(line.gameObject, "Segments");
            Check(line.childCount <= segments, "The knife line grew past its own pool (" +
                line.childCount + " of " + segments + ")");
            Check(line.childCount >= built, "The knife line lost dashes between throws");
            int pooled = line.childCount;
            s.Local.Cooldowns[(int)AbilitySlot.Ultimate] = 0f;
            Check(s.Cast(s.Local, (int)AbilitySlot.Ultimate), "A second Blade Wave was rejected");
            for (int i = 0; i < 8; i++)
            {
                s.Foe.Position = new Vector3(60f, 0f, 0f);
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
                foreach (var blade in Projectiles(s))
                {
                    var go = (GameObject)AnyField(blade, "Go").GetValue(blade);
                    var bladeTrail = go != null ? go.GetComponent<RedTrail>() : null;
                    if (bladeTrail != null) Invoke(bladeTrail, "LateUpdate");
                }
            }
            Check(line.childCount == pooled, "A second throw grew the knife line (" +
                pooled + " -> " + line.childCount + "), so the dashes are not pooled");

            // Nothing that is not a knife drags a line.
            var mage = Match(CharacterId.Mage);
            mage.Local.Position = Vector3.zero;
            mage.Local.Forward = Vector3.right;
            mage.Foe.Position = new Vector3(30f, 0f, 0f);
            Check(mage.Cast(mage.Local, 0), "Orb rejected");
            for (int i = 0; i < 6; i++) mage.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(NoKnifeLine(mage), "The mage's orb dragged a knife line");

            var vanguard = Match(CharacterId.Vanguard);
            vanguard.Local.Position = Vector3.zero;
            vanguard.Local.Forward = Vector3.right;
            vanguard.Foe.Position = new Vector3(30f, 0f, 0f);
            Check(vanguard.Cast(vanguard.Local, 0), "Shield Throw rejected");
            for (int i = 0; i < 6; i++) vanguard.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(NoKnifeLine(vanguard), "The thrown aegis dragged a knife line");
        }

        /// <summary>A private constant on a type, read by name.</summary>
        private static float StaticConst(Type type, string name)
            => (float)type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        /// <summary>
        /// The same constant, found on whichever component the line was built with -
        /// the trail line's own type is internal to the game assembly.
        /// </summary>
        private static object StaticConst(GameObject host, string name)
        {
            foreach (var component in host.GetComponents<MonoBehaviour>())
            {
                var field = component.GetType().GetField(name,
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) return field.GetValue(null);
            }
            throw new Exception("No " + name + " constant on " + host.name);
        }

        private static bool NoKnifeLine(MatchSim sim)
        {
            var root = EffectRoot(sim);
            return root == null || root.transform.Find("RedTrailLine") == null;
        }

        private static GameObject FirstProjectile(MatchSim sim)
        {
            foreach (var projectile in Projectiles(sim))
                return (GameObject)AnyField(projectile, "Go").GetValue(projectile);
            return null;
        }

        /// <summary>
        /// Shadowstep trades the ice for grip: for its three seconds the assassin
        /// tracks the stick and holds a speed nobody else reaches, so he stops and
        /// turns on a coin — and when the timer runs out the ice comes back.
        /// </summary>
        private static void Shadowstep()
        {
            var plain = Match(CharacterId.Mage);
            var hasted = Match(CharacterId.Assassin);
            var ab = hasted.Local.Def.Get(AbilitySlot.Secondary);
            Check(ab.Id == "shadowstep", "The assassin's second spell is not Shadowstep");

            Check(hasted.Cast(hasted.Local, (int)AbilitySlot.Secondary), "Shadowstep rejected");
            Check(!hasted.Cast(hasted.Local, (int)AbilitySlot.Secondary),
                "Shadowstep recast ignored cooldown");
            Near(hasted.Local.Haste, ab.BuffDuration);

            // Both fighters run the same lane for a second and reach their own
            // top speed: the buff's is the higher one.
            for (int i = 0; i < 60; i++)
            {
                plain.Tick(GameConfig.FixedTimestep, Vector3.left);
                hasted.Tick(GameConfig.FixedTimestep, Vector3.left);
            }
            float iceSpeed = plain.Local.Velocity.magnitude;
            Near(hasted.Local.Velocity.magnitude, ab.BuffSpeed, 0.5f);
            Check(hasted.Local.Velocity.magnitude > iceSpeed * 1.25f,
                $"Shadowstep is no faster ({hasted.Local.Velocity.magnitude:0.00} vs {iceSpeed:0.00})");

            // Let go: the ice carries the mage on, the buff stops the assassin.
            for (int i = 0; i < 12; i++)
            {
                plain.Tick(GameConfig.FixedTimestep, Vector3.zero);
                hasted.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Check(hasted.Local.Velocity.magnitude < ab.BuffSpeed * 0.2f,
                "The buff slides like the ice: " + hasted.Local.Velocity.magnitude);
            Check(plain.Local.Velocity.magnitude > iceSpeed * 0.7f,
                "The ice stopped sliding for everyone: " + plain.Local.Velocity.magnitude);

            // The whole point: it ends. Three seconds after the cast the fighter
            // is back on the ice, top speed and all.
            hasted.Local.Position = Vector3.zero;
            for (int i = 0; i < 240; i++) hasted.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(hasted.Local.Haste, 0f);
            for (int i = 0; i < 60; i++) hasted.Tick(GameConfig.FixedTimestep, Vector3.left);
            Check(hasted.Local.Velocity.magnitude < ab.BuffSpeed,
                "The buff outlived its own timer: " + hasted.Local.Velocity.magnitude);
            for (int i = 0; i < 12; i++) hasted.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(hasted.Local.Velocity.magnitude > iceSpeed * 0.7f,
                "The ice never came back after Shadowstep: " + hasted.Local.Velocity.magnitude);
        }

        /// <summary>
        /// A cast with nobody left to aim at is refused without spending the slot.
        /// The cooldown was written before the target was looked up, so a refusal
        /// still cost the player the spell: a match with every opponent down is the
        /// one case the HUD's "only real casts start a bar" rule cannot cover on its
        /// own, and the sim has to refuse it for free.
        /// </summary>
        private static void NoTargetCast()
        {
            var s = Match(CharacterId.Assassin);
            var ab = s.Local.Def.Get(AbilitySlot.Primary);
            Check(s.Cast(s.Local, (int)AbilitySlot.Primary), "A cast with a live target was refused");
            Near(s.Local.Cooldowns[(int)AbilitySlot.Primary], ab.Cooldown);

            for (int i = 1; i < s.Fighters.Count; i++)
            {
                s.Fighters[i].Health = 0f;
                s.Fighters[i].Dead = true;
            }
            s.Local.Cooldowns[(int)AbilitySlot.Primary] = 0f;
            Check(!s.Cast(s.Local, (int)AbilitySlot.Primary),
                "A cast with no target left standing must be refused");
            Near(s.Local.Cooldowns[(int)AbilitySlot.Primary], 0f);
        }

        /// <summary>
        /// The grip buff's own numbers cross the wire. The shipped Shadowstep happens
        /// to run at exactly the GameConfig defaults, so a guest that recomputed them
        /// from those constants instead of reading the snapshot would look correct
        /// today and drift the moment the ability is retuned. The host is therefore
        /// handed a pair of values that match no constant, and both machines run the
        /// same lane afterwards: a guest on the constants would move differently from
        /// the first step of the buff.
        /// </summary>
        private static void HasteReplication()
        {
            var host = Match(CharacterId.Assassin); var guest = Match(CharacterId.Assassin);
            var ab = host.Local.Def.Get(AbilitySlot.Secondary);
            Check(ab.Id == "shadowstep", "The grip buff is not Shadowstep");
            Check(host.Cast(host.Local, (int)AbilitySlot.Secondary), "Shadowstep rejected");

            host.Local.HasteSpeed = GameConfig.HasteTopSpeed + 7f;
            host.Local.HasteGrip = GameConfig.HasteGrip + 8f;
            var state = JsonUtility.FromJson<MatchSnapshot>(JsonUtility.ToJson(host.CaptureSnapshot()));
            guest.ApplySnapshot(state);
            Near(guest.Local.HasteSpeed, host.Local.HasteSpeed);
            Near(guest.Local.HasteGrip, host.Local.HasteGrip);

            for (int i = 0; i < 60; i++)
            {
                host.Tick(GameConfig.FixedTimestep, Vector3.left);
                guest.Tick(GameConfig.FixedTimestep, Vector3.left);
            }
            Near(guest.Local.Haste, host.Local.Haste);
            Near((guest.Local.Velocity - host.Local.Velocity).magnitude, 0f, 0.001f);
        }

        /// <summary>
        /// Blade Wave is the assassin's ultimate: a whole fan of blades at once,
        /// thrown wide enough to catch a target who dodges sideways — but a
        /// fighter standing inside the fan is charged by one blade, not seven.
        /// </summary>
        private static void BladeWave()
        {
            var ab = GameConfig.Assassin.Get(AbilitySlot.Ultimate);
            Check(ab.Id == "blade_wave" && ab.FanCount > 1 && ab.FanSpread > 0f,
                "The assassin's ultimate is not a wave of blades");

            var s = Match(CharacterId.Assassin);
            s.Local.Position = Vector3.zero;
            s.Local.Forward = Vector3.right;
            s.Foe.Position = new Vector3(6f, 0f, 0f);
            s.Foe.Velocity = Vector3.zero;
            float health = s.Foe.Health;
            Check(s.Cast(s.Local, (int)AbilitySlot.Ultimate), "Blade Wave rejected");
            Check(Projectiles(s).Count == ab.FanCount,
                $"The wave threw {Projectiles(s).Count} of {ab.FanCount} blades");

            // The fan is a fan: most blades are heading off the aim line.
            int fanned = 0;
            foreach (var blade in Projectiles(s))
                if (Mathf.Abs(ProjectileVelocity(blade).z) > 1f) fanned++;
            Check(fanned >= ab.FanCount - 1, "The wave came out as a single line: " + fanned);

            for (int i = 0; i < 90; i++)
            {
                s.Foe.Position = new Vector3(6f, 0f, 0f);
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(s.Foe.Health, health - ab.Damage);
            Check(Projectiles(s).Count == 0, "The wave did not clear");
        }

        private static void Orb()
        {
            foreach (bool immune in new[] { false, true })
            {
                var s = Match(CharacterId.Mage);
                s.Foe.Position = new Vector3(3f,0,0);
                s.Foe.Velocity = Vector3.left * 300f;
                if (immune) s.Foe.Invulnerable = 0.1f;
                Check(s.Cast(s.Local, 0), "Orb rejected");
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
                Near(s.Foe.Health, immune ? 100f : 91f);
            }
        }

        private static void OrbWall()
        {
            var s = Match(CharacterId.Mage);
            s.Local.Position = new Vector3(GameConfig.PlayLimit - 0.1f, 0f, 0f);
            s.Foe.Position = new Vector3(GameConfig.PlayLimit + 0.9f, 0f, 0f);
            s.Cast(s.Local, 0);
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 100f - GameConfig.ZoneDamagePerSecond * GameConfig.FixedTimestep);
            // Also test an orb crossing the boundary from inside over several steps.
            var inside = Match(CharacterId.Mage);
            inside.Local.Position = new Vector3(GameConfig.PlayLimit - 1.6f, 0, 0);
            inside.Foe.Position = new Vector3(-10f, 0, 0);
            inside.Cast(inside.Local, 0);
            for (int i = 0; i < 10; i++) inside.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(Projectiles(inside).Count == 0,
                "Orb crossing the wall from inside was retained");
            var list = Projectiles(s);
            Check(list.Count == 0, "Orb beyond inner wall was retained");
        }

        private static void Blink()
        {
            var s = Match(CharacterId.Mage);
            s.Local.Position = Vector3.zero; s.Local.Forward = Vector3.right;
            s.Foe.Position = new Vector3(3f,0,0);
            Check(s.Cast(s.Local, 1), "Blink rejected");
            Near(s.Local.Position.x, 6f);
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 100f);
            Check(s.Local.Invulnerable > 0f, "Blink lost invulnerability");
        }

        /// <summary>
        /// The Earthrift is a circle drawn around its caster, not a corridor: a
        /// fighter standing inside the radius eats the hit and is thrown *outward*
        /// from the Vanguard in the middle of it, one who walks out of the circle
        /// during the warning is missed, and the Vanguard is never caught by his own
        /// ground.
        /// </summary>
        private static void Area()
        {
            var ab = GameConfig.Vanguard.Get(AbilitySlot.Ultimate);
            Check(ab.Telegraph > 0f, "The Earthrift must telegraph its ring");
            Check(ab.RiftBand > 0f && ab.RiftBand < ab.Range,
                "The rift's broken band is " + ab.RiftBand + " wide inside a radius of " +
                ab.Range + ", so the ground that breaks is not the ground that hits");
            int warningSteps = Mathf.CeilToInt(ab.Telegraph / GameConfig.FixedTimestep) + 10;

            // Nothing lands while the warning is on the ground, so a fighter in
            // the circle still has time to walk out of it.
            var inside = Match();
            inside.Local.Position = Vector3.zero;
            inside.Foe.Position = Vector3.right * (ab.Range - 0.1f);
            Check(inside.Cast(inside.Local, (int)AbilitySlot.Ultimate), "Rift cast rejected");
            inside.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(inside.Foe.Health, 100f);
            Check(!inside.Cast(inside.Local, (int)AbilitySlot.Ultimate), "Area recast ignored cooldown");

            // Stay in the ring and the ground opens under you: the launch points away
            // from the caster at the centre, and the caster is standing inside his own
            // rift without taking a scratch from it.
            float launch = 0f;
            bool landed = false;
            for (int i = 0; i < warningSteps; i++)
            {
                inside.Local.Position = Vector3.zero;
                inside.Local.Velocity = Vector3.zero;
                inside.Foe.Position = Vector3.right * (ab.Range - 0.1f);
                inside.Foe.Velocity = Vector3.zero;
                inside.Tick(GameConfig.FixedTimestep, Vector3.zero);
                // Read the launch on the one tick that lands it: the victim is held in
                // place by the loop, so every pass after the hit re-zeroes the knock.
                if (inside.Foe.Health < 100f && !landed)
                {
                    landed = true;
                    launch = inside.Foe.Velocity.x;
                }
            }
            Near(inside.Foe.Health, 100f - ab.Damage);
            Check(launch > 0f,
                "The rift threw its victim inward (v=" + launch.ToString("0.00") +
                ") instead of out of the circle");
            Near(inside.Local.Health, 100f);
            Near(inside.Local.Velocity.x, 0f);

            // Step out of the circle during the warning and it misses entirely.
            var dodge = Match();
            dodge.Foe.Position = Vector3.right * (ab.Range * 0.5f);
            dodge.Cast(dodge.Local, (int)AbilitySlot.Ultimate);
            for (int i = 0; i < warningSteps; i++)
            {
                dodge.Local.Position = Vector3.zero;
                dodge.Local.Velocity = Vector3.zero;
                dodge.Foe.Position = Vector3.right * (ab.Range + 2f);
                dodge.Foe.Velocity = Vector3.zero;
                dodge.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(dodge.Foe.Health, 100f);

            // The ring is the caster's own circle, so it follows him: cast from one
            // side of the arena and carried to the victim, the ground still opens
            // under whoever is standing in it when the warning runs out.
            var carried = Match();
            carried.Cast(carried.Local, (int)AbilitySlot.Ultimate);
            for (int i = 0; i < warningSteps; i++)
            {
                carried.Local.Position = new Vector3(8f, 0f, 0f);
                carried.Local.Velocity = Vector3.zero;
                carried.Foe.Position = new Vector3(8f, 0f, ab.Range - 0.2f);
                carried.Foe.Velocity = Vector3.zero;
                carried.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(carried.Foe.Health, 100f - ab.Damage);

            // The warning the player dodges is drawn on that same circle: it rides
            // the caster while the ground cracks, and its markers stand on the rim
            // of the radius the hit will cover - no wider, no narrower.
            var riding = Match();
            riding.Cast(riding.Local, (int)AbilitySlot.Ultimate);
            riding.Tick(GameConfig.FixedTimestep, Vector3.zero);
            var warning = FirstWarning(riding);
            Check(warning != null, "The Earthrift laid no ground warning");
            Check(ReferenceEquals(AnyField(warning, "Follows").GetValue(warning), riding.Local),
                "The Earthrift's ring does not ride the fighter in its middle");
            riding.Local.Position = new Vector3(6f, 0f, 0f);
            riding.Tick(GameConfig.FixedTimestep, Vector3.zero);
            var root = (Transform)AnyField(warning, "Root").GetValue(warning);
            Near(Vector3.Distance(root.position, riding.Local.Position), 0f, 0.06f);
            var markers = (List<Transform>)AnyField(warning, "Markers").GetValue(warning);
            var rim = new List<float>();
            foreach (var marker in markers)
                rim.Add(new Vector2(marker.localPosition.x, marker.localPosition.z).magnitude);
            Check(rim.Count > 0 && Mathf.Max(rim.ToArray()) >= ab.Range - 0.02f,
                "The widest marker of the warning sits " +
                (rim.Count > 0 ? Mathf.Max(rim.ToArray()).ToString("0.00") : "nowhere") +
                " m out, short of the " + ab.Range + " m the rift covers");
            Check(Mathf.Min(rim.ToArray()) < ab.Range * 0.5f,
                "The warning marks only the rim: the disc the spell covers is not painted");
        }

        /// <summary>
        /// The Frost Aegis soaks incoming damage before health — spells and zone
        /// pressure alike — replicates that state to the guest, and drops on its
        /// own when the timer runs out.
        /// </summary>
        private static void Aegis()
        {
            var s = Match();
            var ab = GameConfig.Vanguard.Get(AbilitySlot.Secondary);
            Check(s.Cast(s.Local, (int)AbilitySlot.Secondary), "Aegis rejected");
            Near(s.Local.Shield, ab.ShieldAmount);
            Check(!s.Cast(s.Local, (int)AbilitySlot.Secondary), "Aegis recast ignored cooldown");

            // A spell hit is absorbed: the Mage's vortex bursts for 10 where its
            // throw ends, and health must not move while the shield holds.
            var v = Match();
            v.Cast(v.Local, (int)AbilitySlot.Secondary);
            var vortex = GameConfig.Mage.Get(AbilitySlot.Ultimate);
            v.Cast(v.Foe, (int)AbilitySlot.Ultimate);
            // The vortex is thrown down its line from the caster, so the victim
            // has to sit where the throw ends to eat the burst.
            var burstSpot = new Vector3(2f, 0f, 0f);
            // Three seconds: past the ground warning and the throw down the line.
            for (int i = 0; i < 190; i++)
            {
                v.Local.Position = burstSpot;
                v.Local.Velocity = Vector3.zero;
                v.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(v.Local.Health, 100f);
            Near(v.Local.Shield, ab.ShieldAmount - vortex.Damage);

            // Zone pressure goes through the shield as well.
            var z = Match();
            z.Cast(z.Local, (int)AbilitySlot.Secondary);
            for (int i = 0; i < 30; i++)
            {
                z.Local.Position = new Vector3(GameConfig.ZoneCircleRadius + 4f, 0f, 0f);
                z.Local.Velocity = Vector3.zero;
                z.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(z.Local.Health, 100f);
            Check(z.Local.Shield < ab.ShieldAmount, "Zone ignored the shield");

            // The guest rebuilds the shield from the host snapshot.
            var guest = Match();
            var state = JsonUtility.FromJson<MatchSnapshot>(
                JsonUtility.ToJson(s.CaptureSnapshot()));
            guest.ApplySnapshot(state);
            Near(guest.Local.Shield, s.Local.Shield);
            Near(guest.Local.Shield, ab.ShieldAmount);

            // And the shield drops on its own when its timer runs out.
            var e = Match();
            e.Cast(e.Local, (int)AbilitySlot.Secondary);
            for (int i = 0; i < 250; i++) e.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(e.Local.Shield, 0f);
        }

        private static void Vortex()
        {
            var ab = GameConfig.Mage.Get(AbilitySlot.Ultimate);
            Check(ab.Telegraph > 0f, "The Vortex must telegraph its line");
            // The funnel is drawn at the pull diameter, so a smaller tornado is a
            // smaller pull: 4.5 is what "a little smaller" meant, and it is pinned here
            // so the balance change cannot drift back without a failing test.
            Near(ab.HazardRadius, 4.5f);
            var s = Match(CharacterId.Mage);
            var target = new Vector3(8f, 0f, 0f);
            s.Foe.Position = target;

            // The warning comes first: nothing has been thrown and nobody is
            // already being dragged, so the throw can be dodged.
            s.Cast(s.Local, 2);
            Check(Vortices(s).Count == 0, "Vortex landed before its warning finished");
            // The wind-up is the funnel itself, gathering under the caster: the
            // throw has to start where the player watched it form, so the spell
            // is never "a marker somewhere out in the lane".
            var warning = FirstWarning(s);
            Check(warning != null &&
                  (bool)AnyField(warning, "Charge").GetValue(warning) &&
                  (Transform)AnyField(warning, "Root").GetValue(warning) != null,
                "The vortex wind-up is not a funnel gathering under the caster");
            var gathering = (Transform)AnyField(warning, "Root").GetValue(warning);
            Check((gathering.position - s.Local.Position).magnitude < 1f,
                "The vortex funnel gathered away from the caster (at " +
                gathering.position + ", caster at " + s.Local.Position + ")");
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 100f);
            Near(s.Foe.Velocity.x, 0f);

            // Then it is thrown down the line, not dropped at the caster's feet.
            for (int i = 0; i < 60; i++)
            {
                s.Foe.Position = target;
                s.Foe.Velocity = Vector3.zero;
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            var hazard = FirstVortex(s);
            Check(hazard != null, "Vortex never appeared after its warning");
            var centre = (Vector3)AnyField(hazard, "Centre").GetValue(hazard);
            Check(centre.x > 1f, "Vortex was dropped on the ground it was cast from");

            // The pull travels with it: standing nearer the thrower is dragged
            // out along the line, the way the spell reads on screen.
            s.Foe.Position = target;
            s.Foe.Velocity = Vector3.zero;
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(s.Foe.Velocity.x > 0f, "The thrown vortex does not drag its victim (vx=" +
                s.Foe.Velocity.x + ", centre.x=" + centre.x + ", target.x=" + s.Foe.Position.x +
                ", line=" + ab.LineLength + ", life=" + ab.HazardLifetime + ")");

            // Contact hurts while it travels: the swept fighter is already wounded
            // mid-throw, not only when the vortex runs out of line at the end.
            Near(s.Foe.Health, 100f - ab.Damage);

            // The funnel rides the hazard. It used to be built once when the spell
            // was cast and then only animated in place, so the tornado sat at the
            // caster's feet while the pull and the burst happened down the line.
            var funnel = (GameObject)AnyField(hazard, "Go").GetValue(hazard);
            var live = (Vector3)AnyField(hazard, "Centre").GetValue(hazard);
            Check(funnel != null && (funnel.transform.position - live).magnitude < 1f,
                "The vortex funnel did not travel with the hazard (funnel=" +
                (funnel != null ? funnel.transform.position.ToString() : "null") +
                ", hazard=" + live + ")");

            // The end of the line still bursts, but the fighter that was already
            // swept is not charged for the same throw twice.
            for (int i = 0; i < 150; i++)
            {
                s.Foe.Position = target;
                s.Foe.Velocity = Vector3.zero;
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(s.Foe.Health, 100f - ab.Damage);
        }

        private static void Overlap()
        {
            var s = Match(CharacterId.Mage, true); s.Foe.Position = s.Local.Position;
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(!float.IsNaN(s.Foe.Position.x) && !float.IsNaN(s.Foe.Velocity.x), "Overlap produced NaN");
            Check((s.Local.Position - s.Foe.Position).magnitude >= 1.29f, "Overlap unresolved");
        }

        private static readonly List<Material> releasedMaterials = new List<Material>();

        private static void Materials()
        {
            var arena = Own(new GameObject("RegressionArena")).AddComponent<ArenaBuilder>();
            foreach (var owner in arena.GetComponentsInChildren<OwnedMaterial>())
                releasedMaterials.Add(owner.Material);
            arena.SetShape(ZoneShape.Square);
            foreach (var owner in arena.GetComponentsInChildren<OwnedMaterial>())
                if (!releasedMaterials.Contains(owner.Material)) releasedMaterials.Add(owner.Material);
            var sim = Match(CharacterId.Mage);
            sim.Cast(sim.Local, 0); sim.Cast(sim.Local, 1); sim.Cast(sim.Local, 2);
            var root = EffectRoot(sim);
            foreach (var owner in root.GetComponentsInChildren<OwnedMaterial>())
                releasedMaterials.Add(owner.Material);
            Check(releasedMaterials.Count >= 10, "Missing ownership on generated renderers");
            sim.Clear();
            Check(Resources.Load<Material>("Arena/ArenaLit") != null, "Shared material asset destroyed");
        }

        /// <summary>
        /// The rigged FBX models are what the arena shows, so these checks are on the
        /// artifacts the game loads, one character at a time: the primitives are gone
        /// and the model replaced them, all 21 kit bones resolved, the figure stands
        /// on the sand at human height instead of on its back or 100x off, every
        /// material renders in this pipeline, and the skin actually follows the bones
        /// when a pose is applied - a mesh bound to nothing travels 0.000 m.
        /// </summary>
        private static void FighterModels()
        {
            foreach (var id in new[] { CharacterId.Vanguard, CharacterId.Mage, CharacterId.Assassin })
            {
                var host = Own(new GameObject("RegressionModelHost"));
                var view = CharacterView.Create(GameConfig.Get(id), Vector3.zero, host.transform);
                Own(view.gameObject);

                Check(view.HasModel, id + ": the view is not wearing a rigged model");
                var model = view.Model;
                Check(model.MissingBones.Count == 0,
                    id + ": bones the kit requires are missing: " +
                    string.Join(", ", model.MissingBones));
                Check(!model.LyingDown, id + ": the model imports lying on its back");
                Check(model.FacesForward, id + ": the model faces backwards");

                var bounds = model.RestBounds;
                Check(bounds.min.y > -0.05f && bounds.min.y < 0.25f,
                    id + ": the model does not stand on the sand, its lowest point is y=" +
                    bounds.min.y.ToString("0.000"));
                Check(bounds.size.y > 1.60f && bounds.size.y < 2.40f,
                    id + ": the model stands " + bounds.size.y.ToString("0.00") +
                    " m tall (imported " + model.ImportedHeight.ToString("0.00") +
                    " m, fitted x" + model.FittedScale.ToString("0.000") + ")");

                foreach (var r in model.Renderers)
                {
                    if (r == null) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        Check(m != null, id + ": an empty material slot on " + r.name);
                        Check(m.shader != null && m.shader.name.Contains("Universal"),
                            id + ": " + m.name + " is on " +
                            (m.shader == null ? "no shader" : m.shader.name) +
                            ", which this pipeline cannot render");
                    }
                }

                float travel = model.SkinTravel();
                Check(travel > 0.05f, id + ": the skin does not follow the bones, it moved " +
                    travel.ToString("0.000") + " m when the skeleton was posed");

                // Thrown weapons leave from these, so they have to be on the figure,
                // off the ground, and - because they are read off the live skeleton -
                // they have to travel when the skeleton does.
                var nowhere = new Vector3(-999f, -999f, -999f);
                foreach (var anchor in new[] { FighterAnchor.RightHand, FighterAnchor.LeftForearm })
                {
                    Vector3 point = view.Anchor(anchor, nowhere);
                    Check(point != nowhere, id + ": no " + anchor + " anchor on the model");
                    Check(point.y > 0.5f && point.y < 2.2f,
                        id + ": the " + anchor + " anchor sits at y=" +
                        point.y.ToString("0.00") + " m, which is not on the body");
                    Check(Vector3.Distance(point, view.transform.position) < 1.5f,
                        id + ": the " + anchor + " anchor is " +
                        Vector3.Distance(point, view.transform.position).ToString("0.00") +
                        " m from the fighter");
                }

                Vector3 restHand = view.Anchor(FighterAnchor.RightHand, nowhere);
                model.Pose(1.5f, 1f, 0.9f, 0f, 0f);
                Vector3 posedHand = view.Anchor(FighterAnchor.RightHand, nowhere);
                model.Rest();
                Check(Vector3.Distance(restHand, posedHand) > 0.10f,
                    id + ": the hand anchor stays put when the arm swings, it moved " +
                    Vector3.Distance(restHand, posedHand).ToString("0.000") + " m");

                bool skinned = false, primitiveBody = false;
                foreach (var r in view.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is SkinnedMeshRenderer) skinned = true;
                    if (r.name == "Torso" || r.name == "Head") primitiveBody = true;
                }
                Check(skinned, id + ": no skinned mesh under the view");
                Check(!primitiveBody, id + ": the primitive body is still built under the model");
            }
        }

        /// <summary>
        /// The hand props, which used to be welded into the body mesh and so could never
        /// leave the hand. Each is its own object now: the shield rides the left forearm
        /// and the knife the right fist, life size and actually on the figure; a throw
        /// sends the object the fighter is carrying rather than a slab standing in for
        /// it, empties the hand for exactly as long as a copy is in the air, gives it
        /// back when the last copy is spent, and tells the guest which prop and which
        /// hand so it can draw the same thing. A character with no such prop still
        /// throws, on the primitive slab. The sizes are asserted because the props are
        /// baked in metres in the bone's own frame: a unit mistake would be a 100x
        /// shield, not a slightly wrong one.
        /// </summary>
        private static void Props()
        {
            var nowhere = new Vector3(-999f, -999f, -999f);

            // --- what each character carries -------------------------------------
            var vanguard = ModelView(GameConfig.Vanguard, "RegressionPropHost", Vector3.zero);
            Check(vanguard.HasProp(ProjectileShape.Shield),
                "The Vanguard is not wearing the shield he throws");
            var worn = vanguard.Model.Prop(PropLibrary.WireShield);
            Check(worn != null && worn.activeSelf, "The worn shield is not in the scene");
            Bounds shield = Worn(worn);
            Check(shield.size.y > 0.50f && shield.size.y < 1.10f,
                "The worn shield stands " + shield.size.y.ToString("0.000") +
                " m tall, which is not a shield on an arm");
            Vector3 forearm = vanguard.Anchor(FighterAnchor.LeftForearm, nowhere);
            Check(Vector3.Distance(shield.center, forearm) < 0.45f,
                "The shield is " + Vector3.Distance(shield.center, forearm).ToString("0.000") +
                " m from the forearm it is strapped to");

            var assassin = ModelView(GameConfig.Assassin, "RegressionKnifeHost", Vector3.zero);
            Check(assassin.HasProp(ProjectileShape.Dagger),
                "The Assassin is not carrying the knife he throws");
            Bounds knife = Worn(assassin.Model.Prop(PropLibrary.WireDagger));
            Vector3 fist = assassin.Anchor(FighterAnchor.RightHand, nowhere);
            Check(Vector3.Distance(knife.center, fist) < 0.45f,
                "The knife is " + Vector3.Distance(knife.center, fist).ToString("0.000") +
                " m from the fist it is held in");
            float blade = Mathf.Max(knife.size.x, Mathf.Max(knife.size.y, knife.size.z));
            float edge = Mathf.Min(knife.size.x, Mathf.Min(knife.size.y, knife.size.z));
            Check(blade > 0.40f && blade < 0.75f,
                "The worn knife is " + blade.ToString("0.000") + " m long");
            Check(edge < 0.22f, "The worn knife is " + edge.ToString("0.000") +
                " m thick, which is a brick rather than a blade");

            var mage = ModelView(GameConfig.Mage, "RegressionMageHost", new Vector3(0f, 0f, 0f));
            Check(!mage.HasProp(ProjectileShape.Shield) && !mage.HasProp(ProjectileShape.Dagger),
                "The mage is carrying a weapon that belongs to another character");

            // --- the throw ---------------------------------------------------------
            var sim = ModelMatch(GameConfig.Vanguard, vanguard);
            sim.Local.Position = Vector3.zero;
            sim.Local.Forward = Vector3.right;
            Check(sim.Cast(sim.Local, (int)AbilitySlot.Primary), "Shield Throw rejected");
            sim.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(!worn.activeSelf,
                "The shield is still on the arm while a copy of it is in the air");

            object shot = null;
            foreach (var p in Projectiles(sim)) shot = p;
            Check(shot != null, "The shield never left the hand");
            var flying = (GameObject)AnyField(shot, "Prop").GetValue(shot);
            Check(flying != null,
                "The throw is a placeholder slab, not the Vanguard's own shield");
            var flyingRenderer = flying.GetComponentInChildren<Renderer>();
            Check(flyingRenderer != null, "The thrown shield has nothing to draw");
            Bounds air = flyingRenderer.bounds;
            float reach = Mathf.Max(air.size.x, Mathf.Max(air.size.y, air.size.z));
            Check(reach > 0.50f && reach < 1.10f,
                "The flying shield is " + reach.ToString("0.000") +
                " m across, which is not the size of the shield that left the arm");

            // The wire has to say which prop and which hand, or the guest cannot draw the
            // same object or empty the same arm.
            var snapshot = sim.CaptureSnapshot();
            Check(snapshot.effects.Length >= 1, "The throw was not captured for the guest");
            Check(snapshot.effects[0].prop == PropLibrary.WireShield,
                "The snapshot calls the shield " + snapshot.effects[0].prop);
            Check(snapshot.effects[0].owner == 0,
                "The snapshot put the throw in slot " + snapshot.effects[0].owner);

            var guest = ModelMatch(GameConfig.Vanguard,
                ModelView(GameConfig.Vanguard, "RegressionPropGuest", new Vector3(10f, 0f, 0f)));
            guest.ApplySnapshot(snapshot);
            Transform replica = null;
            foreach (var t in EffectRoot(guest).GetComponentsInChildren<Transform>(true))
                if (t.name == "Replica_prop" + PropLibrary.WireShield) replica = t;
            Check(replica != null, "The guest drew a slab where the host threw a shield");
            var replicaRenderer = replica.GetComponentInChildren<Renderer>();
            Check(replicaRenderer != null, "The guest's shield replica has nothing to draw");
            // The replica is the throw frame, so its length is across the throw rather
            // than up: the size is what matters here, not which axis holds it.
            Bounds copy = replicaRenderer.bounds;
            float copyReach = Mathf.Max(copy.size.x, Mathf.Max(copy.size.y, copy.size.z));
            Check(copyReach > 0.50f && copyReach < 1.10f,
                "The guest's shield replica is " + copyReach.ToString("0.000") +
                " m across, which is not the size of the shield that left the arm");
            Check(!guest.Local.View.Model.Prop(PropLibrary.WireShield).activeSelf,
                "The guest's hand did not empty with the host's");

            // --- and it comes home -------------------------------------------------
            for (int i = 0; i < 180; i++)
            {
                sim.Foe.Position = new Vector3(6f, 0f, 0f);
                sim.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Check(Projectiles(sim).Count == 0, "The thrown shield never came home");
            Check(worn.activeSelf, "The shield did not go back onto the arm after the catch");

            // --- a knife is a different hand ----------------------------------------
            var knifeSim = ModelMatch(GameConfig.Assassin, assassin);
            knifeSim.Local.Position = Vector3.zero;
            knifeSim.Local.Forward = Vector3.right;
            Check(knifeSim.Cast(knifeSim.Local, (int)AbilitySlot.Primary), "Dagger Throw rejected");
            knifeSim.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(!assassin.Model.Prop(PropLibrary.WireDagger).activeSelf,
                "The knife is still in the fist while a copy of it is in the air");

            // The fan of blades is a prop throw too, five knives sharing one hand: the
            // hand only fills again when the last of them is spent.
            for (int i = 0; i < 200; i++) knifeSim.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(Projectiles(knifeSim).Count == 0, "A thrown knife never went away");
            Check(assassin.Model.Prop(PropLibrary.WireDagger).activeSelf,
                "The knife did not come back to the fist");
        }

        /// <summary>A fighter with a real rigged model, for the prop checks.</summary>
        private static CharacterView ModelView(CharacterDef def, string name, Vector3 at)
        {
            var host = Own(new GameObject(name));
            host.transform.position = at;
            var view = CharacterView.Create(def, at, host.transform);
            Own(view.gameObject);
            Check(view.HasModel, def.DisplayName + ": the view is not wearing a rigged model");
            return view;
        }

        /// <summary>A two-fighter match with the local fighter wearing a model.</summary>
        private static MatchSim ModelMatch(CharacterDef localDef, CharacterView localView)
        {
            var foe = CharacterView.Create(GameConfig.Mage, new Vector3(12f, 0f, 0f),
                Own(new GameObject("RegressionPropFoe")).transform);
            Own(foe.gameObject);
            var sim = new MatchSim(new List<MatchSim.Setup>
            {
                new MatchSim.Setup { Def = localDef, View = localView, IsBot = false },
                new MatchSim.Setup { Def = GameConfig.Mage, View = foe, IsBot = false }
            }, ZoneShape.Circle);
            sims.Add(sim);
            return sim;
        }

        /// <summary>The world bounds of a worn prop, read off the copy that is drawn.</summary>
        private static Bounds Worn(GameObject prop)
        {
            Check(prop != null, "No prop to measure");
            var renderer = prop.GetComponentInChildren<Renderer>();
            Check(renderer != null, "The worn prop " + prop.name + " has nothing to draw");
            return renderer.bounds;
        }

        /// <summary>
        /// The Frost Aegis on the fighter itself: a gold dome over an ice-blue
        /// Vanguard, crowned by studs that orbit it so the barrier reads as hardware
        /// rather than a tinted ball, and a brighter dome for the beat after it soaks
        /// a hit - the read that says the damage was eaten rather than taken. The
        /// colour is read off the material the dome actually draws with; the flash is
        /// driven from Update, so the test steps that by hand.
        /// </summary>
        private static void AegisFx()
        {
            var def = GameConfig.Vanguard;
            float amount = def.Get(AbilitySlot.Secondary).ShieldAmount;
            var host = Own(new GameObject("RegressionAegisHost"));
            var view = CharacterView.Create(def, Vector3.zero, host.transform);
            Own(view.gameObject);

            // A fighter who never shields pays nothing for the effect.
            view.SetShield(0f, amount);
            Check(view.transform.Find("ShieldBubble") == null,
                "The aegis was built for a fighter with no shield up");

            view.SetShield(amount, amount);
            var bubble = view.transform.Find("ShieldBubble");
            Check(bubble != null && bubble.gameObject.activeSelf,
                "The aegis did not appear when the shield went up");
            var crown = bubble.Find("ShieldRing");
            Check(crown != null && crown.childCount == 8,
                "The aegis crown has " + (crown == null ? "no studs" : crown.childCount.ToString()) +
                " studs, not 8");

            var dome = bubble.Find("ShieldDome");
            Check(dome != null, "The aegis has no dome");
            var renderer = dome.GetComponent<Renderer>();
            var material = dome.GetComponent<OwnedMaterial>().Material;
            Check(material != null && renderer.sharedMaterial == material,
                "The aegis dome does not draw with the material it owns");
            Check(material.shader != null && material.shader.name.Contains("Universal"),
                "The aegis dome is on " +
                (material.shader == null ? "no shader" : material.shader.name));

            Color gold = MaterialColor(material);
            Check(gold.a < 1f, "The aegis dome is opaque");
            Check(IsGold(gold), "The aegis dome is " + gold.ToString("F2") +
                ", which is not the yellow effect it is supposed to be");
            var stud = crown.GetChild(0).GetComponent<Renderer>();
            var studMaterial = crown.GetChild(0).GetComponent<OwnedMaterial>().Material;
            Check(studMaterial != null && stud.sharedMaterial == studMaterial,
                "An aegis stud does not draw with the material it owns");
            Check(IsGold(MaterialColor(studMaterial)), "The aegis studs are not yellow");

            // A soak is the shield dropping: the next animated step flashes the dome.
            view.SetShield(amount - 12f, amount);
            Invoke(view, "AnimateShield");
            Color flash = MaterialColor(material);
            Check(flash.a > gold.a + 0.10f && flash.g > gold.g,
                "The aegis did not flash when it soaked a hit: " + gold.ToString("F2") +
                " stayed put");

            // The crown goes with the dome, so nothing is left orbiting an empty air.
            view.SetShield(0f, amount);
            Check(!bubble.gameObject.activeSelf, "The aegis survived the shield expiring");
        }

        private static Color MaterialColor(Material m)
        {
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
            return Color.black;
        }

        /// <summary>
        /// Yellow, as a band rather than one value: the aegis has to read warm on the
        /// ice-blue fighter it sits over, and the lit and resting colours are the two
        /// ends of that band.
        /// </summary>
        private static bool IsGold(Color c)
        {
            return c.r > 0.85f && c.g > 0.55f && c.g < 0.98f && c.b < 0.5f && c.b < c.g - 0.2f;
        }

        /// <summary>
        /// Every perimeter run must lie along its own edge of the play square and
        /// stay out of the sand. A run built with span and thickness swapped is
        /// still built, still lit and still owns its material, but it stands
        /// across the middle of the arena - so shape and placement are asserted,
        /// not just presence.
        /// </summary>
        private static void Perimeter()
        {
            var arena = Own(new GameObject("RegressionPerimeter")).AddComponent<ArenaBuilder>();
            var geometry = arena.transform.Find("Geometry");
            Check(geometry != null, "Arena geometry node missing");
            float half = GameConfig.ArenaWidth * 0.5f;
            float clear = half - GameConfig.ArenaWallInset - GameConfig.PlayerRadius;

            foreach (var side in new[] { "E", "W", "N", "S" })
            {
                bool alongZ = side == "E" || side == "W";
                foreach (var course in new[] { "WallLo_", "WallHi_", "Band_" })
                {
                    var part = geometry.Find(course + side);
                    Check(part != null, "Missing " + course + side);
                    var bounds = part.GetComponent<Renderer>().bounds;

                    // Long along its own edge, thin across it.
                    float breadth = alongZ ? bounds.size.z : bounds.size.x;
                    float gauge = alongZ ? bounds.size.x : bounds.size.z;
                    Check(breadth > gauge * 4f, course + side + " is not aligned with its edge");

                    // Centred on that edge, and clear of the play square.
                    Near(alongZ ? Mathf.Abs(part.position.x) : Mathf.Abs(part.position.z), half, 0.01f);
                    float gap = alongZ
                        ? Mathf.Min(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x))
                        : Mathf.Min(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z));
                    Check(gap >= clear, course + side + " reaches into the play area (" + gap + ")");
                }
            }
        }

        /// <summary>
        /// The colosseum's seating is a circle while the arena is a square, so
        /// the playable region has its four corners cut. Without that cut a
        /// fighter driven into a corner stands in the second row of the stands:
        /// (14.6, 14.6) is 20.6 from the centre, well past the bowl's inner face.
        /// </summary>
        private static void ArenaReach()
        {
            var s = Match();
            var probes = new[]
            {
                new Vector3(200f, 0f, 200f), new Vector3(-200f, 0f, 200f),
                new Vector3(200f, 0f, -200f), new Vector3(-200f, 0f, -200f),
                new Vector3(200f, 0f, 0f), new Vector3(0f, 0f, -200f),
                new Vector3(200f, 0f, 120f), new Vector3(-120f, 0f, 200f),
                new Vector3(GameConfig.PlayDiagonal, 0f, GameConfig.PlayDiagonal)
            };

            foreach (var probe in probes)
            {
                s.Local.Position = probe;
                s.Local.Velocity = Vector3.zero;
                s.Tick(GameConfig.FixedTimestep, Vector3.zero);

                float reach = new Vector2(s.Local.Position.x, s.Local.Position.z).magnitude
                    + GameConfig.PlayerRadius;
                Check(reach <= GameConfig.SpectatorInnerRadius,
                    $"Fighter reached the seating at {reach} from {probe}");
            }

            // The cut corners must not have shrunk the field along its axes.
            s.Local.Position = new Vector3(GameConfig.PlayLimit, 0f, 0f);
            s.Local.Velocity = Vector3.zero;
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Local.Position.x, GameConfig.PlayLimit, 0.01f);
        }

        private static void TwoPlayers()
        {
            var sim = Match();
            sim.Tick(GameConfig.FixedTimestep, Vector3.right, Vector3.left * 0.5f);
            Check(sim.Local.Velocity.x > 0f && sim.Foe.Velocity.x < 0f, "Remote movement not applied");
            Near(sim.Local.Velocity.x, -sim.Foe.Velocity.x * 2f);
            Check(sim.Cast(sim.Foe, 0), "Remote fighter cannot cast");
            Check(!sim.Cast(sim.Foe, 0), "Remote cooldown not enforced");
        }

        /// <summary>
        /// The lobby roster is the leader's join screen: bots fill slots, open
        /// slots wait for a human, closed slots stay out of the match, and only
        /// the leader may hand out or take back a seat.
        /// </summary>
        private static void Lobby()
        {
            var roster = new LobbyRoster(LobbyRoster.MaxSlots, CharacterId.Vanguard);
            Check(roster.PlayerCount == 1 && roster.BotCount == 0, "Leader missing from a new lobby");
            Check(!roster.CanStart, "A one-player lobby must not be startable");
            Near(roster.FirstOpenSlot, -1);   // every other slot starts closed

            // Quick setup: bots fill 1..n with cycling characters, rest stays closed.
            roster.SetBotCount(3, CharacterId.Vanguard);
            Check(roster.BotCount == 3 && roster.ParticipantCount == 4, "Bot fill wrong");
            Check(roster[1].Character == CharacterId.Vanguard, "First bot character wrong");
            Check(roster[2].Character == CharacterId.Mage, "Bot characters must cycle");
            Check(roster[3].Character == CharacterId.Assassin, "The cycle skipped a class");
            Check(LobbyRoster.CycleCharacter(CharacterId.Vanguard, 3) == CharacterId.Vanguard,
                "Cycle did not wrap");
            Check(roster[4].State == SlotState.Closed, "Slots past the bots must stay closed");
            Check(roster.CanStart, "Four participants must be startable");

            // A human takes the lowest open seat, and the leader is safe from kicks.
            Check(roster.Open(4) && roster[4].State == SlotState.Open, "Open failed");
            Check(roster.Join("Guest", CharacterId.Mage) == 4, "Join must take the first open slot");
            Check(roster[4].State == SlotState.Player && roster.PlayerCount == 2, "Join state wrong");
            Check(!roster.Kick(LobbyRoster.LeaderIndex), "The leader must not be kickable");
            Check(roster.Kick(4) && roster[4].State == SlotState.Open, "Kick must free the slot");
            Check(!roster.Kick(4), "A free slot is not a player to kick");
            Check(roster.Join("Second", CharacterId.Vanguard) == 4, "Rejoin slot wrong");
            Check(roster.Leave(4) && roster[4].State == SlotState.Open, "Leave must free the slot");

            // Bots come and go without evicting the humans already seated.
            Check(roster.AddBot(CharacterId.Mage) && roster.BotCount == 4, "AddBot wrong");
            Check(roster.RemoveBot() && roster.BotCount == 3, "RemoveBot wrong");
            Check(roster.Close(5) && roster[5].State == SlotState.Closed, "Close wrong");
            Check(!roster.Close(LobbyRoster.LeaderIndex), "The leader slot must stay seated");
            Check(roster.RemoveBot() && roster.RemoveBot() && roster.RemoveBot(), "Bots did not drain");
            Check(!roster.RemoveBot(), "There was no bot left to remove");
            Check(roster.ParticipantCount == 1 && !roster.CanStart, "An empty lobby must not start");

            // A full lobby turns players away instead of stacking them.
            var full = new LobbyRoster(4, CharacterId.Vanguard);
            full.SetBotCount(3, CharacterId.Mage);
            Near(full.FirstOpenSlot, -1);
            Near(full.Join("Late", CharacterId.Mage), -1);

            // Cycling reaches every class rather than repeating one.
            var seen = new HashSet<CharacterId>();
            for (int i = 0; i < 6; i++) seen.Add(LobbyRoster.CycleCharacter(CharacterId.Mage, i));
            Check(seen.Count == System.Enum.GetValues(typeof(CharacterId)).Length,
                "Character cycle never reaches every class");
        }

        /// <summary>
        /// Free-for-all: more than two fighters share one arena, everyone can be
        /// hit by everyone, and the match is decided by who is left standing.
        /// </summary>
        /// <summary>
        /// The flow the game opens with: three mode tiles, then the fighter
        /// picker for the mode that was chosen, and the match with its countdown
        /// as soon as START is pressed.
        /// </summary>
        private static void Flow()
        {
            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            Invoke(bootstrap, "ReturnToMenu");
            var modes = (ModeSelectView)Field(bootstrap, "modes").GetValue(bootstrap);
            var picker = (CharacterSelectView)Field(bootstrap, "signup").GetValue(bootstrap);
            Check(modes.gameObject.activeSelf && !picker.gameObject.activeSelf,
                "The mode tiles must be the first thing the player sees");
            foreach (var tile in new[] { "Tile_Campaign", "Tile_Bots", "Tile_Lan" })
                Check(modes.transform.Find("ModeContent/" + tile) != null, "Missing mode tile " + tile);

            // A mode opens the picker, and the picker is only UI: no arena, no
            // practice sim, no clock running against the player.
            modes.transform.Find("ModeContent/Tile_Campaign").GetComponent<Button>().onClick.Invoke();
            Check(Field(bootstrap, "mode").GetValue(bootstrap).ToString() == "Campaign",
                "The tile click never reached the bootstrap");
            Check(picker.gameObject.activeSelf && !modes.gameObject.activeSelf,
                "A mode tile did not open the fighter picker (picker=" + picker.gameObject.activeSelf +
                ", modes=" + modes.gameObject.activeSelf + ", phase=" +
                Field(bootstrap, "phase").GetValue(bootstrap) + ")");
            Check(Field(bootstrap, "phase").GetValue(bootstrap).ToString() == "Select",
                "The picker phase was not entered");
            Check(Field(bootstrap, "sim").GetValue(bootstrap) == null,
                "The picker must not be simulating a match");
            foreach (var id in (CharacterId[])System.Enum.GetValues(typeof(CharacterId)))
                Check(picker.transform.Find("SelectContent/CardRow/Card_" + id) != null,
                    "The picker does not offer " + id);
            Check(picker.transform.Find("SelectContent/Options/Seg_ZONE/Seg/Opt1") != null,
                "The picker lost the zone row");

            // Picking a fighter and starting hands over to the real match, and
            // the fighter that was picked is the one that spawns.
            picker.transform.Find("SelectContent/CardRow/Card_Mage").GetComponent<Button>().onClick.Invoke();
            Check(picker.Character == CharacterId.Mage, "Tapping a card did not select that fighter");
            picker.transform.Find("SelectContent/Actions/Action_START").GetComponent<Button>().onClick.Invoke();
            Check(Field(bootstrap, "phase").GetValue(bootstrap).ToString() == "Countdown",
                "START did not begin the match");
            Near((float)Field(bootstrap, "timer").GetValue(bootstrap), GameConfig.CountdownSeconds, 0.01f);
            var sim = (MatchSim)Field(bootstrap, "sim").GetValue(bootstrap);
            Check(sim != null && sim.Local.Def.Id == CharacterId.Mage,
                "The match did not use the fighter that was picked");
            Check(sim.Foe.Def.Id != sim.Local.Def.Id, "The opponent must be a different class");
            Check(sim.Fighters.Count == 2, "One opponent was asked for, so the match has two fighters");
            Invoke(bootstrap, "ReturnToMenu");
            Check(modes.gameObject.activeSelf && !picker.gameObject.activeSelf,
                "ReturnToMenu did not restore the mode tiles");
        }

        private static void FreeForAll()
        {
            var sim = Match(CharacterId.Vanguard, true, 2);      // player + three bots
            Check(sim.Fighters.Count == 4, "Free-for-all dropped fighters");
            Check(sim.Local == sim.Fighters[0] && sim.Foe == sim.Fighters[1], "Slot order wrong");
            Check(sim.AliveCount == 4 && !sim.MatchOver, "A four-way match must start live");

            for (int i = 0; i < 120; i++) sim.Tick(GameConfig.FixedTimestep, Vector3.right * 0.3f);
            foreach (var fighter in sim.Fighters)
            {
                Check(!float.IsNaN(fighter.Position.x) && !float.IsNaN(fighter.Health),
                    "Free-for-all produced NaN");
                float reach = new Vector2(fighter.Position.x, fighter.Position.z).magnitude
                    + GameConfig.PlayerRadius;
                Check(reach <= GameConfig.SpectatorInnerRadius,
                    $"A free-for-all fighter left the sand at {reach}");
            }

            // Reset the scoreboard so the knockout below is deterministic, then
            // leave exactly one fighter standing.
            foreach (var fighter in sim.Fighters)
            {
                fighter.Health = GameConfig.MaxHealth;
                fighter.Dead = false;
            }
            for (int i = 1; i < sim.Fighters.Count; i++)
            {
                sim.Fighters[i].Health = 0f;
                sim.Fighters[i].Dead = true;
            }
            Check(sim.AliveCount == 1 && sim.MatchOver, "Last fighter standing not detected");
            Check(sim.LastAlive == sim.Local, "The survivor must be the one still alive");
            Check(sim.Foe.Dead, "A knocked-out fighter must read as dead");
        }

        private static void Snapshots()
        {
            var host = Match(CharacterId.Mage); var guest = Match(CharacterId.Mage);
            host.Local.Health = 42f; host.Local.Forward = Vector3.back;
            host.Cast(host.Foe, 0); host.Cast(host.Local, 2);
            host.Tick(GameConfig.FixedTimestep, Vector3.left, Vector3.right);
            var state = JsonUtility.FromJson<MatchSnapshot>(JsonUtility.ToJson(host.CaptureSnapshot()));
            guest.ApplySnapshot(state);
            Near((host.Foe.Position - guest.Foe.Position).magnitude, 0f);
            Near(guest.Local.Health, 42f);
            Near(guest.Foe.Cooldowns[0], host.Foe.Cooldowns[0]);
            Near((guest.Local.Forward - host.Local.Forward).magnitude, 0f);
            var root = EffectRoot(guest);
            Check(root.transform.childCount == state.effects.Length, "Effects missing on guest");
            for (int i = 0; i < 20; i++) guest.ApplySnapshot(state);
            Check(root.transform.childCount == state.effects.Length, "Replica effects leak per snapshot");
        }

        private static void Until(Func<bool> done, Action pump)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < 3)
            { pump(); System.Threading.Thread.Sleep(1); }
            Check(done(), "LAN loopback timed out");
        }

        private static void LanTransport()
        {
            using (var host = LanSession.Host(0))
            using (var guest = LanSession.Join("127.0.0.1", host.ListeningPort))
            {
                LanMessage input = null, snapshot = null;
                host.Received += m => input = m;
                guest.Received += m => snapshot = m;
                Action pump = () => { host.Pump(); guest.Pump(); };
                Until(() => host.Connected && guest.Connected, pump);
                guest.Send(new LanMessage { kind = "hello", character = CharacterId.Mage });
                Until(() => input != null, pump);
                Check(input.character == CharacterId.Mage && input.kind == "hello", "Handshake corrupted");
                input = null;
                guest.Send(new LanMessage { kind = "input", input = Vector3.left * 0.5f });
                guest.Send(new LanMessage { kind = "cast", slot = 2 });
                int received = 0;
                host.Received += m => received++;
                Until(() => received == 2, pump);
                Check(input.kind == "cast" && input.slot == 2, "Messages reordered");
                var sim = Match();
                host.Send(new LanMessage { kind = "state", snapshot = sim.CaptureSnapshot(), phase = 1 });
                Until(() => snapshot != null, pump);
                Near(snapshot.snapshot.host.health, 100f);
                guest.Dispose();
                Until(() => host.Error != null, host.Pump);
                Check(!host.Active && !host.Connected, "Disconnected socket retained");
            }
        }

        private static void LanValidation()
        {
            bool rejected = false;
            try { using (LanSession.Join("not-an-IP")) { } }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid address accepted");
            // An empty or null address is refused with a message, never with a
            // NullReferenceException from inside the socket call.
            foreach (var blank in new[] { string.Empty, null })
            {
                bool refused = false;
                try { using (LanSession.Join(blank)) { } }
                catch (ArgumentException) { refused = true; }
                Check(refused, "A blank address (" + (blank == null ? "null" : "empty") +
                    ") was not refused with a message");
            }
            using (var host = LanSession.Host(0))
            using (var raw = new TcpClient())
            {
                raw.Connect(IPAddress.Loopback, host.ListeningPort);
                Until(() => host.Connected, host.Pump);
                var bytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(999999));
                raw.GetStream().Write(bytes, 0, bytes.Length);
                Until(() => host.Error != null, host.Pump);
                Check(!host.Active, "Malformed peer retained");
            }
        }

        private static void LanTimeout()
        {
            int port;
            using (var host = LanSession.Host(0))
            {
                port = host.ListeningPort;
                Check(!host.CheckTimeout(100), "Waiting host must allow indefinite waiting");
                using (var guest = LanSession.Join("127.0.0.1", port))
                {
                    Until(() => host.Connected && guest.Connected, () => { host.Pump(); guest.Pump(); });
                    Check(guest.CheckTimeout(100) && guest.Error != null, "Idle connection did not time out");
                }
            }
            using (var reopened = LanSession.Host(port)) Check(reopened.Active, "Cancel leaked listening port");
        }

        private static void LanMenuFlow()
        {
            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            Invoke(bootstrap, "ReturnToMenu");
            var modes = (ModeSelectView)Field(bootstrap, "modes").GetValue(bootstrap);
            var picker = (CharacterSelectView)Field(bootstrap, "signup").GetValue(bootstrap);
            Button ButtonAt(string path) => picker.transform.Find("SelectContent/" + path).GetComponent<Button>();
            // The three mode tiles are the first screen; the network tile opens
            // the picker, which owns host/join, the zone and the action.
            modes.transform.Find("ModeContent/Tile_Lan").GetComponent<Button>().onClick.Invoke();
            Check(picker.gameObject.activeSelf && !modes.gameObject.activeSelf,
                "The network tile did not open the fighter picker (picker=" + picker.gameObject.activeSelf +
                ", modes=" + modes.gameObject.activeSelf + ", phase=" +
                Field(bootstrap, "phase").GetValue(bootstrap) + ", mode=" +
                Field(bootstrap, "mode").GetValue(bootstrap) + ")");
            ButtonAt("Actions/Action_START").onClick.Invoke();
            var host = (LanSession)Field(bootstrap, "network").GetValue(bootstrap);
            Check(host != null && host.IsHost, "Host UI did not create listener (mode=" +
                Field(bootstrap, "mode").GetValue(bootstrap) + ")");
            // The zone picker stays live while the host waits, and its choice
            // must survive until the guest joins (it used to be silently lost).
            var zoneSquare = ButtonAt("Options/Seg_ZONE/Seg/Opt1");
            Check(zoneSquare.interactable, "Zone picker locked while the host waits");
            ButtonAt("Options/Seg_ZONE/Seg/Opt0").onClick.Invoke();
            Check(picker.Zone == ZoneShape.Circle, "Zone tap ignored while the host waits");
            zoneSquare.onClick.Invoke();
            Check(picker.Zone == ZoneShape.Square, "Zone picker lost the waiting host's choice");
            using (var guest = LanSession.Join("127.0.0.1"))
            {
                LanMessage state = null, welcome = null;
                guest.Received += m =>
                {
                    if (m.kind == "state") state = m;
                    else if (m.kind == "welcome") welcome = m;
                };
                Action pump = () => { Invoke(bootstrap, "StepNetwork"); guest.Pump(); };
                Until(() => guest.Connected && host.Connected, pump);
                guest.Send(new LanMessage { kind = "hello", character = CharacterId.Mage });
                Until(() => (bool)Field(bootstrap, "handshake").GetValue(bootstrap), pump);
                Until(() => welcome != null, pump);
                var sim = (MatchSim)Field(bootstrap, "sim").GetValue(bootstrap);
                Check(!sim.Foe.IsBot && sim.Foe.Def.Id == CharacterId.Mage, "Guest pick ignored");
                Check(welcome.zone == ZoneShape.Square, "Welcome carried a stale zone");
                Check(sim.Zone == ZoneShape.Square, "Host handshake ignored the zone picked while waiting");
                var builtArena = (ArenaBuilder)Field(bootstrap, "arena").GetValue(bootstrap);
                Check(builtArena.Shape == ZoneShape.Square, "Arena kept the previous zone shape");
                Field(bootstrap, "timer").SetValue(bootstrap, 0f);
                Invoke(bootstrap, "StepCountdown");
                guest.Send(new LanMessage { kind = "input", input = Vector3.left });
                guest.Send(new LanMessage { kind = "cast", slot = 1 });
                Until(() => ((Queue<int>)Field(bootstrap, "remoteCasts").GetValue(bootstrap)).Count == 1, pump);
                Invoke(bootstrap, "StepMatch");
                Check(sim.Foe.Invulnerable > 0f && sim.Foe.Position.x < 10f, "Guest movement/cast not authoritative");
                state = null;
                Field(bootstrap, "sendTimer").SetValue(bootstrap, 0f);
                Until(() => state != null, pump);
                Check(state.phase == 1, "Host playing phase not replicated");
                guest.Dispose();
                Until(() => Field(bootstrap, "network").GetValue(bootstrap) == null, () => Invoke(bootstrap, "StepNetwork"));
                Check(modes.gameObject.activeSelf && !picker.gameObject.activeSelf,
                    "Disconnect did not restore the mode screen");
            }
            // Join path uses the same input field and button that phone users touch.
            using (var remoteHost = LanSession.Host())
            {
                modes.transform.Find("ModeContent/Tile_Lan").GetComponent<Button>().onClick.Invoke();
                ButtonAt("Options/LanBox/Seg/Role/Seg/Opt1").onClick.Invoke();
                Check(!ButtonAt("Options/Seg_ZONE/Seg/Opt0").interactable,
                    "Join phone still offers the zone the host decides");
                var address = (TMPro.TMP_InputField)Field(picker, "hostAddress").GetValue(picker);
                address.text = "127.0.0.1";
                ButtonAt("Actions/Action_START").onClick.Invoke();
                LanMessage hello = null;
                remoteHost.Received += m => hello = m;
                Action pump = () => { Invoke(bootstrap, "StepNetwork"); remoteHost.Pump(); };
                Until(() => hello != null, pump);
                Check(hello.kind == "hello" && hello.character == picker.Character,
                    "Join did not announce the local fighter");
                remoteHost.Send(new LanMessage { kind = "welcome", hostCharacter = CharacterId.Vanguard,
                    character = CharacterId.Mage, zone = ZoneShape.Square });
                Until(() => (bool)Field(bootstrap, "handshake").GetValue(bootstrap), pump);
                var sim = (MatchSim)Field(bootstrap, "sim").GetValue(bootstrap);
                var snapshot = sim.CaptureSnapshot();
                snapshot.guest.health = 73f; snapshot.guest.cooldowns[2] = 4f;
                remoteHost.Send(new LanMessage { kind = "state", snapshot = snapshot, phase = 1 });
                Until(() => sim.Foe.Health == 73f, pump);
                var rig = (MobaCameraRig)Field(bootstrap, "rig").GetValue(bootstrap);
                Check(Field(rig, "target").GetValue(rig) == sim.Foe.View.transform, "Guest camera follows wrong player");
                Invoke(bootstrap, "ReturnToMenu");
            }
        }

        /// <summary>
        /// The picker shares one row between the zone, the opponents and the
        /// network box, laid out against a 1280x720 reference. Phones are not
        /// that shape, so the row is measured where it is actually used: a
        /// screen-space canvas sized like the phone's reference units, plus the
        /// game's own canvas in this session. What matters for a hotspot match
        /// is that the HOST / JOIN buttons and the address field a guest types
        /// into are on screen and stay big enough to hit - a squeezed or
        /// overflowed row is how "the buttons do not work" looks on a phone.
        /// </summary>
        private static void LanPickerFits()
        {
            foreach (float aspect in new[] { 16f / 9f, 18f / 9f, 20f / 9f, 4f / 3f })
            {
                float units = 720f * aspect;      // canvas reference units, height matched
                var host = Own(new GameObject("RegressionPickerCanvas", typeof(RectTransform), typeof(Canvas)));
                var canvas = host.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var root = (RectTransform)host.transform;
                root.sizeDelta = new Vector2(units, 720f);

                var picker = CharacterSelectView.Build(root);
                picker.Show(GameMode.LanJoin, CharacterId.Vanguard, ZoneShape.Circle, 1, 1);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(root);

                string label = aspect.ToString("0.00") + ":1";
                Fits(root, label, "Options/LanBox/Seg/Role/Seg/Opt0", 60f, 44f);
                Fits(root, label, "Options/LanBox/Seg/Role/Seg/Opt1", 60f, 44f);
                Fits(root, label, "Options/LanBox/Seg/IpField", 150f, 40f);
                Fits(root, label, "Actions/Action_START", 200f, 50f);
                Fits(root, label, "Actions/Action_BACK", 120f, 50f);
            }
        }

        /// <summary>
        /// One control inside the picker, measured against the screen it is drawn
        /// on: fully on screen, and at least the given size in canvas units.
        /// </summary>
        private static void Fits(RectTransform screen, string label, string path,
            float minWidth, float minHeight)
        {
            var rt = (RectTransform)screen.Find("SelectBackdrop/SelectContent/" + path);
            Check(rt != null, label + ": the picker has no " + path);
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = screen.InverseTransformPoint(corners[i]);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            Vector2 half = screen.rect.size * 0.5f;
            Check(minX >= -half.x - 0.5f && maxX <= half.x + 0.5f &&
                  minY >= -half.y - 0.5f && maxY <= half.y + 0.5f,
                label + ": " + path + " runs off the screen (x " + minX.ToString("0") +
                ".." + maxX.ToString("0") + " of " + (-half.x).ToString("0") + ".." +
                half.x.ToString("0") + ")");
            Check(rt.rect.width >= minWidth && rt.rect.height >= minHeight,
                label + ": " + path + " is squeezed to " + rt.rect.width.ToString("0") + "x" +
                rt.rect.height.ToString("0") + ", under the " + minWidth.ToString("0") + "x" +
                minHeight.ToString("0") + " a finger needs");
        }

        /// <summary>
        /// Discovery is what makes a hotspot match start without anyone reading
        /// an address out loud: the host shouts its port on the broadcast
        /// address of every network it is on, a joining phone hears it, and the
        /// address it heard is one you can actually connect to. Junk on the port
        /// is ignored rather than trusted.
        /// </summary>
        private static void LanDiscoveryFindsHost()
        {
            using (var host = LanSession.Host(0))
            using (var beacon = new LanBeacon(host.ListeningPort))
            using (var finder = new LanFinder())
            {
                Check(beacon.Error == null, "The host could not shout: " + beacon.Error);
                Check(finder.Error == null, "The guest could not listen: " + finder.Error);

                // The beacon's real work is a broadcast per interface; a directed
                // datagram is the same send, aimed at something a test can check.
                beacon.SendTo(IPAddress.Loopback);
                Until(() => { finder.Poll(); return finder.Best != null; },
                    () => System.Threading.Thread.Sleep(10));
                var found = finder.Best;
                Check(found != null, "The shouted port never arrived");
                Check(found.Port == host.ListeningPort,
                    "The beacon carried port " + found.Port + " instead of " + host.ListeningPort);
                Check(found.Address == "127.0.0.1", "The beacon carried " + found.Address);

                // What discovery offers is a host you can join.
                using (var guest = LanSession.Join(found.Address, found.Port))
                {
                    LanMessage hello = null;
                    host.Received += m => hello = m;
                    Until(() => host.Connected && guest.Connected, () => { host.Pump(); guest.Pump(); });
                    guest.Send(new LanMessage { kind = "hello", character = CharacterId.Mage });
                    Until(() => hello != null, () => { host.Pump(); guest.Pump(); });
                    Check(hello.character == CharacterId.Mage, "The discovered host dropped the handshake");
                }
            }

            using (var finder = new LanFinder())
            {
                Check(finder.Error == null, "The guest could not listen: " + finder.Error);
                Check(!finder.Observe(System.Text.Encoding.UTF8.GetBytes("nonsense"), IPAddress.Loopback),
                    "A junk datagram was read as a host");
                Check(!finder.Observe(System.Text.Encoding.UTF8.GetBytes(LanDiscovery.Token + " not-a-port"),
                        IPAddress.Loopback),
                    "A beacon without a usable port was read as a host");
                Check(finder.Best == null, "Junk became a joinable host");
            }
        }

        /// <summary>
        /// The two ways a hotspot session used to look like a dead button: a
        /// join that cannot reach anyone dropped the player back to the mode
        /// tiles with a one-line notice they never saw, and a host waiting for a
        /// guest had no way out except BACK. Both now belong to the picker the
        /// player is standing on.
        /// </summary>
        private static void LanRetry()
        {
            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            Invoke(bootstrap, "ReturnToMenu");
            var modes = (ModeSelectView)Field(bootstrap, "modes").GetValue(bootstrap);
            var picker = (CharacterSelectView)Field(bootstrap, "signup").GetValue(bootstrap);
            Button ButtonAt(string path) => picker.transform.Find("SelectContent/" + path).GetComponent<Button>();
            var start = ButtonAt("Actions/Action_START");
            var startLabel = picker.transform.Find("SelectContent/Actions/Action_START/Text")
                .GetComponent<TMPro.TextMeshProUGUI>();
            var status = (TMPro.TextMeshProUGUI)Field(picker, "status").GetValue(picker);

            // A host that waits is offered the way out it is looking at.
            modes.transform.Find("ModeContent/Tile_Lan").GetComponent<Button>().onClick.Invoke();
            start.onClick.Invoke();
            Check(Field(bootstrap, "network").GetValue(bootstrap) != null,
                "The host wait never opened a listener");
            Check(startLabel.text == "CANCEL",
                "A waiting host has no way out: the button says " + startLabel.text);

            // The waiting host shouts, so the other phone can find it without
            // anyone reading an address out loud.
            Invoke(bootstrap, "StepDiscovery");
            var beacon = (LanBeacon)Field(bootstrap, "beacon").GetValue(bootstrap);
            Check(beacon != null, "The waiting host never started shouting");
            using (var finder = new LanFinder())
            {
                Check(finder.Error == null, "The other phone could not listen: " + finder.Error);
                var shout = System.Diagnostics.Stopwatch.StartNew();
                while (finder.Best == null && shout.Elapsed.TotalSeconds < 3)
                {
                    Invoke(bootstrap, "StepDiscovery");
                    finder.Poll();
                    System.Threading.Thread.Sleep(25);
                }
                var heard = finder.Best;
                Check(heard != null, "Nobody heard the waiting host on the hotspot");
                Check(heard.Port == ((LanSession)Field(bootstrap, "network").GetValue(bootstrap)).ListeningPort,
                    "The shout carried port " + heard.Port + ", not the listening one");
            }

            start.onClick.Invoke();
            Check(Field(bootstrap, "network").GetValue(bootstrap) == null, "Cancel left the listener running");
            Check(modes.gameObject.activeSelf && !picker.gameObject.activeSelf,
                "Cancel did not come back to the mode tiles");

            // A join nobody can answer stays put and says so.
            modes.transform.Find("ModeContent/Tile_Lan").GetComponent<Button>().onClick.Invoke();
            ButtonAt("Options/LanBox/Seg/Role/Seg/Opt1").onClick.Invoke();
            var address = (TMPro.TMP_InputField)Field(picker, "hostAddress").GetValue(picker);

            // JOIN with an empty address is the one press that can never work:
            // it must ask for the host's IP, not throw out of the button press
            // (it used to raise a NullReferenceException and look like a crash).
            address.text = string.Empty;
            Field(bootstrap, "discoveredAddress").SetValue(bootstrap, null);
            start.onClick.Invoke();
            Check(picker.gameObject.activeSelf && !modes.gameObject.activeSelf,
                "An empty join dropped the player out of the picker");
            Check(status.text.ToLowerInvariant().Contains("host ip"),
                "An empty join never asked for the host's IP address: " + status.text);
            Check(status.color == UiFactory.Bad, "An empty join was not shown as a failure");
            Check(start.interactable && startLabel.text == "JOIN BATTLE",
                "An empty join left the action button unusable (" + startLabel.text + ")");
            Check(Field(bootstrap, "network").GetValue(bootstrap) == null, "An empty join opened a session");

            address.text = "not-an-address";
            start.onClick.Invoke();
            Check(picker.gameObject.activeSelf && !modes.gameObject.activeSelf,
                "A failed join dropped the player out of the picker");
            Check(status.text.Length > 0, "A failed join said nothing at all");
            Check(status.color == UiFactory.Bad, "A failed join was not shown as a failure");
            Check(start.interactable && startLabel.text == "JOIN BATTLE",
                "A failed join left the action button unusable (" + startLabel.text + ")");
            Check(Field(bootstrap, "network").GetValue(bootstrap) == null, "A failed join kept a session");
            Invoke(bootstrap, "ReturnToMenu");
        }

        /// <summary>
        /// The whole point of discovery, driven the way a player drives it: the
        /// picker is opened on JOIN, a host starts shouting nearby, and the
        /// address field fills itself. The same button a guest presses then
        /// connects to that host and handshakes - nothing was typed.
        /// </summary>
        private static void LanAutoJoin()
        {
            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            Invoke(bootstrap, "ReturnToMenu");
            var modes = (ModeSelectView)Field(bootstrap, "modes").GetValue(bootstrap);
            var picker = (CharacterSelectView)Field(bootstrap, "signup").GetValue(bootstrap);
            Button ButtonAt(string path) => picker.transform.Find("SelectContent/" + path).GetComponent<Button>();

            using (var host = LanSession.Host())
            using (var beacon = new LanBeacon(host.ListeningPort))
            {
                Check(beacon.Error == null, "The host could not shout: " + beacon.Error);
                modes.transform.Find("ModeContent/Tile_Lan").GetComponent<Button>().onClick.Invoke();
                ButtonAt("Options/LanBox/Seg/Role/Seg/Opt1").onClick.Invoke();
                var address = (TMPro.TMP_InputField)Field(picker, "hostAddress").GetValue(picker);
                Check(address.text.Length == 0, "The address field started out filled");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (address.text.Length == 0 && clock.Elapsed.TotalSeconds < 3)
                {
                    beacon.Tick();
                    Invoke(bootstrap, "StepDiscovery");
                    System.Threading.Thread.Sleep(25);
                }
                Check(address.text.Length > 0,
                    "Discovery never offered the host shouting on the hotspot");
                var status = (TMPro.TextMeshProUGUI)Field(picker, "status").GetValue(picker);
                Check(status.text.Contains(address.text),
                    "The picker never said which host it found: " + status.text);

                // Nothing was typed: this is the address a guest would have had
                // to read off the other phone.
                ButtonAt("Actions/Action_START").onClick.Invoke();
                LanMessage hello = null;
                host.Received += m => hello = m;
                Action pump = () => { Invoke(bootstrap, "StepNetwork"); host.Pump(); };
                Until(() => hello != null, pump);
                Check(hello.kind == "hello" && hello.character == picker.Character,
                    "The guest did not announce itself to the host discovery found");
                host.Send(new LanMessage { kind = "welcome", hostCharacter = CharacterId.Vanguard,
                    character = CharacterId.Mage, zone = ZoneShape.Circle });
                Until(() => (bool)Field(bootstrap, "handshake").GetValue(bootstrap), pump);
                Check(Field(bootstrap, "sim").GetValue(bootstrap) != null,
                    "A discovered join never built the match");
                Invoke(bootstrap, "ReturnToMenu");
            }
        }

        private static void SnapshotSmoothing()
        {
            var host = Match(CharacterId.Mage); var guest = Match(CharacterId.Mage);
            host.Local.Position = new Vector3(2f, 0f, -3f);
            var first = JsonUtility.FromJson<MatchSnapshot>(JsonUtility.ToJson(host.CaptureSnapshot()));
            guest.ApplySnapshot(first);
            guest.AdvanceSnapshotView(1f);
            var view = guest.Local.View.transform;
            Near((view.position - first.host.position).magnitude, 0f);

            host.Local.Position = new Vector3(12f, 0f, -3f);
            var second = JsonUtility.FromJson<MatchSnapshot>(JsonUtility.ToJson(host.CaptureSnapshot()));
            guest.ApplySnapshot(second);
            // Simulation state jumps to the newest snapshot at once...
            Near((guest.Local.Position - second.host.position).magnitude, 0f);
            // ...while the rendered view eases from the previous snapshot.
            guest.AdvanceSnapshotView(0f);
            Near(view.position.x, first.host.position.x, 0.01f);
            float interval = (float)Field(guest, "renderInterval").GetValue(guest);
            Check(interval > 0f, "Snapshot spacing was not measured");
            guest.AdvanceSnapshotView(interval * 0.5f);
            Near(view.position.x, (first.host.position.x + second.host.position.x) * 0.5f, 0.02f);
            guest.AdvanceSnapshotView(interval);
            Near(view.position.x, second.host.position.x, 0.01f);
            // Replaying the newest snapshot must not drag the view backwards.
            guest.ApplySnapshot(second);
            guest.AdvanceSnapshotView(interval);
            Near(view.position.x, second.host.position.x, 0.01f);
        }

        private static void Orientation()
        {
            // Read the settings that end up in the Android manifest: auto
            // rotation with both landscapes and no portrait is what Unity
            // emits as sensorLandscape, which opens landscape even when the
            // phone's auto-rotate lock is off.
            string settings = File.ReadAllText("ProjectSettings/ProjectSettings.asset");
            Check(settings.Contains("defaultScreenOrientation: 4"),
                "Default screen orientation must stay Auto Rotation");
            Check(settings.Contains("allowedAutorotateToPortrait: 0"),
                "Portrait is still reachable, so a rotation-locked phone stays portrait");
            Check(settings.Contains("allowedAutorotateToPortraitUpsideDown: 0"),
                "Upside-down portrait is still reachable");
            Check(settings.Contains("allowedAutorotateToLandscapeLeft: 1") &&
                  settings.Contains("allowedAutorotateToLandscapeRight: 1"),
                "Both landscape directions must stay allowed");
            // The editor must hold those values too, otherwise a build from
            // this session would still ship portrait.
            Check(!PlayerSettings.allowedAutorotateToPortrait &&
                  !PlayerSettings.allowedAutorotateToPortraitUpsideDown &&
                  PlayerSettings.allowedAutorotateToLandscapeLeft &&
                  PlayerSettings.allowedAutorotateToLandscapeRight,
                "Editor PlayerSettings did not reload the landscape-only orientation");
        }

        private static void CountdownLifecycle()
        {
            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            Check(bootstrap != null, "Bootstrap not installed");
            var start = bootstrap.GetType().GetMethod("StartMatch", BindingFlags.Instance | BindingFlags.NonPublic);
            Invoke(bootstrap, "ReturnToMenu");
            start.Invoke(bootstrap, new object[] { GameMode.Bot, ZoneShape.Circle, CharacterId.Vanguard, CharacterId.Mage });
            Invoke(bootstrap, "ShowGo");
            Invoke(bootstrap, "ReturnToMenu");
            Near((float)Field(bootstrap, "goTimer").GetValue(bootstrap), -1f);
            start.Invoke(bootstrap, new object[] { GameMode.Bot, ZoneShape.Square, CharacterId.Mage, CharacterId.Vanguard });
            Invoke(bootstrap, "HideCountdown");
            var label = (TMPro.TextMeshProUGUI)Field(bootstrap, "countdownLabel").GetValue(bootstrap);
            Check(label.gameObject.activeSelf && label.text == "3", "Stale callback hid new countdown");
            bootstrap.GetType().GetMethod("ShowEndCard", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bootstrap, new object[] { "WIN!" });
            Invoke(bootstrap, "HideCountdown");
            Check(label.gameObject.activeSelf && label.text == "WIN!", "Stale callback hid result");
            Invoke(bootstrap, "ReturnToMenu");
        }

        private static void Reload()
        {
            var s = Match(); var view = s.Local.View;
            Field(view, "block").SetValue(view, null);
            Invoke(view, "OnValidate");
            Check(Field(view, "block").GetValue(view) == null, "OnValidate called renderer APIs immediately");
            Invoke(view, "Update");
            Check(Field(view, "block").GetValue(view) != null, "Reload did not recreate property block");
        }
    }
}
