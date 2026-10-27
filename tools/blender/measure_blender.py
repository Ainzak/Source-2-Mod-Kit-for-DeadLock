"""Read raw mesh basis and rest-bone references; render without saving input scenes."""

from collections import defaultdict
import json
from pathlib import Path
import sys

import bpy
from bpy_extras.object_utils import world_to_camera_view
from mathutils import Matrix, Vector

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from measure_contract import load, sha, validate
from measure_math import (compare, dot, geometry_hash, length, require, rig_hash,
                          section, sub, weighted, world)


def extract(input_spec):
    meshes = {}
    for key, spec in input_spec['meshes'].items():
        obj = bpy.data.objects.get(spec['object'])
        require(obj is not None and obj.type == 'MESH', 'MEASURE_OBJECT_SELECTOR')
        me = obj.data
        require(len(me.vertices) <= 500000 and len(me.polygons) <= 500000, 'MEASURE_MESH_SIZE')
        points = [list(v.co) for v in me.vertices]
        faces = [list(f.vertices) for f in me.polygons]
        matrix = [list(row) for row in obj.matrix_world]
        require(geometry_hash(points, faces, matrix) == spec['geometryHash'], 'MEASURE_MESH_HASH')
        require(all(abs(x) <= 1000000 for p in points for x in p), 'MEASURE_NONFINITE_GEOMETRY')
        require(all(abs(x) <= 1000000 for row in matrix for x in row), 'MEASURE_MATRIX')
        require(matrix[3] == [0, 0, 0, 1], 'MEASURE_AFFINE_MATRIX')
        me.calc_loop_triangles()
        triangles = defaultdict(list)
        for tri in me.loop_triangles:
            triangles[tri.polygon_index].append(list(tri.vertices))
        meshes[key] = {'points': [world(p, matrix) for p in points], 'faces': faces,
                       'triangles': triangles}
    obj = bpy.data.objects.get(input_spec['rig']['object'])
    require(obj is not None and obj.type == 'ARMATURE', 'MEASURE_RIG_SELECTOR')
    bones = {b.name: {'parent': b.parent.name if b.parent else None, 'head': list(b.head_local),
                      'tail': list(b.tail_local), 'matrix': [list(row) for row in b.matrix_local]}
             for b in obj.data.bones}
    matrix = [list(row) for row in obj.matrix_world]
    require(rig_hash(bones, matrix) == input_spec['rig']['restHash'], 'MEASURE_RIG_HASH')
    landmarks = {}
    for name, spec in input_spec['landmarks'].items():
        point = None
        if spec['kind'] == 'surface':
            point = weighted(meshes[spec['mesh']]['points'], spec['ids'], spec['weights'])
        elif spec['kind'] == 'bone':
            require(spec['bone'] in bones, 'MEASURE_BONE_SELECTOR')
            point = world(bones[spec['bone']][spec['endpoint']], matrix)
        landmarks[name] = {'point': point, 'origin': spec['origin'], 'note': spec['note'],
                           'selector': spec}
    regions = {}
    for name, spec in input_spec['regions'].items():
        points, triangles = [], []
        if spec['origin'] != 'unavailable':
            me = meshes[spec['mesh']]
            require(max(spec['faceIds']) < len(me['faces']), 'MEASURE_FACE_ID')
            ids = sorted(set(i for f in spec['faceIds'] for i in me['faces'][f]))
            points = [me['points'][i] for i in ids]
            triangles = [(f, [me['points'][i] for i in tri]) for f in spec['faceIds']
                         for tri in me['triangles'][f]]
        regions[name] = {'points': points, 'triangles': triangles, 'origin': spec['origin'],
                         'note': spec['note'], 'mesh': spec['mesh'], 'faceIds': spec['faceIds']}
    return landmarks, regions


def measure(m, landmarks, regions, sections, ground):
    ls, rs = [landmarks[x] for x in m['landmarks']], [regions[x] for x in m['regions']]
    origins = sorted(set(x['origin'] for x in ls + rs))
    value, reason, points = None, None, []
    kind, axis = m['kind'], m['axis']
    if any(x['point'] is None for x in ls) or any(not x['points'] for x in rs):
        reason = '; '.join(x['note'] for x in ls+rs if x['origin'] == 'unavailable')
    elif kind in ['distance', 'delta', 'angle']:
        points = [x['point'] for x in ls]
        d = sub(points[1], points[0])
        if kind == 'distance':
            value = length(d)
        elif kind == 'delta':
            value = dot(d, axis)
        elif length(d) == 0:
            reason = 'zero-length orientation'
        else:
            import math
            value = math.degrees(math.acos(max(-1, min(1, dot(d, axis)/length(d)))))
    elif kind == 'span':
        positions = [dot(p, axis) for p in rs[0]['points']]
        value = max(positions)-min(positions)
    elif kind == 'projection':
        value = max(dot(sub(p, ls[0]['point']), axis) for p in rs[0]['points'])
    elif kind == 'clearance':
        value = min(dot(sub(p, ground['point']), ground['normal']) for p in rs[0]['points'])
    elif kind == 'peak_distance':
        for region in rs:
            maximum = max(dot(p, axis) for p in region['points'])
            hits = [p for p in region['points'] if maximum-dot(p, axis) <= 1e-5]
            points.append([sum(p[k] for p in hits)/len(hits) for k in range(3)])
        value = length(sub(*points))
    elif kind == 'section_span':
        s = sections[m['section']]
        origins = [s['origin']]
        if s['spans'] is None:
            reason = s['reason']
        else:
            value = s['spans'][s['axes'].index(axis)]
    return {'value': value, 'origins': origins, 'reason': reason, 'points': points}


def render(input_spec, views, landmarks, sections, output, up):
    scene = bpy.context.scene
    for obj in bpy.data.objects:
        obj.hide_render = True
    copies = []
    for key in input_spec['visibleMeshes']:
        original = bpy.data.objects[input_spec['meshes'][key]['object']]
        matrix = original.matrix_world.copy()
        clone = original.copy()
        clone.data = original.data.copy()
        clone.parent = None
        clone.constraints.clear()
        clone.modifiers.clear()
        clone.animation_data_clear()
        if clone.data.shape_keys is not None:
            clone.shape_key_clear()
        clone.matrix_world = matrix
        bpy.context.collection.objects.link(clone)
        clone.hide_render = False
        copies.append(clone)
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.render.resolution_x = scene.render.resolution_y = 750
    scene.render.resolution_percentage = 100
    scene.display.shading.color_type = 'SINGLE'
    scene.display.shading.single_color = (.6, .67, .72)
    scene.display.shading.show_cavity = scene.display.shading.show_shadows = False
    camdata = bpy.data.cameras.new('measurement_camera')
    camera = bpy.data.objects.new('measurement_camera', camdata)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camdata.type = 'ORTHO'
    projections = {}
    for view in views:
        center = Vector(view['center'])
        camdata.ortho_scale = view['scale']
        camdata.clip_end = max(1000, view['scale']*20)
        camera.location = center + Vector(view['direction'])*view['scale']*10
        back = Vector(view['direction'])
        right = Vector(up).cross(back).normalized()
        vertical = back.cross(right).normalized()
        camera.rotation_euler = Matrix((right, vertical, back)).transposed().to_euler()
        bpy.context.view_layer.update()
        scene.render.filepath = str(output / f"{input_spec['id']}-{view['id']}.png")
        bpy.ops.render.render(write_still=True)

        def project(point):
            p = world_to_camera_view(scene, camera, Vector(point))
            return [p.x*750, (1-p.y)*750]

        projections[view['id']] = {
            'landmarks': {key: project(landmarks[key]['point']) for key in view['landmarks']
                          if landmarks[key]['point'] is not None},
            'sections': {key: [project(p) for p in sections[key]['points']]
                         for key in view['sections']}}
    for clone in copies:
        data = clone.data
        bpy.data.objects.remove(clone, do_unlink=True)
        bpy.data.meshes.remove(data)
    return projections


def main():
    args = sys.argv[sys.argv.index('--') + 1:]
    require(len(args) == 1, 'MEASURE_REQUEST')
    request = load(args[0])
    p = validate(request['profile'])
    output = Path(request['output'])
    results = []
    for spec in p['inputs']:
        require(sha(spec['scene']) == spec['sceneHash'], 'MEASURE_SCENE_HASH')
        bpy.ops.wm.open_mainfile(filepath=spec['scene'], use_scripts=False)
        landmarks, regions = extract(spec)
        sections = {}
        for definition in p['sections']:
            region = regions[definition['region']]
            s = section(region['triangles'], definition['point'], definition['normal'],
                        definition['axes'], definition['expectedComponents'])
            s.update(origin=region['origin'], note=region['note'], mesh=region['mesh'])
            if region['origin'] == 'unavailable':
                s.update(status='unavailable', reason=region['note'])
            sections[definition['id']] = s
        measurements = {m['id']: measure(m, landmarks, regions, sections, p['frame']['ground'])
                        for m in p['measurements']}
        projections = render(spec, p['views'], landmarks, sections, output, p['frame']['axes'][2])
        results.append({'id': spec['id'], 'landmarks': landmarks, 'sections': sections,
                        'measurements': measurements, 'projections': projections})
        require(sha(spec['scene']) == spec['sceneHash'], 'MEASURE_INPUT_CHANGED')
    comparisons = []
    for m in p['measurements']:
        values = [dict(x['measurements'][m['id']], input=x['id']) for x in results]
        comparisons.append(dict(m, comparisons=compare(values, m['expectation'], m['absoluteTolerance'])))
    report = {'operation': 'character_measure_result@1', 'version': 1, 'frame': p['frame'],
              'blenderVersion': bpy.app.version_string, 'inputs': results,
              'comparisons': comparisons, 'inputFilesUnchanged': True,
              'offline': True, 'gameReady': False, 'poseTested': False}
    with (output / 'measurements.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2, allow_nan=False)
    print('Read-only measurements:', len(comparisons), 'definitions;', len(p['sections']),
          'sections;', len(results), 'inputs', flush=True)


if __name__ == '__main__':
    main()
