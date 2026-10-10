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
`44/44 passed; failures=0`. Paused Play Mode is briefly resumed and restored afterward.
Note that stopping Play Mode now also clears the world it built: see the leftovers sweep below.

Both routes need the editor to have *imported* the edit first: the request file only starts a
run, it does not compile. With the editor idle in the background an
`assets-refresh` (the Unity-MCP tool, or refocusing the window) is what lands the new scripts,
and a run started before that silently exercises the previous assembly.

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
10. The Earthrift ring (the radius gates the hit, the launch throws outward from the Vanguard
    standing in its middle and the caster is never caught by his own ground) and the Frost Aegis
    shield
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
29. The three rigged fighter models, one character at a time, through the arena's own spawn path:
    `CharacterView.Create` puts the model in place of the primitive body (the torso and head are
    gone, the skinned mesh is there), all 21 kit bones resolve, the figure stands on the sand at
    human height rather than on its back or 100x off, every material slot renders in this
    pipeline, and the    skin follows the bones — a skeleton that is not bound to the mesh travels
    0.000 m when it is posed, which is what this asserts against. The hand and forearm
    anchors a throw leaves from are on the body, off the ground and inside the fighter's
    own reach, and the hand anchor moves when the arm swings — the point a returning
    aegis homes to is read off the live skeleton rather than remembered from the throw.
30. The Frost Aegis as the player reads it on the fighter: a gold dome over the ice-blue
    Vanguard with eight studs orbiting its equator, both drawn with their own translucent
    material on this pipeline, nothing built for a fighter who never shields, a soak — the
    shield dropping between two reports — flashing the dome brighter, and the crown going
    with the dome when the shield expires.
31. The camera zoom, against the two bounds it is chosen from rather than by taste: the rig
    applies `GameConfig.CameraOrthoSize`, keeps the player in the middle of the screen, on a
    20:9 landscape window still holds the foe at its 20u spawn standoff inside the frame, and
    looks over enough ground ahead (`size / cos(pitch)`, 11.2u at 9.5) for the longest line
    spell in the roster — read off the abilities themselves, so a longer spell or a tighter
    camera fails the check instead of the player finding out mid-match.
32. The health bar clears the fighter's head on screen, for all three rigs: the plate turns to
    the camera, so its height is world height times `cos(CameraPitch)`, and the bar's bottom is
    read from the built hierarchy — the real rect, not a second copy of the constant — then
    compared with the model's own crown. At the old anchor of 2.65 the bar sat 0.25 m *below*
    the crown in screen units and covered the head; 3.85 leaves 0.29–0.38 m of clear air.
33. A cast is refused for free once no opponent is left standing: the cooldown is committed
    only after the sim found a target, so a refused cast cannot leave the spell bar running
    for a spell that never happened.
34. The grip buff's own top speed and pull cross the wire. The shipped Shadowstep runs at
    exactly the `GameConfig` fallbacks, so the host is retuned off them on purpose and the
    guest runs the same lane: a guest that recomputed the numbers from the constants would
    move differently from the first step of the buff.


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

### The floor is ice inside the walls and molten rock outside them

- **44/44 passed; failures=0** (2026-10-09, 21:51, after an `assets-refresh`, the editor already
  in Paused Play Mode from the readback below; raw output in `Temp/arena-regression-results.txt`).
  The suite has no case for the ground materials' UV windows yet - the numbers below are what
  stands behind this, not a guard. A mistyped offset would still only be caught by the readback
  and by eye.
- The whole floor is one painted sheet, `Assets/Resources/Arena/ArenaField.png` (2048x2048,
  imported uncompressed at full size), drawn by `Tools/Blender/arena_field.py` in Blender 5.2
  through this repo's MCP bridge. The image is a top-down map of a 124 m square - the outer
  plinth's own footprint - so u,v `0..1` is world -62..62 m, and 1 m is the same 16.5 px
  everywhere. Everything the game puts on the ground reads its own window out of that one image
  with `_BaseMap_ST`:
  - the arena floor (64.4 m square) is `.240...760`, `Arena_Ice`, lit, scale 0.519 / offset 0.240;
  - the inner plinth (108 m square) is `.065...935`, `Arena_Lava_Inner`, unlit, 0.871 / 0.065;
  - the outer plinth (124 m square) is `0...1`, `Arena_Lava_Outer`, unlit, 1:1.
- The ice/rock split follows the arena's *square*, not a circle, because the floor the player
  stands on is a square slab and the four wall runs stand on it: the wall is 0.9 m thick and
  centred on x = +-32, so it covers the seam and no straight edge of ice has to line up with a
  straight edge of rock where the player can see it.
- `F2 - F1` over a Worley field gives both surfaces their plates and their crevices, but they are
  coloured as opposites: the ice plates are bright and their crevices a shade deeper, the rock
  plates are near-black and its crevices are the light, because that is where the field is still
  molten. The rock's heat falls off with distance from the wall, and the last metre and a half of
  the arena's ice is painted as if it were melting - darker, wetter, then a white-hot rim where
  the two meet.
- Read straight out of the painted file, along the z = 0 row: ice core 0..28 m `(0.84,0.92,0.98)`
  with 0.0% hot pixels; the melt rim 30..33 m `(0.91,0.95,0.95)`; the seam under the wall
  33..36 m `(0.63,0.51,0.27)` at 45% hot; rock 36..54 m `(0.15,0.11,0.12)`; outer rock 54..62 m
  `(0.11,0.08,0.09)`. Bright (>0.55) covers 53.5% of the arena's square against 2.0% outside it.
- Read out of the *running game* as well, not only out of the file: an orthographic camera
  straight down and sized to exactly the sheet's footprint (`orthographicSize` 62, 1024x1024,
  `Docs/shots/arena-field-overhead.png`), which makes world -> pixel linear and takes the guesswork
  out of the projection. The unlit plinth windows are 1:1, so their pixels are the sheet's own:
  the frame at +-40,+-40 m reads `(0.11..0.13, 0.09..0.11, 0.10..0.12)` where the sheet says
  `(0.12..0.13, 0.10..0.12, 0.11..0.12)`, and the same holds at x = 46 m and 58 m.
- The frame's own row through z = 0: ice side mean `(0.76,0.79,0.81)` with **0.0%** hot pixels,
  rock side `(0.27,0.23,0.23)` with 4.8% - the only hot pixels on that half are the melt rim at
  the wall (+-33.5 m: 73% hot, `(0.51,0.39,0.24)`) and the rock's own cracks. All four sides of
  the bowl read amber at +-33.5 m, so the ring is a ring and not a band.
- Not mirrored: correlating the frame's inner-plinth band (34..54 m) against the sheet's same-u
  column gives **r = +0.63**, against the mirrored u column +0.21 (a flipped v column, which for
  this symmetric map only tests the neighbouring row, +0.74). The sheet's u runs with world x.
- The bright span on a straight row is -35.2..+35.1 m rather than +-32.2 m: the extra ~3 m a side
  is the wall's own footprint and its stone cap plus the painted white-hot rim, not ice that
  leaked past the wall. The arena's own square is what the paint is measured against.
- Looked at, not only measured: `Docs/shots/arena-field-overhead.png` is the whole bowl from
  above in one frame, and `Docs/shots/arena-field-game.png` is the game's own Game View
  (997x416, HUD included) with the lower half at `(0.70,0.77,0.82)` and 0% hot pixels - the
  player's feet are on ice.
- No Android device on this machine: how the sheet survives a phone's own texture import,
  compression and mip chain is not verified here, only in the editor's pipeline.

### The assassin's knives drag the Shadowstep's line, cut shorter

- **44/44 passed; failures=0** (2026-10-09, 21:11, the runner booted its own Play Mode after an
  `assets-refresh`; raw output in `Temp/arena-regression-results.txt`). The knife line's case is
  part of `Thrown weapons are the fighter's own shield and knife`.
- `RedTrailLine` - the line the thrown dagger and its guest replica drag, derived from their own
  movement and never from the wire - now draws in the dash line's own ink rather than a thinner
  copy of it: `RedTrail.LineRed`, the dash's `TrailWidth` 0.16 m and its `TrailThickness` 0.02 m.
  It is shorter only where "shorter" can be measured: a knife mark is capped at 0.30 m against
  the dash's 1.1 m, and it lives 0.10 s against the dash's 0.30 s - and life is what sets how
  long a streak looks, because the dagger is twice as fast as the dash. That is
  36 m/s x 0.10 s = 3.6 m of line against 18 m/s x 0.30 s = 5.4 m.
- The test reads every one of those numbers off both lines instead of restating them, so the two
  cannot drift apart, and pins two things the first cut of the line got wrong:
  - *Not dotted.* A knife covers 0.60 m between frames at 36 m/s - two marks' worth - so the
    segment it crossed is filled with as many marks as it takes. One mark pinned per frame left a
    0.18 m hole every 0.42 m; the assertion measures the widest gap between the marks of one
    frame's flight, which was 0.18 m and is now 0.00 m. Measured on the running build as well, by
    dropping one exactly-0.60 m step through the line's own `Drop`: 2 marks, 0.600 m covered,
    0.000 m of gap, 0.300 m each, against the live constants `MaxSpan` 0.3, `Fade` 0.1,
    `Width` 0.16, `Thickness` 0.02.
  - *Shorter on screen, not just in the constants.* The streak the player sees is speed x life,
    and the knife's is the shorter of the two by the speeds `GameConfig` gives those two spells.
- Looked at, not only asserted, from a live match in Play Mode: `Docs/shots/knife-trail.png` is
  the game's own Game View (997x416, HUD included) with the dagger in the air and its line behind
  it, and `Docs/shots/knife-trail-closeup.png` is the same frozen frame photographed from 3.2 m,
  where the line measures 130 crimson pixels in a 30 x 106 px band - the marks run up the screen
  because the knife is flying away from the camera. At gameplay framing the line is a few pixels
  across, exactly like the dash line it copies: at that distance a 0.16 m mark is under two
  pixels wide, so what the player sees is a thin red streak rather than a band.
- The editor window was unfocused while this ran, so Play Mode was not ticking at all - clock and
  frame count frozen, the match stuck in its countdown. The frames were advanced one at a time
  with `EditorApplication.Step()` with the editor paused, which is what makes the staged throw
  reproducible. The throw itself runs at 0.08 time and is then frozen.
- One earlier screenshot of this work, taken before the stalled loop was understood, showed an
  empty arena with no knife in it at all. It was deleted rather than kept: it would have read as
  a picture of the line.
- No Android device on this machine: how the line reads on a phone is verified through the
  editor's own Game View and not on a device.

### Hotspot play: the host is found, and failures stay on the screen that caused them

- **43/43 passed; failures=0** (2026-10-09, 20:12, the runner booted its own Play Mode after an
  `assets-refresh`; raw output in `Temp/arena-regression-results.txt`). Four cases are new:
  - `The LAN picker stays tappable on every phone aspect` builds the picker on canvases of
    960, 1280 (= 16:9), 1440 (18:9) and 1600 (20:9) reference units and measures the controls a
    hotspot match needs - the HOST and JOIN buttons, the address field, START and BACK. All of
    them are on screen and at least 60x44 units, so the row's 1640 units of preferred width never
    squeeze or push the network box off a phone. This is the check that rules out "the buttons do
    not work" being a layout problem.
  - `A joining phone finds its host over the hotspot without an address` sends one beacon at a
    finder, checks the address and port it heard, and then *joins the address it heard* and
    completes the handshake. Junk on the discovery port is ignored rather than trusted.
  - `A guest joins the host the hotspot offered it, with nothing typed` drives the real UI:
    mode tile -> JOIN -> discovery fills the address field (the picker says `Found host …`), and
    the JOIN BATTLE button a guest presses connects to that host. Nothing is typed. This test is
    what caught the beacon starting with an empty target list - it shouted into nothing.
  - `A failed join says why, and a waiting host can cancel` presses HOST and finds the waiting
    button reading CANCEL, hears the waiting host's shout with a raw finder, cancels the wait,
    then requires three failure presses to behave: JOIN with an *empty* address (the picker must
    ask for the host's IP - it used to throw a NullReferenceException out of the button press and
    look like a crash), JOIN with an unparseable address, and both must leave the picker up, red,
    and live for a retry. `LAN rejects invalid addresses and oversized frames` also pins the
    transport side: empty and null addresses are refused with an `ArgumentException`, never an
    NPE from inside the socket call.
- The broadcast path itself, on a real interface rather than loopback: a host + beacon and a
  finder in this editor, with the beacon shouting its production targets - one tick was enough
  for the finder to hear `192.168.1.103:<host port>` (the machine's own LAN address, taken from
  the beacon's directed broadcast). Logged evidence: `[BroadcastProbe] ticks: 1 -> heard
  192.168.1.103:65491`.
- Seen running, not only asserted, from Play Mode at the editor's 997x416 game view:
  `Docs/shots/lan-join-found-host.png` (the JOIN picker with the address discovery wrote in and
  `Found host 192.168.1.103 — press JOIN BATTLE.` under it),
  `Docs/shots/lan-join-empty-address.png` (JOIN pressed with nothing typed: the red line asks for
  the host's IP and the button still reads JOIN BATTLE) and `Docs/shots/lan-host-waiting.png`
  (a waiting host: the button reads CANCEL and the line lists its addresses, `Waiting on … — a
  phone on this hotspot finds you by itself`). A host that could not open its port - the raw
  session from the first screenshot still holding 28741 - showed the new failure path instead:
  red text on the picker, button live, no bounce back to the mode tiles.

- Fighters are named Vanguard / Mage / Assassin, the Earthrift is a ring around its caster and
  the assassin's knives are longer: **39/39 passed; failures=0** (2026-10-09, 18:52, the runner
  booted its own Play Mode after an `assets-refresh`; raw output in
  `Temp/arena-regression-results.txt`).
  - The rename reaches the code and not only the labels: `CharacterId.Storm` is
    `CharacterId.Mage` (the enum keeps its order, so saved and wire data still line up),
    `GameConfig.Storm` is `GameConfig.Mage`, and the display names are exactly Vanguard, Mage
    and Assassin. The Blender scripts' docstrings and README follow; `build_paladin.py` keeps
    its file name, which the README has always flagged.
  - The Earthrift's `Range` is now the radius of a circle drawn around the caster - 4.5u, the
    same footprint the 11u x 5.2u corridor covered - and `RiftBand` (1.5u) is the broken band
    inside it. `CombatResolver.TryCircleHit` replaces the corridor test and throws the victim
    *outward* from the fighter in the middle, `SpawnRingWarning` paints the disc the spell
    covers with three rings of discs and rides the caster, and `SpawnRift` builds the crack
    from 12 plates plus a dark seam ring. The `Area` test asserts each of those: the launch
    points outward, the Vanguard takes nothing from his own ground, a fighter who steps out of
    the circle during the warning is missed, the ring lands where the caster walked to, and
    the warning's markers stand on the rim of the radius the hit will cover. The test is now
    `Earthrift rings its caster: radius gates the hit, never the Vanguard`.
  - Knives: `build_assassin.py` scales the knife assembly about the grip (`DAGGER_SCALE`,
    1.30) before the prop kit cuts the right-hand copy out of it, so the worn knife, the throw
    in the air and the one in the left fist all grew together. `assassin.fbx` and
    `assassin_dagger.fbx` were rebuilt (the body still passes `check_fbx.py` - 54888 tris, no
    degenerate faces, feet at z=0 - and `check_rig.py`, 21 bones, 100% weighted) and the worn
    knife measures 0.653 m against 0.502 m before, which stays inside the `Props` test's knife
    envelope (0.40-0.75 m: a knife, not a sword).
  - Looked at as well as asserted, from a scratch scene (the real arena, the real rigs, the
    match sim ticked by hand; the open scene was reopened clean afterwards):
    `Docs/shots/earthrift-ring.png` is the crack with the mage inside it being thrown out,
    `Docs/shots/earthrift-telegraph.png` the warning ring around the Vanguard,
    `Docs/shots/assassin-knives.png` the worn knife beside a thrown copy, and the Blender
    views `Tools/Blender/renders/assassin_front.png` and `assassin_blades.png` are the rebuilt
    fighter. The numbers were logged from that same session: 12 rift plates and 12 seam discs,
    every one at 3.75 m from the caster (`Range` minus half the band), the warning's 26 markers
    spread from 1.17u to its 4.50u rim, the worn knife 0.653 m. Measured on the ring picture
    rather than eyeballed: every dark crack pixel lands in the annulus the projected 4.5u
    circle predicts (96% of them inside 0.65-1.45 of that radius, none past 3x, and none at
    all inside 0.6 - the middle of the circle is clean sand, which is where the Vanguard is
    standing).
- Standalone compile: `bash verify_compile.sh`, exit 0 (54 sources).
- Thrown weapons are the fighters' own props and the dash leaves a short red line:
  **37/37 passed; failures=0** (2026-10-09, 15:25, the runner booted its own Play Mode after
  an `assets-refresh`; raw output in `Temp/arena-regression-results.txt`).
  The new test is `Thrown weapons are the fighter's own shield and knife`: it drives both
  throws and asserts the object in the air is the prop from `PropLibrary` - the Vanguard's
  lion shield, upright on the forearm while it is worn, and the knife - rather than the slab
  that stood in for it, with the hand empty for the beat the prop is away. Looked at rather
  than only asserted, from one scratch scene (real arena, staged throws):
  `Docs/shots/prop-throws.png` shows the shield airborne and face-on with the knife fan out
  of the assassin's fist, `Docs/shots/shadowstep-line.png` the red line the dash lays behind
  a hasted assassin walking six steps, and `Docs/shots/spell-fx.png` with
  `Docs/shots/spell-fx-wide.png` the vortex funnel and the electric orb (those two predate the
  ring and still show the old corridor earthrift).
  The line's dashes fade out over 0.3 s of *wall* time, so an editor frame - which can be
  longer than the whole fade - leaves every dash already dark; the harness re-lights them at
  the width and colour a normal frame in play shows before it renders, which is why the
  staged frames are legible at all.
- Play Mode leftovers are swept, because this project enters Play Mode with Reload Domain
  and Reload Scene both off: Unity does not rebuild the scene when Play starts or stops, so
  everything the game built is still there afterwards - a match stopped mid-throw left a fan
  of thrown knives and a shield lying on the sand, visible in the Scene view with the game
  stopped and again under the next session's match, and each session added another Arena and
  another `MatchFx` root to the pile. `GameBootstrap.ClearLeftoverSceneObjects()` now destroys
  the roots this game builds (anything carrying a fighter view, plus `GameBootstrap`, `Arena`,
  `GameCanvas`, `MatchFx`, `EventSystem`; the scene's authored camera, light and volume are
  never in that list). The bootstrap calls it before it builds, and `PlayModeLeftovers`
  calls it when Play Mode stops, which is what returns the editor's scene to as-authored;
  `Tools/Arena/Clear Play-Mode Leftovers` runs the same sweep on demand for a scene that is
  already dirty (edit mode only - during Play it would delete the world the match is played
  in). Verified by driving Play Mode from the editor rather than by argument: the scene held
  3 roots before Play (Main Camera, Directional Light, Global Volume), 7 while playing (those
  plus GameBootstrap, EventSystem, Arena, GameCanvas with 708 and 182 children), and 3 again
  after stopping. The suite that boots its own session still passes with the boot sweep in
  place.
- Previous record: rigged models in the arena, the aegis over them, the camera that frames
  them and the nameplate above them: **36/36 passed; failures=0** (2026-10-09, 13:15, driven through
  Unity-MCP after an `assets-refresh`, on assemblies built after the last source edit; raw
  output in `Temp/arena-regression-results.txt`).
  Rendered from an additive scratch scene, which leaves the open scene untouched:
  `Docs/shots/models-rest.png`, `models-posed.png` (the stride and cast pose on the bones) and
  `models-side.png`. The models import at 1.916 m (mage 2.088 m with the staff) and 1.919 m with
  their soles at y=0.000 and no unit fix-up, all 21 bones resolve, their imported materials are
  already on URP/Lit, and the skin travels 0.66 m (vanguard), 0.69 m (mage) and 0.52 m
  (assassin) between the rest pose and a mid-stride pose - a mesh bound to nothing travels
  0.000 m, which is what the check asserts.
- The Frost Aegis is gold, not ice: it is the one warm thing on the ice-blue Vanguard, which is
  what makes it read as a barrier instead of another cold spell. The bubble carries a crown of
  eight studs orbiting the dome and flashes brighter for the beat after it soaks, and the thrown
  aegis of Shield Throw is the same gold and leaves the forearm it is strapped to, so the plate in
  the air is visibly the shield that was on the arm.
- The nameplate's anchor went from `HeadWidgetY` 2.65 to **3.85**, because a plate that faces
  the camera is measured on screen, not in world units: its height shrinks by `cos(58°)` =
  0.53 per metre and the health bar hangs 0.64 screen units below its anchor, so 2.65 put the
  bar's bottom 0.25 m *under* the fighter's crown and the head was painted behind it. The
  frames say the same thing: with the old anchor, 88 model pixels sat hidden inside the
  plate's band in the same 240x160 crop and the visible figure only started 4.5 px below the
  bar; with 3.85 nothing is hidden and the head starts 13.5 px (0.43 screen units) below it —
  `Docs/shots/nameplate-before.png` against `Docs/shots/nameplate-clearance.png`, both 2x
  crops of one 1440x600 frame from the same scratch scene (real arena, the two spawns, the
  game's own rig), the plate turned to the camera the way LateUpdate turns it in play.
- The camera came in from `CameraOrthoSize` 11 to **9.5** (the same fighter is 1.16x bigger:
  5.4% of the screen height instead of 4.6%), which is the closest it can come without
  costing a player something, and both bounds come out of the game's own numbers. Looking
  ahead: the camera is pitched 58° down, so a metre forward is 0.85 m up the screen and the
  top edge is `size / 0.85` = 11.2u of ground ahead — the Mage's Vortex is thrown 12u down its
  lane, and at 8.5 the top edge is 10u, so the funnel would spend its last stretch off screen
  where its own warning could not be read.
  Standing back: the spawns are 20u apart along X, so at 20:9 the foe starts at 0.97 of the
  viewport width and stays in frame; at 16:9 it is 1.01, and it was 1.01 at the old 11 as
  well, so the spawn pair was never what that framed. Measured on the rendered frames rather
  than argued: the two nameplates stand 547 px apart at 11 and 627 px at 9.5 in
  `Docs/shots/camera-before.png` and `Docs/shots/camera-closer.png` (1.146x, against the
  1.158x the camera sizes predict). Those two frames come from one scratch scene - the real
  `ArenaBuilder`, the two fighters at GameBootstrap's spawns and the game's own
  `MobaCameraRig` - shot through the rig's camera at the same 1440x600 window, and the scene
  was closed again with the open scene left active.
- The aegis was looked at, not only asserted: `Docs/shots/aegis-gold.png` is the gold dome over
  the rigged Vanguard at full shield, with the body showing through it. Measured on the picture,
  the warm part spans 544 of the 720 px vertically - about 1.99 m at that camera, on a figure
  1.92 m tall - and the crown band is 570 px wide where the dome just above and below it is
  430 px, so the studs do break the silhouette at the equator the way they are built to. It was
  rendered from a scratch scene 2 km from anything else in the editor, with the nameplate canvas
  hidden to keep the camera tight on the body; the scratch scene was closed afterwards and the
  open scene in the editor was never touched.
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
