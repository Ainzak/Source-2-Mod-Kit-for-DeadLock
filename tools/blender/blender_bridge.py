"""Run with Blender --background --python ... -- create|extract ... ."""
import argparse
import os
from pathlib import Path
import sys
import tempfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import handoff

EDIT_KEY = "S2ModKit_Edit"


def create(bundle, bundle_hash):
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for m in bundle["members"]:
        mesh = bpy.data.meshes.new(m["memberId"])
        mesh.vertices.add(len(m["positions"]))
        mesh.vertices.foreach_set("co", [handoff.decode(c) for p in m["positions"] for c in p])
        mesh.loops.add(len(m["triangles"]))
        mesh.loops.foreach_set("vertex_index", m["triangles"])
        mesh.polygons.add(len(m["triangles"])//3)
        mesh.polygons.foreach_set("loop_start", list(range(0,len(m["triangles"]),3)))
        mesh.polygons.foreach_set("loop_total", [3]*(len(m["triangles"])//3))
        mesh.update()
        point_ids = mesh.attributes.new("s2mod_vertex_id", "INT", "POINT")
        point_ids.data.foreach_set("value", list(range(len(m["positions"]))))
        corner_ids = mesh.attributes.new("s2mod_corner_id", "INT", "CORNER")
        corner_ids.data.foreach_set("value", list(range(len(m["triangles"]))))
        protected = mesh.attributes.new("s2mod_protected", "BOOLEAN", "POINT")
        ids = set(m["protectedIds"])
        protected.data.foreach_set("value", [i in ids for i in range(len(m["positions"]))])
        obj = bpy.data.objects.new(m["memberId"], mesh)
        bpy.context.scene.collection.objects.link(obj)
        obj["s2mod_member_id"] = m["memberId"]
        obj["s2mod_bundle_sha256"] = bundle_hash
        obj["s2mod_source_decoded_sha256"] = m["sourceDecodedHash"]
        if m["editable"]:
            obj.shape_key_add(name="Source_basis", from_mix=False)
            obj.shape_key_add(name=EDIT_KEY, from_mix=False).value = 1
            obj.active_shape_key_index = 1
            group = obj.vertex_groups.new(name="S2ModKit_Protected")
            if m["protectedIds"]: group.add(m["protectedIds"],1,"REPLACE")
        visible = m["editable"] and m["lod"] == bundle["requiredLods"][0]
        obj.hide_set(not visible)
        obj.hide_render = not visible
        if visible:
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
    bpy.context.scene["s2mod_bundle_sha256"] = bundle_hash
    bpy.context.scene["s2mod_input_sha256"] = bundle["inputHash"]
    bpy.context.scene["s2mod_scope"] = "geometry-only authoring; no compiled writer, rig, UV, weights or frames"


def check_ids(mesh, name, domain, expected):
    attribute = mesh.attributes.get(name)
    handoff.require(attribute is not None and attribute.data_type == "INT" and attribute.domain == domain,
                    "STABLE_ID_ATTRIBUTE_INVALID")
    ids = [0]*len(attribute.data)
    attribute.data.foreach_get("value", ids)
    handoff.require(ids == list(range(expected)), "STABLE_IDS_CHANGED")


def extract(bundle, bundle_hash):
    import bpy
    scene = bpy.context.scene
    handoff.require(scene.get("s2mod_bundle_sha256") == bundle_hash and
                    scene.get("s2mod_input_sha256") == bundle["inputHash"], "SCENE_SOURCE_MISMATCH")
    meshes = [o for o in scene.objects if o.type == "MESH"]
    handoff.require(len(meshes) == len(bundle["members"]), "SCENE_MEMBER_INVENTORY_CHANGED")
    expected = {m["memberId"]:m for m in bundle["members"]}
    seen, returned = set(), []
    for obj in meshes:
        mid = obj.get("s2mod_member_id")
        handoff.require(isinstance(mid, str) and mid in expected and mid not in seen, "SCENE_MEMBER_ID_CHANGED")
        seen.add(mid); m = expected[mid]; mesh = obj.data
        handoff.require(obj.get("s2mod_bundle_sha256") == bundle_hash and
                        obj.get("s2mod_source_decoded_sha256") == m["sourceDecodedHash"], "SCENE_SOURCE_MISMATCH")
        handoff.require(obj.parent is None and not obj.constraints and not obj.modifiers, "SCENE_EVALUATION_UNSUPPORTED")
        handoff.require(all(obj.matrix_world[i][j] == (1 if i==j else 0) for i in range(4) for j in range(4)) and
                        all(obj.matrix_basis[i][j] == (1 if i==j else 0) for i in range(4) for j in range(4)), "OBJECT_TRANSFORM_CHANGED")
        handoff.require(len(mesh.vertices) == len(m["positions"]) and len(mesh.loops) == len(m["triangles"]), "TOPOLOGY_CHANGED")
        check_ids(mesh,"s2mod_vertex_id","POINT",len(m["positions"]))
        check_ids(mesh,"s2mod_corner_id","CORNER",len(m["triangles"]))
        protected = mesh.attributes.get("s2mod_protected")
        handoff.require(protected is not None and protected.data_type == "BOOLEAN" and protected.domain == "POINT",
                        "PROTECTED_ATTRIBUTE_CHANGED")
        flags = [False]*len(mesh.vertices); protected.data.foreach_get("value",flags)
        handoff.require([i for i,flag in enumerate(flags) if flag] == m["protectedIds"], "PROTECTED_ATTRIBUTE_CHANGED")
        indices = [0]*len(mesh.loops); mesh.loops.foreach_get("vertex_index",indices)
        handoff.require(indices == m["triangles"] and len(mesh.polygons) == len(indices)//3 and
                        all(p.loop_start==3*i and p.loop_total==3 for i,p in enumerate(mesh.polygons)), "TOPOLOGY_CHANGED")
        if m["editable"]:
            keys = mesh.shape_keys
            handoff.require(keys is not None and [k.name for k in keys.key_blocks] == ["Source_basis",EDIT_KEY], "SHAPE_KEYS_CHANGED")
            basis, target = keys.key_blocks
            handoff.require(target.relative_key == basis and target.vertex_group == "" and not target.mute and target.value == 1 and
                            keys.use_relative and keys.animation_data is None, "SHAPE_KEY_EVALUATION_CHANGED")
            source = [[handoff.word(c) for c in p.co] for p in basis.data]
            points = [[handoff.word(c) for c in p.co] for p in target.data]
        else:
            handoff.require(mesh.shape_keys is None, "CONTEXT_KEYS_CHANGED")
            source = [[handoff.word(c) for c in p.co] for p in mesh.vertices]
        handoff.require(source == m["positions"], "SOURCE_BASIS_CHANGED")
        handoff.require([[handoff.word(c) for c in p.co] for p in mesh.vertices] == m["positions"], "RAW_SOURCE_MESH_CHANGED")
        if m["editable"]: returned.append({"memberId":mid,"positions":points})
    handoff.require(seen == set(expected), "SCENE_MEMBER_INVENTORY_CHANGED")
    result = {"schemaVersion":1,"kind":"geometry_authoring_delta", "operation":{"name":"author_positions","version":1},
              "inputHash":bundle["inputHash"],"bundleSha256":bundle_hash,
              "blenderVersion":bpy.app.version_string,"targetKey":EDIT_KEY,
              "members":[next(r for r in returned if r["memberId"] == m["memberId"])
                         for m in bundle["members"] if m["editable"]]}
    handoff.verify(bundle,bundle_hash,result)
    return result


def main():
    import bpy
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command",choices=["create","extract"])
    parser.add_argument("--bundle",required=True); parser.add_argument("--bundle-sha256",required=True)
    parser.add_argument("--output",required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--")+1:])
    try:
        bundle = handoff.trusted_bundle(args.bundle,args.bundle_sha256)
        output = Path(args.output).resolve()
        handoff.require(not output.exists(),"OUTPUT_ALREADY_EXISTS")
        if args.command == "create":
            create(bundle,args.bundle_sha256)
            # Validate the unchanged scene before publication, including all collapsed source faces.
            extract(bundle,args.bundle_sha256)
            output.parent.mkdir(parents=True,exist_ok=True)
            fd,name = tempfile.mkstemp(prefix=".s2mod-scene-",suffix=".blend",dir=output.parent)
            os.close(fd); temp = Path(name)
            try:
                bpy.context.preferences.filepaths.save_version = 0
                bpy.ops.wm.save_as_mainfile(filepath=str(temp))
                handoff.publish_existing(temp,output)
            finally:
                temp.unlink(missing_ok=True)
        else:
            handoff.publish_bytes(output,handoff.canonical(extract(bundle,args.bundle_sha256)))
        print(f"OK: Blender {args.command}; geometry-only authoring output published.")
    except (handoff.HandoffError, OSError) as error:
        raise RuntimeError(f"Handoff rejected: {error}") from error


if __name__ == "__main__":
    main()
