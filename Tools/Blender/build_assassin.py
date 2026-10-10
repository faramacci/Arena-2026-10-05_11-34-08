"""Build the Assassin (the hooded, red-sashed knife fighter) from the sheet.

Same shape language and helpers as the Vanguard and the Mage - the geometry kit is
imported from build_paladin.py rather than copied, so a primitive change
reaches all three characters at once. What is new here is cloth and blades: the
reference is dark leather under a torn crimson skirt, and the knives curve.

Drive it in stages so a running Blender shows the build happening:

    import build_assassin
    build_assassin.prepare()            # clear the scene, set up materials
    for stage in build_assassin.STAGES: print(stage, build_assassin.build(stage))
    build_assassin.finish()             # join, clean, unwrap, export FBX, render

Headless:

    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_assassin.py

Outputs:
    Assets/Resources/Arena/Fighters/assassin.fbx
    Assets/Resources/Arena/Fighters/assassin_dagger.fbx   the right-hand knife on its
                                                          own, for the arm and the throw
    Tools/Blender/renders/assassin_*.png
    Tools/Blender/assassin_review.blend

Landmarks read off the reference against a 1.90 m figure: boot sole 0.00, knee
0.62, hip 1.06, belt 1.08-1.23, chest 1.40, shoulder 1.46, chin 1.68, crown
1.89, hood peak 1.93, dagger tips 0.37 (the blades hang to just above the knee).
The reference's shape grammar, which the stages below follow part by part:

* every plate is *bright* steel over dark leather - the first pass used dark iron
  and the shoulders disappeared into the body;
* the front of the torso is plain leather with a crossed harness; the red is a
  torn skirt behind and beside the legs, plus the sash tail on the left hip;
* the hood is a cowl with a peak at the back, not a closed tube over the face.
"""

import importlib.util
import math
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
FBX_OUT = os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters", "assassin.fbx")
RENDER_DIR = os.path.join(HERE, "renders")
BLEND_OUT = os.path.join(HERE, "assassin_review.blend")
RES = (700, 980)

NAME = "Assassin"
LEG_X = 0.130
ARM_X = 0.234
# How much bigger the fighting knives are than the reference sheet draws them: the
# blades read as letter openers from the game's camera. See `dagger()`.
DAGGER_SCALE = 1.30


def load_kit():
    """Import build_paladin.py for its helpers only.

    Its build is guarded behind __main__, so importing only defines loft/shell/
    dome/ball/finish/place_all/clean_mesh/unwrap and the palette dicts."""
    path = os.path.join(HERE, "build_paladin.py")
    spec = importlib.util.spec_from_file_location("paladin_kit", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_rig():
    """Import rig_kit.py, the shared armature/skinning module."""
    path = os.path.join(HERE, "rig_kit.py")
    spec = importlib.util.spec_from_file_location("rig_kit", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


KIT = load_kit()
loft, shell, dome, ball = KIT.loft, KIT.shell, KIT.dome, KIT.ball
finish, place_all, clean_mesh, unwrap, log = (
    KIT.finish, KIT.place_all, KIT.clean_mesh, KIT.unwrap, KIT.log)
RIG = load_rig()

# The knife in the right fist is the one the assassin throws, so it is lifted off the
# body and shipped as its own prop, exactly like the knight's shield. The left knife
# stays part of the mesh: he keeps hold of that one.
DAGGER = KIT.PROP_KIT.Prop(
    prefix="ass_dagger_1", bone="hand.R", name="AssassinDagger",
    path=os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters",
                      "assassin_dagger.fbx"))
PROPS = (DAGGER,)

# Where the skeleton's joints sit in this character, read off the same landmarks
# the stages are built from.
RIG_LANDMARKS = RIG.landmarks(hip_z=1.020, spine_z=1.160, chest_z=1.340,
                              neck_z=1.520, head_z=1.630, head_top=1.890,
                              shoulder_x=0.060, shoulder_out=0.212,
                              shoulder_z=1.452, arm_x=0.234, elbow_z=1.166,
                              wrist_z=0.900, hand_end_z=0.780, leg_x=0.130,
                              knee_z=0.620, ankle_z=0.150, toe_y=0.130,
                              toe_end_y=0.230, ground=0.030)

# Palette lifted from the reference: brown-black leather, torn crimson cloth,
# cold steel on the shoulders, forearms, knees and boots, and two burning eyes in
# the shadow of the hood.
ASSASSIN_COLORS = {
    # Warm, not neutral: the first pass was blue-black and rendered as grey
    # plastic next to the crimson, because the reference's leather is brown-black.
    "leather_black": (0.036, 0.029, 0.025),
    "leather_dark":  (0.021, 0.017, 0.014),
    "leather_mid":   (0.072, 0.054, 0.040),
    "cloth_red":     (0.360, 0.044, 0.050),
    "cloth_red_dark": (0.215, 0.026, 0.032),
    "cloth_red_deep": (0.112, 0.013, 0.019),
    "steel_bright":  (0.760, 0.772, 0.805),
    # The plate bodies. The paladin's steel (0.63) is a knight's polish and read
    # as pale blue slabs here; the reference is darker gunmetal with bright edges.
    "steel":         (0.450, 0.468, 0.505),
    "iron":          (0.150, 0.155, 0.170),
    "bronze_dark":   (0.220, 0.175, 0.115),
    "eye_glow":      (1.000, 0.430, 0.080),
}
ASSASSIN_METAL = {"leather_black": 0.0, "leather_dark": 0.0, "leather_mid": 0.05,
                  "steel_bright": 0.92, "steel": 0.88, "iron": 0.86,
                  "bronze_dark": 0.70, "eye_glow": 0.0}
ASSASSIN_ROUGH = {"leather_black": 0.58, "leather_dark": 0.66, "leather_mid": 0.62,
                  "cloth_red": 0.84, "cloth_red_dark": 0.88, "cloth_red_deep": 0.90,
                  "steel_bright": 0.26, "steel": 0.34, "iron": 0.44,
                  "bronze_dark": 0.50, "eye_glow": 0.18}
# Only the eyes glow. Strength 4 blew them out to flat white in the render, which
# reads as a robot's visor; 2.4 keeps them as two amber coals inside the hood.
ASSASSIN_GLOW = {"eye_glow": 2.4}

KIT.COLORS.update(ASSASSIN_COLORS)
KIT.METAL.update(ASSASSIN_METAL)
KIT.ROUGH.update(ASSASSIN_ROUGH)

PARTS = []


def emissive(key):
    """Push emission into the Principled node so the material really glows."""
    material = KIT.material(key)
    strength = ASSASSIN_GLOW.get(key, 0.0)
    if strength <= 0.0:
        return material
    color = ASSASSIN_COLORS[key]
    for node in material.node_tree.nodes:
        if node.type != "BSDF_PRINCIPLED":
            continue
        for slot, value in (("Emission Color", (color[0], color[1], color[2], 1.0)),
                            ("Emission", (color[0], color[1], color[2], 1.0)),
                            ("Emission Strength", strength)):
            if node.inputs.get(slot) is not None:
                node.inputs[slot].default_value = value
    material.diffuse_color = (color[0], color[1], color[2], 1.0)
    return material


def band(name, z0, z1, w, d, r, mat_key, loc=(0.0, 0.0, 0.0), sides=5, bevel=0.004,
         parts=None):
    """A short ring: belt edges, straps and the studded bands on the boots."""
    return loft(name, [{"z": z0, "w": w, "d": d, "r": r},
                       {"z": z1, "w": w, "d": d, "r": r}],
                mat_key, sides=sides, bevel=bevel, loc=loc, parts=parts)


def spike(name, base, length, mat_key, loc, rot=(0.0, 0.0, 0.0), parts=None):
    """A four-sided spike tapering to a point: the crown on the pauldrons and the
    blades that stand off the bracers and the boots.

    Built along +Z from the base ring at the origin, so rot=(0, +tilt*side, 0)
    aims it away from the body - rotating about Y by +tilt maps +Z to
    (sin tilt, 0, cos tilt)."""
    stations = [{"z": 0.0, "w": base, "d": base, "r": base * 0.32},
                {"z": length * 0.35, "w": base * 0.72, "d": base * 0.72,
                 "r": base * 0.22},
                {"z": length * 0.78, "w": base * 0.30, "d": base * 0.30,
                 "r": base * 0.10},
                {"z": length, "w": base * 0.07, "d": base * 0.07, "r": base * 0.02}]
    return loft(name, stations, mat_key, sides=4, bevel=0.003, smooth=True,
                loc=loc, rot=rot, parts=parts)


def lame(name, w, h, mat_key, loc, rot=(0.0, 0.0, 0.0), bow=0.050, taper=0.86,
         tip=0.0, rim=None, parts=None):
    """One overlapping armour lame: a `shell` plate with a bright top edge."""
    return shell(name, w, h, mat_key, loc, rot=rot, bow=bow, taper=taper, rows=4,
                 cols=5, thickness=0.014, bevel=0.005, tip=tip, rim_mat=rim,
                 rim_rows=1, rim_cols=0, parts=parts)


def dagger(name, parts=None):
    """A fighting knife built along -Z from the grip at the origin, like sword().

    The reference's blades sweep and carry a hooked barb on the back edge, so the
    blade stations carry a growing `dx` - `loft`'s per-station offset bends the
    blade outward without a curve modifier.

    The pair of knives read as too small on screen - in the fist and in the air - so
    the finished assembly is scaled about the grip, which is the origin it is built
    from. That keeps the knife in the hand and the hand's own reach unchanged while
    the blade grows; it covers all three copies at once, because the prop_kit cuts
    the right-hand knife out of this same geometry. DAGGER_SCALE is deliberately
    small: this is a bigger knife, not a short sword."""
    out = []
    out.append(ball(name + "_pommel", 0.024, "steel", (0.0, 0.0, 0.088),
                    parts=parts))
    out.append(loft(name + "_grip",
                    [{"z": -0.060, "w": 0.036, "d": 0.034, "r": 0.014},
                     {"z": 0.012, "w": 0.040, "d": 0.040, "r": 0.016},
                     {"z": 0.080, "w": 0.032, "d": 0.032, "r": 0.012}],
                    "leather_dark", sides=4, smooth=True, parts=parts))
    out.append(loft(name + "_guard",
                    [{"z": -0.100, "w": 0.126, "d": 0.034, "r": 0.011},
                     {"z": -0.062, "w": 0.094, "d": 0.030, "r": 0.010}],
                    "steel", sides=4, bevel=0.004, parts=parts))
    out.append(loft(name + "_blade",
                    [{"z": -0.094, "w": 0.050, "d": 0.020, "r": 0.008, "dx": 0.000},
                     {"z": -0.190, "w": 0.060, "d": 0.022, "r": 0.009, "dx": 0.006},
                     {"z": -0.280, "w": 0.054, "d": 0.019, "r": 0.007, "dx": 0.016},
                     {"z": -0.360, "w": 0.038, "d": 0.013, "r": 0.005, "dx": 0.028},
                     {"z": -0.430, "w": 0.012, "d": 0.006, "r": 0.002, "dx": 0.038}],
                    "steel_bright", sides=3, bevel=0.003, parts=parts))
    for index, zz in enumerate((-0.150, -0.245)):
        out.append(loft(name + "_barb_%d" % index,
                        [{"z": -0.026, "w": 0.030, "d": 0.012, "r": 0.005},
                         {"z": 0.024, "w": 0.013, "d": 0.006, "r": 0.002}],
                        "steel", sides=3, bevel=0.002, parts=parts,
                        loc=(-0.030, 0.006, zz), rot=(0.0, 0.0, 30.0)))
    for obj in out:
        for vertex in obj.data.vertices:
            vertex.co = vertex.co * DAGGER_SCALE
    return out


def hip_knife(name, parts=None):
    """The short belt knife: sheath with the blade's point showing past the tip."""
    out = []
    out.append(ball(name + "_pommel", 0.019, "steel_bright", (0.0, 0.0, 0.062),
                    parts=parts))
    out.append(loft(name + "_grip",
                    [{"z": -0.012, "w": 0.030, "d": 0.030, "r": 0.011},
                     {"z": 0.050, "w": 0.034, "d": 0.034, "r": 0.013}],
                    "leather_dark", sides=4, smooth=True, parts=parts))
    out.append(loft(name + "_guard",
                    [{"z": -0.032, "w": 0.092, "d": 0.028, "r": 0.009},
                     {"z": -0.012, "w": 0.070, "d": 0.024, "r": 0.008}],
                    "steel", sides=4, bevel=0.003, parts=parts))
    out.append(loft(name + "_blade",
                    [{"z": -0.030, "w": 0.042, "d": 0.018, "r": 0.006},
                     {"z": -0.130, "w": 0.046, "d": 0.019, "r": 0.007},
                     {"z": -0.210, "w": 0.030, "d": 0.012, "r": 0.004},
                     {"z": -0.256, "w": 0.008, "d": 0.005, "r": 0.002}],
                    "steel_bright", sides=3, bevel=0.003, parts=parts))
    out.append(loft(name + "_sheath",
                    [{"z": -0.026, "w": 0.070, "d": 0.048, "r": 0.016},
                     {"z": -0.152, "w": 0.062, "d": 0.042, "r": 0.014}],
                    "leather_mid", sides=4, bevel=0.004, parts=parts))
    return out


# ------------------------------------------------------------------------- stages

def stage_legs():
    """Boots with pointed steel toes, plated greaves and spiked knee guards.

    The reference's leg is dark leather with steel laid over it in overlapping
    lames; a full steel leg (the way the paladin is built) turns the character
    into a knight, so the plates stay narrow and sit on the front of the shin."""
    for side in (-1, 1):
        x = LEG_X * side
        loft("ass_thigh_%d" % side,
             [{"z": 0.600, "w": 0.176, "d": 0.208, "r": 0.058, "dx": x},
              {"z": 0.770, "w": 0.196, "d": 0.226, "r": 0.064, "dx": x},
              {"z": 0.960, "w": 0.218, "d": 0.248, "r": 0.070, "dx": x}],
             "leather_black", bevel=0.006, parts=PARTS)
        band("ass_thigh_strap_%d" % side, 0.700, 0.744, 0.204, 0.234, 0.065,
             "leather_mid", loc=(x, 0.0, 0.0), parts=PARTS)
        band("ass_thigh_buckle_%d" % side, 0.706, 0.738, 0.052, 0.030, 0.010,
             "steel", loc=(x, 0.118, 0.0), sides=4, parts=PARTS)
        # Outer thigh plate. A `shell` bows along +Y, so getting it onto the side
        # of the leg means turning the panel onto its side (rot z -90*side).
        shell("ass_thigh_plate_%d" % side, 0.110, 0.155, "steel",
              (x + 0.086 * side, 0.012, 0.936), rot=(0.0, 0.0, -90.0 * side),
              bow=0.048, taper=0.86, rows=4, cols=4, thickness=0.012, bevel=0.004,
              tip=0.30, rim_mat="steel_bright", rim_rows=1, rim_cols=0, parts=PARTS)
        loft("ass_shin_%d" % side,
             [{"z": 0.175, "w": 0.148, "d": 0.176, "r": 0.048, "dx": x},
              {"z": 0.330, "w": 0.158, "d": 0.190, "r": 0.052, "dx": x},
              {"z": 0.520, "w": 0.172, "d": 0.206, "r": 0.057, "dx": x}],
             "leather_black", bevel=0.005, parts=PARTS)
        lame("ass_greave_hi_%d" % side, 0.120, 0.162, "steel", (x, 0.036, 0.530),
             bow=0.050, taper=0.84, tip=0.26, rim="steel_bright", parts=PARTS)
        lame("ass_greave_lo_%d" % side, 0.114, 0.140, "steel", (x, 0.028, 0.392),
             bow=0.046, taper=0.84, tip=0.26, rim="steel_bright", parts=PARTS)
        band("ass_shin_band_%d" % side, 0.250, 0.284, 0.160, 0.194, 0.054,
             "leather_mid", loc=(x, 0.006, 0.0), parts=PARTS)
        # Knee: bright poleyn, a red inset in the middle and a blade over the top.
        shell("ass_knee_%d" % side, 0.178, 0.140, "steel", (x, 0.030, 0.648),
              bow=0.074, taper=0.88, rows=4, cols=5, thickness=0.014, bevel=0.005,
              tip=0.42, rim_mat="steel_bright", rim_rows=1, rim_cols=0, parts=PARTS,
              subsurf=1)
        spike("ass_knee_spike_%d" % side, 0.034, 0.072, "steel_bright",
              (x, 0.078, 0.634), rot=(34.0, 0.0, 0.0), parts=PARTS)
        shell("ass_knee_slit_%d" % side, 0.046, 0.070, "cloth_red",
              (x, 0.100, 0.606), bow=0.020, taper=0.42, rows=3, cols=3,
              thickness=0.008, bevel=0.002, parts=PARTS)
        lame("ass_knee_low_%d" % side, 0.152, 0.090, "steel", (x, 0.026, 0.562),
             bow=0.058, taper=0.90, parts=PARTS)
        # Boot: leather, a pointed steel toe, a heel plate and an ankle blade.
        loft("ass_boot_%d" % side,
             [{"z": 0.0, "w": 0.170, "d": 0.296, "r": 0.056, "dx": x, "dy": 0.034},
              {"z": 0.086, "w": 0.172, "d": 0.264, "r": 0.056, "dx": x, "dy": 0.022},
              {"z": 0.186, "w": 0.150, "d": 0.190, "r": 0.047, "dx": x, "dy": 0.002}],
             "leather_black", bevel=0.006, parts=PARTS)
        # Sabaton toe: the same taper laid on its face by rotating it about X, so
        # its local +Z becomes forward-and-up. rot x -80 maps +Z to (0, .985, .17).
        loft("ass_toe_%d" % side,
             [{"z": 0.0, "w": 0.148, "d": 0.120, "r": 0.038},
              {"z": 0.048, "w": 0.132, "d": 0.100, "r": 0.034},
              {"z": 0.092, "w": 0.086, "d": 0.058, "r": 0.020},
              {"z": 0.126, "w": 0.032, "d": 0.022, "r": 0.007}],
             "steel", sides=5, bevel=0.004, parts=PARTS, loc=(x, 0.098, 0.062),
             rot=(-80.0, 0.0, 0.0))
        shell("ass_heel_%d" % side, 0.128, 0.098, "steel", (x, -0.106, 0.126),
              rot=(14.0, 0.0, 180.0), bow=0.032, taper=0.84, rows=3, cols=4,
              thickness=0.012, bevel=0.004, tip=0.24, parts=PARTS)
        lame("ass_ankle_%d" % side, 0.126, 0.100, "steel", (x, 0.058, 0.196),
             bow=0.046, taper=0.80, tip=0.30, parts=PARTS)
        spike("ass_boot_spike_%d" % side, 0.028, 0.056, "steel_bright",
              (x + 0.078 * side, -0.014, 0.170), rot=(0.0, 62.0 * side, 0.0),
              parts=PARTS)
        for i, zz in enumerate((0.044, 0.116)):
            band("ass_boot_band_%d_%d" % (side, i), zz, zz + 0.030, 0.178, 0.296,
                 0.060, "leather_mid", loc=(x, 0.030, 0.0), parts=PARTS)
            band("ass_boot_buckle_%d_%d" % (side, i), zz, zz + 0.030, 0.046, 0.028,
                 0.009, "steel_bright", loc=(x, 0.150, 0.0), sides=4, parts=PARTS)


def stage_cloth():
    """The torn crimson skirt: panels around the hips, plus the sash tail.

    The reference does *not* hang red down the front - its chest is plain leather
    and the crimson is a ragged skirt behind and beside the legs - so the panels
    are placed around the hips in polar coordinates (theta 0 = front) instead of
    as one central banner, which is what made the first pass read as a tabard.
    They hang from inside the belt, so the hem of the cloth stays visible."""
    # Narrow panels: a `shell`'s width runs *across* the body, which from the
    # side is the fore-aft direction, so wide cloth here reads as a red slab
    # hanging off the hip. The radius stays where it is - closer in and the panel
    # would sit inside the thigh - and only the width comes down.
    panels = ((180.0, 0.184, 1.100, 0.196, 0.760, 0.72, 0.45),
              (140.0, 0.200, 1.090, 0.130, 0.660, 0.70, 0.50),
              (-136.0, 0.200, 1.100, 0.130, 0.610, 0.70, 0.50),
              (100.0, 0.226, 1.080, 0.106, 0.560, 0.68, 0.55),
              (-102.0, 0.226, 1.090, 0.106, 0.500, 0.68, 0.55),
              (44.0, 0.192, 1.100, 0.092, 0.600, 0.60, 0.75),
              (-30.0, 0.194, 1.100, 0.092, 0.340, 0.66, 0.60))
    for index, (theta, radius, z0, width, height, taper, tip) in enumerate(panels):
        angle = math.radians(theta)
        shell("ass_skirt_%d" % index, width, height, "cloth_red",
              (radius * math.sin(angle), radius * math.cos(angle), z0),
              rot=(0.0, 0.0, -theta), bow=0.018, taper=taper, rows=8, cols=4,
              thickness=0.012, bevel=0.004, tip=tip, hem_mat="cloth_red_deep",
              hem_frac=0.14, parts=PARTS, subsurf=1)
    # Shreds: two narrow points hanging off the back panel's hem, so the tear
    # reads as cloth instead of as a cut-off cone.
    for index, (dx, top, height) in enumerate(((-0.078, 0.440, 0.240),
                                               (0.086, 0.410, 0.200))):
        shell("ass_shred_%d" % index, 0.070, height, "cloth_red_dark",
              (dx, -0.170, top), bow=0.020, taper=0.40, rows=4, cols=3,
              thickness=0.010, bevel=0.003, tip=0.92, hem_mat="cloth_red_deep",
              hem_frac=0.20, parts=PARTS, subsurf=1)
    # The sash tail on the left hip: narrow, and the longest of the red pieces.
    shell("ass_sash_tail", 0.082, 0.640, "cloth_red", (0.168, 0.128, 1.114),
          rot=(0.0, 0.0, -38.0), bow=0.030, taper=0.58, rows=10, cols=3,
          thickness=0.011, bevel=0.003, tip=0.80, hem_mat="cloth_red_deep",
          hem_frac=0.18, parts=PARTS, subsurf=1)


def stage_torso():
    """Leather body, the crossed chest harness and the spiked steel pauldrons.

    The pauldrons are the character's silhouette - bright steel over a dark
    shoulder, four blades off the top and a lame cascading down the sleeve - so
    they are the one place the steel is allowed to be big."""
    loft("ass_torso",
         [{"z": 1.055, "w": 0.320, "d": 0.228, "r": 0.082},
          {"z": 1.190, "w": 0.334, "d": 0.236, "r": 0.086},
          {"z": 1.350, "w": 0.376, "d": 0.258, "r": 0.096},
          {"z": 1.480, "w": 0.394, "d": 0.268, "r": 0.100},
          {"z": 1.570, "w": 0.312, "d": 0.226, "r": 0.084}],
         "leather_black", bevel=0.006, parts=PARTS)
    # Chest panel and the single red gimp line under the collar. A red *frame*
    # around the panel (the first pass) read as a life jacket.
    # The panel is deliberately shallow: at bow 0.052 its front face sat in front
    # of the harness straps and hid the crossed leather the reference leads with.
    shell("ass_chest", 0.244, 0.360, "leather_black", (0.0, 0.140, 1.552),
          bow=0.030, taper=0.86, rows=6, cols=5, thickness=0.014, bevel=0.004,
          rim_mat="leather_mid", rim_rows=1, rim_cols=0, parts=PARTS, subsurf=1)
    band("ass_chest_trim", 1.512, 1.532, 0.248, 0.244, 0.094, "cloth_red_deep",
         sides=6, bevel=0.003, parts=PARTS)
    # Central strap down the chest with three fittings, the way the reference
    # runs one between the crossed harness straps.
    shell("ass_chest_strap", 0.062, 0.420, "leather_mid", (0.0, 0.152, 1.556),
          bow=0.046, taper=0.94, rows=6, cols=2, thickness=0.010, bevel=0.003,
          parts=PARTS)
    for i, zz in enumerate((1.540, 1.428, 1.316)):
        shell("ass_chest_fitting_%d" % i, 0.044, 0.044, "steel",
              (0.0, 0.170, zz), bow=0.012, taper=0.92, rows=2, cols=2,
              thickness=0.010, bevel=0.004, tip=0.20, parts=PARTS)
    # Crossed harness: each strap is a slanted loft, so it follows the chest
    # instead of floating on it, and they cross under a steel plate.
    for side in (-1, 1):
        loft("ass_harness_%d" % side,
             [{"z": 1.235, "w": 0.058, "d": 0.026, "r": 0.012, "dx": -0.130 * side,
               "dy": 0.214},
              {"z": 1.560, "w": 0.058, "d": 0.026, "r": 0.012, "dx": 0.130 * side,
               "dy": 0.198}],
             "leather_mid", sides=4, bevel=0.004, parts=PARTS)
    loft("ass_harness_buckle",
         [{"z": -0.032, "w": 0.084, "d": 0.030, "r": 0.012},
          {"z": 0.032, "w": 0.084, "d": 0.030, "r": 0.012}],
         "steel", sides=4, bevel=0.004, parts=PARTS, loc=(0.0, 0.198, 1.400),
         rot=(0.0, 0.0, 45.0))
    for side in (-1, 1):
        for i, (dx, zz) in enumerate(((0.156, 1.462), (0.170, 1.330), (0.156, 1.216))):
            ball("ass_rivet_%d_%d" % (side, i), 0.008, "steel_bright",
                 (dx * side, 0.142, zz), rings=6, sides=8, parts=PARTS)
    band("ass_collar", 1.548, 1.596, 0.226, 0.220, 0.088, "steel", sides=6,
         bevel=0.004, parts=PARTS)
    # Shoulders: a leather pad, a bright ring under a steel cap, two lames down
    # the sleeve - and the blades the reference leads with.
    for side in (-1, 1):
        x = 0.212 * side
        dome("ass_shoulder_pad_%d" % side, 0.116, 0.146, 0.100, "leather_black",
             (x, 0.0, 1.470), scale=(1.0, 1.05, 1.0), parts=PARTS)
        dome("ass_pauldron_%d" % side, 0.140, 0.158, 0.112, "steel",
             (x + 0.014 * side, 0.0, 1.446), rot=(0.0, 20.0 * side, 0.0),
             parts=PARTS)
        loft("ass_pauldron_rim_%d" % side,
             [{"z": -0.022, "w": 0.286, "d": 0.318, "r": 0.132},
              {"z": 0.022, "w": 0.272, "d": 0.304, "r": 0.126}],
             "steel_bright", sides=6, bevel=0.005, parts=PARTS,
             loc=(x + 0.014 * side, 0.0, 1.446), rot=(0.0, 20.0 * side, 0.0))
        for i in range(2):
            size = 0.232 - i * 0.024
            depth = 0.256 - i * 0.024
            tilt = (30.0 + 16.0 * i) * side
            spot = (x + (0.022 + 0.026 * i) * side, 0.0, 1.398 - i * 0.082)
            loft("ass_lame_%d_%d" % (side, i),
                 [{"z": -0.028, "w": size, "d": depth, "r": size * 0.43},
                  {"z": 0.028, "w": size - 0.008, "d": depth - 0.010,
                   "r": size * 0.42}],
                 "steel", sides=6, bevel=0.005, parts=PARTS, loc=spot,
                 rot=(0.0, tilt, 0.0))
            loft("ass_lame_trim_%d_%d" % (side, i),
                 [{"z": -0.048, "w": size, "d": depth, "r": size * 0.43},
                  {"z": -0.028, "w": size, "d": depth, "r": size * 0.43}],
                 "steel_bright", sides=6, bevel=0.004, parts=PARTS, loc=spot,
                 rot=(0.0, tilt, 0.0))
        blades = ((0.034, 0.126, 0.0, 18.0, 0.0, 1.522),
                  (0.038, 0.138, 0.0, 38.0, 0.0, 1.498),
                  (0.030, 0.120, 48.0, 30.0, -0.068, 1.520),
                  (0.028, 0.094, 0.0, 58.0, -0.012, 1.460),
                  (0.024, 0.070, -40.0, 20.0, 0.066, 1.512))
        for i, (base, length, tip_x, tilt, dy, zz) in enumerate(blades):
            spike("ass_spike_%d_%d" % (side, i), base, length, "steel_bright",
                  (x + 0.052 * side, dy, zz), rot=(tip_x, tilt * side, 0.0),
                  parts=PARTS)


def stage_belt():
    """Wide belt with a pointed steel emblem, pouches and the sheathed knives."""
    loft("ass_belt",
         [{"z": 1.075, "w": 0.400, "d": 0.288, "r": 0.098},
          {"z": 1.150, "w": 0.416, "d": 0.300, "r": 0.102},
          {"z": 1.226, "w": 0.400, "d": 0.288, "r": 0.098}],
         "leather_black", bevel=0.006, parts=PARTS)
    band("ass_belt_edge_top", 1.226, 1.258, 0.412, 0.296, 0.100, "leather_mid",
         sides=6, parts=PARTS)
    band("ass_belt_edge_low", 1.042, 1.075, 0.412, 0.296, 0.100, "leather_mid",
         sides=6, parts=PARTS)
    # The reference's buckle is a pointed shield of steel, not a rectangle.
    shell("ass_belt_emblem", 0.112, 0.144, "steel", (0.0, 0.156, 1.236), bow=0.030,
          taper=0.80, rows=4, cols=4, thickness=0.014, bevel=0.005, tip=0.62,
          rim_mat="steel_bright", rim_rows=1, rim_cols=1, parts=PARTS)
    for i, dx in enumerate((-0.132, 0.132)):
        shell("ass_belt_plate_%d" % i, 0.072, 0.094, "steel",
              (dx, 0.130, 1.218), rot=(0.0, 0.0, 22.0 * (1 if dx > 0 else -1)),
              bow=0.026, taper=0.80, rows=3, cols=3, thickness=0.012, bevel=0.004,
              tip=0.28, parts=PARTS)
    for side in (-1, 1):
        # Pouches: the one piece of mid-brown in the figure, as on the sheet.
        shell("ass_pouch_%d" % side, 0.128, 0.176, "leather_mid",
              (0.208 * side, 0.056, 1.086), rot=(0.0, 0.0, -20.0 * side), bow=0.046,
              taper=0.82, rows=4, cols=4, thickness=0.022, bevel=0.006,
              hem_mat="leather_dark", hem_frac=0.18, parts=PARTS)
        shell("ass_pouch_flap_%d" % side, 0.118, 0.062, "leather_black",
              (0.208 * side, 0.060, 1.090), rot=(0.0, 0.0, -20.0 * side), bow=0.030,
              taper=0.94, rows=2, cols=4, thickness=0.012, bevel=0.004, parts=PARTS)
        ball("ass_pouch_stud_%d" % side, 0.013, "steel_bright",
             (0.208 * side, 0.104, 1.062), rings=6, sides=10, parts=PARTS)
        knife = hip_knife("ass_hip_knife_%d" % side, parts=None)
        place_all(knife, (0.232 * side, 0.112, 1.048), (14.0, 0.0, -24.0 * side))
        PARTS.extend(knife)


def stage_arms():
    """Leather sleeves, spiked steel bracers and the gloves holding the knives.

    The bracer blades are the second half of the reference's silhouette: three
    off the outer edge of each forearm, aimed away from the body."""
    for side in (-1, 1):
        x = ARM_X * side
        loft("ass_upper_arm_%d" % side,
             [{"z": 1.170, "w": 0.158, "d": 0.178, "r": 0.058, "dx": x + 0.022 * side},
              {"z": 1.300, "w": 0.174, "d": 0.194, "r": 0.064, "dx": x + 0.012 * side},
              {"z": 1.446, "w": 0.190, "d": 0.212, "r": 0.072, "dx": x}],
             "leather_black", bevel=0.005, parts=PARTS)
        band("ass_arm_strap_%d" % side, 1.276, 1.310, 0.182, 0.202, 0.066,
             "leather_mid", loc=(x + 0.012 * side, 0.0, 0.0), parts=PARTS)
        shell("ass_arm_plate_%d" % side, 0.098, 0.130, "steel",
              (x + 0.080 * side, 0.010, 1.330), rot=(0.0, 0.0, -90.0 * side),
              bow=0.044, taper=0.86, rows=3, cols=4, thickness=0.012, bevel=0.004,
              tip=0.30, parts=PARTS)
        # Elbow: a steel cap with a point aimed back and up.
        dome("ass_elbow_%d" % side, 0.100, 0.106, 0.076, "steel",
             (x + 0.030 * side, -0.024, 1.166), rot=(0.0, 18.0 * side, 0.0),
             parts=PARTS)
        spike("ass_elbow_spike_%d" % side, 0.044, 0.086, "steel_bright",
              (x + 0.030 * side, -0.058, 1.172), rot=(52.0, 14.0 * side, 0.0),
              parts=PARTS)
        loft("ass_forearm_%d" % side,
             [{"z": 0.930, "w": 0.138, "d": 0.150, "r": 0.052, "dx": x + 0.056 * side},
              {"z": 1.020, "w": 0.148, "d": 0.160, "r": 0.056, "dx": x + 0.048 * side},
              {"z": 1.112, "w": 0.158, "d": 0.172, "r": 0.060, "dx": x + 0.036 * side}],
             "leather_black", bevel=0.005, parts=PARTS)
        # Bracer: bright plate over the forearm, pointed toward the elbow, with a
        # red lining showing under it.
        shell("ass_bracer_%d" % side, 0.172, 0.248, "steel",
              (x + 0.046 * side, 0.020, 1.128), rot=(0.0, -6.0 * side, 0.0),
              bow=0.076, taper=0.84, rows=5, cols=5, thickness=0.014, bevel=0.005,
              tip=0.28, rim_mat="steel_bright", rim_rows=1, rim_cols=0, parts=PARTS)
        shell("ass_bracer_lining_%d" % side, 0.118, 0.184, "cloth_red_deep",
              (x + 0.046 * side, 0.016, 1.120), rot=(0.0, -6.0 * side, 0.0),
              bow=0.070, taper=0.84, rows=4, cols=4, thickness=0.010, bevel=0.003,
              tip=0.28, parts=PARTS)
        for i, (zz, tilt, length, base) in enumerate(((1.150, 58.0, 0.076, 0.028),
                                                      (1.078, 70.0, 0.096, 0.032),
                                                      (1.008, 80.0, 0.080, 0.028))):
            spike("ass_bracer_spike_%d_%d" % (side, i), base, length, "steel_bright",
                  (x + 0.122 * side, 0.010, zz), rot=(0.0, tilt * side, 0.0),
                  parts=PARTS)
        band("ass_wrist_band_%d" % side, 0.882, 0.912, 0.152, 0.164, 0.056,
             "steel", loc=(x + 0.056 * side, 0.0, 0.0), parts=PARTS)
        # Glove: closed around the grip, fingers wrapped over it, plate on the back.
        ball("ass_hand_%d" % side, 0.072, "leather_black",
             (x + 0.062 * side, 0.028, 0.816), scale=(0.92, 1.05, 1.0), parts=PARTS)
        for finger in range(4):
            ball("ass_finger_%d_%d" % (side, finger), 0.023, "leather_black",
                 (x + 0.062 * side + 0.006, 0.086, 0.862 - finger * 0.036),
                 scale=(1.0, 0.85, 0.9), rings=6, sides=8, parts=PARTS)
        ball("ass_thumb_%d" % side, 0.024, "leather_black",
             (x + 0.030 * side, 0.070, 0.836), scale=(1.15, 0.9, 0.85), rings=6,
             sides=8, parts=PARTS)
        shell("ass_hand_plate_%d" % side, 0.104, 0.076, "steel",
              (x + 0.062 * side, 0.014, 0.884), rot=(0.0, 0.0, -90.0 * side),
              bow=0.030, taper=0.86, rows=3, cols=3, thickness=0.011, bevel=0.004,
              parts=PARTS)


def stage_head():
    """The hood, the mask under it, the red scarf and the two burning eyes.

    A hood is not a closed tube: a tube puts a wall of leather in front of the
    face. This one is an assembly - a crown, a drape down the back, a peak above
    it and a panel on each cheek - which leaves the front open exactly where the
    reference has the shadowed opening. The crown is pushed back (loc y -0.028)
    so its front rim stops behind the eyes instead of over them, which is what
    hid them at the first pass."""
    loft("ass_neck",
         [{"z": 1.580, "w": 0.136, "d": 0.132, "r": 0.052, "dy": 0.012},
          {"z": 1.690, "w": 0.124, "d": 0.120, "r": 0.048, "dy": 0.014}],
         "leather_dark", smooth=True, subsurf=1, parts=PARTS)
    loft("ass_head",
         [{"z": 1.664, "w": 0.080, "d": 0.098, "r": 0.038, "dy": 0.030},
          {"z": 1.712, "w": 0.113, "d": 0.130, "r": 0.049, "dy": 0.026},
          {"z": 1.768, "w": 0.136, "d": 0.154, "r": 0.059, "dy": 0.018},
          {"z": 1.824, "w": 0.138, "d": 0.154, "r": 0.059, "dy": 0.010},
          {"z": 1.866, "w": 0.108, "d": 0.122, "r": 0.047, "dy": 0.004}],
         "leather_dark", smooth=True, subsurf=1, parts=PARTS)
    # Mask over the lower face only, with a seam down the middle. A full-face
    # plate (the first pass) put a wall of leather in front of the eyes.
    # Short on purpose: a long panel here (h 0.142 at the first pass) reached the
    # jaw and read as a shovel poking out under the eyes.
    shell("ass_mask", 0.150, 0.108, "leather_black", (0.0, 0.084, 1.796), bow=0.020,
          taper=0.68, rows=4, cols=4, thickness=0.013, bevel=0.004, tip=0.28,
          parts=PARTS, subsurf=1)
    loft("ass_mask_ridge",
         [{"z": -0.044, "w": 0.024, "d": 0.026, "r": 0.008, "dy": 0.016},
          {"z": 0.044, "w": 0.020, "d": 0.022, "r": 0.007, "dy": 0.020}],
         "leather_mid", sides=3, smooth=True, parts=PARTS,
         loc=(0.0, 0.100, 1.764))
    # The eyes sit proud of the face and above the mask, so the hood shadows
    # them without covering them.
    for side in (-1, 1):
        ball("ass_socket_%d" % side, 0.028, "leather_dark",
             (0.038 * side, 0.100, 1.812), scale=(1.30, 0.40, 0.46), rings=8,
             sides=12, parts=PARTS)
        ball("ass_eye_%d" % side, 0.019, "eye_glow", (0.038 * side, 0.110, 1.812),
             scale=(1.45, 0.45, 0.34), rings=8, sides=12, parts=PARTS)
    dome("ass_hood_crown", 0.138, 0.136, 0.142, "leather_black", (0.0, -0.030, 1.742),
         parts=PARTS)
    # The peak the sheet leads with: a cone leaning back off the crown.
    loft("ass_hood_peak",
         [{"z": 0.0, "w": 0.148, "d": 0.156, "r": 0.062},
          {"z": 0.062, "w": 0.092, "d": 0.096, "r": 0.036},
          {"z": 0.108, "w": 0.026, "d": 0.026, "r": 0.008}],
         "leather_black", sides=6, smooth=True, parts=PARTS,
         loc=(0.0, -0.074, 1.828), rot=(40.0, 0.0, 0.0))
    shell("ass_hood_back", 0.286, 0.330, "leather_black", (0.0, -0.082, 1.836),
          rot=(-13.0, 0.0, 0.0), bow=0.030, taper=0.74, rows=6, cols=6,
          thickness=0.012, bevel=0.004, tip=0.35, parts=PARTS, subsurf=1)
    # No brim over the eyes: a shell there (the first attempt) stuck out like a
    # cap bill. The crown's own curved rim is what shades them - at eye level its
    # front surface is y 0.088 against the eye front at 0.119, so the slits read
    # as two coals set back in the opening.
    # Side drapes: narrow and long, hugging the jaw. Round cheek panels here (the
    # first pass) stuck out beside the head like ear muffs.
    for side in (-1, 1):
        shell("ass_hood_drape_%d" % side, 0.104, 0.240, "leather_black",
              (0.114 * side, 0.028, 1.766), rot=(0.0, 0.0, -90.0 * side),
              bow=0.016, taper=0.86, rows=4, cols=3, thickness=0.011, bevel=0.003,
              tip=0.35, parts=PARTS, subsurf=1)
    # Cowl over the shoulders, with the red scarf tucked inside it and the ends
    # hanging over the chest and back.
    loft("ass_cowl",
         [{"z": 1.462, "w": 0.312, "d": 0.292, "r": 0.116, "dy": -0.010},
          {"z": 1.556, "w": 0.276, "d": 0.262, "r": 0.104, "dy": -0.006},
          {"z": 1.652, "w": 0.226, "d": 0.222, "r": 0.088, "dy": 0.000}],
         "leather_black", bevel=0.005, parts=PARTS)
    band("ass_scarf", 1.558, 1.642, 0.236, 0.228, 0.094, "cloth_red", sides=8,
         parts=PARTS)
    shell("ass_scarf_bib", 0.134, 0.244, "cloth_red", (0.0, 0.130, 1.614),
          bow=0.042, taper=0.40, rows=5, cols=4, thickness=0.012, bevel=0.004,
          tip=0.55, hem_mat="cloth_red_deep", hem_frac=0.16, parts=PARTS,
          subsurf=1)
    shell("ass_scarf_back", 0.174, 0.236, "cloth_red_dark", (0.0, -0.136, 1.556),
          bow=0.020, taper=0.78, rows=4, cols=5, thickness=0.012, bevel=0.004,
          tip=0.60, hem_mat="cloth_red_deep", hem_frac=0.16, parts=PARTS,
          subsurf=1)


def stage_blades():
    """The pair of knives: one in each fist, blade down and tipped outward.

    The left knife is the right one turned through 180 degrees about Z (rot z),
    which mirrors the curve and the barbs without negating any coordinates - a
    mirrored mesh with flipped winding renders inside out."""
    for side in (-1, 1):
        blade = dagger("ass_dagger_%d" % side, parts=None)
        yaw = 0.0 if side > 0 else 180.0
        place_all(blade, (0.300 * side, 0.030, 0.808),
                  (16.0 * side, -13.0 * side, yaw))
        PARTS.extend(blade)


STAGES = ("legs", "cloth", "torso", "belt", "arms", "head", "blades")
_STAGE_FUNCS = {
    "legs": stage_legs, "cloth": stage_cloth, "torso": stage_torso,
    "belt": stage_belt, "arms": stage_arms, "head": stage_head,
    "blades": stage_blades,
}


def build(stage):
    """Add one stage's objects. Returns the running part count."""
    if stage not in _STAGE_FUNCS:
        raise ValueError("unknown stage %r (known: %s)" % (stage, ", ".join(STAGES)))
    _STAGE_FUNCS[stage]()
    log("stage %-6s -> %d parts" % (stage, len(PARTS)))
    return len(PARTS)


def prepare():
    """Empty the scene and switch the viewport to material preview.

    Only an untouched startup scene is cleared: anything with a name outside the
    default set is left alone and reported, because deleting someone's unsaved
    work is not recoverable."""
    defaults = {"Cube", "Camera", "Light", "Light.001", "Lamp", "Sphere", "Plane",
                "Circle", "Suzanne", "Torus", "Cylinder", "Cone", "Text",
                "Empty", "Armature", "Grid"}
    review_rig = {"key", "fill", "rim", "AssassinReviewCam"}

    def ours(name):
        base = name.split(".")[0]
        return (base in defaults or base in review_rig
                or name.lower().startswith(NAME.lower()))

    strangers = [obj.name for obj in bpy.data.objects if not ours(obj.name)]
    if strangers:
        log("REFUSING to clear: unexpected objects %s" % ", ".join(strangers))
        return "kept %d object(s): %s" % (len(strangers), ", ".join(strangers))

    removed = [obj.name for obj in list(bpy.data.objects)]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for key in ("eye_glow",):
        emissive(key)
    for area in bpy.context.screen.areas if bpy.context.screen else []:
        for space in area.spaces:
            if space.type == "VIEW_3D":
                try:
                    space.shading.type = "MATERIAL"
                    space.overlay.show_floor = True
                except Exception:
                    pass
    log("cleared %d object(s): %s" % (len(removed), ", ".join(removed) or "none"))
    return "cleared " + (", ".join(removed) if removed else "nothing")


def render_views(obj, views=None, suffix="%s"):
    os.makedirs(RENDER_DIR, exist_ok=True)
    scene = bpy.context.scene
    scene.render.resolution_x, scene.render.resolution_y = RES
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    log("render engine: " + KIT.light_scene(scene))

    cam_data = bpy.data.cameras.new("AssassinReviewCam")
    cam_data.type = "ORTHO"
    cam_data.sensor_fit = "VERTICAL"
    cam = bpy.data.objects.new("AssassinReviewCam", cam_data)
    bpy.context.collection.objects.link(cam)
    scene.camera = cam

    zs = [v.co.z for v in obj.data.vertices]
    xs = [v.co.x for v in obj.data.vertices]
    low, high = min(zs), max(zs)
    span = max(high - low, max(xs) - min(xs))
    focus = Vector((0.0, 0.0, (low + high) * 0.5))
    log("frame: z %.3f..%.3f (span %.3f)" % (low, high, span))

    # Whole-figure views first, then the places the reference puts its detail:
    # the hood, the shoulder blades, the harness, the skirt, the knives and the
    # pointed sabatons.
    views = VIEWS if views is None else views
    shots = []
    for name, yaw, pitch, custom_focus, custom_scale in views:
        target = focus if custom_focus is None else custom_focus
        cam_data.ortho_scale = custom_scale if custom_scale else span * 1.12
        yaw_r, pitch_r = math.radians(yaw), math.radians(pitch)
        offset = Vector((math.cos(yaw_r) * math.cos(pitch_r),
                         math.sin(yaw_r) * math.cos(pitch_r),
                         math.sin(pitch_r))) * 6.0
        cam.location = target + offset
        cam.rotation_euler = (target - cam.location).normalized() \
            .to_track_quat("-Z", "Y").to_euler()
        path = os.path.join(RENDER_DIR, "assassin_%s.png" % (suffix % name))
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        log("rendered " + path)
        shots.append(path)
    return shots


# Whole-figure views first, then the places the reference puts its detail: the
# hood, the shoulder blades, the harness, the skirt, the knives and the pointed
# sabatons.
VIEWS = (("front", 90.0, 6.0, None, None),
         ("threequarter", 128.0, 12.0, None, None),
         ("side", 0.0, 6.0, None, None),
         ("back", 270.0, 6.0, None, None),
         ("head", 104.0, 4.0, Vector((0.0, 0.0, 1.80)), 0.54),
         ("shoulder", 148.0, 14.0, Vector((0.24, 0.0, 1.47)), 0.78),
         ("torso", 92.0, 8.0, Vector((0.0, 0.0, 1.32)), 0.74),
         ("skirt", 74.0, 10.0, Vector((0.0, 0.0, 0.86)), 0.94),
         ("blades", 96.0, 8.0, Vector((0.30, 0.0, 0.62)), 0.90),
         ("boots", 108.0, 16.0, Vector((0.13, 0.0, 0.20)), 0.54))

# The same view twice, once at rest and once posed: the pair is the only proof
# that the skin follows the bones rather than the rig being decoration.
POSE_VIEW = (("posed", 128.0, 12.0, None, None),)


# Parts the rig cannot place from their own position, because they hang away from
# the bone that carries them or bridge both legs. A knife in a fist sits 25 cm
# below the hand - closer to the shin - and the back skirt panel is equidistant
# from the two thighs, so both are named here rather than left to proximity.
PART_BONES = (("ass_dagger_-1", "hand.L"), ("ass_dagger_1", "hand.R"),
              # Pelvis gear: the belt sits as close to the thigh bones as to the
              # pelvis, so proximity alone would swing it with each step.
              ("ass_belt", "hips"), ("ass_pouch_", "hips"),
              ("ass_hip_knife_", "hips"), ("ass_skirt_", "hips"),
              ("ass_shred_", "hips"), ("ass_sash_tail", "hips"))


def part_bone(name):
    """The bone a part must ride, or None to let the rig work it out."""
    for prefix, bone in PART_BONES:
        if name.startswith(prefix):
            return bone
    return None


def finish(export=True, render=True, rig=True):
    """Join, clean, unwrap, rig, write the FBX and (optionally) render the views."""
    if not PARTS:
        raise RuntimeError("nothing built yet - call build(stage) first")
    armature = None
    if rig:
        # Weight the parts before the join: each one is judged from its own centre,
        # which is the difference between a belt buckle following the pelvis and it
        # following the forearm.
        armature = RIG.build_armature(NAME + "_Rig", RIG_LANDMARKS)
        # Lift the throwing knife off before anything is weighted or joined.
        for prop in PROPS:
            KIT.PROP_KIT.extract(PARTS, RIG, armature, prop, clean=clean_mesh,
                                 unwrap=unwrap, log=log)
        segments = RIG.deform_bones(armature)
        pinned = 0
        for part in PARTS:
            bone = part_bone(part.name)
            pinned += 1 if bone else 0
            RIG.weight_part(part, armature, bone=bone, segments=segments)
        log("rig: %d parts weighted (%d pinned by name)" % (len(PARTS), pinned))
    assassin = KIT.join_parts(PARTS, name=NAME)
    clean_mesh(assassin)
    unwrap(assassin)
    triangles = sum(len(poly.vertices) - 2 for poly in assassin.data.polygons)
    height = max(v.co.z for v in assassin.data.vertices)
    width = max(v.co.x for v in assassin.data.vertices) - min(
        v.co.x for v in assassin.data.vertices)
    depth = max(v.co.y for v in assassin.data.vertices) - min(
        v.co.y for v in assassin.data.vertices)
    log("mesh: %d triangles, %d faces, %d material slots, %.2f m tall, "
        "%.2f m wide, %.2f m deep"
        % (triangles, len(assassin.data.polygons), len(assassin.data.materials),
           height, width, depth))
    if armature is not None:
        RIG.bind(assassin, armature)
        primary = RIG.primary_from_groups(assassin)
        test = RIG.pose_test(assassin, armature, primary)
        for bone, (own, head, far) in sorted(test.items()):
            log("rig %-12s own skin %.3f m, head %.3f m, far foot %.3f m"
                % (bone, own, head, far))
        bleed = max(max(head, far) for _, head, far in test.values())
        weak = min(own for own, _, _ in test.values())
        log("rig check: weakest bone moves %.3f m, worst bleed %.3f m"
            % (weak, bleed))
        log("rig: %d bones, %d skinned vertices"
            % (len(armature.data.bones), len(assassin.data.vertices)))
    if export:
        if armature is not None:
            RIG.export_skinned(assassin, armature, FBX_OUT)
            log("exported " + FBX_OUT + " (skinned)")
        else:
            save_fbx = KIT.FBX_OUT
            KIT.FBX_OUT = FBX_OUT
            try:
                KIT.export(assassin)
            finally:
                KIT.FBX_OUT = save_fbx
    shots = render_views(assassin) if render else []
    if armature is not None and render:
        RIG.showcase_pose(armature)
        shots += render_views(assassin, POSE_VIEW)
        RIG.clear_pose(armature)
    try:
        bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT, copy=True)
        log("saved " + BLEND_OUT)
    except Exception as exc:
        log("blend save skipped (%s)" % exc)
    return "%s: %d triangles, %.2f m tall, %d slots, %d bones, %d views" % (
        NAME, triangles, height, len(assassin.data.materials),
        0 if armature is None else len(armature.data.bones), len(shots))


def main():
    KIT.reset_scene()
    prepare()
    for stage in STAGES:
        build(stage)
    log(finish())


if __name__ == "__main__":
    main()
