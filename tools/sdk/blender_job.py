"""Headless installed-addon adapter. Run inside Blender with -- --request file."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import MAX_BYTES, canonical, require, sha, write


def snapshot():
    import bpy
    rigs = [o for o in bpy.context.scene.objects if o.type == 'ARMATURE']
    require(len(rigs) == 1, 'ARMATURE_AMBIGUOUS')
    rig = rigs[0]
    meshes = {}
    for obj in bpy.context.scene.objects:
        if obj.type != 'MESH':
            continue
        obj.data.calc_loop_triangles()
        meshes[obj.name] = {
            'positions': [list(obj.matrix_world @ v.co) for v in obj.data.vertices],
            'weights': [{obj.vertex_groups[g.group].name: g.weight for g in v.groups}
                        for v in obj.data.vertices],
            'normals': [list(x.vector) for x in obj.data.corner_normals],
            'uv': [list(x.uv) for x in obj.data.uv_layers.active.data] if obj.data.uv_layers.active else [],
            'corners': [x.vertex_index for x in obj.data.loops],
            'triangles': [{'loops': list(t.loops), 'material': t.material_index}
                          for t in obj.data.loop_triangles],
            'materials': [m.name if m else '' for m in obj.data.materials],
            'matrix': [list(row) for row in obj.matrix_world]}
    require(0 < len(rig.data.bones) <= 512, 'BONE_BUDGET')
    require(sum(len(m['positions']) for m in meshes.values()) <= 1000000, 'VERTEX_BUDGET')
    require(sum(len(m['triangles']) for m in meshes.values()) <= 2000000, 'TRIANGLE_BUDGET')
    return {'bones': [{'name': b.name, 'parent': b.parent.name if b.parent else None,
                       'matrix': [list(row) for row in b.matrix_local]} for b in rig.data.bones],
            'meshes': meshes}


def import_model(path):
    import bpy
    bpy.ops.wm.read_homefile(use_empty=True)
    result = bpy.ops.sourceio.vmdl(filepath=str(path), directory=str(path.parent) + '/',
                                 files=[{'name': path.name}], discover_resources=False,
                                 import_materials=False, import_physics=False,
                                 import_attachments=False, lod_mask=1, scale=1)
    require(result == {'FINISHED'}, 'MODEL_IMPORT_FAILED')


def verify_geometry(expected, actual):
    """Compare actual triangle corners using spatial lookup plus UV/material/named weights.

    Each oriented triangle maps to an explicit equivalence class of identical authored
    corners. Face multiplicity is retained, including inherited duplicate/degenerate faces.
    """
    from mathutils.kdtree import KDTree
    records, triangles = [], []
    for mesh in expected['meshes'].values():
        for tri in mesh['triangles']:
            ids = []
            for loop in tri['loops']:
                vertex = mesh['corners'][loop]
                ids.append(len(records))
                records.append((mesh['positions'][vertex], mesh['uv'][loop],
                                mesh['weights'][vertex], mesh['materials'][tri['material']].split('/')[-1]))
            triangles.append(tuple(ids))
    tree = KDTree(len(records))
    for i, record in enumerate(records):
        tree.insert(record[0], i)
    tree.balance()
    def rotate(ids):
        return min(tuple(ids[i:] + ids[:i]) for i in range(3))
    # Corners with identical declared attributes share a logical identity. Face multiplicity
    # remains explicit; a duplicated face cannot hide a removed different face.
    ids, identities = [], {}
    for p, uv, weights, material in records:
        key = canonical([p, uv, weights, material])
        if key not in identities:
            identities[key] = len(identities)
        ids.append(identities[key])
    wanted = Counter(rotate([ids[i] for i in tri]) for tri in triangles)
    got, max_position, max_uv, max_weight = Counter(), 0, 0, 0
    for mesh in actual['meshes'].values():
        for tri in mesh['triangles']:
            matched = []
            for loop in tri['loops']:
                vertex = mesh['corners'][loop]
                p, uv, weights = mesh['positions'][vertex], mesh['uv'][loop], mesh['weights'][vertex]
                material = mesh['materials'][tri['material']].split('/')[-1]
                matches = {}
                for _, index, distance in tree.find_range(p, 0.0001):
                    _, ref_uv, ref_weights, ref_material = records[index]
                    uv_error = max(abs(x-y) for x, y in zip(uv, ref_uv))
                    weight_error = max((abs(weights.get(k, 0)-ref_weights.get(k, 0))
                                        for k in weights.keys() | ref_weights.keys()), default=0)
                    if material == ref_material and uv_error < 0.000001 and weight_error < 0.005:
                        matches[ids[index]] = (distance, uv_error, weight_error)
                require(len(matches) == 1, 'COMPILED_CORNER_MISSING_OR_AMBIGUOUS')
                identity, errors = next(iter(matches.items()))
                matched.append(identity)
                max_position = max(max_position, errors[0])
                max_uv = max(max_uv, errors[1])
                max_weight = max(max_weight, errors[2])
            got[rotate(matched)] += 1
        require(all(math.isfinite(v) for normal in mesh['normals'] for v in normal), 'NONFINITE_NORMAL')
    require(wanted == got, 'COMPILED_FACE_CORRESPONDENCE_FAILED')
    return {'triangles': sum(got.values()), 'maxPositionError': max_position,
            'maxUvError': max_uv, 'maxNamedWeightError': max_weight,
            'nativeFrameFidelity': 'unverified', 'sourceExportFidelity': 'unverified'}


def main(request):
    import bpy
    bpy.ops.preferences.addon_enable(module='SourceIO')
    bpy.ops.preferences.addon_enable(module='io_scene_valvesource2')
    mode = request['mode']
    out = Path(request['output'])
    import SourceIO
    import io_scene_valvesource2
    modules = {}
    for module in (SourceIO, io_scene_valvesource2):
        folder = Path(module.__file__).parent
        files = {p.relative_to(folder).as_posix(): sha(p) for p in sorted(folder.rglob('*.py'))}
        modules[module.__name__] = {'version': list(module.bl_info.get('version', [])),
                                   'codeHash': __import__('hashlib').sha256(canonical(files)).hexdigest()}
    write(out / 'addons.json', {'blender': bpy.app.version_string, 'addons': modules})
    if mode == 'probe':
        pass
    elif mode == 'prepare':
        import_model(Path(request['source']))
        write(out / 'baseline.json', snapshot())
        bpy.context.preferences.filepaths.save_version = 0
        bpy.ops.wm.save_as_mainfile(filepath=str(out / 'baseline.blend'))
    elif mode == 'export':
        bpy.context.preferences.filepaths.use_scripts_auto_execute = False
        bpy.ops.wm.open_mainfile(filepath=request['baseline'])
        before = snapshot()
        intent = request['intent']
        if intent['version'] == 4:
            source_rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
            source_pose = ([list(r) for r in source_rig.matrix_world],
                           {b.name:[list(r) for r in b.matrix_basis] for b in source_rig.pose.bones})
        if intent['version'] in (2, 3, 4):
            bpy.ops.wm.open_mainfile(filepath=request['returnedBlend'], use_scripts=False)
            returned = snapshot()
            require(set(returned['meshes']) == set(before['meshes']), 'RETURNED_SCENE_INVENTORY_CHANGED')
            require(returned['bones'] == before['bones'], 'AUTHORING_RIG_CHANGED')
            returned_rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
            if intent['version'] == 4:
                require(source_pose == ([list(r) for r in returned_rig.matrix_world],
                        {b.name:[list(r) for r in b.matrix_basis] for b in returned_rig.pose.bones}),
                        'SURFACE_RIG_POSE_CHANGED')
                if intent['surface']['geometryMode'] == 'authored':
                    target = bpy.data.objects[intent['surface']['mesh']].data
                    from surface import derive_surface
                    _, provenance = derive_surface(before['meshes'][intent['surface']['mesh']],
                                                   intent['surface'],before['bones'])
                    require(target.attributes.get('s2_vertex_id') is not None and
                            [x.value for x in target.attributes['s2_vertex_id'].data] == list(range(len(target.vertices))),
                            'SURFACE_STABLE_IDS_CHANGED')
                    require(target.attributes.get('s2_source_face') is not None and
                            [x.value for x in target.attributes['s2_source_face'].data] == provenance['faceSources'],
                            'SURFACE_FACE_IDS_CHANGED')
            require(all(not b.constraints for b in returned_rig.pose.bones), 'RETURNED_POSE_CONSTRAINT_UNSUPPORTED')
            for name, mesh in before['meshes'].items():
                if name != intent.get('replacement', intent.get('patch', intent.get('surface')))['mesh']:
                    require(mesh == returned['meshes'][name], 'RETURNED_PROTECTED_MESH_CHANGED')
            for obj in bpy.context.scene.objects:
                require(not obj.constraints and not obj.animation_data, 'RETURNED_SCENE_ANIMATION_UNSUPPORTED')
                if obj.type == 'MESH':
                    require(not obj.data.shape_keys and all(m.type == 'ARMATURE' for m in obj.modifiers),
                            'RETURNED_MODIFIER_UNSUPPORTED')
                    require(all(m.object == returned_rig for m in obj.modifiers), 'RETURNED_MODIFIER_RIG_CHANGED')
        selected = set(intent['selectedMeshes'])
        require(selected and selected <= before['meshes'].keys(), 'MESH_SELECTION_INVALID')
        require(set(intent['excludedMeshes']) == before['meshes'].keys() - selected,
                'EXCLUDED_INVENTORY_INVALID')
        require(all(name.endswith('_LOD0') for name in selected), 'LOD_PROFILE_INVALID')
        scales = {x['mesh']: x['axes'] for x in intent['scales']}
        require(len(scales) == len(intent['scales']) and scales.keys() <= selected, 'SCALE_SELECTION_INVALID')
        rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
        group = bpy.data.collections.new('sdkcontrol')
        bpy.context.scene.collection.children.link(group)
        group.objects.link(rig)
        for name in sorted(selected):
            obj = bpy.data.objects[name]
            for vertex in obj.data.vertices:
                for axis, scale in enumerate(scales.get(name, [1, 1, 1])):
                    vertex.co[axis] *= scale
            group.objects.link(obj)
        for obj in list(bpy.context.scene.objects):
            if obj != rig and obj.name not in selected:
                bpy.data.objects.remove(obj, do_unlink=True)
        for mat in bpy.data.materials:
            if mat.name in request['materials']:
                mat.name = request['materials'][mat.name]
        rig.animation_data_clear()
        require(snapshot()['bones'] == before['bones'], 'AUTHORING_RIG_CHANGED')
        authored = snapshot()
        write(out / 'authored.json', authored)
        rig.data.vs.implicit_zero_bone = False
        scene = bpy.context.scene
        scene.vs.export_path = str(out)
        scene.vs.export_format = 'DMX'
        scene.vs.dmx_format = '22_modeldoc'
        scene.vs.dmx_encoding = '9'
        scene.vs.dmx_kv2 = True
        scene.vs.qc_compile = False
        import io_scene_valvesource2
        io_scene_valvesource2.utils.State.update_scene()
        bpy.ops.wm.save_as_mainfile(filepath=str(out / 'edited.blend'))
        result = bpy.ops.export_scene.smd(collection='sdkcontrol')
        require(result == {'FINISHED'} and (out / 'sdkcontrol.dmx').is_file(), 'DMX_EXPORT_FAILED')
        if intent['version'] == 4 and intent['bindTransport'] == 'source_rest':
            from io_scene_valvesource2 import datamodel
            from source_facts import restore_bind
            dm = datamodel.load(str(out / 'sdkcontrol.dmx'))
            report = restore_bind(dm,Path(request['sourceData']).read_text(),datamodel)
            (out / 'sdkcontrol.dmx').rename(out / 'sdkcontrol-exporter.dmx')
            dm.write(str(out / 'sdkcontrol.dmx'),'binary',9)
            write(out / 'source-bind-report.json',report)
        require(snapshot()['bones'] == before['bones'], 'EXPORT_RIG_CHANGED')
        require((out / 'sdkcontrol.dmx').stat().st_size <= MAX_BYTES, 'DMX_TOO_LARGE')
    elif mode in ('surface-prepare', 'surface-return', 'surface-review'):
        from surface_blender import execute
        execute(request)
    elif mode == 'verify':
        import_model(Path(request['source']))
        actual = snapshot()
        expected = json.loads(Path(request['authored']).read_text())
        report = verify_geometry(expected, actual)
        write(out / 'geometry.json', report)
        write(out / 'reopened.json', actual)
        bpy.context.preferences.filepaths.save_version = 0
        bpy.ops.wm.save_as_mainfile(filepath=str(out / 'reopened.blend'))
    else:
        raise ValueError('BLENDER_MODE_INVALID')
    print('OK: SDK Blender job ' + mode)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    main(json.loads(Path(args.request).read_text()))
