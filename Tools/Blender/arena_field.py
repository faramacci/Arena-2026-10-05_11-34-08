"""Paints the arena field: ice inside the walls, molten rock outside them.

One 2048 sheet is a top-down map of the whole colosseum floor, 124 world units
across - the outer plinth's own diameter, so the ring of rock the player sees
beyond the wall is the ring the sheet paints. Everything the game puts on the
ground then reads its own window out of the one image with `_BaseMap_ST`:

    sheet u,v        world (x, z)          surface
    ---------------------------------------------------------------------
    0    .. 1         -62 .. 62            the whole field map
    .240 .. .760      -32.2 .. 32.2        the 64.4 arena floor      (ICE)
    .065 .. .935      -54 .. 54            the inner plinth cap      (ROCK)
    0    .. 1         -62 .. 62            the outer plinth cap      (ROCK)

The split is the arena's square, at ±32.2, because that is what the floor
actually is - a square slab that the four wall runs stand on. The wall is 0.9
thick and sits on x = ±32, so it covers the seam: no crack of ice and no lick of
lava has to line up with a straight edge that the player can see.

Ice and rock are both built the same way - a Worley cellular field gives the
plates, and F2 - F1 gives their edges - but they are coloured as opposites: the
plates of ice are bright and the crevices between them are a shade deeper, while
the plates of rock are near-black and the crevices are the light, because that is
where the field is still molten. The rock's heat falls off with distance from the
wall, and the last half metre of the arena's ice is painted as if it were melting:
darker, wetter, then a white-hot rim where the two meet.

    python Tools/Blender/arena_field.py out.png     (headless: blender -b -P)

Nothing here reads the filesystem: call build(path) from a Blender snippet, or run
the module as a script to write the sheet and a small preview scene.
"""

import math
import os

import numpy as np

# The sheet is the outer plinth's footprint, centred, in world units.
FIELD = 124.0
# The arena floor slab: ArenaWidth + 0.4. Its edge is where ice stops.
ARENA = 64.4
ARENA_HALF = ARENA * 0.5
HALF = FIELD * 0.5
SIZE = 2048

# How far inside the arena's edge the ice is painted as melting, and how far
# outside it the rock is painted as still white hot.
MELT = 1.6
HOT = 9.0


def _smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _blur(a, passes=2):
    """A cheap separable blur: average each pixel with its four neighbours."""
    for _ in range(passes):
        a = (a + np.roll(a, 1, 0) + np.roll(a, -1, 0) +
             np.roll(a, 1, 1) + np.roll(a, -1, 1)) * 0.2
    return a


def _noise(rng, cells, passes=3):
    """Smooth value noise: one random value per cell, blurred up."""
    k = max(1, int(math.ceil(SIZE / cells)))
    grid = rng.random((cells + 1, cells + 1)).astype(np.float32)
    field = np.kron(grid, np.ones((k, k), dtype=np.float32))[:SIZE, :SIZE]
    return _blur(field, passes)


def _fbm(rng, cells, octaves=4):
    total = np.zeros((SIZE, SIZE), dtype=np.float32)
    weight = 0.0
    for i in range(octaves):
        step = cells * (2 ** i)
        total += _noise(rng, step, passes=4 - min(i, 3)) / (2 ** i)
        weight += 1.0 / (2 ** i)
    return total / weight


def _worley(rng, cells):
    """Cellular field: F1 (distance to the nearest seed) and F2 - F1, in pixels.

    Seeds are one per cell with a random jitter and the neighbourhood is wrapped,
    which keeps the whole thing to nine shifted reads per pixel.
    """
    cell = SIZE / cells
    jitter = rng.random((cells, cells, 2)).astype(np.float32)
    ys, xs = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    cy = (ys / cell).astype(np.int32)
    cx = (xs / cell).astype(np.int32)
    f1 = np.full((SIZE, SIZE), 1e9, dtype=np.float32)
    f2 = np.full((SIZE, SIZE), 1e9, dtype=np.float32)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            ny = np.mod(cy + dy, cells)
            nx = np.mod(cx + dx, cells)
            px = ((cx + dx) + jitter[ny, nx, 0]) * cell
            py = ((cy + dy) + jitter[ny, nx, 1]) * cell
            d = np.sqrt((px - xs) ** 2 + (py - ys) ** 2)
            swap = d < f1
            f2 = np.where(swap, f1, np.minimum(f2, d))
            f1 = np.where(swap, d, f1)
    return f1, f2 - f1


def _cell_tint(rng, cells):
    k = max(1, int(math.ceil(SIZE / cells)))
    return np.kron(rng.random((cells, cells)).astype(np.float32),
                   np.ones((k, k), dtype=np.float32))[:SIZE, :SIZE]


def _crack(edge, width):
    """How much of this pixel is a crevice: 1 on the edge, 0 in the middle."""
    return 1.0 - _smoothstep(0.0, width, edge)


def build(path, seed=71234):
    """Paints the sheet and writes it as a PNG."""
    rng = np.random.default_rng(seed)

    ys, xs = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    # world units, y of the image is z of the arena
    wx = (xs / (SIZE - 1)) * FIELD - HALF
    wz = (ys / (SIZE - 1)) * FIELD - HALF
    # The arena's edge, roughened: the boundary wanders by most of a metre so the
    # melt is not a drawn rectangle. |x| and |z| both count: it is a square field.
    wander = (_noise(rng, 24, passes=4) - 0.5) * 1.9
    edge = np.maximum(np.abs(wx), np.abs(wz)) - ARENA_HALF
    ragged = edge + wander
    outside = _smoothstep(-0.25, 0.25, ragged)
    dist_out = np.maximum(ragged, 0.0)

    # ---------------------------------------------------------------- the ice
    ice_shade = (_fbm(rng, 3, 5) - 0.5) * 0.16
    ice_plate = _cell_tint(rng, 9) - 0.5
    ice_base = np.stack([
        0.78 + ice_shade * 0.6 + ice_plate * 0.05,
        0.88 + ice_shade * 0.7 + ice_plate * 0.04,
        0.97 + ice_shade * 0.5 + ice_plate * 0.02,
    ], axis=-1)

    # Three crack networks, widest first: plates that broke apart, the pressure
    # ridges between them, then the fine crazing that frosts over both. At 16.5
    # texels to the metre the widest of them is about a ruler's width - the width
    # that still reads as a line from the game's camera, twenty-odd metres up.
    _, plates = _worley(rng, 8)
    _, ridge = _worley(rng, 26)
    _, crazing = _worley(rng, 68)
    break_ = _crack(plates, 7.0)
    broad = _crack(ridge, 4.6)
    fine = _crack(crazing, 1.7)
    # A crevice is a deeper blue; the ice beside it has frosted up bright.
    frost = _crack(np.maximum(plates - 7.0, 0.0), 6.0) * 0.5
    deep = np.stack([0.34, 0.53, 0.72], axis=-1)
    lit = np.stack([0.96, 0.99, 1.0], axis=-1)
    ice = ice_base * (1.0 - break_[..., None] * 0.55) + deep * (break_[..., None] * 0.55)
    ice = ice * (1.0 - broad[..., None] * 0.72) + deep * (broad[..., None] * 0.72)
    ice = ice * (1.0 - fine[..., None] * 0.22) + deep * (fine[..., None] * 0.22)
    ice = np.clip(ice + lit * (frost * (1.0 - break_))[..., None] * 0.45, 0.0, 1.0)

    # The last stretch of ice is going: wet darker ice, then a white-hot lip.
    melt = _smoothstep(-MELT, 0.0, ragged)
    wet = np.stack([0.30, 0.52, 0.72], axis=-1)
    ice = ice * (1.0 - melt[..., None] * 0.35) + wet * (melt[..., None] * 0.35)
    lip = np.clip(1.0 - np.abs(ragged + 0.28) / 0.62, 0.0, 1.0) ** 2
    ice = np.clip(ice + np.stack([1.0, 0.93, 0.80], axis=-1) * lip[..., None] * 0.9, 0.0, 1.0)

    # --------------------------------------------------------------- the rock
    crust_shade = (_fbm(rng, 4, 5) - 0.5) * 0.20
    plate = _cell_tint(rng, 11) - 0.5
    rock = np.stack([
        0.115 + crust_shade * 0.5 + plate * 0.05,
        0.095 + crust_shade * 0.4 + plate * 0.04,
        0.105 + crust_shade * 0.5 + plate * 0.05,
    ], axis=-1)

    _, rifts = _worley(rng, 6)
    _, seams = _worley(rng, 22)
    _, hairline = _worley(rng, 58)
    # Three scales again, the other way round: whole fissures running through the
    # field, the seams between its plates, and the hairline cracks in the crust.
    rift = _crack(rifts, 9.0)
    seam = _crack(seams, 5.4)
    hair = _crack(hairline, 1.9)
    vein = np.clip(rift * 0.9 + seam + hair * 0.6, 0.0, 1.0)

    # Heat: white hot at the wall, cooling as the field runs out. A seam close to
    # the wall is a river of orange; the same seam far out is a dull red.
    heat = (1.0 - _smoothstep(0.0, HOT, dist_out)) * 0.85
    heat += (1.0 - _smoothstep(HOT, HOT * 5.0, dist_out)) * 0.15
    # Each plate holds its own temperature, so the field is not one gradient.
    heat = np.clip(heat * (0.62 + 1.25 * _cell_tint(rng, 22)), 0.0, 1.0)
    glow = vein * heat
    hot = np.stack([1.0, 0.66, 0.20], axis=-1)
    cool = np.stack([0.62, 0.13, 0.05], axis=-1)
    ember = hot * heat[..., None] + cool * (1.0 - heat)[..., None]
    rock = rock * (1.0 - glow[..., None] * 0.96) + ember * (glow[..., None] * 0.96)
    # A crust right at the wall is broken and running: lift the whole band.
    molten = (1.0 - _smoothstep(0.0, 2.2, dist_out)) * (0.35 + 0.65 * heat)
    rock = np.clip(rock + np.stack([1.0, 0.55, 0.16], axis=-1) * molten[..., None] * 0.55, 0.0, 1.0)

    # ------------------------------------------------------------ the two meet
    field = ice * (1.0 - outside[..., None]) + rock * outside[..., None]
    # The lip of ice burns the rock white for a hand's width outside the wall.
    scorch = np.clip(1.0 - np.abs(ragged - 0.5) / 1.2, 0.0, 1.0) ** 2
    field = np.clip(field + np.stack([1.0, 0.88, 0.55], axis=-1) * scorch[..., None] * 0.75,
                    0.0, 1.0)

    image = np.clip(field, 0.0, 1.0)
    write_png(path, image)
    return image


def write_png(path, image):
    """Writes the sheet as painted: the bytes in the file are the colours above.

    Blender's writer is used when Blender is running (so the image also lands in the
    session as a data-block); outside Blender, Pillow writes the same bytes.
    """
    try:
        import bpy
    except ImportError:
        from PIL import Image
        data = (np.clip(image, 0.0, 1.0) ** (1.0 / 1.0) * 255.0 + 0.5).astype(np.uint8)
        Image.fromarray(data, "RGB").save(path)
        return None

    height, width = image.shape[:2]
    rgba = np.concatenate(
        [image, np.ones((height, width, 1), dtype=np.float32)], axis=-1)
    # Blender's pixel buffer starts at the bottom left and the sheet starts at the
    # top, which is the only place the two coordinate systems meet.
    rgba = rgba[::-1]

    name = os.path.splitext(os.path.basename(path))[0]
    existing = bpy.data.images.get(name)
    if existing is not None:
        bpy.data.images.remove(existing)
    img = bpy.data.images.new(name, width=width, height=height, alpha=False)
    img.colorspace_settings.name = "sRGB"
    img.pixels.foreach_set(np.ascontiguousarray(rgba.reshape(-1), dtype=np.float32))
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    return img


def preview(sheet_path, out_dir, size=(1280, 720), seed=1):
    """Lays the sheet out as the arena floor in the open Blender session and
    renders it twice: from the game's own camera angle, and straight down as a map.

    The rock is a 124 m plane, so the sheet lands on it one to one. The arena is a
    64.4 m plane a hand's width above it, and its mapping is the same window the
    game sets on `_BaseMap_ST` - scale 0.519, offset 0.240 - so what this preview
    shows is what the floor will sample.
    """
    import bpy

    collection = bpy.data.collections.get("ArenaFieldPreview")
    if collection is None:
        collection = bpy.data.collections.new("ArenaFieldPreview")
        bpy.context.scene.collection.children.link(collection)
    for child in list(collection.objects):
        bpy.data.objects.remove(child, do_unlink=True)

    image = bpy.data.images.get(os.path.splitext(os.path.basename(sheet_path))[0])
    if image is None:
        image = bpy.data.images.load(sheet_path)

    def texture_material(name, scale, offset):
        """Shows the sheet itself: emission only, so the render is the paint and not
        a lit, tone-mapped approximation of it."""
        material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        material.use_nodes = True
        nodes = material.node_tree.nodes
        nodes.clear()
        out = nodes.new("ShaderNodeOutputMaterial")
        out.location = (520, 0)
        emit = nodes.new("ShaderNodeEmission")
        emit.location = (200, 0)
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = image
        tex.location = (-480, 80)
        coords = nodes.new("ShaderNodeTexCoord")
        coords.location = (-880, 80)
        mapping = nodes.new("ShaderNodeMapping")
        mapping.location = (-680, 80)
        mapping.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
        mapping.inputs["Location"].default_value = (offset[0], offset[1], 0.0)
        material.node_tree.links.new(coords.outputs["UV"], mapping.inputs["Vector"])
        material.node_tree.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
        material.node_tree.links.new(tex.outputs["Color"], emit.inputs["Color"])
        material.node_tree.links.new(emit.outputs["Emission"], out.inputs["Surface"])
        return material

    def plane(name, side, height, material):
        mesh = bpy.data.meshes.new(name)
        half = side * 0.5
        mesh.from_pydata([(-half, -half, 0), (half, -half, 0), (half, half, 0), (-half, half, 0)],
                         [], [(0, 1, 2, 3)])
        mesh.update()
        uvs = mesh.uv_layers.new(name="UVMap")
        for corner, uv in zip(range(4), [(0, 0), (1, 0), (1, 1), (0, 1)]):
            uvs.data[corner].uv = uv
        obj = bpy.data.objects.new(name, mesh)
        obj.location = (0.0, 0.0, height)
        obj.data.materials.append(material)
        collection.objects.link(obj)
        return obj

    arena_scale = ARENA / FIELD
    rock_material = texture_material("ArenaFieldRock", (1.0, 1.0), (0.0, 0.0))
    ice_material = texture_material("ArenaFieldIce", (arena_scale, arena_scale),
                                    (0.5 - arena_scale * 0.5, 0.5 - arena_scale * 0.5))
    plane("FieldRock", FIELD, -0.05, rock_material)
    plane("ArenaIce", ARENA, 0.02, ice_material)

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    previous_camera = scene.camera
    previous_transform = scene.view_settings.view_transform
    scene.view_settings.view_transform = "Standard"
    if scene.world is None:
        scene.world = bpy.data.worlds.new("ArenaFieldWorld")
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    if background is not None:
        background.inputs[0].default_value = (0.03, 0.025, 0.03, 1.0)

    camera_data = bpy.data.cameras.get("ArenaFieldCam") or bpy.data.cameras.new("ArenaFieldCam")
    camera = bpy.data.objects.get("ArenaFieldCam")
    if camera is None:
        camera = bpy.data.objects.new("ArenaFieldCam", camera_data)
        collection.objects.link(camera)
    scene.camera = camera
    written = []

    def shoot(name, location, rotation, lens, shot):
        camera.location = location
        camera.rotation_euler = rotation
        camera_data.lens = lens
        scene.render.resolution_x, scene.render.resolution_y = shot
        target = os.path.join(out_dir, name)
        scene.render.filepath = target
        bpy.ops.render.render(write_still=True)
        written.append(scene.render.filepath)

    # The game's own rig: 26 m back along a 58 degree pitch, 45 degrees of view.
    # A 45 degree vertical field down a 416 px tall frame of a 997 px wide render
    # is an 18.1 mm lens on Blender's 36 mm sensor.
    pitch = math.radians(58.0)
    distance = 26.0
    camera_data.type = "PERSP"
    shoot("blender-arena-field.png",
          (0.0, -distance * math.cos(pitch), distance * math.sin(pitch)),
          (math.radians(90.0 - 58.0), 0.0, 0.0), 18.1, (997, 416))
    # And straight down: the sheet as the map every surface samples.
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = FIELD + 8.0
    shoot("blender-arena-field-map.png", (0.0, 0.0, 130.0), (0.0, 0.0, 0.0), 50.0,
          (1024, 1024))
    scene.camera = previous_camera
    scene.view_settings.view_transform = previous_transform
    return written


def region_report(image):
    """A few numbers per region, so the paint can be checked without eyes on it."""
    height, width = image.shape[:2]
    ys, xs = np.mgrid[0:height, 0:width].astype(np.float32)
    wx = (xs / (width - 1)) * FIELD - HALF
    wz = (ys / (height - 1)) * FIELD - HALF
    inside = np.maximum(np.abs(wx), np.abs(wz)) <= ARENA_HALF - 2.0
    outside = np.maximum(np.abs(wx), np.abs(wz)) >= ARENA_HALF + 6.0
    def of(mask, label):
        pixels = image[mask]
        mean = pixels.mean(axis=0)
        hot = float(np.mean((pixels[:, 0] > 0.45) & (pixels[:, 1] < 0.45)))
        return ("%-8s px=%7d  mean rgb=(%.3f, %.3f, %.3f)  warm(>0.45 r)=%.1f%%"
                % (label, int(mask.sum()), mean[0], mean[1], mean[2], hot * 100))
    return [of(inside, "ice"), of(outside, "rock")]


if __name__ == "__main__":
    import sys

    target = sys.argv[1] if len(sys.argv) > 1 else "arena_field.png"
    sheet = build(os.path.abspath(target))
    for line in region_report(sheet):
        print(line)
    print("wrote", target)
