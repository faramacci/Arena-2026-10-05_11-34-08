"""Re-import an exported FBX and report what a game engine will actually get.

The build scripts write the file; this reads it back through Blender's FBX
importer, so scale, orientation, material slots and triangle count are verified
on the shipped artifact instead of on the in-memory mesh that produced it.

    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_fbx.py
    "O:/Blender/blender.exe" --background --factory-startup --python Tools/Blender/check_fbx.py -- mage
"""

import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
FIGHTERS = os.path.join(PROJECT, "Assets", "Resources", "Arena", "Fighters")


def main():
    # argparse would eat Blender's own flags, so take a bare name after "--".
    name = "paladin"
    if "--" in sys.argv:
        rest = sys.argv[sys.argv.index("--") + 1:]
        if rest:
            name = rest[0]
    FBX = os.path.join(FIGHTERS, name + ".fbx") if not name.endswith(".fbx") else name
    if not os.path.exists(FBX):
        print("[check] missing " + FBX)
        return 1
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=FBX)
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not meshes:
        print("[check] FAIL: the FBX contains no mesh objects")
        return 1

    triangles = 0
    slots = set()
    degenerate = 0
    low = [1e9] * 3
    high = [-1e9] * 3
    for obj in meshes:
        for poly in obj.data.polygons:
            triangles += len(poly.vertices) - 2
            if poly.area < 1e-9:
                degenerate += 1
        for slot in obj.data.materials:
            slots.add(slot.name if slot else "None")
        for vertex in obj.data.vertices:
            world = obj.matrix_world @ vertex.co
            for axis in range(3):
                low[axis] = min(low[axis], world[axis])
                high[axis] = max(high[axis], world[axis])

    print("[check] file        %s (%.0f KB)" % (FBX, os.path.getsize(FBX) / 1024.0))
    print("[check] mesh name   %s" % ", ".join(obj.name for obj in meshes))
    print("[check] meshes      %s" % ", ".join(obj.name for obj in meshes))
    print("[check] triangles   %d" % triangles)
    print("[check] degenerate  %d" % degenerate)
    print("[check] materials   %d  (%s)" % (len(slots), ", ".join(sorted(slots))))
    print("[check] uv layers   %s"
          % ", ".join(sorted({layer.name for obj in meshes for layer in obj.data.uv_layers})))
    # Unity is Y-up: Blender's x/y/z map to Unity's x/z/y with the character
    # facing +Z, so the height Unity reports is Blender's z extent.
    print("[check] bounds      height %.3f m, width %.3f m, depth %.3f m"
          % (high[2] - low[2], high[0] - low[0], high[1] - low[1]))
    print("[check] feet at     z=%.3f (0 means the model stands on the origin)"
          % low[2])
    ok = (degenerate == 0 and abs(low[2]) < 0.01
          and 1.6 < (high[2] - low[2]) < 2.4)
    print("[check] verdict     " + ("PASS" if ok else "REVIEW"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
