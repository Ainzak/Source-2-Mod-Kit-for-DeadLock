"""Actual triangle geometry diagnostics, independent of normals and export code."""
import math
from common import require
from skinned import cross

EPS = 1e-7


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def sub(a, b):
    return [x-y for x, y in zip(a, b)]


def triangle(mesh, ti):
    return [mesh['positions'][mesh['corners'][l]] for l in mesh['triangles'][ti]['loops']]


def point_inside(point, tri, strict=False):
    a, b, c = tri
    u, v, w = sub(b, a), sub(c, a), sub(point, a)
    uu, uv, vv, wu, wv = dot(u, u), dot(u, v), dot(v, v), dot(w, u), dot(w, v)
    denom = uu*vv-uv*uv
    if abs(denom) < 1e-16: return False
    s, t = (vv*wu-uv*wv)/denom, (uu*wv-uv*wu)/denom
    return min(s, t, 1-s-t) > EPS if strict else min(s, t, 1-s-t) >= -EPS


def intersects(a, b):
    """Reject overlap, allowing only shared geometric points/edges."""
    shared = [p for p in a if any(math.dist(p, q) < EPS for q in b)]
    na, nb = cross(*a), cross(*b)
    if dot(na, na) < 1e-12 or dot(nb, nb) < 1e-12: return False
    distances = [dot(nb, sub(p, b[0])) for p in a]
    coplanar = max(abs(d) for d in distances) < EPS*math.sqrt(dot(nb, nb))
    if coplanar:
        # Strict interior vertex or centroid containment captures identical/contained faces.
        for src, dst in ((a, b), (b, a)):
            if any(point_inside(p, dst, True) for p in src): return True
            center = [sum(p[k] for p in src)/3 for k in range(3)]
            if point_inside(center, dst, True): return True
        axis = max(range(3), key=lambda i: abs(na[i]))
        keep = [i for i in range(3) if i != axis]
        def orient(p, q, r):
            x, y = keep
            return (q[x]-p[x])*(r[y]-p[y])-(q[y]-p[y])*(r[x]-p[x])
        for p, q in zip(a, a[1:]+a[:1]):
            for r, s in zip(b, b[1:]+b[:1]):
                if orient(p,q,r)*orient(p,q,s) < -EPS and orient(r,s,p)*orient(r,s,q) < -EPS:
                    return True
        return False
    for src, dst, normal in ((a,b,nb), (b,a,na)):
        for p, q in zip(src, src[1:]+src[:1]):
            d0, d1 = dot(normal, sub(p,dst[0])), dot(normal, sub(q,dst[0]))
            if abs(d0-d1) < 1e-12: continue
            t = d0/(d0-d1)
            if -EPS <= t <= 1+EPS:
                point = [x+t*(y-x) for x,y in zip(p,q)]
                if any(math.dist(point,s) < EPS for s in shared): continue
                if len(shared) == 2:
                    edge = sub(shared[1],shared[0]); rel = sub(point,shared[0])
                    length = dot(edge,edge)
                    along = dot(rel,edge)/length
                    if -EPS <= along <= 1+EPS and math.dist(point,
                            [shared[0][k]+along*edge[k] for k in range(3)]) < EPS: continue
                if point_inside(point,dst): return True
    return False


def intersections(neutral, actual, changed):
    if not changed: return {'checkedPairs': 0, 'introduced': [], 'sourceDefects': [], 'scope': 'changed_faces_vs_mesh'}
    bounds = []
    for ti in range(len(actual['triangles'])):
        tri = triangle(actual, ti)
        bounds.append(([min(p[k] for p in tri) for k in range(3)],
                       [max(p[k] for p in tri) for k in range(3)], ti))
    active, count, defects = [], 0, []
    for low, high, ti in sorted(bounds, key=lambda x:x[0][0]):
        active = [(l,h,i) for l,h,i in active if h[0]+EPS >= low[0]]
        for other_low, other_high, other in active:
            if ti not in changed and other not in changed: continue
            if any(high[k]+EPS < other_low[k] or other_high[k]+EPS < low[k] for k in (1,2)): continue
            count += 1
            require(count <= 2000000, 'SURFACE_INTERSECTION_PAIR_BUDGET')
            if intersects(triangle(actual,ti),triangle(actual,other)):
                # An old defect is evidence, not permission to move intersecting faces.
                old = intersects(triangle(neutral,ti),triangle(neutral,other))
                defects.append({'faces': [other,ti], 'preExisting': old})
        active.append((low,high,ti))
    require(not defects, 'SURFACE_INTERSECTION:' + str(defects[:8]))
    return {'checkedPairs': count, 'introduced': [], 'sourceDefects': [], 'scope': 'changed_faces_vs_mesh'}


def section(mesh, normal, offset):
    segments = []
    for ti in range(len(mesh['triangles'])):
        tri, points = triangle(mesh,ti), []
        distances = [dot(normal,p)-offset for p in tri]
        # Coplanar triangles are reported by their edges, not silently discarded.
        if max(abs(d) for d in distances) < EPS:
            segments.extend({'face': ti, 'points': [p,q], 'coplanar': True}
                            for p,q in zip(tri,tri[1:]+tri[:1]))
            continue
        for k in range(3):
            p,q = tri[k],tri[(k+1)%3]; a,b = distances[k],distances[(k+1)%3]
            if abs(a) < EPS: points.append(p)
            if a*b < 0:
                t = a/(a-b); points.append([x+t*(y-x) for x,y in zip(p,q)])
        unique = []
        for p in points:
            if not any(math.dist(p,q) < EPS for q in unique): unique.append(p)
        if len(unique) == 2: segments.append({'face':ti,'points':unique,'coplanar':False})
    return segments


def cross_sections(source, actual, specs):
    result = []
    for spec in specs:
        require(abs(dot(spec['normal'],spec['normal'])-1) < 1e-6, 'SURFACE_SECTION_NORMAL')
        result.append({'name':spec['name'], 'normal':spec['normal'], 'offset':spec['offset'],
                       'source':section(source,spec['normal'],spec['offset']),
                       'authored':section(actual,spec['normal'],spec['offset'])})
    return result
