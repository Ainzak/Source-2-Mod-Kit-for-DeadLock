"""Independent parsed metadata and world-bind checks; no exporter success flags."""
import math
from common import canonical, require
from kv import field, metadata


def animation_masks(text):
    names = field(text, 'm_localBoneNameArray')
    require(len(names) == len(set(names)), 'MASK_BONE_INVENTORY_INVALID')
    result = {}
    for mask in field(text, 'm_localBoneMaskArray'):
        name = mask['m_sName']
        indices, weights = mask['m_nLocalBoneArray'], mask['m_flBoneWeightArray']
        require(name and name not in result and len(indices) == len(weights) and
                len(indices) == len(set(indices)), 'MASK_INVENTORY_INVALID')
        require(all(type(i) is int and 0 <= i < len(names) for i in indices), 'MASK_BONE_INDEX_INVALID')
        require(all(type(w) in (int, float) and math.isfinite(w) and 0 <= w <= 1 for w in weights),
                'MASK_WEIGHT_INVALID')
        require(set(mask) == {'m_sName', 'm_nLocalBoneArray', 'm_flBoneWeightArray',
                             'm_flDefaultMorphCtrlWeight', 'm_morphCtrlWeightArray'}, 'UNSUPPORTED_COMPILED_MASK')
        result[name] = {'bones': dict(zip((names[i] for i in indices), weights)),
                        'defaultMorph': mask['m_flDefaultMorphCtrlWeight'],
                        'morph': mask['m_morphCtrlWeightArray']}
    return result


def compare_animation_masks(source, actual):
    require([m['m_sName'] for m in field(source, 'm_localBoneMaskArray')] ==
            [m['m_sName'] for m in field(actual, 'm_localBoneMaskArray')], 'ANIMATION_MASK_ORDER_CHANGED')
    require(canonical(animation_masks(source)) == canonical(animation_masks(actual)), 'ANIMATION_MASKS_CHANGED')


def matrix(position, quaternion):
    length = math.sqrt(sum(x*x for x in quaternion))
    require(length > 0 and len(position) == 3 and len(quaternion) == 4, 'INVALID_BIND')
    x, y, z, w = [v / length for v in quaternion]
    return [[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w), position[0]],
            [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w), position[1]],
            [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y), position[2]],
            [0, 0, 0, 1]]


def multiply(a, b):
    return [[sum(a[i][k]*b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def rig(text):
    names, parents, positions, rotations = [field(text, key) for key in
        ('m_boneName', 'm_nParent', 'm_bonePosParent', 'm_boneRotParent')]
    require(0 < len(names) <= 512 and len(set(names)) == len(names), 'BONE_INVENTORY_INVALID')
    require(len(names) == len(parents) == len(positions) == len(rotations), 'BONE_ARRAY_LENGTH')
    require(all(type(p) is int and -1 <= p < len(names) for p in parents), 'INVALID_BONE_PARENT')
    world, visiting = {}, set()
    def visit(i):
        if i in world:
            return world[i]
        require(i not in visiting, 'CYCLIC_BONE_PARENT')
        visiting.add(i)
        value = matrix(positions[i], rotations[i])
        if parents[i] >= 0:
            value = multiply(visit(parents[i]), value)
        visiting.remove(i)
        world[i] = value
        return value
    return {name: (names[parents[i]] if parents[i] >= 0 else None, visit(i))
            for i, name in enumerate(names)}


def compare(source, actual):
    require(canonical(metadata(source)) == canonical(metadata(actual)), 'HERO_METADATA_CHANGED')
    a, b = rig(source), rig(actual)
    require(a.keys() == b.keys(), 'BONE_NAMES_CHANGED')
    max_position, max_rotation = 0, 0
    for name, (parent, x) in a.items():
        other_parent, y = b[name]
        require(parent == other_parent, 'BONE_PARENTS_CHANGED')
        position = math.sqrt(sum((x[i][3]-y[i][3])**2 for i in range(3)))
        trace = sum(x[i][j]*y[i][j] for i in range(3) for j in range(3))
        rotation = math.degrees(math.acos(max(-1, min(1, (trace-1)/2))))
        max_position = max(position, max_position)
        max_rotation = max(rotation, max_rotation)
    require(max_position < 0.002 and max_rotation < 0.002, 'BIND_TOLERANCE_EXCEEDED')
    return max_position, max_rotation
