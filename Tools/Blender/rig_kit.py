"""Skeleton and skinning for the fighters built by build_*.py.

The character scripts build a static mesh; this module hangs a humanoid armature
under it and binds the skin, so the FBX can be animated instead of imported as a
prop. It is deliberately separate from build_paladin.py: the geometry kit is about
`loft`/`shell` primitives, and a rig is a different job.

    armature = rig_kit.build_armature("Assassin_Rig", rig_kit.landmarks(hip_z=1.02))
    segments = rig_kit.deform_bones(armature)
    for part in parts:                              # before the parts are joined
        rig_kit.weight_part(part, armature, bone=bone_for(part.name),
                            segments=segments)
    mesh = join(parts)
    rig_kit.bind(mesh, armature)
    print(rig_kit.pose_test(mesh, armature, rig_kit.primary_from_groups(mesh)))
    rig_kit.export_skinned(mesh, armature, "assassin.fbx")

Why the weights are computed here instead of with ARMATURE_AUTO: bone-heat needs a
closed, fairly clean surface, and these figures are a few hundred overlapping open
shells (belts, plates, cloth panels). Bone heat refuses that mesh outright, and a
failed parenting op in a background Blender build is silent. The weights here are
computed from the bone segments directly, are deterministic, and give the same
rigid-plate-with-soft-joints result a rigger would paint by hand.

Weighting is per *part*, before the parts are joined, and each part is bound with
the bones nearest its own centre. That matters more than it sounds: on the joined
mesh a belt buckle is about as far from the pelvis as it is from the thigh and the
forearm, and a knife held in a fist hangs 25 cm below the hand, closer to the shin
than to the bone that carries it. Per part, judged from the part's own centre, a
belt is a belt and a knife is in the hand.

Facing: the characters face +Y in Blender (the exporter maps that to Unity's +Z),
so the character's left is -X and bone suffixes are `.L` = -X, `.R` = +X.
"""

import math

import bpy
from mathutils import Vector

# Landmarks for a figure about 1.9 m tall, matching the build scripts' numbers.
# Every one is a plain number, so a shorter or longer character only has to pass
# its own set (see `landmarks`).
DEFAULT_LANDMARKS = {
    "hip_z": 1.020, "spine_z": 1.160, "chest_z": 1.340, "neck_z": 1.520,
    "head_z": 1.630, "head_top": 1.890,
    "shoulder_x": 0.060, "shoulder_out": 0.212, "shoulder_z": 1.452,
    "arm_x": 0.234, "elbow_z": 1.166, "wrist_z": 0.900, "hand_end_z": 0.780,
    "leg_x": 0.130, "knee_z": 0.620, "ankle_z": 0.150,
    "toe_y": 0.130, "toe_end_y": 0.230, "ground": 0.030,
}


def landmarks(**overrides):
    """The default landmark set with the caller's numbers on top."""
    out = dict(DEFAULT_LANDMARKS)
    unknown = [key for key in overrides if key not in out]
    if unknown:
        raise KeyError("unknown landmark(s): %s" % ", ".join(sorted(unknown)))
    out.update(overrides)
    return out


def bone_table(L):
    """(name, head, tail, parent, connect) for the 21-bone humanoid rig.

    Mirroring is done here rather than with Blender's X-mirror, because the
    characters face +Y and the mirror tools assume the opposite."""
    table = [
        ("hips", (0.0, 0.0, L["hip_z"]), (0.0, 0.0, L["spine_z"]), None, False),
        ("spine", (0.0, 0.0, L["spine_z"]), (0.0, 0.0, L["chest_z"]), "hips", True),
        ("chest", (0.0, 0.0, L["chest_z"]), (0.0, 0.0, L["neck_z"]), "spine", True),
        ("neck", (0.0, 0.0, L["neck_z"]), (0.0, 0.0, L["head_z"]), "chest", True),
        ("head", (0.0, 0.0, L["head_z"]), (0.0, 0.0, L["head_top"] - 0.02), "neck", True),
    ]
    for sign, suffix in ((-1.0, ".L"), (1.0, ".R")):
        x, arm = L["leg_x"] * sign, L["arm_x"] * sign
        table += [
            ("shoulder" + suffix, (L["shoulder_x"] * sign, 0.0, L["shoulder_z"]),
             (L["shoulder_out"] * sign, 0.0, L["shoulder_z"]), "chest", False),
            ("upper_arm" + suffix, (L["shoulder_out"] * sign, 0.0, L["shoulder_z"] - 0.004),
             (arm + 0.030 * sign, 0.0, L["elbow_z"]), "shoulder" + suffix, True),
            ("forearm" + suffix, (arm + 0.030 * sign, 0.0, L["elbow_z"]),
             (arm + 0.056 * sign, 0.0, L["wrist_z"]), "upper_arm" + suffix, True),
            ("hand" + suffix, (arm + 0.056 * sign, 0.0, L["wrist_z"]),
             (arm + 0.064 * sign, 0.0, L["hand_end_z"]), "forearm" + suffix, True),
            ("thigh" + suffix, (x, 0.0, L["hip_z"] - 0.020), (x, 0.0, L["knee_z"]),
             "hips", False),
            ("shin" + suffix, (x, 0.0, L["knee_z"]), (x, 0.0, L["ankle_z"]),
             "thigh" + suffix, True),
            ("foot" + suffix, (x, 0.0, L["ankle_z"]),
             (x, L["toe_y"], L["ground"]), "shin" + suffix, True),
            ("toe" + suffix, (x, L["toe_y"], L["ground"]),
             (x, L["toe_end_y"], L["ground"]), "foot" + suffix, True),
        ]
    return table


def build_armature(name, landmarks_dict=None, collection=None):
    """Create the armature and return the object (in OBJECT mode, at rest)."""
    L = landmarks_dict or landmarks()
    arm_data = bpy.data.armatures.new(name)
    arm = bpy.data.objects.new(name, arm_data)
    (collection or bpy.context.collection).objects.link(arm)
    bpy.ops.object.select_all(action="DESELECT")
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        for bone_name, head, tail, parent, connect in bone_table(L):
            bone = arm_data.edit_bones.new(bone_name)
            bone.head = Vector(head)
            bone.tail = Vector(tail)
            bone.roll = 0.0
            if parent:
                bone.parent = arm_data.edit_bones[parent]
                bone.use_connect = bool(connect)
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
    return arm


def deform_bones(arm):
    """The bones a vertex can bind to, as (name, head, tail) in world space."""
    out = []
    for bone in arm.data.bones:
        out.append((bone.name, arm.matrix_world @ bone.head_local,
                    arm.matrix_world @ bone.tail_local))
    return out


def chain_bones(arm, name):
    """A bone plus its parent and children: what a part is allowed to blend across.

    Blending across the skeleton rather than across space is what stops the outer
    face of a thigh from picking up the *hand*, which sits the same distance away
    on the other side of the hip and would otherwise drag the leg with every arm
    swing. A part blends at a joint only with the bones that meet there."""
    bone = arm.data.bones.get(name)
    if bone is None:
        raise KeyError("no such bone: %s" % name)
    names = [bone.name]
    if bone.parent is not None:
        names.append(bone.parent.name)
    names.extend(child.name for child in bone.children)
    return names


def _distance_to_segment(point, head, tail):
    segment = tail - head
    length2 = segment.length_squared
    if length2 < 1e-12:
        return (point - head).length
    t = max(0.0, min(1.0, (point - head).dot(segment) / length2))
    return (point - (head + segment * t)).length


def weight_part(obj, arm, bone=None, segments=None, power=4.0, blend=0.004,
                ratio=2.2, floor=0.03):
    """Give one part vertex groups for `arm` and return its primary bone per vertex.

    `bone` pins every vertex of the part to that bone at weight 1.0, which is what
    the wielded knives and the hanging cloth need. A *sequence* of names instead
    asks for that exact set to be blended across, which is how a floor-length robe
    is handled: it hangs around the pelvis, both thighs and both shins at once, so
    no single bone's chain describes it, and pinning it to the pelvis would leave a
    raised knee outside the cloth. That path also drops `ratio` and `floor` (they
    are step functions in space, and a step in the weights is a step in the skin:
    with them the robe tore a 7 cm gap along the line where one leg's shin stopped
    being the nearer bone). Otherwise the candidates are the bone nearest the
    part's own centre and that bone's parent and children, and each vertex is
    blended between those by inverse distance.

    `power` sets how rigid the result is: a vertex mid-thigh is hundreds of times
    closer to its own bone than to the next candidate, so it lands at weight ~1,
    while a vertex on the knee joint is nearly equidistant and splits. `ratio` and
    `floor` drop contributions that would only add noise (a far bone at 2% weight
    is still a visible twitch once the limb swings)."""
    segments = segments if segments is not None else deform_bones(arm)
    bones = {name: (head, tail) for name, head, tail in segments}
    blend_set = None
    if bone is not None and not isinstance(bone, str):
        blend_set = list(bone)
        missing = [name for name in blend_set if name not in bones]
        if missing:
            raise KeyError("no such bone(s): %s" % ", ".join(missing))
        bone = None
        # Softer than the proximity path too: with power 4 the hem right beside one
        # ankle was 98/2 between the two shins and its neighbour four centimetres
        # away was 78/22, which stretched the cloth x2.4 when a leg lifted.
        ratio, floor, power = float("inf"), 0.0, 2.0
    elif bone is not None and bone not in bones:
        raise KeyError("no such bone: %s" % bone)
    groups = {name: obj.vertex_groups.get(name) or obj.vertex_groups.new(name=name)
              for name, _, _ in segments}
    total_verts = max(1, len(obj.data.vertices))
    centre = Vector((0.0, 0.0, 0.0))
    for vertex in obj.data.vertices:
        centre += obj.matrix_world @ vertex.co
    centre /= total_verts
    candidates = list(blend_set) if blend_set is not None else []
    if bone is None and blend_set is None:
        nearest = min((_distance_to_segment(centre, head, tail), name)
                      for name, head, tail in segments)[1]
        candidates = [name for name in chain_bones(arm, nearest) if name in bones]
    primary = []
    for index, vertex in enumerate(obj.data.vertices):
        if bone is not None:
            groups[bone].add([index], 1.0, "REPLACE")
            primary.append(bone)
            continue
        point = obj.matrix_world @ vertex.co
        scored = sorted(((_distance_to_segment(point, bones[name][0], bones[name][1]),
                          name) for name in candidates))
        cutoff = scored[0][0] * ratio + blend
        scored = [(distance, name) for distance, name in scored if distance <= cutoff]
        weights = [(name, 1.0 / (distance + blend) ** power) for distance, name in scored]
        total = sum(weight for _, weight in weights)
        weights = [(name, weight / total) for name, weight in weights]
        kept = [(name, share) for name, share in weights if share >= floor]
        if not kept:
            kept = [max(weights, key=lambda item: item[1])]
        kept_total = sum(share for _, share in kept)
        best, best_weight = None, 0.0
        for name, share in kept:
            share /= kept_total
            groups[name].add([index], share, "REPLACE")
            if share > best_weight:
                best, best_weight = name, share
        primary.append(best)
    return primary


def bind(mesh, arm):
    """Attach an already-weighted mesh to the armature."""
    mesh.parent = arm
    mesh.matrix_parent_inverse = arm.matrix_world.inverted()
    modifier = mesh.modifiers.new("Armature", "ARMATURE")
    modifier.object = arm
    modifier.use_vertex_groups = True
    return modifier


def primary_from_groups(mesh):
    """The heaviest group per vertex, i.e. what `pose_test` sorts by."""
    names = {group.index: group.name for group in mesh.vertex_groups}
    out = []
    for vertex in mesh.data.vertices:
        best = max(vertex.groups, key=lambda element: element.weight, default=None)
        out.append(names.get(best.group) if best else None)
    return out


def deformed_coords(mesh):
    """Vertex positions after modifiers - the only honest read of a skinned mesh."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = mesh.evaluated_get(depsgraph)
    temporary = evaluated.to_mesh()
    coords = [vertex.co.copy() for vertex in temporary.vertices]
    evaluated.to_mesh_clear()
    return coords


def set_pose(arm, pose):
    """pose is {bone_name: (rx, ry, rz)} in degrees. The rest pose is {}."""
    for bone in arm.pose.bones:
        bone.rotation_mode = "XYZ"
        bone.rotation_euler = (0.0, 0.0, 0.0)
        bone.location = (0.0, 0.0, 0.0)
    for name, angles in (pose or {}).items():
        bone = arm.pose.bones.get(name)
        if bone is None:
            raise KeyError("no such bone: %s" % name)
        bone.rotation_euler = tuple(math.radians(a) for a in angles)
    bpy.context.view_layer.update()


PROBES = (("upper_arm.L", "Z"), ("thigh.R", "X"), ("head", "X"))
FAR_SIDE = {"upper_arm.L": ("foot.R",), "thigh.R": ("foot.L",),
            "head": ("foot.L", "foot.R")}


def pose_test(mesh, arm, primary=None, probes=PROBES, angle=45.0):
    """Rotate one bone at a time and measure what the skin actually does.

    Each row is (own, head, far): how far the probe bone's own vertices move, how
    far the head moves, and how far the foot on the other side moves. Movement in
    the first column is the rig working; movement in the other two is weight
    bleeding across the body, where a leg bone must not move the head and one arm
    must not move the opposite foot. A mesh that is not bound at all reports 0.000
    in all three because nothing deforms."""
    set_pose(arm, {})
    rest = deformed_coords(mesh)
    report = {}
    for name, axis in probes:
        pose = {name: tuple(angle if index == "XYZ".index(axis) else 0.0
                            for index in range(3))}
        set_pose(arm, pose)
        moved = deformed_coords(mesh)
        far = FAR_SIDE.get(name, ())
        own = head = foreign = 0.0
        for index, (before, after) in enumerate(zip(rest, moved)):
            shift = (after - before).length
            group = None if primary is None else primary[index]
            if group == name:
                own = max(own, shift)
            elif group == "head":
                head = max(head, shift)
            elif group in far:
                foreign = max(foreign, shift)
        report[name] = (own, head, foreign)
    set_pose(arm, {})
    return report


def showcase_pose(arm):
    """A readable test pose: right arm forward, left arm out, one knee up."""
    set_pose(arm, {
        "upper_arm.R": (0.0, 0.0, -52.0),
        "forearm.R": (0.0, 0.0, -38.0),
        "upper_arm.L": (0.0, 0.0, 34.0),
        "forearm.L": (0.0, 0.0, 24.0),
        "thigh.L": (26.0, 0.0, 0.0),
        "shin.L": (-34.0, 0.0, 0.0),
        "spine": (-6.0, 0.0, 8.0),
        "head": (4.0, 0.0, -10.0),
    })


def clear_pose(arm):
    set_pose(arm, {})


def export_skinned(mesh, arm, path):
    """Write the FBX with the armature, the weights and the rest pose.

    bake_space_transform stays off here, unlike the unrigged exports: baking the
    axis conversion into the vertices of a skinned mesh moves the skin without
    moving the bind pose, so the armature no longer matches the body."""
    import os
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        object_types={"MESH", "ARMATURE"},
        path_mode="COPY",
        mesh_smooth_type="FACE",
        bake_space_transform=False,
        add_leaf_bones=False,
    )
    return path
