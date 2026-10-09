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
                { "30/60/120 FPS produce identical state", FrameRates },
                { "Catch-up work is bounded and backlog is retained", CatchUp },
                { "Half stick produces half acceleration", AnalogAcceleration },
                { "Shield Throw flies out, lands once each way and comes home", ShieldThrow },
                { "Dagger Throw outruns an orb and is spent on its hit", DaggerThrow },
                { "Shadowstep trades the ice for grip and speed for three seconds", Shadowstep },
                { "Blade Wave throws a fan that charges each fighter once", BladeWave },
                { "Orb sweeps moving target and respects invulnerability", Orb },
                { "Orb cannot hit a target beyond the wall", OrbWall },
                { "Blink teleport does not become a swept attack", Blink },
                { "Earthrift corridor hit respects length, width and cooldown", Area },
                { "Frost Aegis soaks damage, replicates and expires", Aegis },
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
                { "Guest view eases between host snapshots", SnapshotSmoothing },
                { "Landscape-only orientation for rotation-locked phones", Orientation },
                { "Countdown restart and result are immune to stale hide timers", CountdownLifecycle },
                { "Procedural materials are destroyed with their owners", Materials },
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
            var b = GameConfig.Storm;
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
        /// Shadowstep trades the ice for grip: for its three seconds the assassin
        /// tracks the stick and holds a speed nobody else reaches, so he stops and
        /// turns on a coin — and when the timer runs out the ice comes back.
        /// </summary>
        private static void Shadowstep()
        {
            var plain = Match(CharacterId.Storm);
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
                var s = Match(CharacterId.Storm);
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
            var s = Match(CharacterId.Storm);
            s.Local.Position = new Vector3(GameConfig.PlayLimit - 0.1f, 0f, 0f);
            s.Foe.Position = new Vector3(GameConfig.PlayLimit + 0.9f, 0f, 0f);
            s.Cast(s.Local, 0);
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 100f - GameConfig.ZoneDamagePerSecond * GameConfig.FixedTimestep);
            // Also test an orb crossing the boundary from inside over several steps.
            var inside = Match(CharacterId.Storm);
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
            var s = Match(CharacterId.Storm);
            s.Local.Position = Vector3.zero; s.Local.Forward = Vector3.right;
            s.Foe.Position = new Vector3(3f,0,0);
            Check(s.Cast(s.Local, 1), "Blink rejected");
            Near(s.Local.Position.x, 6f);
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 100f);
            Check(s.Local.Invulnerable > 0f, "Blink lost invulnerability");
        }

        /// <summary>
        /// The Earthrift is a corridor, not a circle: a fighter standing in the
        /// line eats the hit, while one past its end or off to the side does not.
        /// </summary>
        private static void Area()
        {
            var ab = GameConfig.Vanguard.Get(AbilitySlot.Ultimate);
            Check(ab.Telegraph > 0f, "The Earthrift must telegraph its line");
            int warningSteps = Mathf.CeilToInt(ab.Telegraph / GameConfig.FixedTimestep) + 10;

            // Nothing lands while the warning is on the ground, so a fighter in
            // the corridor still has time to walk out of it.
            var inside = Match();
            inside.Foe.Position = Vector3.right * (ab.Range - 0.1f);
            Check(inside.Cast(inside.Local, (int)AbilitySlot.Ultimate), "Rift cast rejected");
            inside.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(inside.Foe.Health, 100f);
            Check(!inside.Cast(inside.Local, (int)AbilitySlot.Ultimate), "Area recast ignored cooldown");

            // Stay in the line and the rift opens under you.
            for (int i = 0; i < warningSteps; i++)
            {
                inside.Foe.Position = Vector3.right * (ab.Range - 0.1f);
                inside.Foe.Velocity = Vector3.zero;
                inside.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(inside.Foe.Health, 100f - ab.Damage);

            // Step out of the corridor during the warning and it misses entirely.
            var dodge = Match();
            dodge.Foe.Position = Vector3.right * (ab.Range * 0.5f);
            dodge.Cast(dodge.Local, (int)AbilitySlot.Ultimate);
            for (int i = 0; i < warningSteps; i++)
            {
                dodge.Foe.Position = Vector3.right * (ab.Range + 4f);
                dodge.Foe.Velocity = Vector3.zero;
                dodge.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(dodge.Foe.Health, 100f);

            // Past the end of the corridor: out of reach.
            var past = Match();
            past.Foe.Position = Vector3.right * (ab.Range + 2f);
            past.Cast(past.Local, (int)AbilitySlot.Ultimate);
            for (int i = 0; i < warningSteps; i++)
            {
                past.Foe.Position = Vector3.right * (ab.Range + 2f);
                past.Foe.Velocity = Vector3.zero;
                past.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(past.Foe.Health, 100f);

            // Well to the side of the corridor: also out of reach.
            var beside = Match();
            beside.Foe.Position = new Vector3(ab.Range * 0.5f, 0f, ab.RiftWidth + 1.5f);
            beside.Cast(beside.Local, (int)AbilitySlot.Ultimate);
            for (int i = 0; i < warningSteps; i++)
            {
                beside.Foe.Position = new Vector3(ab.Range * 0.5f, 0f, ab.RiftWidth + 1.5f);
                beside.Foe.Velocity = Vector3.zero;
                beside.Tick(GameConfig.FixedTimestep, Vector3.zero);
            }
            Near(beside.Foe.Health, 100f);
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

            // A spell hit is absorbed: Storm's vortex bursts for 10 where its
            // throw ends, and health must not move while the shield holds.
            var v = Match();
            v.Cast(v.Local, (int)AbilitySlot.Secondary);
            var vortex = GameConfig.Storm.Get(AbilitySlot.Ultimate);
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
            var ab = GameConfig.Storm.Get(AbilitySlot.Ultimate);
            Check(ab.Telegraph > 0f, "The Vortex must telegraph its line");
            var s = Match(CharacterId.Storm);
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
            var s = Match(CharacterId.Storm, true); s.Foe.Position = s.Local.Position;
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
            var sim = Match(CharacterId.Storm);
            sim.Cast(sim.Local, 0); sim.Cast(sim.Local, 1); sim.Cast(sim.Local, 2);
            var root = EffectRoot(sim);
            foreach (var owner in root.GetComponentsInChildren<OwnedMaterial>())
                releasedMaterials.Add(owner.Material);
            Check(releasedMaterials.Count >= 10, "Missing ownership on generated renderers");
            sim.Clear();
            Check(Resources.Load<Material>("Arena/ArenaLit") != null, "Shared material asset destroyed");
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
            Check(roster[2].Character == CharacterId.Storm, "Bot characters must cycle");
            Check(roster[3].Character == CharacterId.Assassin, "The cycle skipped a class");
            Check(LobbyRoster.CycleCharacter(CharacterId.Vanguard, 3) == CharacterId.Vanguard,
                "Cycle did not wrap");
            Check(roster[4].State == SlotState.Closed, "Slots past the bots must stay closed");
            Check(roster.CanStart, "Four participants must be startable");

            // A human takes the lowest open seat, and the leader is safe from kicks.
            Check(roster.Open(4) && roster[4].State == SlotState.Open, "Open failed");
            Check(roster.Join("Guest", CharacterId.Storm) == 4, "Join must take the first open slot");
            Check(roster[4].State == SlotState.Player && roster.PlayerCount == 2, "Join state wrong");
            Check(!roster.Kick(LobbyRoster.LeaderIndex), "The leader must not be kickable");
            Check(roster.Kick(4) && roster[4].State == SlotState.Open, "Kick must free the slot");
            Check(!roster.Kick(4), "A free slot is not a player to kick");
            Check(roster.Join("Second", CharacterId.Vanguard) == 4, "Rejoin slot wrong");
            Check(roster.Leave(4) && roster[4].State == SlotState.Open, "Leave must free the slot");

            // Bots come and go without evicting the humans already seated.
            Check(roster.AddBot(CharacterId.Storm) && roster.BotCount == 4, "AddBot wrong");
            Check(roster.RemoveBot() && roster.BotCount == 3, "RemoveBot wrong");
            Check(roster.Close(5) && roster[5].State == SlotState.Closed, "Close wrong");
            Check(!roster.Close(LobbyRoster.LeaderIndex), "The leader slot must stay seated");
            Check(roster.RemoveBot() && roster.RemoveBot() && roster.RemoveBot(), "Bots did not drain");
            Check(!roster.RemoveBot(), "There was no bot left to remove");
            Check(roster.ParticipantCount == 1 && !roster.CanStart, "An empty lobby must not start");

            // A full lobby turns players away instead of stacking them.
            var full = new LobbyRoster(4, CharacterId.Vanguard);
            full.SetBotCount(3, CharacterId.Storm);
            Near(full.FirstOpenSlot, -1);
            Near(full.Join("Late", CharacterId.Storm), -1);

            // Cycling reaches every class rather than repeating one.
            var seen = new HashSet<CharacterId>();
            for (int i = 0; i < 6; i++) seen.Add(LobbyRoster.CycleCharacter(CharacterId.Storm, i));
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
            picker.transform.Find("SelectContent/CardRow/Card_Storm").GetComponent<Button>().onClick.Invoke();
            Check(picker.Character == CharacterId.Storm, "Tapping a card did not select that fighter");
            picker.transform.Find("SelectContent/Actions/Action_START").GetComponent<Button>().onClick.Invoke();
            Check(Field(bootstrap, "phase").GetValue(bootstrap).ToString() == "Countdown",
                "START did not begin the match");
            Near((float)Field(bootstrap, "timer").GetValue(bootstrap), GameConfig.CountdownSeconds, 0.01f);
            var sim = (MatchSim)Field(bootstrap, "sim").GetValue(bootstrap);
            Check(sim != null && sim.Local.Def.Id == CharacterId.Storm,
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
            var host = Match(CharacterId.Storm); var guest = Match(CharacterId.Storm);
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
                guest.Send(new LanMessage { kind = "hello", character = CharacterId.Storm });
                Until(() => input != null, pump);
                Check(input.character == CharacterId.Storm && input.kind == "hello", "Handshake corrupted");
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
                guest.Send(new LanMessage { kind = "hello", character = CharacterId.Storm });
                Until(() => (bool)Field(bootstrap, "handshake").GetValue(bootstrap), pump);
                Until(() => welcome != null, pump);
                var sim = (MatchSim)Field(bootstrap, "sim").GetValue(bootstrap);
                Check(!sim.Foe.IsBot && sim.Foe.Def.Id == CharacterId.Storm, "Guest pick ignored");
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
                    character = CharacterId.Storm, zone = ZoneShape.Square });
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

        private static void SnapshotSmoothing()
        {
            var host = Match(CharacterId.Storm); var guest = Match(CharacterId.Storm);
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
            start.Invoke(bootstrap, new object[] { GameMode.Bot, ZoneShape.Circle, CharacterId.Vanguard, CharacterId.Storm });
            Invoke(bootstrap, "ShowGo");
            Invoke(bootstrap, "ReturnToMenu");
            Near((float)Field(bootstrap, "goTimer").GetValue(bootstrap), -1f);
            start.Invoke(bootstrap, new object[] { GameMode.Bot, ZoneShape.Square, CharacterId.Storm, CharacterId.Vanguard });
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
