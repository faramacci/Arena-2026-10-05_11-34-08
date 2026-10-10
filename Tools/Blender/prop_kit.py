"""The characters' hand props, exported so they can actually leave the hand.

Every fighter is built as one mesh, and the knight's lion shield and the assassin's
right-hand knife are parts like any other: they are joined into the body and
skinned to the forearm or the fist. That is exactly right while they are held - and
exactly wrong the moment the game throws one, because a single skinned mesh cannot
lose a piece and a hand cannot empty.

So each prop is cut out of the body *before* the join and shipped as its own small
FBX holding two copies of the same geometry:

    <Name>_arm  the geometry where the body modelled it, in the body's own frame.
                The game binds it to the live skeleton (see `FighterModel.WearProps`)
                rather than trusting a baked bone frame, so it rides the arm through
                every pose without this export having to know how the FBX writer
                spells a bone's axes.
    <Name>_air  the same geometry centred on its own middle and turned into the
                frame a thrown object is drawn in: its length along the throw
                direction (the object's local +Z) and the side it lies flat on
                facing up (local +Y), which is what makes the game's LookRotation
                plus a roll about the throw axis tumble it the way a thrown shield
                or knife really tumbles.

That flight frame is *measured* off the mesh instead of hard-coded: of the
bounding box's three axes the longest is the object's length and the shortest is
the one it lies flat on, and the length's sign comes from whichever end is farther
from the carrying bone - the point of a knife, the far edge of a shield - so a
knife flies point first whatever angle the reference posed it at.

Both copies come out of one file, so the game loads one asset per prop and keeps
the half it needs. The originals stay in the scene, parented to the bone, because
the review renders still have to show the fighter carrying his shield.

Called from the build scripts, not by hand:

    prop_kit.extract(parts, RIG, armature, PROP, clean=clean_mesh, unwrap=unwrap)
"""

import os
from collections import namedtuple

import bpy
from mathutils import Matrix, Vector

# A prop to lift off the body: the part-name prefix that identifies it, the bone it
# rides, the object name it takes, and where the FBX goes.
Prop = namedtuple("Prop", "prefix bone name path")


def _plain_log(message):
    print("[prop] " + message)


def split(parts, prefix):
    """Take the parts whose name starts with `prefix` out of `parts`."""
    mine = [part for part in parts if part.name.startswith(prefix)]
    if mine:
        parts[:] = [part for part in parts if not part.name.startswith(prefix)]
    return mine


def merge(parts, name, log=_plain_log):
    """Join parts into one object at world coordinates, without moving it.

    `join_parts` in the build scripts drops the result onto z=0, which is right for
    a body standing on the sand and wrong here: a shield has to keep the offset it
    was modelled at, or the game cannot place it on the arm.
    """
    for obj in bpy.context.selected_objects:
        obj.select_set(False)
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.context.view_layer.update()
    bpy.ops.object.join()
    merged = bpy.context.view_layer.objects.active
    merged.name = name
    merged.data.name = name
    # Bake whatever transform the first part happened to carry, so the vertices are
    # world coordinates and the two exported copies can be written straight out.
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return merged


def bone_frame(arm, bone_name):
    """The bone's rest matrix in world space."""
    return arm.matrix_world @ arm.data.bones[bone_name].matrix_local


def bounds(obj):
    """(min, max) of the mesh in object space, per axis."""
    low = Vector((float("inf"),) * 3)
    high = Vector((float("-inf"),) * 3)
    for vertex in obj.data.vertices:
        low = Vector((min(low[i], vertex.co[i]) for i in range(3)))
        high = Vector((max(high[i], vertex.co[i]) for i in range(3)))
    return low, high


def _duplicate(obj, name, collection=None):
    copy = obj.copy()
    copy.data = obj.data.copy()
    copy.name = name
    copy.data.name = name
    (collection or bpy.context.collection).objects.link(copy)
    return copy


def _unit(axis):
    out = Vector((0.0, 0.0, 0.0))
    out[axis] = 1.0
    return out


def orient_for_flight(obj, pivot, log=_plain_log):
    """Centre the mesh on its own middle and turn it into the flight frame.

    The game draws a thrown weapon with `LookRotation(velocity)`, so the object's
    local +Z is the throw direction and its local +Y is up; the roll about the local
    +Z is what tumbles it. In *Blender* those are -Y and +Z, because the FBX axis
    conversion maps the rig's front, Blender's +Y, to Unity's -Z (see
    `FighterModel.FacingYaw`, which yaws the body half a turn for exactly this
    reason). So: length -> Blender -Y, the side it lies flat on -> +Z (up), and the
    width -> -X, which after the conversion reads as length along +Z, flat side up,
    width along +X.

    The three source axes are orthonormal but their handedness is whatever the
    bounding box gave, so the width here is built as along x flat rather than taken
    from the box, which makes (length, flat, width) a right-handed set and the turn a
    rotation by construction - the determinant is logged, because a mirror would
    silently flip the mesh and still look plausible.
    """
    low, high = bounds(obj)
    size = high - low
    order = sorted(range(3), key=lambda axis: size[axis])
    thin, mid, length = order[0], order[1], order[2]

    along = _unit(length)
    # The end farther from the carrying bone is the business end: a knife's point,
    # not its pommel. A shield is near enough symmetric that either sign rolls the
    # same way, so this rule costs it nothing.
    if abs(low[length] - pivot[length]) > abs(high[length] - pivot[length]):
        along = -along
    flat = _unit(thin)
    across = along.cross(flat)

    # `to_flight` has along -> -Y, flat -> +Z and across -> -X as its columns; those
    # images are right-handed for the same reason the source axes are, so this is the
    # rotation that carries the mesh from one to the other.
    to_flight = Matrix(((0.0, 0.0, -1.0),
                        (-1.0, 0.0, 0.0),
                        (0.0, 1.0, 0.0)))
    turn = to_flight @ Matrix((along, flat, across))
    centre = (low + high) * 0.5
    for vertex in obj.data.vertices:
        vertex.co = turn @ (vertex.co - centre)
    obj.matrix_world = Matrix.Identity(4)
    obj.data.update()
    log("%s: %.3f long (axis %s), %.3f wide, %.3f thick - length down -Y, flat +Z, "
        "turn det %.3f"
        % (obj.name, size[length], "XYZ"[length], size[mid], size[thin], turn.determinant()))
    return size


def export(objects, path, log=_plain_log):
    """Write the given meshes to an FBX, with the skinning export's axis settings.

    The same axis and bake flags as `rig_kit.export_skinned`, deliberately: a prop
    parented to a bone that was converted one way and a body converted another way
    do not line up, and the mismatch is silent until it is seen on screen.
    """
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        object_types={"MESH"},
        path_mode="COPY",
        mesh_smooth_type="FACE",
        bake_space_transform=False,
    )
    log("exported %s (%s)" % (path, ", ".join(obj.name for obj in objects)))
    return path


def extract(parts, rig, armature, prop, clean=None, unwrap=None, log=_plain_log):
    """Lift `prop` off `parts`, export it, and leave it in the scene on the bone.

    Returns the object left in the scene (so the review render still shows the
    fighter carrying it), or None when the builder has no such parts.
    """
    lifted = split(parts, prop.prefix)
    if not lifted:
        log("no %s* parts found - %s not exported" % (prop.prefix, prop.name))
        return None

    body = merge(lifted, prop.name, log=log)
    if clean is not None:
        clean(body)
    if unwrap is not None:
        unwrap(body)
    triangles = sum(len(poly.vertices) - 2 for poly in body.data.polygons)
    frame = bone_frame(armature, prop.bone)
    pivot = frame.translation.copy()

    # Two copies of one object: where the body wears it, and how it looks in the air.
    # Both are written into one file, and both are left at the origin with an identity
    # transform so nothing about their placement is baked into a bone's axes.
    on_arm = _duplicate(body, prop.name + "_arm")
    flying = _duplicate(body, prop.name + "_air")
    orient_for_flight(flying, pivot, log=log)
    export([on_arm, flying], prop.path, log=log)
    for copy in (on_arm, flying):
        bpy.data.objects.remove(copy, do_unlink=True)

    # What stays behind carries the prop through the review renders, including the
    # posed one: parent it to the bone with the inverse of the bone's own matrix, so
    # its world transform is still the identity it was built at.
    body.name = prop.name + "_worn"
    body.parent = armature
    body.parent_type = "BONE"
    body.parent_bone = prop.bone
    body.matrix_parent_inverse = frame.inverted()
    log("%s: %d triangles off %d parts, riding %s (kept in body space)"
        % (prop.name, triangles, len(lifted), prop.bone))
    return body
