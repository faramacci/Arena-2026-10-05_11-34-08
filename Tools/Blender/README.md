# Fighters — Blender character tooling

Three game characters are built procedurally from their reference sheets, so
proportions, plate layers and the palette can be re-tuned without modelling by
hand:

All three carry the same 21-bone `rig_kit.py` skeleton and ship as skinned meshes,
so they can be animated rather than imported as props.

| Character | Script | FBX | Triangles | Slots | Bones | Height |
| --- | --- | --- | --- | --- | --- | --- |
| Vanguard — blue-and-gold plate knight, lion pauldrons, sword and lion shield | `build_paladin.py` | `vanguard.fbx` | 51,260 | 13 | 21 | 1.92 m |
| Mage — indigo robed staff mage with a glowing crystal | `build_mage.py` | `mage.fbx` | 38,308 | 17 | 21 | 2.09 m (staff) |
| Assassin — hooded knife fighter, brown-black leather, torn crimson skirt | `build_assassin.py` | `assassin.fbx` | 56,200 | 9 | 21 | 1.92 m |

The knight is the game's Vanguard, so its FBX ships under that name while the
script, the renders and the review scene still call it the paladin.

Two of the hand props ship as their own FBXs, cut out of the body by `prop_kit.py`:

| Prop | Script that cuts it | FBX | Worn by |
| --- | --- | --- | --- |
| Vanguard's lion shield | `build_paladin.py` | `vanguard_shield.fbx` | `PropLibrary` on the left forearm, in the air on Shield Throw |
| Assassin's knife | `build_assassin.py` | `assassin_dagger.fbx` | `PropLibrary` in the right fist, in the air on Dagger Throw and Blade Wave |

Each prop file carries the piece **twice**: once in the forearm's (or the fist's)
frame, which is what the arm wears, and once in a flight frame with its origin at
the balance point, which is what a throw spawns. `extract()` runs before anything is
weighted or joined, so the prop never touches the body's skinning, and the object
left in the body mesh is the empty space where it used to be - which is why the hand
reads as empty for the beat the prop is away.

The flight frame's forward is Blender **-Y**, not `+Y`: the FBX conversion maps
Blender `+Y` to Unity `-Z`, and the game throws the prop along its own `+Z`.
`orient_for_flight()` builds the frame that way, so a shield that left the arm
edge-on arrives face-first.

## Build

```bash
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_paladin.py
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_mage.py
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_assassin.py
```

Blender lives at `O:\Blender\blender.exe` (`/o/Blender/blender.exe` from Git Bash);
any 4.x/5.x build works. No add-ons, no GPU needed for the build, a few seconds
each. Then look at the result:

```bash
python Tools/Blender/review_page.py --title "paladin" --out Tools/Blender/renders/review.html \
  front=Tools/Blender/renders/paladin_front.png \
  3/4=Tools/Blender/renders/paladin_threequarter.png \
  side=Tools/Blender/renders/paladin_side.png \
  back=Tools/Blender/renders/paladin_back.png \
  head=Tools/Blender/renders/paladin_head.png \
  lion=Tools/Blender/renders/paladin_lion.png \
  sword=Tools/Blender/renders/paladin_sword.png
```

`review_page.py` embeds the pixels as data URIs, so the page works from the
Freebuff Preview pane (which serves the HTML file alone — a page that points at
sibling PNGs comes up with broken images). It also handles a single image, e.g.
a live window screenshot.

## Build it in a Blender someone is watching

`build_mage.py` is staged so each step is a separate call and the objects appear
in the viewport as they are made:

```bash
python Tools/Blender/mcp_client.py ping --port 8599
python Tools/Blender/mcp_client.py run bootstrap.py --port 8599
```

and `bootstrap.py` is:

```python
import sys, importlib
sys.path.insert(0, r"F:\UnityProjects\Arena\Tools\Blender")
import build_mage
importlib.reload(build_mage)
print(build_mage.prepare())                      # clears the startup scene
for stage in build_mage.STAGES:                  # one stage per call if you like
    print(stage, build_mage.build(stage))
print(build_mage.finish())                       # join, UV, FBX, renders
```

`mcp_client.py` speaks the BlenderMCP add-on's socket protocol
(`{"type": "execute_code", ...}` over raw TCP, no HTTP) and exits non-zero when
the socket is refused or the snippet raises. `prepare()` refuses to clear a scene
that holds objects it does not recognise, so unsaved work is never deleted by
accident. `live_bridge.py` / `live_client.py` are an older file-queue route into
a GUI Blender and still work; `mcp_send.py` is a one-shot sender for port 9876.

## Verify

```bash
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_fbx.py -- vanguard
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_fbx.py -- mage
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_fbx.py -- assassin
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_rig.py -- vanguard
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_rig.py -- mage
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_rig.py -- assassin
```

`check_rig.py` does for the skeleton what `check_fbx.py` does for the mesh: it
re-imports the shipped FBX and reports the bones, how much of the mesh carries
weights, whether the bind pose is clean, and how far the skin moves when a bone is
rotated. It exits non-zero when the file has no armature, no weights, or a rig that
does not deform.

`check_fbx.py` re-imports a shipped FBX and reports triangles, material slots, UV
layers, bounds and whether the feet sit on the origin, so the numbers describe the
artifact rather than the mesh that produced it. It exits non-zero when the file is
missing, has degenerate faces, floats off the floor or lost its scale.

## Outputs

| Path | What it is |
| --- | --- |
| `Assets/Resources/Arena/Fighters/*.fbx` | The game assets: one mesh each, UV-mapped, triangulated, feet at the origin |
| `Assets/Resources/Arena/Fighters/vanguard_shield.fbx`, `assassin_dagger.fbx` | The hand props, worn copy plus throwable copy (see above) |
| `Tools/Blender/renders/<name>_*.png` | Lit views, 700×980, EEVEE |
| `Tools/Blender/renders/review*.html` | Self-contained contact sheets for review |
| `Tools/Blender/{paladin,mage,assassin}_review.blend` | The lit review scenes (opening an FBX alone gives a dark viewport, which reads as "the model looks bad") |

Unity imports the FBX files on its next asset refresh. Each lands as a mesh with
per-material Unity materials, feet at the origin and the character facing +Z, so
it can be loaded with `Resources.Load<GameObject>("Arena/Fighters/<name>")`.

## Where to change things

- **`COLORS`, `METAL`, `ROUGH`** in `build_paladin.py` — the shared palette. Metals
  are deliberately matte (roughness ~0.4): a mirror finish reflects the world
  instead of the lamps, which turns the flat gold crosses olive brown.
- **`COLORS`, `METAL`, `ROUGH`, `MAGE_GLOW`** in `build_mage.py` — added on top of
  that palette, plus `emissive()` which pushes emission into the Principled node:
  the crystals have to glow, but at strength 6 they blew out to flat white.
  `MAGE_GLOW` is the fix, so change it carefully.
- **`COLORS`, `METAL`, `ROUGH`, `ASSASSIN_GLOW`** in `build_assassin.py` — the
  leather has to stay brown-black under the review key light (neutral blue-black
  reads as grey plastic) and the two eye coals glow at 2.4; at the gem's 4 they
  blew out to flat white and the face read as a robot visor.
- **`LEG_X`, `ARM_X`, `stage_cloth`'s panel table** in `build_assassin.py` — the
  skirt is placed around the hips in polar coordinates (theta 0 = front), so a new
  torn panel is one row in the table rather than a hand-placed shell.
- **`RIG_LANDMARKS` and `PART_BONES`** in `build_paladin.py`, `build_mage.py` and
  `build_assassin.py` — where each character's joints sit and which of its parts
  are pinned to a bone or blended across a set instead of placed by proximity. The
  landmarks are the build's own numbers (belt height for the pelvis, the gauntlet
  for the wrist, the poleyn for the knee); a new character passes its own set to
  `rig_kit.landmarks()` and lists its own gear. A landmark the character does not
  override keeps the 1.9 m default.
- **`HEIGHT`, `LEG_X`, `ARM_X`, `PLATE`, `STAFF_X`** — overall scale and where the
  limbs and the staff sit.
- **`build()` / `STAGES`** — the characters themselves.
- **`light_scene()`** — the review lighting. Overdriving the key light blows the
  steel to flat white and turns the gold specular pale grey.

### Shape language

`build_mage.py` imports `build_paladin.py` with `importlib` for the helpers
instead of copying them (its build is guarded behind `__main__`, so importing
only defines functions), which keeps the two characters consistent. Three
primitives cover both models:

- `loft(name, stations)` — a stack of rounded-rectangle profiles. `r` of 0 is a
  rectangle, `r = min(w, d)/2` is an ellipse, anything between is the soft-cornered
  slab plate armour actually is. Limbs taper, pauldrons bulge, greaves and belts
  are rings. `sides=1` with `r=0` gives four-point rings, which is how the cut
  crystals stay faceted.
- `shell(name, w, h, ...)` — a bowed quad grid plus a Solidify modifier: armour
  lames, the tabard, the cape, the mage's robe panels, and the shield (with
  `tip`/`dome_top` shaping its outline and `rim_mat` painting the raised gold
  border into the same mesh).
- `dome()` / `ball()` — hemispheres and ellipsoids for crowns, poleyns, faces,
  hair, the lion's mane and the beard.

`loft`/`shell` can take `smooth`/`subsurf` for skin and cloth and `bevel` for
crisp plate, and `hem_mat` colours a cloth panel's bottom band with a second
material slot so the trim follows the cloth instead of floating beside it.

`lion()` (paladin) and `gem()` / `emissive()` (mage) are the signature details:
the emblem the reference repeats on both pauldrons and the shield, and the
faceted crystals. `build_assassin.py` adds `lame()` (an armour plate with a
bright top edge), `spike()` (a four-sided blade aimed by rotating about Y, which
is how the pauldron crown, the bracer blades and the boot spikes all point away
from the body) and `dagger()` / `hip_knife()`, which bend their blades with
`loft`'s per-station `dx` instead of a curve modifier.

### Rigging

`rig_kit.py` hangs a 21-bone humanoid armature on a built mesh — `hips, spine,
chest, neck, head`, then `shoulder / upper_arm / forearm / hand` and `thigh / shin /
foot / toe` on each side — and writes a skinned FBX. All three characters are rigged,
by the same code, with one copy of the landmarks per character:

```bash
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_paladin.py   # vanguard.fbx
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_mage.py      # mage.fbx
"/o/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_assassin.py  # assassin.fbx
```

Weights are computed in Python rather than with `ARMATURE_AUTO`: bone heat needs a
closed, fairly clean surface, these figures are a few hundred overlapping open
shells (plates, belts, cloth panels), and a refused parenting op in a background
build says nothing on stdout. Each part is weighted *before* the parts are joined,
and each part is judged from its own centre — the belt buckle that sits as close to
the thigh bones as to the pelvis, and the knife that hangs 25 cm below the hand, are
both unanswerable questions for a proximity search on the joined mesh. Blending
happens only across the skeleton (the nearest bone plus its parent and children), so
a thigh cannot pick up the hand that hangs beside it.

`PART_BONES` in each build names the parts geometry cannot place. An entry is either
a single bone, which pins the part rigidly, or a *set* of bones, which the part is
blended across:

| Character | Pinned | Blended across |
| --- | --- | --- |
| Vanguard | sword to `hand.R`, shield to `forearm.L`, belt/skirt/straps to `hips`, chest filigree to `chest` | cape and tabard across `chest, spine, hips` |
| Mage | staff, claws, crystals and sparks to `hand.L`, belt/pouches/tomes to `hips` | the ankle-length skirt across `spine, hips, thigh.L/R, shin.L/R`; the knee-length panels, apron and sigil across `spine, hips, thigh.L/R` |
| Assassin | knives to the hands, belt, pouches, hip knives, skirt, sash to `hips` | — |

A single bone has to be pinned because proximity gets it wrong: the sword in the
fist hangs to the knee and the shield's centre sits nearer the *shoulder* than the
forearm it is strapped to. A robe needs the set because it hangs around the pelvis,
both thighs and both shins at once — the skirt's own centre is nearest a *shin*, so
left to proximity the whole robe would ride one leg. Blended parts are weighted with
a softer falloff and with the ratio cutoff and the weight floor switched off: both
of those are step functions in space, and a step in the weights is a step in the
skin: with them on, the mage's robe tore along the line where one leg's shin
stopped being the nearer bone (an x11 edge stretch on a 45° thigh rotation).

`pose_test()` is the check that matters — it rotates one bone and reports how far
that bone's own skin moves, how far the head moves and how far the opposite foot
moves; the last two must stay at 0.000, and they are what catch a weight painted on
the wrong bone. Stretch is the other half: opening a review `.blend` and measuring
every edge under a 45° pose shows how much the skin is torn rather than moved. What
is left sits in shells that straddle a joint — hair and beard on the neck/head
boundary (x2.2 mage, x2.6 vanguard, x4.6 assassin), the pauldron wing across the
arm/shoulder boundary (x2 vanguard, x15 assassin) and the robe hem where the two
legs diverge (x1.7 mage, on 147 of the robe's edges).

### Cleanup pass

`clean_mesh()` runs after the parts are joined: it drops zero-area slivers and
triangulates. The bevel pass left ~90 degenerate faces on the paladin's smallest
trim bars, and Unity's importer reports those as "polygon ... is self-intersecting
and has been discarded", which looks like a broken import in the console.

## Notes for the next pass

All three fighters now carry the same 21-bone skeleton, but none of them has an
animation clip yet, so the rig is a skeleton to animate rather than a walk cycle. The
game drives those bones itself: `Assets/Scripts/Presentation/FighterModel.cs` loads the
FBX from Resources, yaws it onto the game's forward axis (the skinned export's axis
conversion leaves it facing -Z), and rebuilds the stride, cast and recoil poses as bone
rotations about the character's own axes. A model that is missing leaves the fighter on
the primitive body it used to be, which is what `CharacterView` falls back to.

In Unity each FBX imports as a *generic* rig: to retarget Mecanim clips instead of
posing the bones in code, set Rig → Animation Type to **Humanoid** (or map the bones by
hand) in the model importer, which is an editor-side step no script here can do. The rig
has no IK, no twist bones and no finger bones, and the cloth is linear-blend skinned
rather than simulated. All of them use flat materials only, with no texture atlas. Three
scripts each carry their own copy of the render/export wrapper; batching the three
characters into one build script (and one shared `kit.py`) would be the next cleanup.
