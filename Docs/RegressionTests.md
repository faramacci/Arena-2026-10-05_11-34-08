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
`29/29 passed; failures=0`. Paused Play Mode is briefly resumed and restored afterward.

One editor trap cost more than one run while this flow was built, so it is worth repeating: this
project enters Play Mode with **Enter Play Mode Options** on, i.e. domain and scene reloads
disabled. A play session that outlives a script recompile keeps the component instances it
already had, so fields added by that recompile are `null` in the running session and the
harness's in-flight coroutine dies with it. Runtime wiring is wiped at the same time: the
`onClick` listeners and C# events a screen built in `Awake` are gone, so the UI tests would
click dead tiles and fail while every simulation test still passes. The harness no longer
trusts a session it did not boot: it leaves the current Play Mode, enters a fresh one, waits
for the bootstrap, and returns to Edit Mode when it is done. If a compile still takes that
session down mid-run it boots another one and starts over, three attempts at most, and a run
that cannot be finished writes `FAIL run aborted: <reason>` rather than leaving `RUNNING`
behind. The suite is driven frame by frame, and Unity slows its loop to about one frame per
second while the editor window is covered by another window, which once stalled a run in the
middle of the suite; the harness therefore pauses the game and steps the loop by hand, so the
editor no longer has to stay in the foreground. Paused Play Mode is resumed and restored
afterward. Edit scripts between runs,
not
during one.

## Coverage

1. Pointer-event joystick centering at bottom-left pivot, four directions, half strength,
   small-input smoothing, finger ownership, release and disable reset.
2. Camera input mapping preserves analog magnitude (screen up is +Z at yaw zero).
3. Identical movement, zone damage and cooldown after two seconds at 30/60/120 render FPS.
4. Maximum five 60Hz simulation steps per call, with unprocessed time retained and drained.
5. Half-stick acceleration is half full-stick acceleration; facing remains normalized.
6. The three thrown-and-buffed spells of the current roster, each against a held-still target:
   the Vanguard's Shield Throw sweeps a moving target, scores once on the way out and once on
   the way home before being caught again — never twice on one leg, and not at all against a
   target standing off the lane — while its cooldown holds; the Assassin's Dagger Throw outruns
   an orb and is spent on the fighter it hits; Shadowstep leaves the mage sliding after the
   stick is released but stops the assassin dead, holds the higher top speed for its three
   seconds and is gone by the end of them; and Blade Wave throws its whole fan (most blades
   leaving the aim line) while charging a fighter inside the fan exactly once.
7. An orb detects a target crossing its path; Blink invulnerability rejects its damage.
8. Orbs stop at the inner wall face, including spawn outside that boundary.
9. Blink teleports without sweeping damage through an intervening fighter.
10. The Earthrift corridor (length and width both gate the hit) and the Frost Aegis shield
    (spell and zone absorption, guest replication, timer expiry), plus cooldown rejection.
11. Vortex: the funnel gathers under the caster's feet (it follows them through the wind-up,
    so the throw starts where the player watched it form) and is then thrown down its line. The
    funnel rides the hazard's centre, so what the player watches travel is what hits. Whoever
    it sweeps is wounded on contact — damage, stun and launch at the moment of the touch, not
    only at the end of the line — and dragged along the throw, hardest at the eye and fading to
    nothing at the rim. The end of the line still bursts, but a fighter that was already swept
    is not charged twice for the same throw.
12. Exact body overlap does not produce NaN, including bot decision-making.
13. Reload-like null property block: OnValidate defers renderer work; Update recreates it.
14. Independent two-player movement and remote casts/cooldowns.
15. The lobby roster behaves like a Warcraft-3 join screen: the leader owns slot 0, bots fill
    slots with cycling characters, open slots take the next human, closed slots stay out of the
    match, the leader cannot be kicked, and a match needs two participants.
16. Free-for-all: four fighters share one arena, all of them stay on the sand through two
    seconds of bot combat, every pair collides and charges, and the match is decided by who is
    left standing (`AliveCount` / `LastAlive`).
17. JSON snapshots replicate positions, health, facing, cooldowns and effects without allocating a new effect hierarchy each update.
18. Actual TCP loopback connection, ordered handshake/input/cast messages, state delivery and disconnect cleanup.
19. Invalid IPv4 and oversized-frame rejection.
20. Inactivity timeout and port release for reconnect.
21. The network mode tile opens the warm-up space, its host/join row and READY create a real listener, an authoritative guest spell and guest camera target survive, the zone picked while still waiting arrives in the welcome and is built into the arena, the join phone's zone picker is locked, and a disconnect restores the mode screen.
22. Exit/restart during GO and stale hide calls cannot hide a new countdown or result.
23. Runtime materials from arena rebuilds and spell effects are destroyed after their owners, while shared Resources assets survive.
24. Guest rendering eases from the previous snapshot to the newest one: simulation state jumps at once while the rendered view interpolates over the measured snapshot spacing, and replaying a snapshot does not pull the view backwards.
25. Player settings keep Auto Rotation with both landscapes allowed and portrait disabled, which is what Unity emits as `sensorLandscape` so a rotation-locked phone still opens landscape.
26. Every perimeter wall run is long along its own edge, thin across it, centred on that edge and clear of the play square, so a run can never be built standing across the sand.
27. A fighter driven far outside the field is clamped to the playable octagon — the square arena with its four corners cut — so even at a corner the body plus its radius stays inside the round spectator bowl, while the field is still full width along its axes.
28. The opening flow: the mode tiles (campaign / bots / network) are the first screen; a tile opens the warm-up space, which is the live arena — the player's fighter and one passive training dummy on the sand, `PracticeMode` on, spells castable and cooldowns running; the dummy is topped back up instead of being knocked out and the zone never decides a practice match; and the warm-up clock hands over to the real match with its countdown and fighter line-up.

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

The field is 64u square. Everything that follows from it — the cut-corner playable octagon,
both zone shapes, the spectator bowl radius, the camera's ortho size and every prop distance in
`ArenaBuilder` — is derived from `GameConfig.ArenaWidth` (props through `ArenaBuilder.S()`), so a
future resize stays a one-number change instead of a hunt for stray literals.

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
- Final Unity Play Mode regression runner: **29/29 passed; failures=0** (2026-10-08, 17:01,
  in a session the harness booted itself; raw output in `Temp/arena-regression-results.txt`).
- Previous runner record: **29/29 passed; failures=0** (2026-10-08, 15:12, after the mode tiles →
  warm-up space → match flow replaced the old single-screen menu and `MainMenuView` was
  deleted). The warm-up space has since been removed as well: the flow is mode tiles → fighter
  picker → match, the picker is pure UI with no practice sim or clock, and the leftover
  warm-up wording in `ModeSelectView` and `GameBootstrap` was rewritten.
- Tests ran in an isolated copy using the same installed packages pinned to local paths because the package registry denied requests; the real project's package manifest was not changed.
- The runner waits for actual player frames before checking deferred material destruction; batch mode explicitly steps the player loop.
- Fresh verification summary: `Docs/LanVerification.txt`.
- LAN is tested over real loopback sockets and the real menu flow, not physical hotspot hardware.
- Initial attempts failed due to editor-only coroutine attachment, package setup in the temporary copy, malformed-frame exception handling and deferred destruction timing. Those causes were corrected; the final suite was rerun without dropping tests.
- Existing compile-helper warnings about duplicated RTLTMPro source/reference types and
  unrelated unused/obsolete declarations remain; no warning suppressions were added.
- Android build, physical multi-touch hardware, performance profiling and a full real
  hot-reload round trip are not covered by this suite.
