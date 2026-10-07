# Arena regression checks

## Repeat in Unity

Wait for script import/compile to finish, then choose **Tools > Arena > Run Regression Tests**.
The runner uses the actual game classes in Play Mode, creates temporary fighters/cameras,
restores Unity's random state and deletes its fixtures afterward. If it started Play Mode,
it returns to Edit Mode; otherwise it restores the prior pause state. Do not run during
an important live match: entering Play Mode or allowing frames to advance can affect it.

Results are written to `Temp/arena-regression-results.txt` and summarized in the Console.
A failure includes its exception and stack trace. These are a lightweight custom regression
runner, not tests registered with Unity Test Runner/NUnit.

For automation with this project already open in Unity, create the empty file
`Temp/arena-regression.request`. The editor consumes it when compilation/import is complete.
Wait for a newly written result (do not mistake an older result for this run), and require
`25/25 passed; failures=0`. Paused Play Mode is briefly resumed and restored afterward.

## Coverage

1. Pointer-event joystick centering at bottom-left pivot, four directions, half strength,
   small-input smoothing, finger ownership, release and disable reset.
2. Camera input mapping preserves analog magnitude (screen up is +Z at yaw zero).
3. Identical movement, zone damage and cooldown after two seconds at 30/60/120 render FPS.
4. Maximum five 60Hz simulation steps per call, with unprocessed time retained and drained.
5. Half-stick acceleration is half full-stick acceleration; facing remains normalized.
6. A fast charging fighter crossing a moving target hits once and respects cooldown.
7. An orb detects a target crossing its path; Blink invulnerability rejects its damage.
8. Orbs stop at the inner wall face, including spawn outside that boundary.
9. Blink teleports without sweeping damage through an intervening fighter.
10. Shockwave/Earthquake range, damage and cooldown rejection.
11. Vortex pull and delayed burst.
12. Exact body overlap does not produce NaN, including bot decision-making.
13. Reload-like null property block: OnValidate defers renderer work; Update recreates it.
14. Independent two-player movement and remote casts/cooldowns.
15. JSON snapshots replicate positions, health, facing, cooldowns and effects without allocating a new effect hierarchy each update.
16. Actual TCP loopback connection, ordered handshake/input/cast messages, state delivery and disconnect cleanup.
17. Invalid IPv4 and oversized-frame rejection.
18. Inactivity timeout and port release for reconnect.
19. Host/Join through actual menu buttons/input field, authoritative guest spell, guest camera target, the zone picked while still waiting delivered in the welcome and built into the arena, the disabled zone picker on the join phone, and disconnect recovery.
20. Exit/restart during GO and stale hide calls cannot hide a new countdown or result.
21. Runtime materials from arena rebuilds and spell effects are destroyed after their owners, while shared Resources assets survive.
22. Guest rendering eases from the previous snapshot to the newest one: simulation state jumps at once while the rendered view interpolates over the measured snapshot spacing, and replaying a snapshot does not pull the view backwards.
23. Player settings keep Auto Rotation with both landscapes allowed and portrait disabled, which is what Unity emits as `sensorLandscape` so a rotation-locked phone still opens landscape.
24. Every perimeter wall run is long along its own edge, thin across it, centred on that edge and clear of the play square, so a run can never be built standing across the sand.
25. A fighter driven far outside the field is clamped to the playable octagon — the square arena with its four corners cut — so even at a corner the body plus its radius stays inside the round spectator bowl, while the field is still full width along its axes.

## Implementation notes

`MatchSim.Tick` accepts render-frame elapsed seconds and accumulates time internally. It
executes 1/60-second steps, at most five per call. Excess time is not thrown away; `PendingTime`
exposes backlog and `StepsLastTick` exposes work performed. Severe sustained overload can
still build a backlog; this is not a claim that any device can sustain 60 simulation Hz.
Constant-input frame partitioning is tested; arbitrary input/cast scheduling is not a
network lockstep/replay guarantee. Casts still use the existing button-triggered API.

Charge and projectile hits use ground-plane relative-motion circle sweeps, rather than
only checking end positions. Teleports are captured before movement, so Blink is not a
swept path. Orbs are clipped at the wall before checking hits behind it. Ordinary non-charge
body movement still uses overlap resolution, not a general continuous rigid-body engine.

`CharacterView.OnValidate` now only marks palette state dirty. The main-thread Update
rebuilds the renderer cache and lazily creates the native MaterialPropertyBlock.

The host replicates snapshots and the guest's input at up to 60Hz (the previous shared
30Hz timeslot showed up as guest-only stutter, while the host rendered at 60fps). The guest
has no prediction: `MatchSim.ApplySnapshot` stores the previous and the newest snapshot and
`MatchSim.AdvanceSnapshotView` eases the views and the replica effects between them once per
guest frame. Simulation state itself always holds the newest snapshot, so health, cooldowns
and the camera target stay authoritative. The spacing is measured from the wire cadence and
clamped to 8ms…100ms.

## Verification on this checkout

- Standalone compile: `bash verify_compile.sh`, exit 0.
- Final Unity Play Mode regression runner: 25/25 passed, batch process exit 0.
- Tests ran in an isolated copy using the same installed packages pinned to local paths because the package registry denied requests; the real project's package manifest was not changed.
- The runner waits for actual player frames before checking deferred material destruction; batch mode explicitly steps the player loop.
- Fresh verification summary: `Docs/LanVerification.txt`.
- LAN is tested over real loopback sockets and the real menu flow, not physical hotspot hardware.
- Initial attempts failed due to editor-only coroutine attachment, package setup in the temporary copy, malformed-frame exception handling and deferred destruction timing. Those causes were corrected; the final suite was rerun without dropping tests.
- Existing compile-helper warnings about duplicated RTLTMPro source/reference types and
  unrelated unused/obsolete declarations remain; no warning suppressions were added.
- Android build, physical multi-touch hardware, performance profiling and a full real
  hot-reload round trip are not covered by this suite.
