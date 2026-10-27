"""Source-bound authored surfaces. No Blender, SDK, character or displacement recipe."""
import copy
import math
from collections import Counter, defaultdict

from common import require
from skinned import cross


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def angle(a, b):
    length = math.sqrt(dot(a, a)*dot(b, b))
    require(length > 1e-12, 'SURFACE_ZERO_AREA')
    return math.degrees(math.acos(max(-1, min(1, dot(a, b)/length))))


def ids(mesh, tri):
    return [mesh['corners'][loop] for loop in tri['loops']]


def bary_point(points, bary):
    return [sum(p[k]*w for p, w in zip(points, bary)) for k in range(len(points[0]))]


def edges(faces):
    result = Counter()
    for face in faces:
        for a, b in zip(face, face[1:]+face[:1]):
            result[a, b] += 1
    return result


def source_seams(original):
    directed = edges([ids(original,t) for t in original['triangles']])
    geometric = defaultdict(list)
    for (a,b), count in directed.items():
        if count == 1 and not directed[b,a]:
            key = tuple(sorted((tuple(original['positions'][a]),tuple(original['positions'][b]))))
            geometric[key].append((a,b))
    pairs, ambiguous = set(), set()
    for records in geometric.values():
        if len(records) == 1: continue
        if len(records) != 2:
            ambiguous.update(v for edge in records for v in edge); continue
        (a,b),(c,d) = records
        if original['positions'][a] != original['positions'][d] or original['positions'][b] != original['positions'][c]:
            ambiguous.update((a,b,c,d)); continue
        for x,y in ((a,d),(b,c)):
            if original['weights'][x] != original['weights'][y]:
                ambiguous.update((x,y))
            elif x != y: pairs.add(tuple(sorted((x,y))))
    return sorted(pairs), sorted(ambiguous)


def derive_surface(original, policy, bones):
    """Derive neutral topology/attributes; positions are subsequently freely authored."""
    known = {b['name'] for b in bones}
    require(not any(n.startswith('$') for n in known), 'PROCEDURAL_RIG_UNSUPPORTED')
    require(len(original['materials']) == 1, 'SURFACE_MATERIAL_UNSUPPORTED')
    for weights in original['weights']:
        require(1 <= len(weights) <= 4 and set(weights) <= known and
                all(math.isfinite(w) and 0 < w <= 1 for w in weights.values()) and
                abs(sum(weights.values())-1) < 1e-6, 'SURFACE_SOURCE_WEIGHTS_INVALID')
    count = len(original['positions'])
    selected = set(policy['sourceTriangles'])
    editable = set(policy['editableVertices'])
    require(len(selected) == len(policy['sourceTriangles']) and len(selected) <= 8192 and
            all(type(i) is int and 0 <= i < len(original['triangles']) for i in selected),
            'SURFACE_TRIANGLE_SELECTION_INVALID')
    require(len(editable) == len(policy['editableVertices']) and
            all(type(i) is int and 0 <= i < count for i in editable), 'SURFACE_EDITABLE_INVALID')
    face_ids = [ids(original, tri) for tri in original['triangles']]
    seam_pairs, ambiguous_seams = source_seams(original)
    incident = defaultdict(set)
    for ti, face in enumerate(face_ids):
        for vertex in face:
            incident[vertex].add(ti)
    boundary = {v for ti in selected for v in face_ids[ti] if incident[v] - selected}
    for a,b in seam_pairs:
        if bool(incident[a] & selected) != bool(incident[b] & selected):
            boundary.update((a,b))
    require(not editable & boundary and editable <= {v for ti in selected for v in face_ids[ti]},
            'SURFACE_FIXED_BOUNDARY_EDITABLE')
    if policy['geometryMode'] == 'unchanged':
        require(not selected and not editable and not policy['addedVertices'] and not policy['faces'],
                'SURFACE_UNCHANGED_INVALID')
        return copy.deepcopy(original), {'boundary': [], 'collar': [], 'seamPairs':seam_pairs,
                                        'ambiguousSeamVertices':ambiguous_seams, 'faceSources':
                                        list(range(len(face_ids))), 'cornerSources':
                                        [{ 'loops': [i], 'weights': [1]} for i in range(len(original['corners']))]}
    require(selected and (editable or policy['addedVertices']), 'SURFACE_EMPTY_PATCH')
    require(all(dot(cross(*[original['positions'][v] for v in face_ids[ti]]),
                    cross(*[original['positions'][v] for v in face_ids[ti]])) > 1e-12 for ti in selected),
            'SURFACE_SELECTED_SOURCE_ZERO_AREA')
    require(len(policy['addedVertices']) <= 4096, 'SURFACE_ADDED_BUDGET')
    added, weights = [], []
    bary_by_vertex = {}
    bary_by_face = defaultdict(dict)
    for offset, spec in enumerate(policy['addedVertices']):
        ti, bary = spec['sourceTriangle'], spec['barycentric']
        require(ti in selected and len(bary) == 3 and all(math.isfinite(w) and 0 < w < 1 for w in bary)
                and abs(sum(bary)-1) < 1e-8, 'SURFACE_PROVENANCE_INVALID')
        source_ids = face_ids[ti]
        added.append(bary_point([original['positions'][i] for i in source_ids], bary))
        blended = {}
        for vertex, coefficient in zip(source_ids, bary):
            for bone, weight in original['weights'][vertex].items():
                blended[bone] = blended.get(bone, 0) + coefficient*weight
        require(len(blended) <= 4, 'SURFACE_INTERPOLATED_WEIGHT_BUDGET')
        weights.append(blended)
        bary_by_vertex[count+offset] = (ti, bary)
        bary_by_face[ti][count+offset] = bary
    grouped = defaultdict(list)
    for face in policy['faces']:
        require(face['sourceTriangle'] in selected and len(face['vertices']) == 3 and
                len(set(face['vertices'])) == 3, 'SURFACE_FACE_INVALID')
        grouped[face['sourceTriangle']].append(face['vertices'])
    require(set(grouped) == selected, 'SURFACE_FACE_COVERAGE')
    used = set()
    for ti, faces in grouped.items():
        source_ids = face_ids[ti]
        local = {v: [int(k == j) for k in range(3)] for j, v in enumerate(source_ids)}
        local.update(bary_by_face[ti])
        require(all(v in local for f in faces for v in f), 'SURFACE_FACE_PROVENANCE')
        directed = edges(faces)
        exterior = edges([source_ids])
        require(all(n == 1 and (directed[b, a] == 1 if (a, b) not in exterior else
                               directed[b, a] == 0) for (a, b), n in directed.items()) and
                all(directed[e] == 1 for e in exterior), 'SURFACE_TOPOLOGY_COVERAGE')
        area = 0
        for face in faces:
            p, q, r = [[local[v][1], local[v][2]] for v in face]
            signed = (q[0]-p[0])*(r[1]-p[1])-(q[1]-p[1])*(r[0]-p[0])
            require(signed > 1e-12, 'SURFACE_PROVENANCE_FLIPPED')
            area += signed
        require(abs(area-1) < 1e-8, 'SURFACE_PROVENANCE_AREA')
        # Connected face graph, anchored at the complete source boundary.
        graph = defaultdict(set)
        for a, b in directed:
            graph[a].add(b); graph[b].add(a)
        seen, todo = set(), [source_ids[0]]
        while todo:
            v = todo.pop()
            if v not in seen:
                seen.add(v); todo.extend(graph[v]-seen)
        require(seen == set(graph), 'SURFACE_DISCONNECTED_REFINEMENT')
        used.update(v for f in faces for v in f if v >= count)
    require(used == set(bary_by_vertex), 'SURFACE_UNUSED_ADDED_VERTEX')
    target = copy.deepcopy(original)
    target['positions'] += added
    target['weights'] += weights
    target.update(corners=[], triangles=[], uv=[], normals=[])
    face_sources, corner_sources, collar = [], [], []
    for ti, tri in enumerate(original['triangles']):
        source_ids = face_ids[ti]
        local = {v: [int(k == j) for k in range(3)] for j, v in enumerate(source_ids)}
        local.update(bary_by_face[ti])
        for face in grouped[ti] if ti in selected else [source_ids]:
            loops = list(range(len(target['corners']), len(target['corners'])+3))
            target['triangles'].append({'loops': loops, 'material': tri['material']})
            face_sources.append(ti)
            if ti in selected and set(face) & boundary:
                collar.append(len(face_sources)-1)
            for vertex in face:
                bary = local[vertex]
                if vertex < count:
                    loop = tri['loops'][source_ids.index(vertex)]
                    uv, normal = original['uv'][loop], original['normals'][loop]
                    corner_sources.append({'loops': [loop], 'weights': [1]})
                else:
                    uv = [round(v*2048)/2048 for v in
                          bary_point([original['uv'][l] for l in tri['loops']], bary)]
                    normal = bary_point([original['normals'][l] for l in tri['loops']], bary)
                    length = math.sqrt(dot(normal, normal))
                    require(length > 1e-8, 'SURFACE_NORMAL_AMBIGUOUS')
                    normal = [v/length for v in normal]
                    corner_sources.append({'loops': tri['loops'], 'weights': bary})
                target['corners'].append(vertex)
                target['uv'].append(list(uv)); target['normals'].append(list(normal))
    require(len(target['positions']) <= 100000 and len(target['triangles']) <= 200000,
            'SURFACE_MESH_BUDGET')
    return target, {'boundary': sorted(boundary), 'collar': collar,
                    'seamPairs':seam_pairs,'ambiguousSeamVertices':ambiguous_seams,
                    'faceSources': face_sources, 'cornerSources': corner_sources}


def compare_surface(original, actual, policy, bones, materials):
    neutral, provenance = derive_surface(original, policy, bones)
    require(set(actual) == set(neutral), 'SURFACE_LAYOUT_CHANGED')
    require(actual['materials'] == [materials[m] for m in original['materials']], 'AUTHORING_MATERIAL_CHANGED')
    for key in ('matrix', 'corners', 'triangles', 'uv'):
        require(actual[key] == neutral[key], 'SURFACE_UNDECLARED_CHANGE:' + key)
    require(len(actual['weights']) == len(neutral['weights']), 'SURFACE_WEIGHT_COUNT')
    for a, b in zip(actual['weights'], neutral['weights']):
        require(set(a) == set(b) and all(math.isfinite(v) and abs(v-b[k]) < 1e-6 for k, v in a.items()),
                'SURFACE_WEIGHTS_CHANGED')
    require(len(actual['positions']) == len(neutral['positions']), 'SURFACE_POSITION_COUNT')
    movable = set(policy['editableVertices']) | set(range(len(original['positions']), len(neutral['positions'])))
    moved = set()
    max_distance = 0
    for i, (a, b) in enumerate(zip(actual['positions'], neutral['positions'])):
        require(len(a) == 3 and all(math.isfinite(v) for v in a), 'SURFACE_NONFINITE_POSITION')
        distance = math.dist(a, b)
        if i not in movable:
            require(a == b, 'SURFACE_PROTECTED_POSITION_CHANGED')
        require(distance <= policy['maxDisplacement'], 'SURFACE_DISPLACEMENT_LIMIT')
        if distance > 1e-6: moved.add(i)
        max_distance = max(distance, max_distance)
    require(not moved & set(provenance['ambiguousSeamVertices']), 'SURFACE_SEAM_AMBIGUOUS')
    for a,b in provenance['seamPairs']:
        require(actual['positions'][a] == actual['positions'][b], 'SURFACE_SEAM_OPENED')
    changed_faces = {ti for ti, tri in enumerate(actual['triangles']) if set(ids(actual, tri)) & moved}
    halo = {v for ti in changed_faces for v in ids(actual, actual['triangles'][ti])}
    # Refinement can require recoding normals even without displaced positions.
    halo.update(range(len(original['positions']), len(neutral['positions'])))
    for ti, source_ti in enumerate(provenance['faceSources']):
        if source_ti in policy['sourceTriangles'] and len(policy['addedVertices']):
            halo.update(ids(actual, actual['triangles'][ti]))
    require(len(actual['normals']) == len(neutral['normals']), 'SURFACE_NORMAL_COUNT')
    for loop, (a, b) in enumerate(zip(actual['normals'], neutral['normals'])):
        require(len(a) == 3 and all(math.isfinite(v) for v in a) and abs(dot(a, a)-1) < 1e-4,
                'SURFACE_NORMAL_INVALID')
        if actual['corners'][loop] not in halo:
            require(a == b, 'PROTECTED_NORMALS_CHANGED')
    angles = []
    for ti in changed_faces:
        tri = actual['triangles'][ti]
        vertices = ids(actual, tri)
        before = cross(*[neutral['positions'][v] for v in vertices])
        after = cross(*[actual['positions'][v] for v in vertices])
        require(dot(after, after) > 1e-12 and dot(before, after) > 0, 'SURFACE_DEGENERATE_OR_FLIPPED')
        if ti in provenance['collar']:
            delta = angle(before, after)
            require(delta <= policy['maxBoundaryAngleChange'], 'SURFACE_BOUNDARY_TANGENT_CHANGED')
            angles.append(delta)
    from surface_geometry import intersections, cross_sections
    intersection_report = intersections(neutral, actual, changed_faces)
    source_degenerate = [ti for ti, tri in enumerate(neutral['triangles'])
                         if dot(cross(*[neutral['positions'][v] for v in ids(neutral,tri)]),
                                cross(*[neutral['positions'][v] for v in ids(neutral,tri)])) <= 1e-12]
    return {'movedVertices': len(moved), 'changedFaces': len(changed_faces),
            'maxDisplacement': max_distance, 'fixedBoundaryVertices': len(provenance['boundary']),
            'preservedSeamPairs':len(provenance['seamPairs']),
            'maxBoundaryAngleChange': max(angles, default=0), 'intersections': intersection_report,
            'preExistingDegenerateFaces': source_degenerate,
            'crossSections': cross_sections(neutral, actual, policy['sections']),
            'normalHaloVertices': sorted(halo), 'runtimeTested': False, 'productionAdmission': False}
