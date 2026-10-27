"""Asset-free native extraction, world-frame and immutable-input measurement checks."""

import copy
import json
from pathlib import Path
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from measure_contract import sha, validate
from measure_math import geometry_hash, require, rig_hash
from measure_blender import extract, main as worker


def rejection(code, action):
    try:
        action()
    except ValueError as error:
        require(str(error).startswith(code), 'WRONG_REJECTION:' + str(error))
    else:
        raise ValueError('MISSING_REJECTION:' + code)


def main():
    output = Path(sys.argv[sys.argv.index('--')+1]).resolve()
    require(not output.exists() and output.parent.is_dir(), 'FIXTURE_OUTPUT_EXISTS')
    output.mkdir()
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    vertices = [[x, y, z] for z in [0, 4] for y in [-2, 2] for x in [-1, 1]]
    faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3],
             [0, 4, 6, 2], [1, 3, 7, 5]]
    policy = json.loads((Path(__file__).parent/'fixtures/measure-profile.json').read_text())
    for spec in policy['inputs']:
        points = copy.deepcopy(vertices)
        if spec['id'] == 'candidate':
            points[1][0] = 2  # Obvious finger-end extension with identical source rig.
            points[6][2] = 3  # Lowered surface shoulder while structural reference stays fixed.
        name = spec['meshes']['skin']['object']
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(points, [], faces)
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.collection.objects.link(obj)
        obj.matrix_world = Matrix.Translation((10, 0, 0)) @ Matrix.Diagonal((2, 3, 4, 1))
        spec['meshes']['skin']['geometryHash'] = geometry_hash(
            points, faces, [list(row) for row in obj.matrix_world])
    armature = bpy.data.armatures.new('fixture_rig')
    rig = bpy.data.objects.new('fixture_rig', armature)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    root = armature.edit_bones.new('root')
    root.head, root.tail = (0, 0, 0), (0, 0, 1)
    neck = armature.edit_bones.new('neck')
    neck.head, neck.tail, neck.parent = (0, 0, 3), (0, 0, 4), root
    bpy.ops.object.mode_set(mode='OBJECT')
    # Even a posed rig and active key must not leak into declared basis-only reports.
    rig.pose.bones['root'].location = (0, 200, 0)
    for spec in policy['inputs']:
        obj = bpy.data.objects[spec['meshes']['skin']['object']]
        obj.vertex_groups.new(name='root').add(list(range(8)), 1, 'REPLACE')
        modifier = obj.modifiers.new('posed_context', 'ARMATURE')
        modifier.object = rig
        obj.shape_key_add(name='Basis')
        key = obj.shape_key_add(name='active_context')
        for point in key.data:
            point.co.y += 200
        key.value = 1
    bones = {b.name: {'parent': b.parent.name if b.parent else None, 'head': list(b.head_local),
                      'tail': list(b.tail_local), 'matrix': [list(row) for row in b.matrix_local]}
             for b in armature.bones}
    rhash = rig_hash(bones, [list(row) for row in rig.matrix_world])
    scene = output/'fixture.blend'
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(scene))
    input_hash = sha(scene)
    for spec in policy['inputs']:
        spec.update(scene=str(scene), sceneHash=input_hash)
        spec['rig']['restHash'] = rhash
    validate(policy)
    good = policy['inputs'][0]
    for code, change in [
        ('MEASURE_MESH_HASH', lambda p: p['meshes']['skin'].update(geometryHash='a'*64)),
        ('MEASURE_RIG_HASH', lambda p: p['rig'].update(restHash='a'*64)),
        ('MEASURE_VERTEX_ID', lambda p: p['landmarks']['jaw'].update(ids=[1000])),
        ('MEASURE_BONE_SELECTOR', lambda p: p['landmarks']['neck'].update(bone='absent')),
        ('MEASURE_FACE_ID', lambda p: p['regions']['torso'].update(faceIds=[1000])),
        ('MEASURE_OBJECT_SELECTOR', lambda p: p['meshes']['skin'].update(object='absent'))]:
        bad = copy.deepcopy(good)
        change(bad)
        rejection(code, lambda: extract(bad))
    job = output/'job'
    job.mkdir()
    request = output/'request.json'
    request.write_text(json.dumps({'profile': policy, 'output': str(job)}), encoding='utf-8')
    sys.argv = ['blender', '--', str(request)]
    worker()
    result = json.loads((job/'measurements.json').read_text())
    values = {m['id']: m['comparisons'] for m in result['comparisons']}
    require([v['value'] for v in values['finger_length']] == [4, 6], 'FIXTURE_WORLD_DISTANCE')
    require([v['value'] for v in values['jaw_shoulder_height']] == [0, 4], 'FIXTURE_SHOULDER')
    require([v['value'] for v in values['bone_length']] == [3, 3], 'FIXTURE_RIG_REFERENCE')
    require(values['finger_length'][1]['review'] == 'review'
            and values['jaw_shoulder_height'][1]['review'] == 'review', 'FIXTURE_SURFACE_WARNING')
    require(values['jaw_shoulder_height'][1]['percentDelta'] is None, 'FIXTURE_ZERO_PERCENT')
    require([v['value'] for v in values['section_depth']] == [12, 12], 'FIXTURE_SECTION_DEPTH')
    require(abs(values['section_width'][0]['value']-4) < 1e-6, 'FIXTURE_SECTION_WIDTH')
    for spec in policy['inputs']:
        path = job/f"{spec['id']}-full.png"
        image = bpy.data.images.load(str(path), check_existing=False)
        require(max(image.pixels[::4]) > .35, 'FIXTURE_EMPTY_FULL_RENDER')
        bpy.data.images.remove(image)
    require(sha(scene) == input_hash, 'FIXTURE_INPUT_CHANGED')
    result = {'passed': True, 'nativeRejections': 6, 'worldFrameVerified': True,
              'rigUnchangedSurfaceWarnings': True, 'nonemptyWideCameraRenders': True,
              'basisRenderIgnoresPoseAndKeys': True,
              'inputHash': input_hash, 'inputUnchanged': True}
    (output/'checks.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print('OK: native measurement extraction, immutable rig/surface counterexamples and 6 rejections', flush=True)


if __name__ == '__main__':
    main()
