# Arena — audit & session log

Audit performed on the local checkout (`F:\Unity Project's\Arena`) against
Unity Editor `6000.6.4f1`, combining a static code review with a **live MCP
session** (Unity-MCP server on `http://localhost:25521`). This document records
what was measured, what is still unverified, and what changed in the session.

## 1. Git status

- Repository is connected: remote `origin` → `github.com/faramacci/Arena-2026-10-05_11-34-08.git`, branch `main`.
- Before this session there was **one** commit (`28c2ba5 "Initial check-in"`, 2026-10-05) containing only the Unity template and `ProjectSettings`; **none of the game code was committed** (~6100 lines were untracked, one `git clean` away from being lost).
- `.gitignore` is the official Unity template and correctly ignores `Library`, `Temp`, `Logs`, `obj`, `build`, `UserSettings`, `*.csproj`/`*.slnx`, `*.apk`/`*.aab`. It now also ignores agent/tooling output (`.claude/`, `graphify-out/`).
- `Assets/_Recovery/` (Unity crash-recovery scenes) is intentionally left untracked; consider adding `/[Aa]ssets/_Recovery/` to `.gitignore`.

## 2. Architecture

Layered by namespace and folder, deliberately and consistently:

| Layer | Folder | Role |
|---|---|---|
| `Arena.Core` | `Assets/Scripts/Core` | data/tuning, no Unity lifecycle (`GameConfig`) |
| `Arena.Gameplay` | `Assets/Scripts/Gameplay` | simulation + networking |
| `Arena.Presentation` | `Assets/Scripts/Presentation` | rendering / UI |
| `Arena` | `Assets/Scripts/GameBootstrap.cs` | composition root + phase state machine |
| `Arena.Inputs` | `Assets/Scripts/Input` | touch joystick |
| `Arena.Tests` | `Assets/Scripts/Editor` | Play-Mode regression runner |

Patterns in use: Component-Based MonoBehaviours, an explicit phase **state
machine** (`Menu → Countdown → Playing`), a single source of truth for balance
(`GameConfig`), a sim/presentation split, and **host-authoritative** LAN play.

## 3. Modularity (measured)

Before the refactor `MatchSim.cs` was the outlier at **891 lines**, mixing the
fixed-step loop, movement/walls, combat, the bot brain, zone pressure, spell FX
and snapshot replication. It now delegates to four focused collaborators:

| File | Lines | Responsibility |
|---|---|---|
| `Gameplay/MatchSim.cs` | 379 | tick orchestration, movement, snapshot view |
| `Gameplay/FxSpawner.cs` | 343 | spell effects + guest replica effects |
| `Gameplay/CombatResolver.cs` | 140 | body contact, charge window, damage, sweeps, wall clipping |
| `Gameplay/BotBrain.cs` | 93 | opponent decision brain |
| `Gameplay/ZoneField.cs` | 42 | zone containment + pressure |

`MatchSim`'s public API is unchanged (`Local`, `Foe`, `Zone`, `Cast`, `Tick`,
`CaptureSnapshot`, `ApplySnapshot`, `AdvanceSnapshotView`, `Clear`). The three
regression tests that reached into `MatchSim`'s private `projectiles`/`fxRoot`
fields were repointed at the same state on `FxSpawner`; no assertion was
weakened.

## 4. Playable prototype — verified live

- Single enabled scene in `EditorBuildSettings`: `Assets/Scenes/SampleScene.unity`. The scene is intentionally empty (`RootCount = 3`); `GameBootstrap` self-installs via `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`.
- Entering Play Mode live confirmed the runtime hierarchy is created: `GameBootstrap` (`Arena.GameBootstrap`), `Arena` (`ArenaBuilder` + `ArenaAmbient`), `GameCanvas` (`Canvas` + `CanvasScaler` + `GraphicRaycaster`), `EventSystem` (`EventSystem` + `InputSystemUIInputModule`).
- The project's own regression suite was run live in the editor and reported **25/25 passed; failures=0**, including the real menu Host/Join flow, LAN loopback framing/handshake/disconnect, guest snapshot interpolation and procedural-material teardown.
- Gameplay already covers: ice movement, 2 characters × 3 abilities, a bot opponent, health/KO, a shrinking zone, countdown, result cards, and LAN host/join.
- Still unverified: render appearance and real-device FPS/latency (no physical device).

## 5. Android build

- Active build target is already **Android**; the Android module is installed (`IsBuildTargetSupported = True`).
- `SampleScene` is enabled in Scenes In Build.
- Development Build is **off**; output is an **APK** (not an App Bundle).
- Built APK: `build/outputs/Arena.apk` (~47 MB, valid arm64 IL2CPP/`libil2cpp.so` present).
- ⚠️ **Package name is still the URP template default**: `com.UnityTechnologies.com.unity.template.urpblank`. Rename it (e.g. `com.faramacci.arena`) before any release; note this changes the app identity.

## 6. MCP limitations observed

1. **Project path contains spaces/apostrophe** (`F:\Unity Project's\Arena`). Unity logs an error from `com.ivanmurzak.unity.mcp/Editor/Scripts/Startup.cs:33`: *"The project path contains spaces, which may cause issues during usage of AI Game Developer."* Moving the project to a space-free path (e.g. `F:\UnityProjects\Arena`) is recommended.
2. **`Console / Clear Logs` tool fails** with `IOException: The process cannot access the file '...\Temp\mcp-server\ai-editor-logs.txt' because it is being used by another process.` (log-file lock).
3. **`reflection-method-call` could not resolve `Arena.Tests.ArenaRegressionTests.Run()`** (`Method not found`) despite the public static method existing; `script-execute` was used instead.
4. **Strict/undocumented tool schemas**: `gameObjectRef`, `filter` etc. must be JSON objects, not strings (HTTP 400 otherwise).
5. Server readiness took ~50 s after the editor started; `unity-mcp-cli wait-for-ready` handles this.
6. Unrelated to the game: Unity cloud services logged `Account API did not become accessible within 30 seconds`, `NoSubscription` for AI generators, and a RelayService negotiation failure.

## 7. Change log — this session

- **Git rescue** — added `.claude/` and `graphify-out/` to `.gitignore`; staged and committed the game code, docs and required assets (331+ files, Git LFS for binaries) with `feat: initial playable prototype with LAN networking, core architecture, and 25 passing regression tests`; pushed to `origin/main`.
- **Android build** — inspected build settings and package name, produced `build/outputs/Arena.apk` (Development Build off).
- **Refactor** — split `MatchSim.cs` into `BotBrain`, `CombatResolver`, `FxSpawner` and `ZoneField`; repointed the reflection-based tests; re-ran the suite: **25/25 passed**; standalone compile clean (`bash verify_compile.sh`, 46 sources).
- **Docs** — this file.

## 8. Verification commands

- Compile: `bash verify_compile.sh` → `OK: compiled clean`.
- Regression: `unity-mcp-cli run-tool script-execute` calling `Arena.Tests.ArenaRegressionTests.Run()`, then read `Temp/arena-regression-results.txt` → `25/25 passed; failures=0`.
