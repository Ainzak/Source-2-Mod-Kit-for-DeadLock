"""Blender worker for explicitly selected, identity-frame neck authoring."""

import argparse
from bisect import bisect_right
from collections import defaultdict
import json
import math
from pathlib import Path
import sys

import bpy
import bmesh
from mathutils import Matrix, Vector

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from neck_contract import load, require, sha, validate
from neck_verify import capture, verify


def weights(obj, vertex):
    return {obj.vertex_groups[g.group].name: g.weight for g in vertex.groups}


def cyclic_ring(points, edges, center):
    """Reject boundaries that cannot be represented by a single angular ring."""
    ordered = sorted(points, key=lambda p: math.atan2(p.y - center.y, p.x - center.x))
    indices = {tuple(p): i for i, p in enumerate(ordered)}
    require(len(indices) == len(ordered), 'NECK_DUPLICATE_RING_POINT')
    actual = {frozenset((indices[tuple(a)], indices[tuple(b)])) for a, b in edges}
    expected = {frozenset((i, (i + 1) % len(ordered))) for i in range(len(ordered))}
    require(actual == expected, 'NECK_NONSTAR_BOUNDARY')
    return ordered


def prepare(head, body, profile):
    for obj in [head, body]:
        require(obj.type == 'MESH' and obj.data.shape_keys is None, 'NECK_MESH_LAYOUT')
        require(obj.matrix_world == Matrix.Identity(4), 'NECK_IDENTITY_FRAME')
        require(len(obj.data.vertices) <= 200000, 'NECK_MESH_SIZE')
        require(all(math.isfinite(x) for v in obj.data.vertices for x in v.co),
                'NECK_NONFINITE_GEOMETRY')
        require(not any(a.name.startswith('source_head_') or a.name == 'prior_body_vertex_id'
                        for a in obj.data.attributes), 'NECK_RESERVED_ATTRIBUTE')
    require(all(len(f.vertices) == 3 for f in head.data.polygons), 'NECK_HEAD_TRIANGLES')
    for key, obj in [('headBoundaryVertices', head), ('protectedHeadVertices', head),
                     ('protectedBodyVertices', body)]:
        require(max(profile[key]) < len(obj.data.vertices), 'NECK_ID_RANGE')
    selected = {tuple(head.data.vertices[i].co) for i in profile['headBoundaryVertices']}
    require(len(selected) == len(profile['headBoundaryVertices']), 'NECK_BOUNDARY_DUPLICATES')
    # Weld only in this analysis graph. Source mesh UV charts stay intact.
    edgecounts = defaultdict(int)
    for face in head.data.polygons:
        coords = [tuple(head.data.vertices[i].co) for i in face.vertices]
        for a, b in zip(coords, coords[1:] + coords[:1]):
            edgecounts[frozenset((a, b))] += 1
    require(all(n <= 2 for edge, n in edgecounts.items() if edge & selected),
            'NECK_NONMANIFOLD_HEAD_BOUNDARY')
    boundary = [list(e) for e, n in edgecounts.items() if n == 1 and e & selected]
    require(len(boundary) == len(selected)
            and all(set(e) <= selected for e in boundary), 'NECK_HEAD_BOUNDARY')
    center = sum((Vector(p) for p in selected), Vector()) / len(selected)
    top = cyclic_ring([Vector(p) for p in selected],
                      [(Vector(a), Vector(b)) for a, b in boundary], center)
    require(all(p.z > profile['cutZ'] + profile['mergeTolerance'] for p in top),
            'NECK_ENDPOINT_HEIGHT')
    rows = {}
    for vertex in head.data.vertices:
        if tuple(vertex.co) in selected:
            row = weights(head, vertex)
            require(tuple(vertex.co) not in rows or rows[tuple(vertex.co)] == row,
                    'NECK_COINCIDENT_WEIGHTS')
            rows[tuple(vertex.co)] = row
    return top, center


def author(head, body, profile, top, center):
    original_head = head
    copies = []
    for obj, label in [(body, 'body'), (head, 'head')]:
        clone = obj.copy()
        clone.data = obj.data.copy()
        clone.name = profile['outputObject'] + '_' + label
        bpy.context.collection.objects.link(clone)
        clone.hide_set(False)
        clone.hide_render = False
        copies.append(clone)
    body, head = copies
    angle = lambda p: math.atan2(p.y - center.y, p.x - center.x)
    fans = defaultdict(Vector)
    for face in head.data.polygons:
        for i in face.vertices:
            fans[tuple(head.data.vertices[i].co)] += face.normal * face.area

    def tangent(ring, i, normal):
        around = (ring[(i + 1) % len(ring)] - ring[(i - 1) % len(ring)]).normalized()
        up = around.cross(normal.normalized()).normalized()
        if up.z < 0:
            up.negate()
        require(up.z > .3, 'NECK_HORIZONTAL_TANGENT')
        return up / up.z

    topderiv = [tangent(top, i, fans[tuple(p)]) for i, p in enumerate(top)]
    prior = body.data.attributes.new('prior_body_vertex_id', 'INT', 'POINT')
    for i, value in enumerate(prior.data):
        value.value = i
    prior_positions = [tuple(v.co) for v in body.data.vertices]
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bmesh.ops.bisect_plane(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
                          dist=1e-6, plane_co=(0, 0, profile['cutZ']), plane_no=(0, 0, 1),
                          clear_outer=True, clear_inner=False)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    bm.normal_update()
    edges = [e for e in bm.edges if e.is_boundary
             and all(abs(v.co.z - profile['cutZ']) < 1e-5 for v in e.verts)]
    bottomverts = set(v for e in edges for v in e.verts)
    require(3 <= len(bottomverts) <= 4096, 'NECK_CUT_BOUNDARY')
    bottomcoords = cyclic_ring([v.co.copy() for v in bottomverts],
                              [(e.verts[0].co, e.verts[1].co) for e in edges], center)
    vertices = {tuple(v.co): v for v in bottomverts}
    bottom = [vertices[tuple(p)] for p in bottomcoords]
    bottomderiv = [tangent(bottomcoords, i, v.normal) for i, v in enumerate(bottom)]
    bottomangles = [angle(p) for p in bottomcoords]
    topangles = [angle(p) for p in top]

    def interpolate(theta, angles, values):
        theta = (theta - angles[0]) % (2 * math.pi) + angles[0]
        extended = angles + [angles[0] + 2 * math.pi]
        i = min(bisect_right(extended, theta) - 1, len(angles) - 1)
        require(extended[i + 1] - extended[i] > 1e-8, 'NECK_ANGULAR_COLLISION')
        t = (theta - extended[i]) / (extended[i + 1] - extended[i])
        return values[i].lerp(values[(i + 1) % len(values)], t)

    def bridge(a, b):
        aa, bb = [angle(v.co) for v in a], [angle(v.co) for v in b]
        i = j = 0
        while i < len(a) or j < len(b):
            na = (aa[(i + 1) % len(a)] + (2 * math.pi if i + 1 >= len(a) else 0)
                  if i < len(a) else math.inf)
            nb = (bb[(j + 1) % len(b)] + (2 * math.pi if j + 1 >= len(b) else 0)
                  if j < len(b) else math.inf)
            if na < nb:
                bm.faces.new((a[i % len(a)], a[(i + 1) % len(a)], b[j % len(b)]))
                i += 1
            else:
                bm.faces.new((a[i % len(a)], b[(j + 1) % len(b)], b[j % len(b)]))
                j += 1

    angles = sorted(set(round(a, 9) for a in bottomangles + topangles))
    last = bottom
    for t in [.12, .25, .4, .55, .7, .82, .9, .96]:
        ring = []
        for theta in angles:
            b = interpolate(theta, bottomangles, bottomcoords)
            p = interpolate(theta, topangles, top)
            db = interpolate(theta, bottomangles, bottomderiv) * (p.z - b.z)
            dp = interpolate(theta, topangles, topderiv) * (p.z - b.z)
            q = (b * (2 * t**3 - 3 * t*t + 1) + db * (t**3 - 2 * t*t + t)
                 + p * (-2 * t**3 + 3 * t*t) + dp * (t**3 - t*t))
            ring.append(bm.verts.new(q))
        ring.sort(key=lambda v: angle(v.co))
        bridge(last, ring)
        last = ring
    bridge(last, [bm.verts.new(p) for p in top])
    prior_layer = bm.verts.layers.int['prior_body_vertex_id']
    for v in bm.verts:
        old = v[prior_layer]
        if old < 0 or old >= len(prior_positions) or tuple(v.co) != prior_positions[old]:
            v[prior_layer] = -1
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(body.data)
    bm.free()
    for face in body.data.polygons:
        face.use_smooth = True
    for obj, ishead in [(body, False), (head, True)]:
        if ishead:
            prior = obj.data.attributes.new('prior_body_vertex_id', 'INT', 'POINT')
            for value in prior.data:
                value.value = -1
        for name, domain, elements in [('source_head_vertex_id', 'POINT', obj.data.vertices),
                                       ('source_head_face_id', 'FACE', obj.data.polygons),
                                       ('source_head_loop_id', 'CORNER', obj.data.loops)]:
            attr = obj.data.attributes.new(name, 'INT', domain)
            for i, value in enumerate(attr.data):
                value.value = i if ishead else -1
    bpy.ops.object.select_all(action='DESELECT')
    for obj in copies:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.join()
    result = bpy.context.object
    result.name = profile['outputObject']
    bm = bmesh.new()
    bm.from_mesh(result.data)
    idlayer = bm.verts.layers.int['source_head_vertex_id']
    canonical = {tuple(v.co): v.index for v in original_head.data.vertices}
    for v in bm.verts:
        if v[idlayer] < 0 and tuple(v.co) in canonical:
            v[idlayer] = canonical[tuple(v.co)]
    low, high = profile['blendBounds']['min'], profile['blendBounds']['max']
    merge = [v for v in bm.verts if low[2] < v.co.z < high[2]]
    before = len(bm.verts)
    bmesh.ops.remove_doubles(bm, verts=merge, dist=profile['mergeTolerance'])
    merged = before - len(bm.verts)
    # Restore provenance at welded endpoints even when BMesh retained the body vertex.
    for v in bm.verts:
        if v[idlayer] < 0 and tuple(v.co) in canonical:
            v[idlayer] = canonical[tuple(v.co)]
    region = [v for v in bm.verts if all(a < x < b for a, x, b in zip(low, v.co, high))]
    require(region, 'NECK_EMPTY_BLEND')
    smoothstep = lambda x: (lambda t: t*t*(3 - 2*t))(min(1, max(0, x)))
    amounts = {v: smoothstep((v.co.z - low[2]) / profile['fade']['lower'])
               * smoothstep((high[2] - v.co.z) / profile['fade']['upper']) for v in region}
    initial = {v: v.co.copy() for v in region}
    for _ in range(profile['iterations']):
        previous = {v: v.co.copy() for v in region}
        bmesh.ops.smooth_vert(bm, verts=region, factor=profile['factor'],
                             use_axis_x=True, use_axis_y=True, use_axis_z=True)
        for v in region:
            v.co = previous[v].lerp(v.co, amounts[v])
    maximum = max((v.co - initial[v]).length for v in region)
    require(maximum <= profile['maxBlendDisplacement'], 'NECK_DISPLACEMENT')
    bm.normal_update()  # Preserve source face winding, including separate UV charts.
    bm.to_mesh(result.data)
    bm.free()
    me = result.data
    for v in me.vertices:
        source = me.attributes['source_head_vertex_id'].data[v.index].value
        if source >= 0:
            for group in result.vertex_groups:
                group.remove([v.index])
            for name, weight in weights(original_head, original_head.data.vertices[source]).items():
                group = result.vertex_groups.get(name) or result.vertex_groups.new(name=name)
                group.add([v.index], weight, 'REPLACE')
    for loop in me.loops:
        source = me.attributes['source_head_loop_id'].data[loop.index].value
        if source < 0:
            continue
        for uv in original_head.data.uv_layers:
            me.uv_layers[uv.name].data[loop.index].uv = uv.data[source].uv
        for attr in original_head.data.attributes:
            if attr.domain == 'CORNER' and attr.data_type in ['BYTE_COLOR', 'FLOAT_COLOR']:
                me.attributes[attr.name].data[loop.index].color = attr.data[source].color
    if me.attributes.get('custom_normal'):
        me.attributes.remove(me.attributes['custom_normal'])
    me.update()
    normals = [n.vector.copy() for n in me.corner_normals]
    oldnormals = [n.vector.copy() for n in original_head.data.corner_normals]
    protected = set(profile['protectedHeadVertices'])
    for loop in me.loops:
        source = me.attributes['source_head_loop_id'].data[loop.index].value
        if source < 0:
            continue
        vi = original_head.data.loops[source].vertex_index
        z = original_head.data.vertices[vi].co.z
        amount = smoothstep((profile['shadeFade']['maxZ'] - z)
                            / (profile['shadeFade']['maxZ'] - profile['shadeFade']['minZ']))
        normals[loop.index] = (oldnormals[source] if vi in protected else
                               oldnormals[source].lerp(normals[loop.index], amount).normalized())
    me.normals_split_custom_set(normals)
    result['operation'] = 'neck_author@1'
    result['game_ready'] = False
    return result, {'mergedVertices': merged, 'blendVertices': len(region),
                    'maximumBlendDisplacement': maximum}


def previews(head, body, result, profile, output):
    scene = bpy.context.scene
    visibility = {o.name: (o.hide_render, o.hide_get()) for o in bpy.data.objects}
    for obj in bpy.data.objects:
        if obj.type == 'MESH':
            obj.hide_render = True
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.render.resolution_x = scene.render.resolution_y = 650
    scene.render.resolution_percentage = 100
    shading = scene.display.shading
    shading.color_type = 'SINGLE'
    shading.single_color = (.6, .67, .72)
    shading.show_shadows = shading.show_cavity = False
    camdata = bpy.data.cameras.new('neck_author_camera')
    camera = bpy.data.objects.new('neck_author_camera', camdata)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camdata.type = 'ORTHO'
    camdata.ortho_scale = profile['view']['scale']
    center = Vector(profile['view']['center'])
    for label, shown in [('source', [head, body]), ('authored', [result])]:
        for obj in [head, body, result]:
            obj.hide_render = obj not in shown
        for view, direction in [('front', (1, 0, 0)), ('side', (0, 1, 0)),
                                ('rear', (-1, 0, 0)), ('three-quarter', (1, 1, .1))]:
            camera.location = center + Vector(direction).normalized() * camdata.ortho_scale * 10
            camera.rotation_euler = (center - camera.location).to_track_quat('-Z', 'Y').to_euler()
            scene.render.filepath = str(output / f'{label}-{view}.png')
            bpy.ops.render.render(write_still=True)
    for name, (hidden, viewport) in visibility.items():
        bpy.data.objects[name].hide_render = hidden
        bpy.data.objects[name].hide_set(viewport)
    head.hide_render = body.hide_render = True
    head.hide_set(True)
    body.hide_set(True)
    result.hide_set(False)
    result.hide_render = False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    request = load(args.request)
    profile = validate(request['profile'])
    output = Path(request['output'])
    require(sha(request['scene']) == profile['sceneHash'], 'NECK_INPUT_HASH')
    bpy.ops.wm.open_mainfile(filepath=request['scene'], use_scripts=False)
    require(profile['outputObject'] not in bpy.data.objects, 'NECK_OUTPUT_OBJECT_EXISTS')
    head, body = [bpy.data.objects.get(profile[key]) for key in ['head', 'body']]
    require(head is not None and body is not None, 'NECK_OBJECT_SELECTOR')
    top, center = prepare(head, body, profile)
    original = capture(head, body, profile)
    result, facts = author(head, body, profile, top, center)
    previews(head, body, result, profile, output)
    bpy.context.preferences.filepaths.save_version = 0
    candidate = output / 'authored.blend'
    require(not candidate.exists(), 'NECK_OUTPUT_EXISTS')
    bpy.ops.wm.save_as_mainfile(filepath=str(candidate))
    bpy.ops.wm.open_mainfile(filepath=str(candidate), use_scripts=False)
    report = verify(bpy.data.objects[profile['outputObject']], original, profile)
    report.update(facts, operation='neck_author_verification@1', version=1,
                  blenderVersion=bpy.app.version_string, sceneHash=profile['sceneHash'],
                  candidateHash=sha(candidate), passed=True, gameReady=False,
                  poseTested=False, compiled=False)
    (output / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('Neck authoring verified:', json.dumps(facts), flush=True)


if __name__ == '__main__':
    main()
