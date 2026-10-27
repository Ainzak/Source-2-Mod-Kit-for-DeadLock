"""Source-derived local subdivision; independent of Blender and SDK emitters."""
import copy
import math
from common import require


def expand(point, policy):
    delta = [p-c for p, c in zip(point, policy['center'])]
    r2 = sum(x*x for x in delta) / policy['radius']**2
    axial = sum(x*a for x, a in zip(delta, policy['axis']))
    gain = (policy['scale']-1) * max(0, 1-r2)**3
    return [p + gain*(d-axial*a) for p, d, a in zip(point, delta, policy['axis'])]


def cross(a, b, c):
    u, v = [y-x for x, y in zip(a, b)], [y-x for x, y in zip(a, c)]
    return [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]


def derive(original, policy, bones):
    require(len(original['materials']) == 1, 'SKINNED_MATERIAL_UNSUPPORTED')
    require(abs(sum(x*x for x in policy['axis'])-1) < 1e-6, 'SKINNED_AXIS_INVALID')
    known = {b['name'] for b in bones}
    require(not any(n.startswith('$') for n in known), 'PROCEDURAL_RIG_UNSUPPORTED')
    for weights in original['weights']:
        require(1 <= len(weights) <= 4 and set(weights) <= known and
                all(math.isfinite(w) and 0 < w <= 1 for w in weights.values()) and
                abs(sum(weights.values())-1) < 1e-6, 'SKINNED_SOURCE_WEIGHTS_INVALID')
    if policy['geometryMode'] == 'unchanged':
        require(not policy['sourceTriangles'] and policy['scale'] == 1, 'SKINNED_UNCHANGED_INTENT_INVALID')
        return copy.deepcopy(original), set()
    selected = set(policy['sourceTriangles'])
    require(0 < len(selected) <= 1024 and len(selected) == len(policy['sourceTriangles']) and
            all(type(i) is int and 0 <= i < len(original['triangles']) for i in selected),
            'SKINNED_TRIANGLE_SELECTION_INVALID')
    target = copy.deepcopy(original)
    target['positions'] = [expand(p, policy) for p in original['positions']]
    moved = {i for i, (p, q) in enumerate(zip(original['positions'], target['positions'])) if p != q}
    target.update(corners=[], triangles=[], uv=[], normals=[])
    mutable = set()
    for ti, tri in enumerate(original['triangles']):
        require(tri['material'] == 0 and len(tri['loops']) == 3, 'SKINNED_SOURCE_FACE_INVALID')
        ids = [original['corners'][loop] for loop in tri['loops']]
        attrs = [(i, original['uv'][loop], original['normals'][loop]) for i, loop in zip(ids, tri['loops'])]
        source_points = [original['positions'][i] for i in ids]
        source_normal = cross(*source_points)
        faces = [attrs]
        if ti in selected:
            require(all(math.dist(p, policy['center']) < policy['radius'] for p in source_points),
                    'SKINNED_SPLIT_OUTSIDE_SUPPORT')
            require(sum(x*x for x in source_normal) > 1e-12, 'SKINNED_SOURCE_ZERO_AREA')
            weights = {}
            for i in ids:
                for bone, weight in original['weights'][i].items():
                    weights[bone] = weights.get(bone, 0) + weight/3
            require(len(weights) <= 4, 'SKINNED_INTERPOLATED_WEIGHT_BUDGET')
            point = [sum(p[k] for p in source_points)/3 for k in range(3)]
            new_id = len(target['positions'])
            target['positions'].append(expand(point, policy))
            target['weights'].append(weights)
            # Preserve each source face's distinct UV corners, including seams.
            uv = [round(sum(a[1][k] for a in attrs)/3*2048)/2048 for k in range(2)]
            normal = [sum(a[2][k] for a in attrs)/3 for k in range(3)]
            length = math.sqrt(sum(x*x for x in normal))
            require(length > 1e-8, 'SKINNED_NORMAL_AMBIGUOUS')
            center = (new_id, uv, [x/length for x in normal])
            faces = [[attrs[i], attrs[(i+1)%3], center] for i in range(3)]
        for face in faces:
            offset = len(target['corners'])
            loops = list(range(offset, offset+3))
            target['triangles'].append({'loops': loops, 'material': 0})
            edited = ti in selected or any(i in moved for i in ids)
            if edited:
                normal = cross(*[target['positions'][a[0]] for a in face])
                require(sum(x*x for x in normal) > 1e-12 and
                        sum(x*y for x, y in zip(normal, source_normal)) > 0,
                        'SKINNED_EDIT_DEGENERATE_OR_FLIPPED')
                mutable.update(loops)
            for i, uv, normal in face:
                target['corners'].append(i)
                target['uv'].append(list(uv))
                target['normals'].append(list(normal))
    require(len(target['positions']) <= 100000 and len(target['triangles']) <= 200000,
            'SKINNED_MESH_BUDGET')
    require(any(len(target['weights'][i]) > 1 for i in range(len(original['positions']), len(target['positions']))),
            'SKINNED_MULTIBONE_CONTROL_REQUIRED')
    # A changed face alters the normal basis of its incident vertices. Permit only
    # this one-ring shading halo, while all halo positions/UVs/weights stay derived.
    shading_vertices = {target['corners'][i] for i in mutable}
    for tri in target['triangles']:
        if any(target['corners'][i] in shading_vertices for i in tri['loops']):
            mutable.update(tri['loops'])
    return target, mutable


def compare_skinned(original, target, policy, bones, materials):
    expected, mutable = derive(original, policy, bones)
    require(set(target) == set(expected), 'SKINNED_LAYOUT_CHANGED')
    require(target['materials'] == [materials[m] for m in original['materials']], 'AUTHORING_MATERIAL_CHANGED')
    for key in ('matrix', 'corners', 'triangles'):
        require(target[key] == expected[key], 'SKINNED_UNDECLARED_CHANGE:' + key)
    for key, epsilon in (('positions', 1e-5), ('uv', 1e-6)):
        require(len(target[key]) == len(expected[key]), 'SKINNED_ATTRIBUTE_COUNT')
        for a, b in zip(target[key], expected[key]):
            require(len(a) == len(b) and all(math.isfinite(x) and abs(x-y) < epsilon for x, y in zip(a, b)),
                    'SKINNED_UNDECLARED_CHANGE:' + key)
    require(len(target['weights']) == len(expected['weights']), 'SKINNED_ATTRIBUTE_COUNT')
    for a, b in zip(target['weights'], expected['weights']):
        require(set(a) == set(b) and all(math.isfinite(v) and abs(v-b[k]) < 1e-6 for k, v in a.items()),
                'SKINNED_WEIGHTS_CHANGED')
    require(len(target['normals']) == len(expected['normals']), 'SKINNED_ATTRIBUTE_COUNT')
    for i, (a, b) in enumerate(zip(target['normals'], expected['normals'])):
        require(len(a) == 3 and all(math.isfinite(v) for v in a) and
                abs(sum(v*v for v in a)-1) < 0.0001, 'SKINNED_NORMAL_INVALID')
        if i not in mutable:
            require(a == b, 'PROTECTED_NORMALS_CHANGED')
