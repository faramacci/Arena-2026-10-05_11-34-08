"""Re-import a rigged FBX and prove the skeleton and the skin came with it.

`check_fbx.py` reports the mesh; this reports the rig on the *shipped* file, in the
same spirit: an armature with no weights, or weights bound to the wrong bones, is
invisible in a triangle count and very visible in a game. It fails (non-zero exit)
when the file has no armature, when the mesh is not skinned to it, or when posing a
bone does not move the skin.

    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_rig.py
    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_rig.py -- assassin
"""

import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
FIGHTERS = os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters")

# Bones every character must have, and the probes used to prove the skin follows.
REQUIRED = ("hips", "spine", "chest", "neck", "head", "upper_arm.L", "forearm.L",
            "hand.L", "thigh.R", "shin.R", "foot.R")
PROBES = (("upper_arm.L", "Z"), ("forearm.R", "Z"), ("thigh.L", "X"), ("head", "X"))


def deformed_coords(mesh):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = mesh.evaluated_get(depsgraph)
    temporary = evaluated.to_mesh()
    coords = [vertex.co.copy() for vertex in temporary.vertices]
    evaluated.to_mesh_clear()
    return coords


def main():
    name = "assassin"
    if "--" in sys.argv:
        rest = sys.argv[sys.argv.index("--") + 1:]
        if rest:
            name = rest[0]
    FBX = os.path.join(FIGHTERS, name + ".fbx") if not name.endswith(".fbx") else name
    if not os.path.exists(FBX):
        print("[rig] missing " + FBX)
        return 1

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=FBX)
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    arms = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    if not meshes:
        print("[rig] FAIL: no mesh in the FBX")
        return 1
    if not arms:
        print("[rig] FAIL: no armature in the FBX - this is a static export")
        return 1
    mesh, arm = meshes[0], arms[0]
    bones = [bone.name for bone in arm.data.bones]
    missing = [bone for bone in REQUIRED if bone not in bones]

    modifiers = [mod for mod in mesh.modifiers if mod.type == "ARMATURE"]
    groups = {group.name: group.index for group in mesh.vertex_groups}
    weighted = 0
    for vertex in mesh.data.vertices:
        if any(element.weight > 0.01 for element in vertex.groups):
            weighted += 1
    total = len(mesh.data.vertices)

    print("[rig] file        %s (%.0f KB)" % (FBX, os.path.getsize(FBX) / 1024.0))
    print("[rig] armature    %s, %d bones" % (arm.name, len(bones)))
    print("[rig] bones       %s" % ", ".join(bones))
    print("[rig] skinned     modifier %s, %d vertex groups"
          % ("yes" if modifiers else "NO", len(groups)))
    print("[rig] weighted    %d/%d vertices (%.1f%%)"
          % (weighted, total, 100.0 * weighted / max(1, total)))
    print("[rig] bind pose   feet at z=%.3f (armature rest %s)"
          % (min((mesh.matrix_world @ v.co).z for v in mesh.data.vertices),
             "clear" if all(all(abs(a) < 1e-6 for a in bone.rotation_euler)
                            for bone in arm.pose.bones) else "POSED"))

    rest = deformed_coords(mesh)
    moved_any = False
    for bone_name, axis in PROBES:
        bone = arm.pose.bones.get(bone_name)
        if bone is None:
            print("[rig] probe       %s MISSING" % bone_name)
            continue
        bone.rotation_mode = "XYZ"
        for bone in arm.pose.bones:
            bone.rotation_euler = (0.0, 0.0, 0.0)
        target = arm.pose.bones[bone_name]
        target.rotation_euler = tuple(
            0.7854 if index == "XYZ".index(axis) else 0.0 for index in range(3))
        bpy.context.view_layer.update()
        posed = deformed_coords(mesh)
        span = max((after - before).length for before, after in zip(rest, posed))
        moved_any = moved_any or span > 0.01
        print("[rig] probe       %-12s rotate 45 deg -> skin moves %.3f m"
              % (bone_name, span))
    for bone in arm.pose.bones:
        bone.rotation_euler = (0.0, 0.0, 0.0)
    bpy.context.view_layer.update()

    ok = bool(arms) and not missing and bool(modifiers) and weighted > total * 0.9 \
        and moved_any
    if missing:
        print("[rig] missing     %s" % ", ".join(missing))
    print("[rig] verdict     " + ("PASS" if ok else "REVIEW"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
