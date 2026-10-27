"""Independent validation of a position-only intent against source Blender facts."""
import math
from collections import Counter
from common import require
from skinned import compare_skinned
from surface import compare_surface


def compare_authored(baseline, authored, intent, materials):
    require(baseline['bones'] == authored['bones'], 'AUTHORING_RIG_CHANGED')
    require(set(authored['meshes']) == set(intent['selectedMeshes']), 'AUTHORING_MESH_CHANGED')
    scales = {s['mesh']: s['axes'] for s in intent['scales']}
    identity = [[1 if i == j else 0 for j in range(4)] for i in range(4)]
    max_error = 0
    replacement = intent.get('replacement')
    patch = intent.get('patch')
    surface = intent.get('surface')
    if surface:
        require(surface['mesh'] in intent['selectedMeshes'] and not scales, 'SURFACE_SELECTION_INVALID')
    if patch:
        require(patch['mesh'] in intent['selectedMeshes'] and not scales, 'SKINNED_SELECTION_INVALID')
    if replacement:
        require(replacement['mesh'] in intent['selectedMeshes'] and not scales, 'REPLACEMENT_SELECTION_INVALID')
        require(not any(b['name'].startswith('$') for b in baseline['bones']), 'PROCEDURAL_RIG_UNSUPPORTED')
        unit = baseline['meshes'][replacement['mesh']]
        require(len(unit['materials']) == 1 and all(w == {replacement['rigidBone']: 1}
                for w in unit['weights']), 'SOURCE_UNIT_NOT_RIGID')
    for name, target in authored['meshes'].items():
        original = baseline['meshes'][name]
        require(original['matrix'] == target['matrix'] == identity, 'NONIDENTITY_MESH_TRANSFORM_UNSUPPORTED')
        if surface and name == surface['mesh']:
            compare_surface(original, target, surface, baseline['bones'], materials)
            continue
        if patch and name == patch['mesh']:
            compare_skinned(original, target, patch, baseline['bones'], materials)
            continue
        if replacement and name == replacement['mesh'] and replacement['geometryMode'] == 'replace_rigid_closed':
            validate_replacement(original, target, replacement, baseline['bones'], materials)
            continue
        for key in ('weights', 'uv', 'corners', 'triangles'):
            require(original[key] == target[key], 'UNDECLARED_AUTHORING_CHANGE:' + key)
        require([materials[m] for m in original['materials']] == target['materials'],
                'AUTHORING_MATERIAL_CHANGED')
        require(len(original['positions']) == len(target['positions']), 'AUTHORING_VERTEX_COUNT_CHANGED')
        if replacement or patch or surface:
            require(original['normals'] == target['normals'], 'PROTECTED_NORMALS_CHANGED')
        axes = scales.get(name, [1, 1, 1])
        for a, b in zip(original['positions'], target['positions']):
            error = math.dist([v*s for v, s in zip(a, axes)], b)
            require(error < 0.0001, 'UNDECLARED_POSITION_CHANGE')
            max_error = max(error, max_error)
    return max_error


def validate_replacement(original, target, policy, bones, materials):
    require(set(target) == set(original), 'REPLACEMENT_LAYOUT_CHANGED')
    bone = policy['rigidBone']
    require(bone in {b['name'] for b in bones} and not bone.startswith('$'), 'REPLACEMENT_BONE_INVALID')
    require(all(w == {bone: 1} for w in original['weights']), 'SOURCE_UNIT_NOT_RIGID')
    require(len(original['materials']) == 1 and target['materials'] ==
            [materials[original['materials'][0]]], 'REPLACEMENT_MATERIAL_CHANGED')
    points, triangles, corners = target['positions'], target['triangles'], target['corners']
    require(4 <= len(points) <= 4096 and 4 <= len(triangles) <= 8192, 'REPLACEMENT_BUDGET')
    require(len(target['weights']) == len(points) and all(w == {bone: 1} for w in target['weights']),
            'REPLACEMENT_WEIGHTS_CHANGED')
    low, high = policy['boundsMin'], policy['boundsMax']
    require(all(a < b for a, b in zip(low, high)), 'REPLACEMENT_BOUNDS_INVALID')
    for point in points:
        require(len(point) == 3 and all(type(v) in (int, float) and math.isfinite(v) and
                low[i] <= v <= high[i] for i, v in enumerate(point)), 'REPLACEMENT_POSITION_INVALID')
    require(len(target['uv']) == len(target['normals']) == len(corners), 'REPLACEMENT_CORNER_COUNT')
    require(all(type(i) is int and 0 <= i < len(points) for i in corners), 'REPLACEMENT_VERTEX_INDEX')
    for uv in target['uv']:
        require(len(uv) == 2 and all(math.isfinite(v) and 0 <= v <= 1 for v in uv), 'REPLACEMENT_UV_INVALID')
    for normal in target['normals']:
        require(len(normal) == 3 and all(math.isfinite(v) for v in normal) and
                abs(sum(v*v for v in normal) - 1) < 0.0001, 'REPLACEMENT_NORMAL_INVALID')
    edges, faces, used = Counter(), set(), set()
    graph = {i: set() for i in range(len(points))}
    for tri in triangles:
        require(set(tri) == {'loops', 'material'} and tri['material'] == 0 and len(tri['loops']) == 3,
                'REPLACEMENT_TRIANGLE_INVALID')
        require(all(type(i) is int and 0 <= i < len(corners) for i in tri['loops']), 'REPLACEMENT_LOOP_INDEX')
        ids = [corners[i] for i in tri['loops']]
        require(len(set(ids)) == 3 and tuple(sorted(ids)) not in faces, 'REPLACEMENT_DUPLICATE_FACE')
        faces.add(tuple(sorted(ids)))
        a, b, c = [points[i] for i in ids]
        u, v = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
        cross = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
        require(sum(x*x for x in cross) > 1e-12, 'REPLACEMENT_ZERO_AREA')
        for x, y in zip(ids, ids[1:] + ids[:1]):
            edges[x, y] += 1
            graph[x].add(y)
            graph[y].add(x)
        used.update(ids)
    require(len(used) == len(points) and all(n == 1 and edges[y, x] == 1 for (x, y), n in edges.items()),
            'REPLACEMENT_NOT_CLOSED_MANIFOLD')
    seen, todo = set(), [0]
    while todo:
        i = todo.pop()
        if i not in seen:
            seen.add(i)
            todo.extend(graph[i] - seen)
    require(len(seen) == len(points), 'REPLACEMENT_DISCONNECTED')
