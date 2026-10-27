"""Independent saved-mesh preservation and local geometric intersection checks."""

from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'sdk'))
from surface_geometry import intersects
from neck_contract import require


def signature(obj):
    digest = hashlib.sha256()

    def add(value):
        digest.update(json.dumps(value, default=list, sort_keys=True).encode())

    add([list(row) for row in obj.matrix_world])
    add(obj.parent.name if obj.parent else None)
    if obj.type == 'ARMATURE':
        for bone in obj.data.bones:
            add([bone.name, bone.parent.name if bone.parent else None, bone.use_deform,
                 [list(row) for row in bone.matrix_local], list(bone.head_local),
                 list(bone.tail_local), bone.inherit_scale])
        for bone in obj.pose.bones:
            add([bone.name, list(bone.matrix_basis)])
    else:
        me = obj.data
        add([[list(v.co), [(g.group, g.weight) for g in v.groups]] for v in me.vertices])
        add([list(f.vertices) for f in me.polygons])
        add([[f.material_index, f.use_smooth] for f in me.polygons])
        add([g.name for g in obj.vertex_groups])
        add([m.name if m else None for m in me.materials])
        for attr in me.attributes:
            prop = ('vector' if attr.data_type in ['FLOAT_VECTOR', 'FLOAT2'] else
                    'color' if 'COLOR' in attr.data_type else 'value')
            try:
                add([attr.name, attr.domain, attr.data_type,
                     [getattr(x, prop) for x in attr.data]])
            except AttributeError:
                raise ValueError('NECK_UNSUPPORTED_ATTRIBUTE:' + attr.name) from None
        for uv in me.uv_layers:
            add([uv.name, [list(x.uv) for x in uv.data]])
        add([list(n.vector) for n in me.corner_normals])
    return digest.hexdigest()


def capture(head, body, profile):
    return {
        'objects': {o.name: signature(o) for o in bpy.data.objects
                    if o.type in ['MESH', 'ARMATURE']},
        'headPositions': [tuple(v.co) for v in head.data.vertices],
        'headWeights': [{head.vertex_groups[g.group].name: g.weight for g in v.groups}
                        for v in head.data.vertices],
        'headFaces': [list(f.vertices) for f in head.data.polygons],
        'headLoops': [list(f.loop_indices) for f in head.data.polygons],
        'headUV': {uv.name: [tuple(x.uv) for x in uv.data] for uv in head.data.uv_layers},
        'headColors': {a.name: [tuple(x.color) for x in a.data] for a in head.data.attributes
                       if a.domain == 'CORNER' and a.data_type in ['BYTE_COLOR', 'FLOAT_COLOR']},
        'headNormals': [tuple(n.vector) for n in head.data.corner_normals],
        'bodyProtected': {i: tuple(body.data.vertices[i].co)
                          for i in profile['protectedBodyVertices']},
    }


def verify(obj, original, profile):
    changed = [name for name, expected in original['objects'].items()
               if name not in bpy.data.objects or signature(bpy.data.objects[name]) != expected]
    require(not changed, 'NECK_SOURCE_CHANGED:' + ','.join(changed))
    me = obj.data
    coords = [tuple(v.co) for v in me.vertices]
    require(all(math.isfinite(x) for p in coords for x in p), 'NECK_NONFINITE_GEOMETRY')
    prior = me.attributes['prior_body_vertex_id']
    body_records = {prior.data[v.index].value: coords[v.index] for v in me.vertices
                    if prior.data[v.index].value >= 0}
    require(all(body_records.get(i) == p for i, p in original['bodyProtected'].items()),
            'NECK_PROTECTED_BODY')
    faceids = me.attributes['source_head_face_id']
    vertexids = me.attributes['source_head_vertex_id']
    loopids = me.attributes['source_head_loop_id']
    seen = set()
    protected = set(profile['protectedHeadVertices'])
    changedhead = set()
    normal_error = 0
    for face in me.polygons:
        source = faceids.data[face.index].value
        if source < 0:
            continue
        require(0 <= source < len(original['headFaces']) and source not in seen,
                'NECK_HEAD_FACE_COVERAGE')
        seen.add(source)
        actual = [loopids.data[i].value for i in face.loop_indices]
        old = original['headLoops'][source]
        require(any(actual == old[s:] + old[:s] for s in range(len(old))),
                'NECK_HEAD_WINDING')
        for li, vi in zip(face.loop_indices, face.vertices):
            source_loop = loopids.data[li].value
            index = old.index(source_loop)
            source_vi = original['headFaces'][source][index]
            mapped = vertexids.data[vi].value
            require(0 <= mapped < len(original['headPositions']), 'NECK_HEAD_PROVENANCE')
            a, b = original['headPositions'][mapped], original['headPositions'][source_vi]
            require(sum((x - y)**2 for x, y in zip(a, b)) <= profile['mergeTolerance']**2,
                    'NECK_HEAD_PROVENANCE')
            row = {obj.vertex_groups[g.group].name: g.weight for g in me.vertices[vi].groups}
            require(row == original['headWeights'][source_vi], 'NECK_HEAD_WEIGHTS')
            if source_vi in protected:
                require(coords[vi] == b, 'NECK_PROTECTED_HEAD')
                normal_error = max(normal_error, math.sqrt(sum((x-y)**2 for x, y in
                                   zip(me.corner_normals[li].vector,
                                       original['headNormals'][source_loop]))))
            elif coords[vi] != b:
                changedhead.add(source_vi)
            for name, values in original['headUV'].items():
                require(tuple(me.uv_layers[name].data[li].uv) == values[source_loop], 'NECK_HEAD_UV')
            for name, values in original['headColors'].items():
                require(tuple(me.attributes[name].data[li].color) == values[source_loop],
                        'NECK_HEAD_COLOR')
    require(len(seen) == len(original['headFaces']), 'NECK_HEAD_FACE_COVERAGE')
    require(normal_error <= 0.00015, 'NECK_PROTECTED_HEAD_NORMALS')
    edges = Counter()
    for face in me.polygons:
        ids = list(face.vertices)
        for a, b in zip(ids, ids[1:] + ids[:1]):
            edges[tuple(sorted((a, b)))] += 1
    require(not any(n > 2 for n in edges.values()), 'NECK_NONMANIFOLD_RESULT')
    me.calc_loop_triangles()
    triangles = []
    lo, hi = profile['blendBounds']['min'][2], profile['blendBounds']['max'][2]
    # Include the whole axial slab: a changed face can collide outside the XY blend box.
    for tri in me.loop_triangles:
        require(tri.area >= 1e-8, 'NECK_DEGENERATE')
        points = [coords[i] for i in tri.vertices]
        if max(p[2] for p in points) >= lo - 1 and min(p[2] for p in points) <= hi + 1:
            stationary = (faceids.data[tri.polygon_index].value >= 0
                          and all(vertexids.data[i].value in protected for i in tri.vertices))
            triangles.append((tri.polygon_index, points, stationary))
    bounds = [([min(p[k] for p in t[1]) for k in range(3)],
               [max(p[k] for p in t[1]) for k in range(3)], i)
              for i, t in enumerate(triangles)]
    active = []
    pairs = 0
    for low, high, i in sorted(bounds, key=lambda b: b[0][0]):
        active = [(l, h, j) for l, h, j in active if h[0] + 1e-7 >= low[0]]
        for l, h, j in active:
            if any(high[k] + 1e-7 < l[k] or h[k] + 1e-7 < low[k] for k in (1, 2)):
                continue
            if triangles[i][2] and triangles[j][2]:
                continue
            pairs += 1
            require(pairs <= 2000000, 'NECK_INTERSECTION_BUDGET')
            require(not intersects(triangles[i][1], triangles[j][1]),
                    f'NECK_INTERSECTION:{triangles[i][0]}:{triangles[j][0]}')
        active.append((low, high, i))
    return {'sourceObjectsChecked': len(original['objects']), 'changedOriginalObjects': [],
            'headFacesRetained': len(seen), 'headUVsAndColorsExact': True,
            'headWeightRowsExact': True, 'protectedHeadVertices': len(protected),
            'maximumProtectedNormalError': normal_error,
            'changedHeadVertexIds': sorted(changedhead),
            'protectedBodyVertices': len(original['bodyProtected']),
            'localIntersectionPairs': pairs, 'localIntersections': 0,
            'degenerateTriangles': 0, 'meshVertices': len(me.vertices),
            'meshPolygons': len(me.polygons), 'meshTriangles': len(me.loop_triangles)}
