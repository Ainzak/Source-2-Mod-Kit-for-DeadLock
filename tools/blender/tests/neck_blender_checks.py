"""Asset-free Blender integration fixtures for the offline neck helper.

Run with factory startup and scripts disabled; pass an absent output directory after --.
"""

import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from neck_contract import require
from neck_job import author, prepare
from neck_verify import capture, verify


def rejection(code, action):
    try:
        action()
    except ValueError as error:
        require(str(error).startswith(code), 'WRONG_REJECTION:' + str(error))
    else:
        raise ValueError('MISSING_REJECTION:' + code)


def mesh(name, count, heights, radius):
    points = [(radius * math.cos(i * 2 * math.pi / count),
               radius * math.sin(i * 2 * math.pi / count), z)
              for z in heights for i in range(count)]
    faces = []
    for level in range(len(heights) - 1):
        for i in range(count):
            a, b = level * count + i, level * count + (i + 1) % count
            faces += [(a, b, b + count), (a, b + count, a + count)]
    data = bpy.data.meshes.new(name)
    data.from_pydata(points, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def main():
    output = Path(sys.argv[sys.argv.index('--') + 1]).resolve()
    require(not output.exists() and output.parent.is_dir(), 'FIXTURE_OUTPUT_EXISTS')
    output.mkdir()
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    head = mesh('fixture_head', 22, [6, 7, 8], 1)
    body = mesh('fixture_body', 48, [0, 2, 5], 1.3)
    uv = head.data.uv_layers.new(name='head_uv')
    for i, item in enumerate(uv.data):
        item.uv = ((i % 17) / 17, (i % 13) / 13)
    color = head.data.color_attributes.new(name='head_color', type='FLOAT_COLOR', domain='CORNER')
    for i, item in enumerate(color.data):
        item.color = ((i % 7) / 7, .25, .5, 1)
    head.vertex_groups.new(name='root').add(list(range(66)), 1, 'REPLACE')
    profile = dict(headBoundaryVertices=list(range(22)), protectedHeadVertices=list(range(44, 66)),
                   protectedBodyVertices=list(range(96)), cutZ=4,
                   blendBounds={'min': [-2, -2, 3.5], 'max': [2, 2, 7.8]},
                   fade={'lower': .6, 'upper': .6}, iterations=12, factor=.4,
                   mergeTolerance=1e-5, maxBlendDisplacement=1,
                   shadeFade={'minZ': 7.8, 'maxZ': 8}, outputObject='fixture_authored')
    bad = dict(profile, headBoundaryVertices=list(range(21)) + [44])
    rejection('NECK_HEAD_BOUNDARY', lambda: prepare(head, body, bad))
    head.matrix_world = Matrix.Translation((1, 0, 0))
    rejection('NECK_IDENTITY_FRAME', lambda: prepare(head, body, profile))
    head.matrix_world = Matrix.Identity(4)
    bad = dict(profile, protectedHeadVertices=[999])
    rejection('NECK_ID_RANGE', lambda: prepare(head, body, bad))
    # A coincident UV-chart record must not silently replace another weight row.
    original_data = head.data.copy()
    head.data.vertices.add(1)
    head.data.vertices[66].co = head.data.vertices[0].co
    head.vertex_groups['root'].add([66], .5, 'REPLACE')
    rejection('NECK_COINCIDENT_WEIGHTS', lambda: prepare(head, body, profile))
    head.data = original_data
    top, center = prepare(head, body, profile)
    original = capture(head, body, profile)
    result, facts = author(head, body, profile, top, center)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output / 'synthetic.blend'))
    bpy.ops.wm.open_mainfile(filepath=str(output / 'synthetic.blend'), use_scripts=False)
    result = bpy.data.objects['fixture_authored']
    report = verify(result, original, profile)
    li = next(i for i, v in enumerate(result.data.attributes['source_head_loop_id'].data)
              if v.value >= 0)
    value = result.data.uv_layers['head_uv'].data[li].uv.copy()
    result.data.uv_layers['head_uv'].data[li].uv.x += .1
    rejection('NECK_HEAD_UV', lambda: verify(result, original, profile))
    result.data.uv_layers['head_uv'].data[li].uv = value
    loop = next(f for f in result.data.polygons
                if result.data.attributes['source_head_face_id'].data[f.index].value >= 0)
    attr = result.data.attributes['source_head_loop_id']
    a, b = list(loop.loop_indices)[:2]
    attr.data[a].value, attr.data[b].value = attr.data[b].value, attr.data[a].value
    rejection('NECK_HEAD_WINDING', lambda: verify(result, original, profile))
    attr.data[a].value, attr.data[b].value = attr.data[b].value, attr.data[a].value
    body_vi = next(v.index for v in result.data.vertices
                   if result.data.attributes['prior_body_vertex_id'].data[v.index].value == 1)
    value = result.data.vertices[body_vi].co.copy()
    result.data.vertices[body_vi].co.x += .1
    rejection('NECK_PROTECTED_BODY', lambda: verify(result, original, profile))
    result.data.vertices[body_vi].co = value
    vi = next(v.index for v in result.data.vertices
              if result.data.attributes['source_head_vertex_id'].data[v.index].value
              in profile['protectedHeadVertices'])
    value = result.data.vertices[vi].co.copy()
    result.data.vertices[vi].co.z += .1
    rejection('NECK_PROTECTED_HEAD', lambda: verify(result, original, profile))
    result.data.vertices[vi].co = value
    original_head = bpy.data.objects['fixture_head']
    value = original_head.data.vertices[0].co.copy()
    original_head.data.vertices[0].co.x += .1
    rejection('NECK_SOURCE_CHANGED', lambda: verify(result, original, profile))
    original_head.data.vertices[0].co = value
    verify(result, original, profile)
    report.update(facts, rejectionChecks=9, passed=True)
    (output / 'checks.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('OK: synthetic saved neck and 9 rejection checks', flush=True)


if __name__ == '__main__':
    main()
