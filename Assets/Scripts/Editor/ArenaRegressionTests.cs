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
        private const string Request = "Temp/arena-regression.request";
        private const string Result = "Temp/arena-regression-results.txt";
        private static readonly List<GameObject> owned = new List<GameObject>();
        private static readonly List<MatchSim> sims = new List<MatchSim>();
        private static double readyAt;
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
            SessionState.SetBool(Restore, !EditorApplication.isPlaying);
            SessionState.SetBool(RestorePause, EditorApplication.isPaused);
            SessionState.SetBool(Pending, true);
            readyAt = EditorApplication.timeSinceStartup + 2d;
            if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
        }

        private static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (execution != null)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                if (Application.isBatchMode) EditorApplication.Step();
                if (Time.frameCount < resumeAfterFrame) return;
                if (!execution.MoveNext()) execution = null;
                resumeAfterFrame = Time.frameCount + 2;
                return;
            }
            if (File.Exists(Request) && !SessionState.GetBool(Pending, false))
            {
                File.Delete(Request);
                Run();
            }
            if (SessionState.GetBool(Pending, false) && EditorApplication.isPlaying && EditorApplication.isPaused)
            {
                SessionState.SetBool(RestorePause, true);
                EditorApplication.isPaused = false;
            }
            if (!SessionState.GetBool(Pending, false) || !EditorApplication.isPlaying ||
                EditorApplication.timeSinceStartup < readyAt || EditorApplication.isPaused) return;
            SessionState.SetBool(Pending, false);
            execution = Execute();
            resumeAfterFrame = Time.frameCount;
            EditorApplication.QueuePlayerLoopUpdate();
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
                { "Fast charge crossing a moving target hits exactly once", Charge },
                { "Orb sweeps moving target and respects invulnerability", Orb },
                { "Orb cannot hit a target beyond the wall", OrbWall },
                { "Blink teleport does not become a swept attack", Blink },
                { "Area spells respect range and cooldown", Area },
                { "Vortex pulls and bursts", Vortex },
                { "Overlapping fighters and bot stay finite", Overlap },
                { "Reload validation safely recreates property block", Reload },
                { "Both fighters accept independent movement and casts", TwoPlayers },
                { "Snapshots replicate health, cooldowns, facing and effects", Snapshots },
                { "LAN loopback framing, handshake, input and disconnect", LanTransport },
                { "LAN rejects invalid addresses and oversized frames", LanValidation },
                { "LAN timeout and cancel release listener for reconnect", LanTimeout },
                { "Menu Host/Join drives actual gameplay and disconnect recovery", LanMenuFlow },
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
        private static void Invoke(object obj, string name) => obj.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, null);

        // Spell effects live on MatchSim's FxSpawner collaborator; reach it so the
        // orb-retention and guest-replication checks keep probing the real state.
        private static object FxSpawner(MatchSim sim) => Field(sim, "fx").GetValue(sim);
        private static System.Collections.ICollection Projectiles(MatchSim sim) =>
            (System.Collections.ICollection)Field(FxSpawner(sim), "projectiles").GetValue(FxSpawner(sim));
        private static GameObject EffectRoot(MatchSim sim) =>
            (GameObject)Field(FxSpawner(sim), "fxRoot").GetValue(FxSpawner(sim));

        private static MatchSim Match(CharacterId id = CharacterId.Vanguard, bool bot = false)
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
            var sim = new MatchSim(a, b, bot, View(a, Vector3.zero),
                View(b, new Vector3(10f, 0f, 0f)), ZoneShape.Circle);
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
                s.Foe.Position = new Vector3(-20f,0,0);
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

        private static void Charge()
        {
            var s = Match();
            s.Foe.Position = new Vector3(5f,0,0);
            s.Foe.Velocity = Vector3.left * 100f;
            Check(s.Cast(s.Local, 0), "Charge rejected");
            s.Local.Velocity = Vector3.right * 600f;
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 91f);
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 91f);
            Check(!s.Cast(s.Local, 0), "Cooldown not enforced");
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
            s.Local.Position = new Vector3(14.5f,0,0);
            s.Foe.Position = new Vector3(15.5f,0,0);
            s.Cast(s.Local, 0);
            s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Near(s.Foe.Health, 100f - GameConfig.ZoneDamagePerSecond * GameConfig.FixedTimestep);
            // Also test an orb crossing the boundary from inside over several steps.
            var inside = Match(CharacterId.Storm);
            inside.Local.Position = new Vector3(13f, 0, 0);
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

        private static void Area()
        {
            foreach (int slot in new[] { 1, 2 })
            {
                var s = Match(); var ab = s.Local.Def.Get((AbilitySlot)slot);
                s.Foe.Position = Vector3.right * (ab.Range - 0.1f);
                Check(s.Cast(s.Local, slot), "Area cast rejected"); Near(s.Foe.Health, 100f - ab.Damage);
                Check(!s.Cast(s.Local, slot), "Area recast ignored cooldown");
                var far = Match(); far.Foe.Position = Vector3.right * (ab.Range + 0.1f);
                far.Cast(far.Local, slot); Near(far.Foe.Health, 100f);
            }
        }

        private static void Vortex()
        {
            var s = Match(CharacterId.Storm); s.Foe.Position = new Vector3(5f,0,0);
            s.Cast(s.Local, 2); s.Tick(GameConfig.FixedTimestep, Vector3.zero);
            Check(s.Foe.Velocity.x < 0f, "Vortex did not pull");
            // Keep target in the burst without letting body contact move it away.
            for (int i = 0; i < 110; i++)
            { s.Foe.Position = new Vector3(5f,0,0); s.Foe.Velocity = Vector3.zero; s.Tick(GameConfig.FixedTimestep, Vector3.zero); }
            Near(s.Foe.Health, 90f);
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
            var menu = (MainMenuView)Field(bootstrap, "menu").GetValue(bootstrap);
            Button ButtonAt(string path) => menu.transform.Find("MenuContent/" + path).GetComponent<Button>();
            ButtonAt("Seg_GAME MODE/Seg/Opt1").onClick.Invoke();
            ButtonAt("StartWrap/Action_START BATTLE").onClick.Invoke();
            var host = (LanSession)Field(bootstrap, "network").GetValue(bootstrap);
            Check(host != null && host.IsHost, "Host UI did not create listener (mode=" +
                Field(menu, "mode").GetValue(menu) + " busy=" + Field(menu, "busy").GetValue(menu) + ")");
            // The zone picker stays live while the host waits, and its choice
            // must survive until the guest joins (it used to be silently lost).
            var zoneSquare = ButtonAt("Seg_ZONE SHAPE/Seg/Opt1");
            Check(zoneSquare.interactable, "Zone picker locked while the host waits");
            zoneSquare.onClick.Invoke();
            Check(menu.Zone == ZoneShape.Square, "Zone tap ignored while waiting");
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
                Check(menu.gameObject.activeSelf, "Disconnect did not restore menu");
            }
            // Join path uses the same input field and button that phone users touch.
            using (var remoteHost = LanSession.Host())
            {
                ButtonAt("Seg_GAME MODE/Seg/Opt2").onClick.Invoke();
                Check(!ButtonAt("Seg_ZONE SHAPE/Seg/Opt0").interactable,
                    "Join phone still offers the zone the host decides");
                var address = (TMPro.TMP_InputField)Field(menu, "hostAddress").GetValue(menu);
                address.text = "127.0.0.1";
                ButtonAt("StartWrap/Action_START BATTLE").onClick.Invoke();
                LanMessage hello = null;
                remoteHost.Received += m => hello = m;
                Action pump = () => { Invoke(bootstrap, "StepNetwork"); remoteHost.Pump(); };
                Until(() => hello != null, pump);
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
