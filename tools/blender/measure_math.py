"""Pure geometric measurements and triangle-plane contours; no Blender dependency."""

from collections import defaultdict
import hashlib
import json
import math
import struct


def require(condition, code):
    if not condition:
        raise ValueError(code)


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def sub(a, b):
    return [x-y for x, y in zip(a, b)]


def length(a):
    return math.sqrt(dot(a, a))


def geometry_hash(points, faces, matrix):
    h = hashlib.sha256()
    h.update(struct.pack('<II', len(points), len(faces)))
    for row in matrix:
        h.update(struct.pack('<4f', *row))
    for point in points:
        h.update(struct.pack('<3f', *point))
    for face in faces:
        h.update(struct.pack('<I', len(face)))
        h.update(struct.pack('<' + 'I'*len(face), *face))
    return h.hexdigest()


def rig_hash(bones, matrix):
    value = {'bones': bones, 'matrix': matrix}
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                     allow_nan=False).encode()).hexdigest()


def world(point, matrix):
    return [sum(matrix[i][j]*point[j] for j in range(3)) + matrix[i][3] for i in range(3)]


def weighted(points, ids, amounts):
    require(all(i < len(points) for i in ids), 'MEASURE_VERTEX_ID')
    return [sum(points[i][k]*w for i, w in zip(ids, amounts)) for k in range(3)]


def section(triangles, point, normal, axes, expected, epsilon=1e-5):
    """Return all section components; reject ambiguity for scalar calculations only."""
    require(0 < epsilon <= .001, 'MEASURE_SECTION_EPSILON')
    points, cells, edges, coplanar = [], defaultdict(list), {}, []

    def node(p):
        cell = tuple(math.floor(x/epsilon) for x in p)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for i in cells[(cell[0]+dx, cell[1]+dy, cell[2]+dz)]:
                        if length(sub(points[i], p)) <= epsilon:
                            return i
        i = len(points)
        cells[cell].append(i)
        points.append(p)
        return i

    for face_id, tri in triangles:
        distances = [dot(sub(p, point), normal) for p in tri]
        if all(abs(d) <= epsilon for d in distances):
            coplanar.append(face_id)
            continue
        hits = []
        for i, d in enumerate(distances):
            a, b, e = tri[i], tri[(i+1) % 3], distances[(i+1) % 3]
            if abs(d) <= epsilon:
                hits.append(a)
            if (d < -epsilon and e > epsilon) or (d > epsilon and e < -epsilon):
                t = d/(d-e)
                hits.append([x+t*(y-x) for x, y in zip(a, b)])
        distinct = []
        for p in hits:
            if not any(length(sub(p, q)) <= epsilon for q in distinct):
                distinct.append(p)
        if len(distinct) == 2:
            a, b = [node(p) for p in distinct]
            if a != b:
                edges.setdefault(tuple(sorted((a, b))), set()).add(face_id)
        require(len(distinct) <= 2, 'MEASURE_SECTION_TRIANGLE')
    graph = defaultdict(set)
    for a, b in edges:
        graph[a].add(b)
        graph[b].add(a)
    pending = set(graph)
    components = []
    while pending:
        seen, work = set(), [min(pending)]
        while work:
            i = work.pop()
            if i in seen:
                continue
            seen.add(i)
            work.extend(graph[i] - seen)
        pending -= seen
        degrees = [len(graph[i]) for i in seen]
        closed = all(n == 2 for n in degrees)
        open_chain = degrees.count(1) == 2 and all(n in (1, 2) for n in degrees)
        components.append({'pointIds': sorted(seen), 'closed': closed,
                           'branched': not (closed or open_chain)})
    reason = ('coplanar triangles' if coplanar else 'no contour' if not components else
              'branched contour' if any(c['branched'] for c in components) else
              'unexpected component count' if len(components) != expected else None)
    spans = None if reason else [max(dot(p, a) for p in points) - min(dot(p, a) for p in points)
                                 for a in axes]
    return {'point': point, 'normal': normal, 'axes': axes, 'points': points,
            'segments': [{'pointIds': [a, b], 'faceIds': sorted(ids)}
                         for (a, b), ids in sorted(edges.items())],
            'components': components, 'coplanarFaceIds': sorted(set(coplanar)),
            'status': 'unavailable' if reason else 'available', 'reason': reason, 'spans': spans}


def compare(values, expectation, tolerance):
    source = values[0]
    rows = []
    for value in values:
        delta = percent = None
        state = 'unavailable'
        if source['value'] is not None and value['value'] is not None:
            estimated = 'clothing_estimate' in source['origins'] + value['origins']
            delta = value['value'] - source['value']
            percent = None if source['value'] == 0 else delta/abs(source['value'])*100
            state = ('estimate' if estimated else 'review' if expectation == 'preserve'
                     and abs(delta) > tolerance else 'intentional' if expectation == 'intentional'
                     else 'reference' if expectation == 'reference' else 'within_tolerance')
        rows.append(dict(value, absoluteDelta=delta, percentDelta=percent, review=state))
    return rows
