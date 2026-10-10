"""Build the Paladin (blue-and-gold plate knight) from the reference sheet.

Run headless:
    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/build_paladin.py

Outputs:
    Assets/Resources/Arena/Fighters/vanguard.fbx    skinned, feet at z=0, facing +Y in Blender
    Assets/Resources/Arena/Fighters/vanguard_shield.fbx  the lion shield on its own, for
                                                    the arm and for the throw

    Tools/Blender/renders/paladin_*.png             review views incl. a posed one
    Tools/Blender/renders/review.html               self-contained contact sheet
    Tools/Blender/paladin_review.blend              the lit review scene

The knight is the game's Vanguard, so the FBX ships under that name; the
script, the renders and the review scene keep calling it the paladin.

Shape language: every volume is a *loft* - a stack of rounded-rectangle profiles -
so limbs taper, armour bulges and cloth hangs instead of reading as stacked boxes.
Smooth volumes get a subdivision level, plate keeps crisp edges from a bevel, and
the signature details (lion pauldrons, tabard cross, lion shield, held sword) are
built as their own parts.

Blender is Z-up and the knight faces +Y; the FBX exporter converts to Unity's Y-up
with the character facing +Z (the game's forward).

Landmarks (metres, feet on z=0), read off the reference against a 1.95 m figure:
    feet 0.00  knee 0.64  tabard hem 0.55  belt 1.24  chest 1.46
    shoulder 1.58  pauldron top 1.71  chin 1.68  crown 1.90  hair 1.95
"""

import importlib.util
import math
import os
import sys

import bmesh
import bpy
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
FBX_OUT = os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters", "vanguard.fbx")
RENDER_DIR = os.path.join(HERE, "renders")
BLEND_OUT = os.path.join(HERE, "paladin_review.blend")
RES = (700, 980)

# ------------------------------------------------------------------------- config

HEIGHT = 1.95          # metres to the top of the hair
LEG_X = 0.145          # leg centreline
ARM_X = 0.270          # arm centreline at the elbow
PLATE = 0.014          # default armour thickness for solidified shells


def load_rig():
    """Import rig_kit.py, the shared armature/skinning module."""
    path = os.path.join(HERE, "rig_kit.py")
    spec = importlib.util.spec_from_file_location("rig_kit", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_prop_kit():
    """Import prop_kit.py, which cuts the hand props out and exports them."""
    path = os.path.join(HERE, "prop_kit.py")
    spec = importlib.util.spec_from_file_location("prop_kit", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


RIG = load_rig()
PROP_KIT = load_prop_kit()

# The lion shield is thrown in the game, so it cannot stay welded into the body: it
# ships as its own prop, with a copy in the left forearm's frame (what the arm
# wears) and a copy in a throwable frame (what flies). See prop_kit.py.
SHIELD = PROP_KIT.Prop(
    prefix="shield_", bone="forearm.L", name="Shield",
    path=os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters",
                      "vanguard_shield.fbx"))
PROPS = (SHIELD,)

# Where the knight's joints sit, read off the same numbers the build is made from:
# the belt at 1.16-1.26 for the pelvis, the pauldron domes at 1.45 for the
# shoulders, the elbow cop at 1.19, the gauntlet at 0.86-0.96 for the wrist and
# the poleyn at 0.545 for the knee. The arms drift outwards as they come down
# (the fist sits at ARM_X + 0.065), which is why the rig takes a single arm line.
RIG_LANDMARKS = RIG.landmarks(
    hip_z=1.015, spine_z=1.155, chest_z=1.355, neck_z=1.600, head_z=1.680,
    head_top=1.910, shoulder_x=0.070, shoulder_out=0.228, shoulder_z=1.448,
    arm_x=0.262, elbow_z=1.190, wrist_z=0.940, hand_end_z=0.800, leg_x=LEG_X,
    knee_z=0.600, ankle_z=0.150, toe_y=0.160, toe_end_y=0.250, ground=0.035)

# Palette lifted from the reference: white steel, gold trim, royal blue cloth,
# brown leather and the warm skin/hair trio.
COLORS = {
    "steel":       (0.630, 0.660, 0.720),
    "steel_dark":  (0.330, 0.365, 0.430),
    "steel_worn":  (0.470, 0.500, 0.560),
    "gold":        (0.900, 0.690, 0.230),
    "gold_dark":   (0.600, 0.420, 0.115),
    "gold_bright": (0.970, 0.830, 0.430),
    "cloth":       (0.135, 0.245, 0.640),
    "cloth_dark":  (0.070, 0.130, 0.400),
    "leather":     (0.300, 0.200, 0.125),
    "skin":        (0.820, 0.615, 0.500),
    "skin_dark":   (0.440, 0.290, 0.235),
    "hair":        (0.270, 0.180, 0.120),
    "eye":         (0.085, 0.070, 0.060),
}
METAL = {"steel": 0.85, "steel_dark": 0.82, "steel_worn": 0.82, "gold": 0.95,
         "gold_dark": 0.95, "gold_bright": 0.95}
# Roughness stays high enough that the metals catch broad highlights instead of
# mirroring the world: a mirror finish turned the flat gold crosses olive brown,
# and a low roughness plus a bright rig washed the gold out to pale grey.
ROUGH = {"steel": 0.40, "steel_dark": 0.46, "steel_worn": 0.46, "gold": 0.36,
         "gold_dark": 0.44, "gold_bright": 0.32,
         "cloth": 0.78, "cloth_dark": 0.84, "leather": 0.66,
         "skin": 0.58, "skin_dark": 0.78, "hair": 0.70, "eye": 0.30}


def log(message):
    print("[paladin] " + message)
    sys.stdout.flush()


# --------------------------------------------------------------------- scene setup

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def material(key):
    """One flat material per palette entry: the FBX exporter carries the base
    colour and the PBR channels into Unity, and the review render shows it."""
    name = "PL_" + key
    if name in bpy.data.materials:
        return bpy.data.materials[name]
    mat = bpy.data.materials.new(name)
    color = COLORS[key]
    mat.diffuse_color = (color[0], color[1], color[2], 1.0)
    mat.metallic = METAL.get(key, 0.0)
    mat.roughness = ROUGH.get(key, 0.6)
    mat.use_nodes = True
    for node in mat.node_tree.nodes:
        if node.type != "BSDF_PRINCIPLED":
            continue
        for slot, value in (("Base Color", (color[0], color[1], color[2], 1.0)),
                            ("Metallic", METAL.get(key, 0.0)),
                            ("Roughness", ROUGH.get(key, 0.6))):
            if node.inputs.get(slot) is not None:
                node.inputs[slot].default_value = value
    return mat


# ------------------------------------------------------------------ mesh machinery

def transform(verts, loc, rot, scale):
    """Bake the placement into the vertices.

    Every object then has an identity transform, so measuring or joining can never
    read a stale matrix_world (which is how the previous pass ended up buried
    0.76 m under the floor and blamed the geometry)."""
    matrix = Euler((math.radians(rot[0]), math.radians(rot[1]), math.radians(rot[2])),
                   "XYZ").to_matrix().to_4x4()
    size = Vector(scale)
    out = []
    for v in verts:
        point = Vector((v[0] * size.x, v[1] * size.y, v[2] * size.z))
        out.append(tuple(matrix @ point + Vector(loc)))
    return out


def add_modifier(obj, kind, **settings):
    try:
        bpy.context.view_layer.objects.active = obj
        mod = obj.modifiers.new(kind.lower(), kind)
        for key, value in settings.items():
            setattr(mod, key, value)
        bpy.ops.object.modifier_apply(modifier=mod.name)
    except Exception as exc:
        log("modifier %s skipped on %s (%s)" % (kind, obj.name, exc))
        try:
            obj.modifiers.clear()
        except Exception:
            pass


def finish(name, verts, faces, mat_key, loc=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0),
           scale=(1.0, 1.0, 1.0), smooth=False, bevel=0.0, subsurf=0, solidify=0.0,
           hem_mat=None, hem_frac=0.0, rim_mat=None, rim_cells=None, parts=None):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(transform(verts, loc, rot, scale), [], faces)
    mesh.validate()
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(material(mat_key))

    if hem_mat is not None:
        # A second slot for the bottom band of a panel (the gold hem on cloth).
        obj.data.materials.append(material(hem_mat))
        low = min(v.co.z for v in obj.data.vertices)
        cut = low + hem_frac * (max(v.co.z for v in obj.data.vertices) - low)
        for poly in obj.data.polygons:
            if min(obj.data.vertices[i].co.z for i in poly.vertices) <= cut + 1e-6:
                poly.material_index = 1
    if rim_mat is not None:
        # Outer ring of a grid gets its own slot: the raised gold border on the
        # shield, where a separate bar object would float off the curved face.
        obj.data.materials.append(material(rim_mat))
        index = len(obj.data.materials) - 1
        for poly in obj.data.polygons:
            if poly.material_index != 0:
                continue
            for corner in poly.vertices:
                if corner in rim_cells:
                    poly.material_index = index
                    break

    if solidify > 0.0:
        add_modifier(obj, "SOLIDIFY", thickness=solidify, offset=0.0)
    if subsurf > 0:
        add_modifier(obj, "SUBSURF", levels=subsurf, render_levels=subsurf)
    if bevel > 0.0:
        add_modifier(obj, "BEVEL", width=bevel, segments=2, limit_method="ANGLE",
                     angle_limit=math.radians(35.0))
    if smooth:
        for poly in obj.data.polygons:
            poly.use_smooth = True
    if parts is not None:
        parts.append(obj)
    return obj


def rounded_profile(w, d, r, n=5):
    """A rounded rectangle as a closed point loop in the XY plane.

    r=0 gives a rectangle, r=min(w,d)/2 gives an ellipse, anything between gives
    the soft-cornered slab that plate armour actually is."""
    hx, hy = w * 0.5, d * 0.5
    r = min(r, hx * 0.999, hy * 0.999)
    points = []
    for cx, cy, start in ((hx - r, hy - r, 0.0), (-(hx - r), hy - r, 90.0),
                          (-(hx - r), -(hy - r), 180.0), (hx - r, -(hy - r), 270.0)):
        for i in range(n):
            angle = math.radians(start + 90.0 * i / n)
            points.append((cx + r * math.cos(angle), cy + r * math.sin(angle)))
    return points


def loft(name, stations, mat_key, sides=5, close=True, smooth=False, bevel=0.0,
         subsurf=0, cap=True, parts=None, **place):
    """Stack of profiles along Z. Each station is dict(z=, w=, d=, r=, dx=, dy=)."""
    verts, faces = [], []
    ring_size = None
    for station in stations:
        profile = rounded_profile(station["w"], station["d"], station.get("r", 0.0), sides)
        ring_size = len(profile)
        base = len(verts)
        for x, y in profile:
            verts.append((x + station.get("dx", 0.0), y + station.get("dy", 0.0),
                          station["z"]))
    for ring in range(len(stations) - 1):
        start = ring * ring_size
        nxt = start + ring_size
        for i in range(ring_size):
            j = (i + 1) % ring_size
            faces.append((start + i, start + j, nxt + j, nxt + i))
    if cap:
        faces.append(tuple(range(ring_size - 1, -1, -1)))
        last = (len(stations) - 1) * ring_size
        faces.append(tuple(range(last, last + ring_size)))
    return finish(name, verts, faces, mat_key, smooth=smooth, bevel=bevel,
                  subsurf=subsurf, parts=parts, **place)


def dome(name, rx, ry, rz, mat_key, loc, rot=(0.0, 0.0, 0.0), scale=(1.0, 1.0, 1.0),
         rings=6, sides=20, smooth=True, parts=None):
    """Half ellipsoid sitting on its own z=0 plane: pauldron crowns, hair, cranium."""
    verts, faces = [], []
    for ring in range(rings + 1):
        a = (math.pi * 0.5) * ring / rings
        z = rz * math.sin(a)
        shrink = math.cos(a)
        for s in range(sides):
            theta = 2.0 * math.pi * s / sides
            verts.append((rx * shrink * math.cos(theta), ry * shrink * math.sin(theta), z))
    for ring in range(rings):
        start = ring * sides
        nxt = start + sides
        for s in range(sides):
            t = (s + 1) % sides
            faces.append((start + s, start + t, nxt + t, nxt + s))
    faces.append(tuple(range(sides - 1, -1, -1)))
    return finish(name, verts, faces, mat_key, loc=loc, rot=rot, scale=scale,
                  smooth=smooth, parts=parts)


def ball(name, r, mat_key, loc, scale=(1.0, 1.0, 1.0), rot=(0.0, 0.0, 0.0), rings=10,
         sides=18, smooth=True, parts=None):
    verts, faces = [], []
    for ring in range(rings + 1):
        phi = math.pi * ring / rings
        for s in range(sides):
            theta = 2.0 * math.pi * s / sides
            verts.append((r * math.sin(phi) * math.cos(theta),
                          r * math.sin(phi) * math.sin(theta),
                          -r * math.cos(phi)))
    for ring in range(rings):
        start = ring * sides
        nxt = start + sides
        for s in range(sides):
            t = (s + 1) % sides
            faces.append((start + s, start + t, nxt + t, nxt + s))
    return finish(name, verts, faces, mat_key, loc=loc, scale=scale, rot=rot,
                  smooth=smooth, parts=parts)


def shell(name, w, h, mat_key, loc, rot=(0.0, 0.0, 0.0), bow=0.04, taper=1.0,
          rows=6, cols=6, thickness=PLATE, bevel=0.006, dome_top=0.0, tip=0.0,
          rim_mat=None, rim_rows=1, rim_cols=1, parts=None, curve=0.0, wave=0.0,
          hem_mat=None, hem_frac=0.1, subsurf=0):
    """A curved plate or cloth panel: a quad grid bowed forward, then solidified.

    The top edge starts at local z=0 and the panel hangs to z=-h. `taper` narrows
    the hem, `tip` collapses the last row to a point (shield), `dome_top` arcs the
    top edge (shield), `curve`/`wave` bend the cloth away from and across the body.
    """
    verts, faces = [], []
    rim_verts = set()
    for row in range(rows + 1):
        v = row / rows
        scale = 1.0 - (1.0 - taper) * v
        if tip > 0.0 and v > 0.55:
            scale *= max(0.06, 1.0 - tip * ((v - 0.55) / 0.45) ** 1.25)
        z = -h * v + (curve * v * v)
        y = bow * (1.0 - (v - 0.35) ** 2) + wave * math.sin(v * math.pi) * 0.0
        for col in range(cols + 1):
            u = col / cols
            ux = 2.0 * u - 1.0
            width = w * 0.5 * scale
            top = 0.0
            if dome_top != 0.0 and v < 0.5:
                top = dome_top * math.sqrt(max(0.0, 1.0 - (ux / max(0.3, 0.55)) ** 2)) \
                    * (1.0 - v / 0.5)
            verts.append((ux * width, y + bow * (1.0 - ux * ux) * 0.6, z + top))
            if row in (0, rows) or col in (0, cols):
                rim_verts.add(len(verts) - 1)
    for row in range(rows):
        for col in range(cols):
            a = row * (cols + 1) + col
            faces.append((a, a + 1, a + cols + 2, a + cols + 1))
    cells = set()
    for row in range(rows):
        for col in range(cols):
            if row < rim_rows or row >= rows - rim_rows or col < rim_cols \
                    or col >= cols - rim_cols:
                cells.add(row * (cols + 1) + col)
    return finish(name, verts, faces, mat_key, loc=loc, rot=rot, solidify=thickness,
                  bevel=bevel, subsurf=subsurf, rim_mat=rim_mat, rim_cells=rim_verts,
                  hem_mat=hem_mat, hem_frac=hem_frac, parts=parts)


def cross(name, height, bar, thickness, mat_key="gold_bright", loc=(0.0, 0.0, 0.0),
          rot=(0.0, 0.0, 0.0), depth=0.022, parts=None):
    """Latin cross facing +Y: the emblem on the chest, tabard, knees and discs."""
    wide = bar * 2.4
    stem = loft(name + "_stem",
                [{"z": -height * 0.5, "w": bar, "d": depth, "r": 0.004},
                 {"z": height * 0.5, "w": bar * 0.9, "d": depth, "r": 0.004}],
                mat_key, sides=2, bevel=thickness, parts=parts)
    arms = loft(name + "_arms",
                [{"z": -thickness, "w": wide, "d": depth, "r": 0.004},
                 {"z": thickness, "w": wide, "d": depth, "r": 0.004}],
                mat_key, sides=2, bevel=thickness, parts=parts)
    for part, shift in ((stem, (0.0, 0.0, 0.0)), (arms, (0.0, 0.0, height * 0.10))):
        matrix = Euler((math.radians(rot[0]), math.radians(rot[1]), math.radians(rot[2])),
                       "XYZ").to_matrix()
        for vertex in part.data.vertices:
            vertex.co += matrix @ Vector(shift)
        for vertex in part.data.vertices:
            vertex.co = matrix @ vertex.co + Vector(loc)
    return [stem, arms]


def lion(name, size, mat_key, loc, rot=(0.0, 0.0, 0.0), parts=None):
    """A stylized lion head facing +Y, `size` metres across.

    The emblem the reference repeats on both pauldrons and on the shield: a ring of
    short overlapping mane lobes, a face plate, a muzzle with a nose, brows and
    dark eyes. The mane lobes are flattened along their radius and angled outward,
    because tall petals read as a golden crown instead of a mane."""
    out = []
    for i in range(12):
        phi = 2.0 * math.pi * i / 12
        ox, oz = math.sin(phi), math.cos(phi)
        out.append(ball("%s_mane_%d" % (name, i), size * 0.16, mat_key,
                        (loc[0] + ox * size * 0.27, loc[1] + size * 0.02,
                         loc[2] + oz * size * 0.27),
                        scale=(0.62, 0.70, 0.72), rot=(0.0, math.degrees(phi), 0.0),
                        rings=7, sides=10, parts=parts))
    out.append(dome(name + "_face", size * 0.31, size * 0.31, size * 0.22, mat_key,
                    (loc[0], loc[1] + size * 0.05, loc[2]),
                    rot=(-90.0, 0.0, 0.0), scale=(1.0, 1.0, 1.0), parts=parts))
    out.append(loft(name + "_muzzle",
                    [{"z": 0.0, "w": size * 0.30, "d": size * 0.24, "r": size * 0.11,
                      "dy": size * 0.16},
                     {"z": -size * 0.10, "w": size * 0.26, "d": size * 0.20,
                      "r": size * 0.09, "dy": size * 0.18}],
                    mat_key, sides=4, smooth=True, parts=parts,
                    loc=(loc[0], loc[1], loc[2] - size * 0.06)))
    out.append(ball(name + "_nose", size * 0.07, "gold_dark",
                    (loc[0], loc[1] + size * 0.30, loc[2] + size * 0.01),
                    scale=(1.5, 0.9, 0.9), parts=parts))
    for side in (-1, 1):
        out.append(ball(name + "_eye_%d" % side, size * 0.055, "eye",
                        (loc[0] + side * size * 0.115, loc[1] + size * 0.21,
                         loc[2] + size * 0.09), scale=(1.1, 0.7, 0.9), parts=parts))
        out.append(loft(name + "_brow_%d" % side,
                        [{"z": -size * 0.03, "w": size * 0.15, "d": size * 0.09,
                          "r": size * 0.035},
                         {"z": size * 0.03, "w": size * 0.13, "d": size * 0.08,
                          "r": size * 0.035}],
                        "gold_bright", sides=3, smooth=True, parts=parts,
                        loc=(loc[0] + side * size * 0.13, loc[1] + size * 0.20,
                             loc[2] + size * 0.17),
                        rot=(0.0, 0.0, -14.0 * side)))
    return out


def sword(name, parts=None):
    """The knight's greatsword, built along +Z with the grip at the origin.

    Local origin is the middle of the grip; the blade runs down to -z. Returns the
    parts, then the caller places and rotates the whole assembly into the fist."""
    out = []
    out.append(ball(name + "_pommel", 0.040, "gold", (0.0, 0.0, 0.115),
                    scale=(1.0, 1.0, 0.85), parts=parts))
    out.append(loft(name + "_grip",
                    [{"z": -0.075, "w": 0.048, "d": 0.048, "r": 0.022},
                     {"z": 0.0, "w": 0.052, "d": 0.052, "r": 0.024},
                     {"z": 0.085, "w": 0.048, "d": 0.048, "r": 0.022}],
                    "leather", sides=4, smooth=True, parts=parts))
    for i in range(6):                      # grip wrap rings
        out.append(loft(name + "_wrap_%d" % i,
                        [{"z": -0.062 + i * 0.030, "w": 0.058, "d": 0.058, "r": 0.026},
                         {"z": -0.050 + i * 0.030, "w": 0.058, "d": 0.058, "r": 0.026}],
                        "leather", sides=4, bevel=0.004, parts=parts))
    out.append(loft(name + "_guard",
                    [{"z": -0.130, "w": 0.265, "d": 0.058, "r": 0.022},
                     {"z": -0.100, "w": 0.205, "d": 0.054, "r": 0.020},
                     {"z": -0.074, "w": 0.145, "d": 0.048, "r": 0.018}],
                    "gold", sides=4, bevel=0.005, parts=parts))
    out.append(loft(name + "_guard_gem",
                    [{"z": -0.125, "w": 0.075, "d": 0.030, "r": 0.014},
                     {"z": -0.085, "w": 0.075, "d": 0.030, "r": 0.014}],
                    "cloth", sides=4, rot=(0.0, 0.0, 0.0), bevel=0.006, parts=parts))
    out.append(loft(name + "_blade",
                    [{"z": -0.125, "w": 0.140, "d": 0.040, "r": 0.013},
                     {"z": -0.380, "w": 0.135, "d": 0.038, "r": 0.013},
                     {"z": -0.600, "w": 0.112, "d": 0.032, "r": 0.011},
                     {"z": -0.700, "w": 0.060, "d": 0.022, "r": 0.008},
                     {"z": -0.775, "w": 0.014, "d": 0.009, "r": 0.003}],
                    "steel", sides=3, bevel=0.004, parts=parts))
    out.append(loft(name + "_fuller",
                    [{"z": -0.15, "w": 0.028, "d": 0.044, "r": 0.011},
                     {"z": -0.62, "w": 0.022, "d": 0.038, "r": 0.010}],
                    "gold", sides=3, bevel=0.003, parts=parts))
    return out


def place_all(objs, loc, rot):
    """Rotate an assembly about its own origin, then translate it into place."""
    matrix = Euler((math.radians(rot[0]), math.radians(rot[1]), math.radians(rot[2])),
                   "XYZ").to_matrix()
    for obj in objs:
        for vertex in obj.data.vertices:
            vertex.co = matrix @ vertex.co + Vector(loc)
    return objs


# ------------------------------------------------------------------------ the knight

def build():
    parts = []

    # ---- legs: cuisse, poleyn with a gold cross, greave, sabaton -------------
    for side in (-1, 1):
        x = LEG_X * side
        loft("thigh_%d" % side,
             [{"z": 0.98, "w": 0.250, "d": 0.290, "r": 0.075, "dx": -0.012 * side},
              {"z": 0.86, "w": 0.245, "d": 0.285, "r": 0.075},
              {"z": 0.72, "w": 0.222, "d": 0.262, "r": 0.068},
              {"z": 0.62, "w": 0.205, "d": 0.245, "r": 0.062}],
             "steel_dark", loc=(x, 0.0, 0.0), bevel=0.006, parts=parts)
        # Overlapping cuisse lames: rings around the thigh rather than plates, so
        # they wrap the leg instead of sticking out beside it like fins.
        for i in range(2):
            loft("cuisse_lame_%d_%d" % (side, i),
                 [{"z": 0.875 - i * 0.135, "w": 0.268 - i * 0.016,
                   "d": 0.308 - i * 0.016, "r": 0.082},
                  {"z": 0.945 - i * 0.135, "w": 0.272 - i * 0.016,
                   "d": 0.312 - i * 0.016, "r": 0.082}],
                 "steel", sides=5, bevel=0.006, loc=(x, 0.0, 0.0), parts=parts)
        shell("cuisse_front_%d" % side, 0.21, 0.24, "steel", (x, 0.03, 0.93),
              bow=0.058, taper=0.86, rows=5, cols=5, thickness=0.014, bevel=0.005,
              parts=parts)
        body = dome("poleyn_%d" % side, 0.115, 0.115, 0.125, "steel",
                    (x, 0.02, 0.545), rot=(0.0, 0.0, 0.0), scale=(1.0, 1.0, 1.0),
                    parts=parts)
        del body
        shell("poleyn_lip_%d" % side, 0.20, 0.075, "steel", (x, 0.02, 0.60),
              bow=0.07, taper=0.9, rows=2, cols=4, thickness=0.014, bevel=0.005,
              parts=parts)
        cross("knee_cross_%d" % side, 0.115, 0.022, 0.010, "gold_bright",
              (x, 0.135, 0.575), parts=parts)
        loft("greave_%d" % side,
             [{"z": 0.56, "w": 0.200, "d": 0.235, "r": 0.060},
              {"z": 0.44, "w": 0.198, "d": 0.240, "r": 0.060, "dy": 0.006},
              {"z": 0.30, "w": 0.185, "d": 0.220, "r": 0.056},
              {"z": 0.17, "w": 0.145, "d": 0.175, "r": 0.048}],
             "steel", loc=(x, 0.0, 0.0), bevel=0.006, parts=parts)
        shell("greave_shin_%d" % side, 0.175, 0.30, "steel_worn", (x, 0.03, 0.51),
              bow=0.055, taper=0.88, rows=5, cols=5, thickness=0.012, bevel=0.005,
              parts=parts)
        loft("shin_ridge_%d" % side,
             [{"z": 0.235, "w": 0.042, "d": 0.042, "r": 0.016, "dx": x, "dy": 0.112},
              {"z": 0.470, "w": 0.042, "d": 0.042, "r": 0.016, "dx": x, "dy": 0.100}],
             "gold", sides=3, bevel=0.004, parts=parts)
        loft("cuisse_trim_%d" % side,
             [{"z": 0.935, "w": 0.258, "d": 0.298, "r": 0.078},
              {"z": 0.965, "w": 0.258, "d": 0.298, "r": 0.078}],
             "gold_dark", sides=5, bevel=0.005, loc=(x, 0.0, 0.0), parts=parts)
        loft("greave_band_%d" % side,
             [{"z": 0.505, "w": 0.212, "d": 0.248, "r": 0.062},
              {"z": 0.545, "w": 0.212, "d": 0.248, "r": 0.062}],
             "gold", sides=5, loc=(x, 0.0, 0.0), bevel=0.005, parts=parts)
        # Sabaton: ankle, then the pointed steel foot with a gold toe cap.
        loft("sabaton_%d" % side,
             [{"z": 0.175, "w": 0.155, "d": 0.185, "r": 0.05},
              {"z": 0.115, "w": 0.170, "d": 0.215, "r": 0.055},
              {"z": 0.055, "w": 0.185, "d": 0.245, "r": 0.06, "dy": 0.02}],
             "steel_dark", loc=(x, 0.0, 0.0), bevel=0.005, parts=parts)
        loft("boot_%d" % side,
             [{"z": 0.0, "w": 0.185, "d": 0.330, "r": 0.055, "dy": 0.055},
              {"z": 0.075, "w": 0.190, "d": 0.315, "r": 0.058, "dy": 0.05},
              {"z": 0.125, "w": 0.170, "d": 0.215, "r": 0.05, "dy": 0.02}],
             "steel", loc=(x, 0.0, 0.0), bevel=0.006, parts=parts)
        loft("boot_toe_%d" % side,
             [{"z": 0.0, "w": 0.175, "d": 0.105, "r": 0.04, "dy": 0.175},
              {"z": 0.07, "w": 0.150, "d": 0.090, "r": 0.035, "dy": 0.16}],
             "gold", loc=(x, 0.0, 0.0), bevel=0.005, parts=parts)
        loft("boot_heel_%d" % side,
             [{"z": 0.0, "w": 0.170, "d": 0.085, "r": 0.035, "dy": -0.075},
              {"z": 0.055, "w": 0.150, "d": 0.075, "r": 0.03, "dy": -0.07}],
             "gold_dark", loc=(x, 0.0, 0.0), bevel=0.005, parts=parts)
        loft("boot_plate_%d" % side,
             [{"z": 0.02, "w": 0.195, "d": 0.245, "r": 0.06, "dy": 0.04},
              {"z": 0.055, "w": 0.20, "d": 0.235, "r": 0.06, "dy": 0.04}],
             "gold_dark", loc=(x, 0.0, 0.0), bevel=0.004, parts=parts)

    # ---- hips, hanging lames, belt ------------------------------------------
    loft("pelvis",
         [{"z": 0.92, "w": 0.400, "d": 0.290, "r": 0.11},
          {"z": 1.02, "w": 0.435, "d": 0.305, "r": 0.115},
          {"z": 1.13, "w": 0.420, "d": 0.300, "r": 0.11}],
         "steel_dark", bevel=0.006, parts=parts)
    for side in (-1, 1):
        for i in range(3):
            shell("skirt_%d_%d" % (side, i), 0.215 - i * 0.012, 0.135,
                  "steel", (0.208 * side, 0.0, 1.085 - i * 0.105),
                  rot=(0.0, -6.0 * side, 0.0), bow=0.075, taper=0.93, rows=3, cols=4,
                  thickness=0.013, bevel=0.005, rim_mat="gold_dark", parts=parts)
        # The blue under-layer the reference shows between the cuisse and the
        # greave: without it the legs read as bare steel columns.
        shell("underskirt_%d" % side, 0.20, 0.32, "cloth", (0.225 * side, 0.0, 0.97),
              rot=(0.0, -10.0 * side, 0.0), bow=0.085, taper=0.90, rows=5, cols=4,
              thickness=0.012, bevel=0.004, hem_mat="gold_dark", hem_frac=0.12,
              parts=parts, subsurf=1)
    shell("skirt_front", 0.30, 0.20, "steel", (0.0, 0.03, 1.10), bow=0.045, taper=0.92,
          rows=4, cols=5, thickness=0.013, bevel=0.005, rim_mat="gold_dark", parts=parts)
    loft("belt",
         [{"z": 1.155, "w": 0.455, "d": 0.330, "r": 0.10},
          {"z": 1.205, "w": 0.465, "d": 0.340, "r": 0.10},
          {"z": 1.255, "w": 0.450, "d": 0.330, "r": 0.10}],
         "leather", bevel=0.006, parts=parts)
    loft("belt_edge_top",
         [{"z": 1.255, "w": 0.462, "d": 0.342, "r": 0.10},
          {"z": 1.275, "w": 0.462, "d": 0.342, "r": 0.10}],
         "gold_dark", sides=5, bevel=0.004, parts=parts)
    loft("belt_edge_low",
         [{"z": 1.135, "w": 0.462, "d": 0.342, "r": 0.10},
          {"z": 1.155, "w": 0.462, "d": 0.342, "r": 0.10}],
         "gold_dark", sides=5, bevel=0.004, parts=parts)
    shell("buckle", 0.175, 0.155, "gold", (0.0, 0.145, 1.245), bow=0.03, taper=0.72,
          rows=3, cols=4, thickness=0.02, bevel=0.006, parts=parts)
    ball("buckle_gem", 0.038, "cloth", (0.0, 0.205, 1.185), scale=(1.0, 0.7, 1.0),
         parts=parts)
    for side in (-1, 1):
        loft("strap_%d" % side,
             [{"z": 1.02, "w": 0.05, "d": 0.05, "r": 0.02, "dx": 0.19 * side,
               "dy": 0.135},
              {"z": 1.20, "w": 0.05, "d": 0.05, "r": 0.02, "dx": 0.19 * side,
               "dy": 0.135}],
             "leather", sides=4, bevel=0.004, parts=parts)

    # ---- torso: cuirass with the blue tabard over the chest -------------------
    loft("cuirass",
         [{"z": 1.14, "w": 0.415, "d": 0.300, "r": 0.105},
          {"z": 1.30, "w": 0.400, "d": 0.290, "r": 0.10},
          {"z": 1.44, "w": 0.485, "d": 0.350, "r": 0.12},
          {"z": 1.545, "w": 0.455, "d": 0.330, "r": 0.12},
          {"z": 1.615, "w": 0.375, "d": 0.275, "r": 0.105}],
         "steel", bevel=0.006, parts=parts)
    loft("cuirass_low",
         [{"z": 1.08, "w": 0.400, "d": 0.295, "r": 0.10},
          {"z": 1.16, "w": 0.418, "d": 0.305, "r": 0.105}],
         "steel_worn", bevel=0.01, parts=parts)
    for i in range(3):
        loft("cuirass_lame_%d" % i,
             [{"z": 1.175 + i * 0.048, "w": 0.428 - i * 0.006, "d": 0.315, "r": 0.105},
              {"z": 1.205 + i * 0.048, "w": 0.428 - i * 0.006, "d": 0.315, "r": 0.105}],
             "gold_dark", sides=5, bevel=0.005, parts=parts)
    # Gold filigree chevrons on the exposed steel either side of the tabard.
    for side in (-1, 1):
        for i, (zz, tilt) in enumerate(((1.30, 38.0), (1.43, -30.0))):
            loft("filigree_%d_%d" % (side, i),
                 [{"z": -0.085, "w": 0.030, "d": 0.030, "r": 0.010},
                  {"z": 0.085, "w": 0.030, "d": 0.030, "r": 0.010}],
                 "gold", sides=3, bevel=0.004, parts=parts,
                 loc=(0.208 * side, 0.175, zz), rot=(72.0, 0.0, tilt * side))
    # Tabard: a narrow blue column from the collar down past the knees. It stays
    # narrower than the cuirass so the steel and its gold filigree still read, and
    # the crosses sit proud of the cloth instead of sinking into it.
    shell("tabard", 0.245, 1.06, "cloth", (0.0, 0.175, 1.625), bow=0.045, taper=0.80,
          rows=12, cols=6, thickness=0.012, bevel=0.004, curve=-0.05,
          hem_mat="gold_bright", hem_frac=0.05, parts=parts, subsurf=1)
    for side in (-1, 1):
        loft("tabard_piping_%d" % side,
             [{"z": -1.04, "w": 0.024, "d": 0.024, "r": 0.008},
              {"z": 0.0, "w": 0.024, "d": 0.024, "r": 0.008}],
             "gold", sides=3, bevel=0.004, parts=parts,
             loc=(0.108 * side, 0.235, 1.62))
    cross("tabard_cross", 0.30, 0.070, 0.014, "gold", (0.0, 0.278, 1.420),
          parts=parts, depth=0.030)
    cross("tabard_cross_low", 0.22, 0.050, 0.012, "gold", (0.0, 0.262, 0.80),
          parts=parts, depth=0.026)
    shell("tabard_back", 0.200, 0.50, "cloth_dark", (0.0, -0.195, 1.30), bow=0.02,
          taper=0.84, rows=6, cols=5, thickness=0.012, bevel=0.004, curve=-0.05,
          hem_mat="gold_dark", hem_frac=0.07, parts=parts, subsurf=1)

    # ---- cape: two panels falling behind the shoulders -----------------------
    for side in (-1, 1):
        shell("cape_%d" % side, 0.30, 1.18, "cloth", (0.150 * side, -0.245, 1.55),
              rot=(0.0, -5.0 * side, 0.0), bow=0.02, taper=0.80, rows=10, cols=5,
              thickness=0.014, bevel=0.004, curve=-0.10, wave=0.0,
              hem_mat="gold", hem_frac=0.06, parts=parts, subsurf=1)
    shell("cape_mantle", 0.52, 0.13, "cloth_dark", (0.0, -0.245, 1.625), bow=0.02,
          taper=0.94, rows=2, cols=6, thickness=0.02, bevel=0.005, parts=parts)
    loft("cape_clasp",
         [{"z": 1.615, "w": 0.50, "d": 0.045, "r": 0.018, "dy": -0.255},
          {"z": 1.645, "w": 0.50, "d": 0.045, "r": 0.018, "dy": -0.255}],
         "gold", sides=4, bevel=0.005, parts=parts)

    # ---- gorget and collar ---------------------------------------------------
    loft("gorget",
         [{"z": 1.575, "w": 0.315, "d": 0.255, "r": 0.09},
          {"z": 1.645, "w": 0.265, "d": 0.225, "r": 0.08},
          {"z": 1.675, "w": 0.215, "d": 0.195, "r": 0.07}],
         "steel", bevel=0.006, parts=parts)
    loft("collar_gold",
         [{"z": 1.645, "w": 0.290, "d": 0.245, "r": 0.085},
          {"z": 1.672, "w": 0.255, "d": 0.215, "r": 0.075}],
         "gold", sides=5, bevel=0.005, parts=parts)

    # ---- shoulders: pauldron domes, lames, discs, lion crests ----------------
    for side in (-1, 1):
        x = ARM_X * side
        dome("pauldron_top_%d" % side, 0.168, 0.215, 0.152, "steel",
             (x - 0.015 * side, 0.0, 1.450), rot=(0.0, -18.0 * side, 0.0),
             scale=(1.0, 1.05, 1.0), parts=parts)
        loft("pauldron_rim_%d" % side,
             [{"z": -0.024, "w": 0.340, "d": 0.440, "r": 0.170},
              {"z": 0.024, "w": 0.322, "d": 0.420, "r": 0.160}],
             "gold", sides=6, bevel=0.005, parts=parts,
             loc=(x - 0.015 * side, 0.0, 1.450), rot=(0.0, -18.0 * side, 0.0))
        # Two cascading lames. These are slanted rings around the upper arm rather
        # than flat plates: a plate leaves a visible gap between itself and the arm
        # no matter how hard it is bowed, a ring cannot.
        for i in range(2):
            size = 0.330 - i * 0.030
            depth = 0.400 - i * 0.035
            tilt = -44.0 * side - 8.0 * i * side
            spot = (x + 0.050 * side, -0.005, 1.395 - i * 0.115)
            loft("pauldron_lame_%d_%d" % (side, i),
                 [{"z": -0.030, "w": size, "d": depth, "r": size * 0.45},
                  {"z": 0.030, "w": size - 0.010, "d": depth - 0.010,
                   "r": size * 0.44}],
                 "steel", sides=6, bevel=0.006, parts=parts, loc=spot, rot=(0.0, tilt, 0.0))
            loft("pauldron_lame_trim_%d_%d" % (side, i),
                 [{"z": -0.052, "w": size, "d": depth, "r": size * 0.45},
                  {"z": -0.030, "w": size, "d": depth, "r": size * 0.45}],
                 "gold", sides=6, bevel=0.005, parts=parts, loc=spot, rot=(0.0, tilt, 0.0))
        # Blue disc with a gold cross, on the front face of the pauldron where the
        # reference puts it - not on top, where it reads as a button.
        loft("disc_ring_%d" % side,
             [{"z": -0.024, "w": 0.185, "d": 0.185, "r": 0.090},
              {"z": 0.024, "w": 0.175, "d": 0.175, "r": 0.085}],
             "gold", sides=6, bevel=0.005, parts=parts,
             loc=(x + 0.015 * side, 0.152, 1.535), rot=(90.0, 0.0, 16.0 * side))
        loft("disc_gem_%d" % side,
             [{"z": -0.022, "w": 0.150, "d": 0.150, "r": 0.072},
              {"z": 0.022, "w": 0.150, "d": 0.150, "r": 0.072}],
             "cloth", sides=6, parts=parts,
             loc=(x + 0.015 * side, 0.168, 1.535), rot=(90.0, 0.0, 16.0 * side))
        cross("disc_cross_%d" % side, 0.105, 0.017, 0.007, "gold_bright",
              (x + 0.015 * side, 0.188, 1.535), parts=parts, depth=0.020)
        lion("lion_%d" % side, 0.175, "gold", (x - 0.020 * side, -0.010, 1.620),
             rot=(0.0, 0.0, 0.0), parts=parts)
        # Arm: rerebrace under the pauldron, vambrace, gauntlet, fist.
        loft("upper_arm_%d" % side,
             [{"z": 1.46, "w": 0.215, "d": 0.235, "r": 0.082, "dx": x},
              {"z": 1.32, "w": 0.198, "d": 0.218, "r": 0.076, "dx": x + 0.012 * side},
              {"z": 1.19, "w": 0.178, "d": 0.198, "r": 0.068, "dx": x + 0.022 * side}],
             "steel_dark", bevel=0.005, parts=parts)
        shell("vambrace_%d" % side, 0.185, 0.30, "steel",
              (x + 0.045 * side, 0.0, 1.175), rot=(0.0, 0.0, 0.0), bow=0.075,
              taper=0.84, rows=5, cols=5, thickness=0.014, bevel=0.005,
              rim_mat="gold", rim_rows=1, rim_cols=0, parts=parts)
        loft("elbow_%d" % side,
             [{"z": 1.14, "w": 0.175, "d": 0.195, "r": 0.07, "dx": x + 0.028 * side},
              {"z": 1.20, "w": 0.190, "d": 0.210, "r": 0.075, "dx": x + 0.026 * side}],
             "steel", bevel=0.008, parts=parts)
        loft("forearm_%d" % side,
             [{"z": 0.94, "w": 0.150, "d": 0.165, "r": 0.06, "dx": x + 0.05 * side},
              {"z": 1.06, "w": 0.165, "d": 0.180, "r": 0.065, "dx": x + 0.04 * side}],
             "steel_dark", bevel=0.005, parts=parts)
        loft("gauntlet_%d" % side,
             [{"z": 0.855, "w": 0.150, "d": 0.170, "r": 0.055,
               "dx": x + 0.055 * side, "dy": 0.01},
              {"z": 0.955, "w": 0.145, "d": 0.165, "r": 0.055,
               "dx": x + 0.05 * side, "dy": 0.005}],
             "steel", bevel=0.008, parts=parts)
        loft("gauntlet_trim_%d" % side,
             [{"z": 0.905, "w": 0.162, "d": 0.182, "r": 0.06,
               "dx": x + 0.052 * side, "dy": 0.01},
              {"z": 0.935, "w": 0.162, "d": 0.182, "r": 0.06,
               "dx": x + 0.052 * side, "dy": 0.01}],
             "gold", sides=5, bevel=0.004, parts=parts)
        ball("fist_%d" % side, 0.085, "steel",
             (x + 0.065 * side, 0.02, 0.80), scale=(0.9, 1.05, 1.0), parts=parts)
        for finger in range(4):          # fingers closed around grip or strap
            ball("finger_%d_%d" % (side, finger), 0.026, "steel",
                 (x + 0.065 * side, 0.085, 0.855 - finger * 0.037),
                 scale=(1.0, 0.85, 0.9), rings=6, sides=8, parts=parts)

    # ---- head: neck, face volume, hair, beard, eyes ---------------------------
    loft("neck", [{"z": 1.575, "w": 0.148, "d": 0.142, "r": 0.058, "dy": 0.012},
                  {"z": 1.700, "w": 0.136, "d": 0.130, "r": 0.052, "dy": 0.014}],
         "skin", smooth=True, subsurf=1, parts=parts)
    loft("head",
         [{"z": 1.672, "w": 0.082, "d": 0.100, "r": 0.040, "dy": 0.030},
          {"z": 1.716, "w": 0.115, "d": 0.132, "r": 0.050, "dy": 0.026},
          {"z": 1.772, "w": 0.138, "d": 0.156, "r": 0.060, "dy": 0.018},
          {"z": 1.828, "w": 0.140, "d": 0.156, "r": 0.060, "dy": 0.010},
          {"z": 1.872, "w": 0.110, "d": 0.124, "r": 0.048, "dy": 0.004}],
         "skin", smooth=True, subsurf=1, parts=parts)
    loft("nose", [{"z": 1.780, "w": 0.030, "d": 0.058, "r": 0.012, "dy": 0.086},
                  {"z": 1.812, "w": 0.024, "d": 0.046, "r": 0.010, "dy": 0.080}],
         "skin", smooth=True, subsurf=1, parts=parts)
    for side in (-1, 1):
        ball("socket_%d" % side, 0.022, "skin_dark", (0.037 * side, 0.081, 1.812),
             scale=(1.30, 0.35, 0.72), rings=8, sides=12, parts=parts)
        ball("eye_%d" % side, 0.012, "eye", (0.036 * side, 0.088, 1.810),
             scale=(1.40, 0.40, 0.72), rings=8, sides=12, parts=parts)
        loft("brow_%d" % side,
             [{"z": -0.011, "w": 0.056, "d": 0.024, "r": 0.009, "dy": 0.092},
              {"z": 0.011, "w": 0.050, "d": 0.022, "r": 0.009, "dy": 0.088}],
             "hair", sides=3, smooth=True, parts=parts,
             loc=(0.038 * side, 0.0, 1.834))
        ball("ear_%d" % side, 0.030, "skin", (0.072 * side, 0.004, 1.788),
             scale=(0.45, 0.85, 1.05), parts=parts)
    # Hair: a crown cap that comes down the sides to below eye level, a back mass
    # and a swept fringe. Without the sides the head reads as a bare potato.
    dome("hair_cap", 0.084, 0.096, 0.078, "hair", (0.0, -0.010, 1.838), parts=parts)
    ball("hair_back", 0.086, "hair", (0.0, -0.052, 1.796), scale=(0.92, 0.90, 1.05),
         parts=parts)
    dome("hair_fringe", 0.080, 0.052, 0.048, "hair", (0.0, 0.058, 1.850),
         rot=(30.0, 0.0, 0.0), parts=parts)
    for side in (-1, 1):
        ball("hair_side_%d" % side, 0.052, "hair", (0.066 * side, -0.004, 1.796),
             scale=(0.48, 0.95, 1.25), parts=parts)
    # Beard: a jaw mass below the mouth, cheek masses up the sides, moustache.
    loft("beard",
         [{"z": 1.654, "w": 0.098, "d": 0.112, "r": 0.042, "dy": 0.034},
          {"z": 1.700, "w": 0.132, "d": 0.150, "r": 0.055, "dy": 0.032},
          {"z": 1.742, "w": 0.146, "d": 0.160, "r": 0.062, "dy": 0.024}],
         "hair", smooth=True, subsurf=1, parts=parts)
    ball("beard_tip", 0.038, "hair", (0.0, 0.072, 1.668), scale=(1.05, 0.80, 0.95),
         parts=parts)
    for side in (-1, 1):
        ball("beard_cheek_%d" % side, 0.046, "hair", (0.064 * side, 0.026, 1.750),
             scale=(0.55, 1.05, 1.05), parts=parts)
    ball("moustache", 0.036, "hair", (0.0, 0.092, 1.772), scale=(1.35, 0.80, 0.45),
         parts=parts)

    # ---- sword in the right (+X) hand: blade down, tipped forward and out ----
    blade = sword("sword", parts=None)
    place_all(blade, (ARM_X + 0.065, 0.030, 0.835), (10.0, -12.0, 0.0))
    parts.extend(blade)

    # ---- lion shield on the left (-X) forearm ---------------------------------
    shield_x = -(ARM_X + 0.16)
    shell("shield_face", 0.40, 0.72, "cloth", (shield_x, 0.080, 1.475), bow=0.060,
          taper=0.70, rows=10, cols=9, thickness=0.020, bevel=0.005, dome_top=0.050,
          tip=0.80, rim_mat="gold", rim_rows=1, rim_cols=1, parts=parts)
    shell("shield_back", 0.38, 0.68, "leather", (shield_x, 0.050, 1.465), bow=0.045,
          taper=0.70, rows=4, cols=5, thickness=0.016, bevel=0.004, dome_top=0.040,
          tip=0.80, parts=parts)
    # The lion sits on a raised gold boss in the middle of the field, as it does
    # on the reference shield, instead of floating on the flat cloth.
    dome("shield_boss", 0.118, 0.118, 0.048, "gold", (shield_x, 0.110, 1.190),
         rot=(-90.0, 0.0, 0.0), parts=parts)
    lion("shield_lion", 0.190, "gold", (shield_x, 0.135, 1.190), parts=parts)
    cross("shield_cross_top", 0.195, 0.036, 0.010, "gold_bright",
          (shield_x, 0.190, 1.375), parts=parts)
    cross("shield_cross_low", 0.115, 0.024, 0.008, "gold_bright",
          (shield_x, 0.185, 0.950), parts=parts)
    loft("shield_strap",
         [{"z": 0.955, "w": 0.045, "d": 0.045, "r": 0.018, "dx": shield_x + 0.14},
          {"z": 1.075, "w": 0.045, "d": 0.045, "r": 0.018, "dx": shield_x + 0.12}],
         "leather", sides=4, bevel=0.004, parts=parts)

    return parts


# ------------------------------------------------------------------------- assembly

def report_parts(parts):
    """Parts that reach above the head or under the floor are placement bugs."""
    for obj in parts:
        zs = [v.co.z for v in obj.data.vertices]
        if zs and (max(zs) > HEIGHT + 0.10 or min(zs) < -0.02):
            log("suspicious part %-24s z=%.3f..%.3f" % (obj.name, min(zs), max(zs)))


def join_parts(parts, name="Paladin"):
    for obj in bpy.context.selected_objects:
        obj.select_set(False)
    for obj in parts:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.context.view_layer.update()
    report_parts(parts)
    bpy.ops.object.join()
    joined = bpy.context.view_layer.objects.active
    joined.name = name
    joined.data.name = name
    lowest = min(v.co.z for v in joined.data.vertices)
    for vertex in joined.data.vertices:
        vertex.co.z -= lowest
    joined.data.update()
    return joined


def clean_mesh(obj):
    """Collapse zero-area slivers and triangulate.

    The bevel pass leaves a handful of degenerate faces on the smallest trim bars.
    Unity reports those as "polygon ... is self-intersecting and has been discarded"
    and quietly drops them, which looks like a broken import in the console, so
    they are removed here instead - and the count is logged so a regression shows.
    """
    before = len(obj.data.polygons)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    loose = sum(1 for face in bm.faces if face.calc_area() < 1e-7)
    bmesh.ops.dissolve_degenerate(bm, dist=1.0e-4, edges=bm.edges)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    log("cleanup: %d faces, %d degenerate -> %d triangles"
        % (before, loose, len(obj.data.polygons)))


def unwrap(obj):
    """Game-ready UVs. Smart project gives a usable atlas for a multi-material
    mesh without hand-marking seams; a missing op is not worth failing the build."""
    try:
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(angle_limit=math.radians(66.0), island_margin=0.004)
        bpy.ops.object.mode_set(mode="OBJECT")
        log("uv layers: %s" % ", ".join(layer.name for layer in obj.data.uv_layers))
    except Exception as exc:
        log("uv unwrap skipped (%s)" % exc)
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass


def export(obj):
    os.makedirs(os.path.dirname(FBX_OUT), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.fbx(
        filepath=FBX_OUT,
        use_selection=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        object_types={"MESH"},
        path_mode="COPY",
        mesh_smooth_type="FACE",
        bake_space_transform=True,
    )
    log("exported " + FBX_OUT)


# --------------------------------------------------------------------------- review

def set_view_transform(scene):
    for name in ("Khronos PBR Neutral", "Standard", "Filmic", "AgX"):
        try:
            scene.view_settings.view_transform = name
            log("view transform: " + name)
            return
        except Exception:
            continue


def light_scene(scene):
    engine = "BLENDER_WORKBENCH"
    for candidate in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            scene.render.engine = candidate
            engine = candidate
            break
        except Exception:
            continue
    if engine == "BLENDER_WORKBENCH":
        shading = scene.display.shading
        shading.light = "STUDIO"
        shading.color_type = "MATERIAL"
        shading.show_cavity = True
        return engine
    set_view_transform(scene)
    world = bpy.data.worlds.new("Review")
    scene.world = world
    world.use_nodes = True
    background = world.node_tree.nodes.get("Background")
    if background is not None:
        background.inputs[0].default_value = (0.30, 0.34, 0.43, 1.0)
        background.inputs[1].default_value = 0.55

    def lamp(name, energy, loc, size):
        data = bpy.data.lights.new(name, "AREA")
        data.energy = energy
        data.size = size
        go = bpy.data.objects.new(name, data)
        bpy.context.collection.objects.link(go)
        go.location = loc
        go.rotation_euler = (Vector((0.0, 0.0, 1.0)) - Vector(loc)).normalized() \
            .to_track_quat("-Z", "Y").to_euler()

    # Energies stay modest: overdriving the key blew the steel to flat white and
    # turned the gold specular pale grey, which is what made the first pass look
    # washed out beside the reference sheet.
    lamp("key", 170.0, (-2.4, -2.8, 3.4), 3.5)
    lamp("fill", 70.0, (2.8, -2.2, 1.9), 3.0)
    lamp("rim", 110.0, (0.4, 3.2, 2.8), 3.0)
    for attr, value in (("taa_render_samples", 48), ("use_shadows", True),
                        ("use_raytracing", True), ("use_ambient_occlusion", True)):
        try:
            setattr(scene.eevee, attr, value)
        except Exception:
            pass
    return engine


# Whole-figure views first, then the places the reference puts its detail: the
# face, the lion crests, the sword hand and the shield.
VIEWS = (("front", 90.0, 6.0, None, None),
         ("threequarter", 128.0, 12.0, None, None),
         ("side", 0.0, 6.0, None, None),
         ("back", 270.0, 6.0, None, None),
         ("head", 100.0, 3.0, Vector((0.0, 0.0, 1.79)), 0.44),
         ("lion", 74.0, 6.0, Vector((-0.28, 0.0, 1.62)), 0.52),
         ("sword", 92.0, 4.0, Vector((0.40, 0.0, 0.55)), 0.72))

# The same view twice, once at rest and once posed: the pair is the only proof
# that the skin follows the bones rather than the rig being decoration.
POSE_VIEW = (("posed", 128.0, 12.0, None, None),)


def render_views(obj, views=None, suffix="%s"):
    os.makedirs(RENDER_DIR, exist_ok=True)
    scene = bpy.context.scene
    scene.render.resolution_x, scene.render.resolution_y = RES
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    log("render engine: " + light_scene(scene))

    cam_data = bpy.data.cameras.new("ReviewCam")
    cam_data.type = "ORTHO"
    cam_data.sensor_fit = "VERTICAL"
    cam = bpy.data.objects.new("ReviewCam", cam_data)
    bpy.context.collection.objects.link(cam)
    scene.camera = cam

    zs = [v.co.z for v in obj.data.vertices]
    low, high = min(zs), max(zs)
    span = max(high - low, max(v.co.x for v in obj.data.vertices)
               - min(v.co.x for v in obj.data.vertices))
    cam_data.ortho_scale = span * 1.12
    log("frame: z %.3f..%.3f (span %.3f), ortho %.3f" % (low, high, span,
                                                         cam_data.ortho_scale))
    focus = Vector((0.0, 0.0, (low + high) * 0.5))
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
        path = os.path.join(RENDER_DIR, "paladin_%s.png" % (suffix % name))
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        log("rendered " + path)
        shots.append((name, path))
    return shots


def save_blend():
    """Save the lit scene: opening the FBX alone gives a dark viewport with flat
    materials, which reads as 'the model looks bad' in Blender's own dark theme."""
    try:
        bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT, copy=True)
        log("saved " + BLEND_OUT)
    except Exception as exc:
        log("blend save skipped (%s)" % exc)


# ------------------------------------------------------------------------- rig

# Parts the rig cannot place from their own position, because they hang away from
# the bone that carries them. A sword gripped in the fist hangs to the knee, so
# proximity would bind it to the shin; the shield is strapped to the left forearm
# but its centre sits nearer the shoulder; the cape is attached at the collar
# while most of its cloth is down around the hips.
PART_BONES = (
    ("sword_", "hand.R"), ("shield_", "forearm.L"),
    # Cloth that reaches across the torso is blended rather than pinned, so the
    # hem swings with the pelvis while the collar still follows the chest.
    ("cape_", ("chest", "spine", "hips")),
    ("tabard", ("chest", "spine", "hips")),
    # Gold chevrons on the cuirass: same distance from the chest as from the arm
    # bones, and they are armour trim, not part of the arm.
    ("filigree_", "chest"),
    # Pelvis gear: the belt sits as close to the thigh bones as to the pelvis, so
    # proximity alone would swing it with each step.
    ("pelvis", "hips"), ("skirt_", "hips"), ("underskirt_", "hips"),
    ("belt", "hips"), ("buckle", "hips"), ("strap_", "hips"))


def part_bone(name):
    """The bone a part must ride, or the set it must blend across, or None."""
    for prefix, bone in PART_BONES:
        if name.startswith(prefix):
            return bone
    return None


def finish_build(parts, export=True, render=True, rig=True):
    """Join, clean, unwrap, rig, write the FBX and (optionally) render the views.

    Named `finish_build` rather than `finish` because this module is where the
    geometry helper `finish()` lives: loft/shell/dome/ball all call it, so the
    assembly step cannot take that name here. (The mage and the assassin call their
    equivalent `finish`, since they only import the helper.)"""
    if not parts:
        raise RuntimeError("nothing built yet - call build() first")
    armature = None
    if rig:
        # Weight the parts before the join: each one is judged from its own centre,
        # which is the difference between a belt buckle following the pelvis and it
        # following the forearm.
        armature = RIG.build_armature("Paladin_Rig", RIG_LANDMARKS)
        # Lift the props off before anything is weighted or joined: a shield that is
        # part of the body mesh cannot be thrown away from the body.
        for prop in PROPS:
            PROP_KIT.extract(parts, RIG, armature, prop, clean=clean_mesh,
                             unwrap=unwrap, log=log)
        segments = RIG.deform_bones(armature)
        pinned = 0
        for part in parts:
            bone = part_bone(part.name)
            pinned += 1 if bone else 0
            RIG.weight_part(part, armature, bone=bone, segments=segments)
        log("rig: %d parts weighted (%d placed by name)" % (len(parts), pinned))
    knight = join_parts(parts)
    clean_mesh(knight)
    unwrap(knight)
    triangles = sum(len(poly.vertices) - 2 for poly in knight.data.polygons)
    height = max(v.co.z for v in knight.data.vertices)
    width = max(v.co.x for v in knight.data.vertices) - min(
        v.co.x for v in knight.data.vertices)
    log("mesh: %d triangles, %d faces, %d material slots, %.2f m tall, %.2f m wide"
        % (triangles, len(knight.data.polygons), len(knight.data.materials), height,
           width))
    if armature is not None:
        RIG.bind(knight, armature)
        primary = RIG.primary_from_groups(knight)
        test = RIG.pose_test(knight, armature, primary)
        for bone, (own, head, far) in sorted(test.items()):
            log("rig %-12s own skin %.3f m, head %.3f m, far foot %.3f m"
                % (bone, own, head, far))
        bleed = max(max(head, far) for _, head, far in test.values())
        weak = min(own for own, _, _ in test.values())
        log("rig check: weakest bone moves %.3f m, worst bleed %.3f m"
            % (weak, bleed))
        log("rig: %d bones, %d skinned vertices"
            % (len(armature.data.bones), len(knight.data.vertices)))
    if export:
        if armature is not None:
            RIG.export_skinned(knight, armature, FBX_OUT)
            log("exported " + FBX_OUT + " (skinned)")
        else:
            export(knight)
    shots = render_views(knight) if render else []
    if armature is not None and render:
        RIG.showcase_pose(armature)
        shots += render_views(knight, POSE_VIEW)
        RIG.clear_pose(armature)
    log("shots: " + " ".join("%s=%s" % (n, p) for n, p in shots))
    return "%s: %d triangles, %.2f m tall, %d slots, %d bones, %d views" % (
        "Paladin", triangles, height, len(knight.data.materials),
        0 if armature is None else len(armature.data.bones), len(shots))


def main():
    reset_scene()
    log(finish_build(build()))
    save_blend()
    log("done")


if __name__ == "__main__":
    main()
