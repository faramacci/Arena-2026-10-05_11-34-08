"""Build the Storm Mage (the indigo robed staff mage) from the reference sheet.

Same shape language as the Paladin - lofts, shells, domes - and it borrows that
script's geometry helpers instead of copying them, so the two characters stay
consistent when a primitive changes.

Drive it in stages so a running Blender shows the build happening:

    import build_mage
    build_mage.prepare()            # clear the scene, set up materials
    for stage in build_mage.STAGES: print(stage, build_mage.build(stage))
    build_mage.finish()             # join, clean, unwrap, export FBX, render

Headless:

    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_mage.py

Outputs:
    Assets/Resources/Arena/Fighters/mage.fbx
    Tools/Blender/renders/mage_*.png
    Tools/Blender/mage_review.blend

Landmarks read off the reference against a 1.90 m figure: hem 0.16, belt 1.20,
chest 1.44, shoulder 1.54, chin 1.66, crown 1.88, staff crystal 1.78-2.06.
"""

import importlib.util
import math
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
FBX_OUT = os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters", "mage.fbx")
RENDER_DIR = os.path.join(HERE, "renders")
BLEND_OUT = os.path.join(HERE, "mage_review.blend")
RES = (700, 980)

STAFF_X = -0.40          # the staff is held on the model's -X side, as in the sheet
STAFF_Y = 0.12


def load_kit():
    """Import build_paladin.py for its helpers only.

    Its build is guarded behind __main__, so importing only defines loft/shell/
    dome/ball/cross/finish/place_all/clean_mesh/unwrap and the palette dicts."""
    path = os.path.join(HERE, "build_paladin.py")
    spec = importlib.util.spec_from_file_location("paladin_kit", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


KIT = load_kit()
loft, shell, dome, ball, cross = KIT.loft, KIT.shell, KIT.dome, KIT.ball, KIT.cross
finish, place_all, clean_mesh, unwrap, log = (
    KIT.finish, KIT.place_all, KIT.clean_mesh, KIT.unwrap, KIT.log)

# Palette lifted from the reference: indigo robe, gold filigree, bronze fittings,
# brown leather and the pale-blue crystals that light the whole figure.
MAGE_COLORS = {
    "robe":      (0.155, 0.170, 0.470),
    "robe_dark": (0.085, 0.090, 0.300),
    "robe_pale": (0.215, 0.245, 0.575),
    "bronze":    (0.520, 0.360, 0.170),
    "wood":      (0.380, 0.250, 0.140),
    "hair_gray": (0.660, 0.665, 0.700),
    "beard_gray": (0.800, 0.805, 0.830),
    "gem":       (0.090, 0.400, 0.930),
    "gem_glow":  (0.100, 0.430, 0.960),
    "sigil":     (0.300, 0.550, 0.950),
    "boot":      (0.115, 0.120, 0.150),
}
MAGE_METAL = {"bronze": 0.90, "gem": 0.30, "gem_glow": 0.20, "sigil": 0.20,
              "iron": 0.85}
MAGE_ROUGH = {"robe": 0.80, "robe_dark": 0.84, "robe_pale": 0.74,
              "bronze": 0.38, "wood": 0.62, "hair_gray": 0.66, "beard_gray": 0.62,
              "gem": 0.12, "gem_glow": 0.10, "sigil": 0.16, "boot": 0.52}
# The crystals glow, but gently: at strength 6 the chest gems blew out to flat
# white and lost the reference's saturated blue entirely.
MAGE_GLOW = {"gem_glow": 0.9, "gem": 0.4, "sigil": 0.6}

KIT.COLORS.update(MAGE_COLORS)
KIT.METAL.update(MAGE_METAL)
KIT.ROUGH.update(MAGE_ROUGH)

PARTS = []


def emissive(key):
    """Push emission into the Principled node so the material really glows."""
    material = KIT.material(key)
    strength = MAGE_GLOW.get(key, 0.0)
    if strength <= 0.0:
        return material
    color = MAGE_COLORS[key]
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


def gem(name, size, height, mat_key, loc, rot=(0.0, 0.0, 0.0), parts=None):
    """A faceted cut crystal: four-point rings, so the sides stay flat facets."""
    stations = (
        {"z": -height * 0.5, "w": size * 0.16, "d": size * 0.16, "r": 0.0},
        {"z": -height * 0.14, "w": size, "d": size, "r": 0.0},
        {"z": height * 0.18, "w": size * 0.90, "d": size * 0.90, "r": 0.0},
        {"z": height * 0.5, "w": size * 0.16, "d": size * 0.16, "r": 0.0},
    )
    return loft(name, stations, mat_key, sides=1, loc=loc, rot=rot, parts=parts)


def ring(name, z0, z1, w, d, r, mat_key, loc=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0),
         sides=5, bevel=0.005, parts=None):
    """A short band - belt edges, cuffs, bronze fittings on the staff."""
    return loft(name, [{"z": z0, "w": w, "d": d, "r": r},
                       {"z": z1, "w": w, "d": d, "r": r}],
                mat_key, sides=sides, bevel=bevel, loc=loc, rot=rot, parts=parts)


# ------------------------------------------------------------------------- stages

def stage_legs():
    """Boots and the shins the robe hem reveals."""
    for side in (-1, 1):
        x = 0.125 * side
        loft("mage_boot_%d" % side,
             [{"z": 0.0, "w": 0.165, "d": 0.300, "r": 0.055, "dy": 0.035},
              {"z": 0.075, "w": 0.170, "d": 0.270, "r": 0.055, "dy": 0.022},
              {"z": 0.170, "w": 0.150, "d": 0.190, "r": 0.048}],
             "boot", loc=(x, 0.0, 0.0), bevel=0.006, parts=PARTS)
        loft("mage_boot_cap_%d" % side,
             [{"z": 0.0, "w": 0.158, "d": 0.095, "r": 0.035, "dy": 0.160},
              {"z": 0.065, "w": 0.135, "d": 0.080, "r": 0.030, "dy": 0.150}],
             "bronze", loc=(x, 0.0, 0.0), bevel=0.005, parts=PARTS)
        loft("mage_shin_%d" % side,
             [{"z": 0.155, "w": 0.140, "d": 0.170, "r": 0.050},
              {"z": 0.330, "w": 0.150, "d": 0.180, "r": 0.052},
              {"z": 0.520, "w": 0.165, "d": 0.190, "r": 0.055}],
             "boot", loc=(x, 0.0, 0.0), bevel=0.005, parts=PARTS)


def stage_robe():
    """The robe: a long inner skirt under two layers of pointed panels, and the
    front apron with the crystal sigil.

    Each layer has to end at its own height with its own pointed trim. When every
    panel finished level with the skirt the robe read as one corrugated barrel, and
    a straight gold ring around the hem made it worse."""
    # Inner skirt: the longest layer, hanging to the ankles.
    # The cross-section is a flattened ellipse, not a circle: the robe is wide
    # across the hips and thin front-to-back, which is what keeps the side view
    # from reading as a barrel.
    loft("mage_skirt",
         [{"z": 0.100, "w": 0.470, "d": 0.350, "r": 0.128},
          {"z": 0.360, "w": 0.470, "d": 0.350, "r": 0.128},
          {"z": 0.700, "w": 0.400, "d": 0.300, "r": 0.108},
          {"z": 1.000, "w": 0.360, "d": 0.272, "r": 0.100},
          {"z": 1.150, "w": 0.345, "d": 0.264, "r": 0.096}],
         "robe_dark", bevel=0.006, parts=PARTS)
    ring("mage_skirt_hem", 0.100, 0.148, 0.482, 0.362, 0.130, "gold_dark",
         bevel=0.004, parts=PARTS)
    # Middle layer: eight panels whose soft points land at knee height. The tip
    # stays shallow on purpose - a deep taper turned the robe into an artichoke.
    for phi in (0.0, 45.0, -45.0, 90.0, -90.0, 135.0, -135.0, 180.0):
        angle = math.radians(phi)
        shell("mage_panel_mid_%d" % int(phi), 0.215, 0.640, "robe_pale",
              (0.196 * math.sin(angle), 0.150 * math.cos(angle), 1.020),
              rot=(0.0, 0.0, -phi), bow=0.045, taper=0.97, rows=7, cols=5,
              thickness=0.012, bevel=0.004, tip=0.50, hem_mat="gold", hem_frac=0.055,
              parts=PARTS, subsurf=1)
    # Outer layer: longer panels, off-set half a step so the seam lines stagger.
    for phi in (22.0, -22.0, 67.0, -67.0, 112.0, -112.0, 157.0, -157.0):
        angle = math.radians(phi)
        shell("mage_panel_out_%d" % int(phi), 0.235, 0.520, "robe",
              (0.240 * math.sin(angle), 0.184 * math.cos(angle), 1.070),
              rot=(0.0, 0.0, -phi), bow=0.045, taper=0.98, rows=6, cols=5,
              thickness=0.012, bevel=0.004, tip=0.55, hem_mat="gold_dark",
              hem_frac=0.06, parts=PARTS, subsurf=1)
    # Front apron: the long tabard-like panel the reference carries the sigil on.
    shell("mage_apron", 0.290, 0.800, "robe", (0.0, 0.235, 1.130), bow=0.060,
          taper=0.97, rows=8, cols=6, thickness=0.014, bevel=0.004, tip=0.55,
          rim_mat="gold", rim_rows=1, rim_cols=1, parts=PARTS, subsurf=1)
    gem("mage_sigil", 0.085, 0.210, "sigil", (0.0, 0.345, 0.560), parts=PARTS)
    gem("mage_sigil_top", 0.055, 0.120, "sigil", (0.0, 0.352, 0.740), parts=PARTS)


def stage_torso():
    """Chest robe, shoulder mantle, collar, gems and gold filigree."""
    loft("mage_torso",
         [{"z": 1.110, "w": 0.360, "d": 0.280, "r": 0.095},
          {"z": 1.290, "w": 0.390, "d": 0.292, "r": 0.100},
          {"z": 1.450, "w": 0.440, "d": 0.315, "r": 0.110},
          {"z": 1.560, "w": 0.380, "d": 0.272, "r": 0.098}],
         "robe", bevel=0.006, parts=PARTS)
    shell("mage_chest", 0.290, 0.430, "robe_pale", (0.0, 0.150, 1.560), bow=0.050,
          taper=0.82, rows=5, cols=5, thickness=0.014, bevel=0.004, rim_mat="gold",
          rim_rows=1, rim_cols=1, parts=PARTS, subsurf=1)
    # Gems: two rows on the chest plus the tall one at the collar.
    for side in (-1, 1):
        gem("mage_gem_a_%d" % side, 0.062, 0.135, "gem_glow",
            (0.115 * side, 0.235, 1.430), parts=PARTS)
        gem("mage_gem_b_%d" % side, 0.055, 0.120, "gem_glow",
            (0.110 * side, 0.230, 1.320), parts=PARTS)
    gem("mage_gem_c", 0.072, 0.170, "gem_glow", (0.0, 0.250, 1.510), parts=PARTS)
    # Mantle: the wide cape-collar that makes the mage's silhouette.
    # The mantle stops above the belt so the buckle and pouches stay visible.
    # The mantle sits back (dy negative) the way a mantle drapes: pushed forward
    # it swallowed the chest panel, the beard and the neck.
    loft("mage_mantle",
         [{"z": 1.325, "w": 0.600, "d": 0.395, "r": 0.150, "dy": -0.055},
          {"z": 1.420, "w": 0.590, "d": 0.390, "r": 0.148, "dy": -0.050},
          {"z": 1.520, "w": 0.470, "d": 0.320, "r": 0.125, "dy": -0.042},
          {"z": 1.590, "w": 0.310, "d": 0.230, "r": 0.088, "dy": -0.032}],
         "robe", bevel=0.006, parts=PARTS)
    ring("mage_mantle_trim", 1.305, 1.365, 0.615, 0.410, 0.152, "gold",
         sides=6, bevel=0.005, loc=(0.0, -0.055, 0.0), parts=PARTS)
    ring("mage_mantle_trim2", 1.495, 1.535, 0.490, 0.340, 0.128, "gold_dark",
         sides=6, bevel=0.005, loc=(0.0, -0.045, 0.0), parts=PARTS)
    # Flared collar behind the neck.
    loft("mage_collar",
         [{"z": 1.520, "w": 0.215, "d": 0.185, "r": 0.072, "dy": -0.030},
          {"z": 1.640, "w": 0.255, "d": 0.215, "r": 0.084, "dy": -0.042}],
         "robe", bevel=0.005, parts=PARTS)
    ring("mage_collar_trim", 1.635, 1.672, 0.265, 0.225, 0.086, "gold",
         bevel=0.004, loc=(0.0, -0.042, 0.0), parts=PARTS)
    # Gold filigree curls down the robe's front edges and the mantle hem.
    for side in (-1, 1):
        for i, zz in enumerate((1.36, 1.20, 1.04)):
            loft("mage_filigree_%d_%d" % (side, i),
                 [{"z": -0.075, "w": 0.026, "d": 0.026, "r": 0.008, "dx": 0.0},
                  {"z": 0.075, "w": 0.026, "d": 0.026, "r": 0.008,
                   "dx": 0.030 * side}],
                 "gold", sides=3, bevel=0.004, loc=(0.190 * side, 0.215, zz),
                 rot=(0.0, 0.0, 14.0 * side), parts=PARTS)


def stage_belt():
    """Belt, buckle gem, hip pouches and the hanging tombstones... tomes."""
    ring("mage_belt", 1.115, 1.275, 0.420, 0.325, 0.112, "leather", sides=6,
         bevel=0.006, parts=PARTS)
    ring("mage_belt_trim_top", 1.275, 1.310, 0.435, 0.340, 0.114, "gold",
         sides=6, bevel=0.004, parts=PARTS)
    ring("mage_belt_trim_low", 1.080, 1.115, 0.435, 0.340, 0.114, "gold",
         sides=6, bevel=0.004, parts=PARTS)
    gem("mage_buckle_gem", 0.105, 0.185, "gem_glow", (0.0, 0.235, 1.195),
        parts=PARTS)
    for side in (-1, 1):
        shell("mage_pouch_%d" % side, 0.150, 0.190, "leather",
              (0.235 * side, 0.075, 1.115), rot=(0.0, 0.0, -22.0 * side), bow=0.045,
              taper=0.80, rows=3, cols=4, thickness=0.022, bevel=0.006,
              hem_mat="gold_dark", hem_frac=0.2, parts=PARTS)
        loft("mage_tome_%d" % side,
             [{"z": -0.095, "w": 0.085, "d": 0.170, "r": 0.012},
              {"z": 0.095, "w": 0.085, "d": 0.170, "r": 0.012}],
             "robe_dark", sides=2, bevel=0.004, parts=PARTS,
             loc=(0.235 * side, 0.170, 1.010), rot=(0.0, 0.0, 24.0 * side))
        loft("mage_tome_band_%d" % side,
             [{"z": -0.014, "w": 0.090, "d": 0.176, "r": 0.012},
              {"z": 0.014, "w": 0.090, "d": 0.176, "r": 0.012}],
             "gold", sides=2, bevel=0.003, parts=PARTS,
             loc=(0.235 * side, 0.170, 1.010), rot=(0.0, 0.0, 24.0 * side))


def stage_arms():
    """Wide sleeves, bronze bracers and the hands."""
    for side in (-1, 1):
        x = 0.265 * side
        loft("mage_sleeve_%d" % side,
             [{"z": 1.040, "w": 0.270, "d": 0.235, "r": 0.090, "dx": x + 0.055 * side},
              {"z": 1.260, "w": 0.225, "d": 0.200, "r": 0.078, "dx": x + 0.025 * side},
              {"z": 1.430, "w": 0.200, "d": 0.185, "r": 0.072, "dx": x + 0.005 * side},
              {"z": 1.520, "w": 0.215, "d": 0.196, "r": 0.076, "dx": x - 0.010 * side}],
             "robe", bevel=0.005, parts=PARTS)
        ring("mage_cuff_%d" % side, 1.020, 1.075, 0.285, 0.250, 0.094, "gold",
             sides=6, bevel=0.004, loc=(x + 0.055 * side, 0.0, 0.0), parts=PARTS)
        loft("mage_bracer_%d" % side,
             [{"z": 0.905, "w": 0.165, "d": 0.185, "r": 0.062, "dx": x + 0.062 * side},
              {"z": 1.030, "w": 0.175, "d": 0.195, "r": 0.065, "dx": x + 0.058 * side}],
             "bronze", bevel=0.006, parts=PARTS)
        ring("mage_bracer_trim_%d" % side, 0.890, 0.925, 0.178, 0.198, 0.066,
             "gold", sides=6, bevel=0.004, loc=(x + 0.062 * side, 0.0, 0.0),
             parts=PARTS)
        # Hands: the staff hand is closed, the free hand hangs open.
        ball("mage_hand_%d" % side, 0.078, "skin", (x + 0.070 * side, 0.030, 0.830),
             scale=(0.9, 1.05, 1.0), parts=PARTS)
        for finger in range(4):
            if side < 0:                      # gripping the staff
                spot = (x + 0.070 * side + 0.010, 0.095, 0.885 - finger * 0.038)
                scale = (1.0, 0.85, 0.9)
            else:                             # open, fingers slightly curled
                spot = (x + 0.070 * side + 0.045,
                        0.060 - finger * 0.004, 0.865 - finger * 0.042)
                scale = (0.85, 1.0, 1.0)
            ball("mage_finger_%d_%d" % (side, finger), 0.024, "skin", spot,
                 scale=scale, rings=6, sides=8, parts=PARTS)


def stage_head():
    """Grey hair, the long beard and a stern face."""
    loft("mage_neck",
         [{"z": 1.575, "w": 0.150, "d": 0.145, "r": 0.058, "dy": 0.012},
          {"z": 1.700, "w": 0.138, "d": 0.132, "r": 0.052, "dy": 0.014}],
         "skin", smooth=True, subsurf=1, parts=PARTS)
    loft("mage_head",
         [{"z": 1.662, "w": 0.084, "d": 0.102, "r": 0.040, "dy": 0.030},
          {"z": 1.712, "w": 0.118, "d": 0.134, "r": 0.051, "dy": 0.026},
          {"z": 1.768, "w": 0.140, "d": 0.158, "r": 0.060, "dy": 0.018},
          {"z": 1.826, "w": 0.142, "d": 0.158, "r": 0.060, "dy": 0.010},
          {"z": 1.872, "w": 0.112, "d": 0.126, "r": 0.048, "dy": 0.004}],
         "skin", smooth=True, subsurf=1, parts=PARTS)
    loft("mage_nose",
         [{"z": 1.776, "w": 0.030, "d": 0.058, "r": 0.012, "dy": 0.086},
          {"z": 1.808, "w": 0.024, "d": 0.046, "r": 0.010, "dy": 0.080}],
         "skin", smooth=True, subsurf=1, parts=PARTS)
    for side in (-1, 1):
        ball("mage_socket_%d" % side, 0.023, "skin_dark",
             (0.037 * side, 0.081, 1.810), scale=(1.30, 0.35, 0.72), rings=8,
             sides=12, parts=PARTS)
        ball("mage_eye_%d" % side, 0.012, "eye", (0.036 * side, 0.088, 1.808),
             scale=(1.40, 0.40, 0.72), rings=8, sides=12, parts=PARTS)
        loft("mage_brow_%d" % side,
             [{"z": -0.012, "w": 0.058, "d": 0.024, "r": 0.009, "dy": 0.092},
              {"z": 0.012, "w": 0.052, "d": 0.022, "r": 0.009, "dy": 0.086}],
             "hair_gray", sides=3, smooth=True, parts=PARTS,
             loc=(0.038 * side, 0.0, 1.834))
        ball("mage_ear_%d" % side, 0.030, "skin", (0.072 * side, 0.004, 1.786),
             scale=(0.45, 0.85, 1.05), parts=PARTS)
    # Long grey hair: crown, the mass falling to the collar behind, sides.
    dome("mage_hair_cap", 0.086, 0.098, 0.076, "hair_gray", (0.0, -0.010, 1.836),
         parts=PARTS)
    ball("mage_hair_back", 0.095, "hair_gray", (0.0, -0.070, 1.756),
         scale=(0.95, 0.85, 1.35), parts=PARTS)
    ball("mage_hair_nape", 0.070, "hair_gray", (0.0, -0.085, 1.640),
         scale=(1.05, 0.70, 1.10), parts=PARTS)
    dome("mage_hair_fringe", 0.082, 0.054, 0.050, "hair_gray", (0.0, 0.058, 1.848),
         rot=(30.0, 0.0, 0.0), parts=PARTS)
    for side in (-1, 1):
        ball("mage_hair_side_%d" % side, 0.056, "hair_gray",
             (0.068 * side, -0.006, 1.784), scale=(0.48, 0.95, 1.45), parts=PARTS)
    # Beard: long and squared off, hanging to the chest.
    loft("mage_beard",
         [{"z": 1.640, "w": 0.100, "d": 0.115, "r": 0.042, "dy": 0.034},
          {"z": 1.690, "w": 0.134, "d": 0.152, "r": 0.056, "dy": 0.032},
          {"z": 1.738, "w": 0.148, "d": 0.162, "r": 0.062, "dy": 0.024}],
         "beard_gray", smooth=True, subsurf=1, parts=PARTS)
    # The beard hangs over the chest, in front of the collar and the mantle.
    loft("mage_beard_long",
         [{"z": 1.545, "w": 0.078, "d": 0.095, "r": 0.032, "dy": 0.102},
          {"z": 1.605, "w": 0.094, "d": 0.108, "r": 0.038, "dy": 0.100},
          {"z": 1.658, "w": 0.106, "d": 0.120, "r": 0.045, "dy": 0.094}],
         "beard_gray", smooth=True, subsurf=1, parts=PARTS)
    for side in (-1, 1):
        ball("mage_beard_cheek_%d" % side, 0.048, "beard_gray",
             (0.064 * side, 0.026, 1.748), scale=(0.55, 1.05, 1.05), parts=PARTS)
    ball("mage_moustache", 0.038, "beard_gray", (0.0, 0.094, 1.770),
         scale=(1.35, 0.80, 0.45), parts=PARTS)


def stage_staff():
    """The staff: wooden shaft, bronze claws around a big glowing crystal."""
    ring("mage_staff_foot", 0.030, 0.090, 0.062, 0.062, 0.026, "bronze", sides=5,
         bevel=0.005, loc=(STAFF_X, STAFF_Y, 0.0), parts=PARTS)
    loft("mage_staff_shaft",
         [{"z": 0.060, "w": 0.042, "d": 0.042, "r": 0.019, "dx": STAFF_X},
          {"z": 0.500, "w": 0.040, "d": 0.040, "r": 0.019, "dx": STAFF_X + 0.006},
          {"z": 0.950, "w": 0.044, "d": 0.044, "r": 0.020, "dx": STAFF_X + 0.014},
          {"z": 1.380, "w": 0.046, "d": 0.046, "r": 0.021, "dx": STAFF_X + 0.018},
          {"z": 1.680, "w": 0.050, "d": 0.050, "r": 0.022, "dx": STAFF_X + 0.020}],
         "wood", sides=5, smooth=True, loc=(0.0, STAFF_Y, 0.0), parts=PARTS)
    for i, zz in enumerate((0.300, 0.760, 1.180, 1.560)):
        ring("mage_staff_band_%d" % i, zz, zz + 0.050, 0.058, 0.058, 0.024, "bronze",
             sides=5, bevel=0.004,
             loc=(STAFF_X + 0.004 * i, STAFF_Y, 0.0), parts=PARTS)
    # Three bronze claws curling up and around the crystal.
    for i in range(3):
        angle = 2.0 * math.pi * i / 3.0
        for step, (zz, reach, thick) in enumerate((
                (1.660, 0.055, 0.030), (1.790, 0.135, 0.026),
                (1.930, 0.150, 0.022), (2.050, 0.075, 0.016))):
            loft("mage_claw_%d_%d" % (i, step),
                 [{"z": -0.038, "w": thick * 1.55, "d": thick * 1.55, "r": thick * 0.6},
                  {"z": 0.038, "w": thick * 1.30, "d": thick * 1.30, "r": thick * 0.5}],
                 "bronze", sides=4, bevel=0.004, smooth=True, parts=PARTS,
                 loc=(STAFF_X + 0.02 + reach * math.cos(angle),
                      STAFF_Y + reach * math.sin(angle), zz))
    gem("mage_crystal", 0.175, 0.270, "gem_glow", (STAFF_X + 0.02, STAFF_Y, 1.935),
        parts=PARTS)
    gem("mage_crystal_low", 0.085, 0.165, "gem", (STAFF_X + 0.02, STAFF_Y, 1.735),
        parts=PARTS)
    # Floating sparks around the head of the staff.
    for i in range(7):
        angle = 2.0 * math.pi * i / 7.0
        distance = 0.150 + 0.035 * (i % 3)
        ball("mage_spark_%d" % i, 0.016, "gem_glow",
             (STAFF_X + 0.02 + distance * math.cos(angle),
              STAFF_Y + distance * math.sin(angle), 1.880 + 0.045 * ((i % 4) - 1.5)),
             rings=6, sides=8, parts=PARTS)


STAGES = ("legs", "robe", "torso", "belt", "arms", "head", "staff")
_STAGE_FUNCS = {
    "legs": stage_legs, "robe": stage_robe, "torso": stage_torso,
    "belt": stage_belt, "arms": stage_arms, "head": stage_head, "staff": stage_staff,
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

    # The review rig this script adds for its renders (lights and camera) has to
    # go too, or a rebuild finds it and refuses.
    review_rig = {"key", "fill", "rim", "MageReviewCam"}

    def ours(name):
        # The startup objects and anything this script made can go; a rebuild
        # has to clear the joined "Mage" object from the previous run.
        base = name.split(".")[0]
        return (base in defaults or base in review_rig
                or name.lower().startswith("mage"))

    strangers = [obj.name for obj in bpy.data.objects if not ours(obj.name)]
    if strangers:
        log("REFUSING to clear: unexpected objects %s" % ", ".join(strangers))
        return "kept %d object(s): %s" % (len(strangers), ", ".join(strangers))

    removed = [obj.name for obj in list(bpy.data.objects)]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for key in ("gem_glow", "gem", "sigil"):
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


def render_views(obj):
    os.makedirs(RENDER_DIR, exist_ok=True)
    scene = bpy.context.scene
    scene.render.resolution_x, scene.render.resolution_y = RES
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    log("render engine: " + KIT.light_scene(scene))

    cam_data = bpy.data.cameras.new("MageReviewCam")
    cam_data.type = "ORTHO"
    cam_data.sensor_fit = "VERTICAL"
    cam = bpy.data.objects.new("MageReviewCam", cam_data)
    bpy.context.collection.objects.link(cam)
    scene.camera = cam

    zs = [v.co.z for v in obj.data.vertices]
    xs = [v.co.x for v in obj.data.vertices]
    low, high = min(zs), max(zs)
    span = max(high - low, max(xs) - min(xs))
    focus = Vector((0.0, 0.0, (low + high) * 0.5))
    log("frame: z %.3f..%.3f (span %.3f)" % (low, high, span))

    views = (("front", 90.0, 6.0, None, None),
             ("threequarter", 128.0, 12.0, None, None),
             ("side", 0.0, 6.0, None, None),
             ("back", 270.0, 6.0, None, None),
             ("head", 100.0, 3.0, Vector((0.0, 0.0, 1.76)), 0.52),
             ("staff", 78.0, 8.0, Vector((STAFF_X, 0.0, 1.85)), 0.85))
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
        path = os.path.join(RENDER_DIR, "mage_%s.png" % name)
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        log("rendered " + path)
        shots.append(path)
    return shots


def finish(export=True, render=True):
    """Join, clean, unwrap, write the FBX and (optionally) render the views."""
    if not PARTS:
        raise RuntimeError("nothing built yet - call build(stage) first")
    mage = KIT.join_parts(PARTS, name="Mage")
    clean_mesh(mage)
    unwrap(mage)
    triangles = sum(len(poly.vertices) - 2 for poly in mage.data.polygons)
    height = max(v.co.z for v in mage.data.vertices)
    width = max(v.co.x for v in mage.data.vertices) - min(
        v.co.x for v in mage.data.vertices)
    log("mesh: %d triangles, %d faces, %d material slots, %.2f m tall, %.2f m wide"
        % (triangles, len(mage.data.polygons), len(mage.data.materials), height, width))
    if export:
        save_fbx = KIT.FBX_OUT
        KIT.FBX_OUT = FBX_OUT
        try:
            KIT.export(mage)
        finally:
            KIT.FBX_OUT = save_fbx
    shots = render_views(mage) if render else []
    try:
        bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT, copy=True)
        log("saved " + BLEND_OUT)
    except Exception as exc:
        log("blend save skipped (%s)" % exc)
    return "Mage: %d triangles, %.2f m tall, %d slots, %d views" % (
        triangles, height, len(mage.data.materials), len(shots))


def main():
    KIT.reset_scene()
    prepare()
    for stage in STAGES:
        build(stage)
    log(finish())


if __name__ == "__main__":
    main()
