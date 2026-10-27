"""Closed read-only measurement profiles, using existing strict JSON/hash primitives."""

import math
import re

from neck_contract import load as strict_load, sha
from measure_math import dot, require

ORIGINS = {'observed_skin', 'source_skeleton', 'clothing_estimate', 'authored_anatomy',
           'intentional_change', 'unavailable'}


def load(path):
    try:
        return strict_load(path)
    except ValueError as error:
        raise ValueError(str(error).replace('NECK_', 'MEASURE_')) from None


def closed(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys), 'MEASURE_PROPERTIES')


def name(value):
    require(isinstance(value, str) and 0 < len(value) <= 128
            and not any(ord(c) < 32 for c in value), 'MEASURE_NAME')


def identifier(value):
    require(isinstance(value, str) and re.fullmatch('[a-z][a-z0-9_]{0,63}', value),
            'MEASURE_IDENTIFIER')


def number(value, low=-1000000, high=1000000):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high,
            'MEASURE_NUMBER')


def vector(value, unit=False):
    require(isinstance(value, list) and len(value) == 3, 'MEASURE_VECTOR')
    for x in value:
        number(x)
    if unit:
        require(abs(dot(value, value)-1) < 1e-8, 'MEASURE_UNIT_VECTOR')


def unique(values, minimum=0, maximum=500000, integers=False):
    require(isinstance(values, list) and minimum <= len(values) <= maximum, 'MEASURE_LIST')
    if integers:
        require(all(type(x) is int and 0 <= x < 1000000 for x in values), 'MEASURE_IDS')
    else:
        for x in values:
            name(x)
    require(len(set(values)) == len(values), 'MEASURE_DUPLICATE_ID')


def keyed(values):
    require(isinstance(values, dict) and 1 <= len(values) <= 1024, 'MEASURE_MAP')
    for key in values:
        identifier(key)


def origin(value):
    require(isinstance(value, str) and value in ORIGINS, 'MEASURE_ORIGIN')


def digest(value):
    require(isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value), 'MEASURE_HASH')


def validate(p):
    closed(p, ['operation', 'version', 'frame', 'inputs', 'measurements', 'sections', 'views'])
    require(p['operation'] == 'character_measure@1' and type(p['version']) is int
            and p['version'] == 1, 'MEASURE_VERSION')
    f = p['frame']
    closed(f, ['units', 'pose', 'axes', 'ground'])
    require(f['units'] in ['source_units', 'synthetic_units'], 'MEASURE_UNITS')
    require(f['pose'] == 'mesh_basis/bone_rest', 'MEASURE_POSE')
    require(isinstance(f['axes'], list) and len(f['axes']) == 3, 'MEASURE_AXES')
    for axis in f['axes']:
        vector(axis, True)
    require(all(abs(dot(f['axes'][i], f['axes'][j])) < 1e-8 for i in range(3)
                for j in range(i)), 'MEASURE_AXES')
    a, b, c = f['axes']
    cross = [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
    require(dot(cross, c) > .99999999, 'MEASURE_FRAME_HANDEDNESS')
    closed(f['ground'], ['point', 'normal'])
    vector(f['ground']['point'])
    vector(f['ground']['normal'], True)
    require(isinstance(p['inputs'], list) and 2 <= len(p['inputs']) <= 4, 'MEASURE_INPUTS')
    for x in p['inputs']:
        closed(x, ['id', 'scene', 'sceneHash', 'units', 'pose', 'meshes', 'rig',
                   'landmarks', 'regions', 'visibleMeshes'])
    unique([x['id'] for x in p['inputs']], 2, 4)
    for x in p['inputs']:
        identifier(x['id'])
    require(isinstance(p['measurements'], list) and 1 <= len(p['measurements']) <= 256,
            'MEASURE_MEASUREMENTS')
    for m in p['measurements']:
        closed(m, ['id', 'label', 'kind', 'landmarks', 'regions', 'section', 'axis',
                   'expectation', 'absoluteTolerance'])
    unique([x['id'] for x in p['measurements']], 1, 256)
    require(isinstance(p['sections'], list) and len(p['sections']) <= 64, 'MEASURE_SECTIONS')
    for s in p['sections']:
        closed(s, ['id', 'label', 'region', 'point', 'normal', 'axes', 'expectedComponents'])
    unique([x['id'] for x in p['sections']], 0, 64)
    for s in p['sections']:
        identifier(s['id'])
        closed(s, ['id', 'label', 'region', 'point', 'normal', 'axes', 'expectedComponents'])
        name(s['label'])
        name(s['region'])
        vector(s['point'])
        vector(s['normal'], True)
        require(isinstance(s['axes'], list) and len(s['axes']) == 2, 'MEASURE_SECTION_AXES')
        for axis in s['axes']:
            vector(axis, True)
            require(abs(dot(axis, s['normal'])) < 1e-8, 'MEASURE_SECTION_AXES')
        require(abs(dot(*s['axes'])) < 1e-8, 'MEASURE_SECTION_AXES')
        require(type(s['expectedComponents']) is int and 1 <= s['expectedComponents'] <= 16,
                'MEASURE_COMPONENT_COUNT')
    section_ids = {s['id'] for s in p['sections']}
    for m in p['measurements']:
        identifier(m['id'])
        closed(m, ['id', 'label', 'kind', 'landmarks', 'regions', 'section', 'axis',
                   'expectation', 'absoluteTolerance'])
        name(m['label'])
        require(m['kind'] in ['distance', 'delta', 'span', 'projection', 'section_span',
                              'angle', 'peak_distance', 'clearance'], 'MEASURE_KIND')
        unique(m['landmarks'], 0, 2)
        unique(m['regions'], 0, 2)
        vector(m['axis'], True)
        require(m['expectation'] in ['preserve', 'intentional', 'reference'], 'MEASURE_EXPECTATION')
        number(m['absoluteTolerance'], 0)
        counts = {'distance': (2, 0), 'delta': (2, 0), 'angle': (2, 0), 'span': (0, 1),
                  'projection': (1, 1), 'section_span': (0, 0), 'peak_distance': (0, 2),
                  'clearance': (0, 1)}
        require((len(m['landmarks']), len(m['regions'])) == counts[m['kind']], 'MEASURE_ARGUMENTS')
        require((isinstance(m['section'], str) and m['section'] in section_ids if m['kind'] == 'section_span'
                 else m['section'] is None), 'MEASURE_SECTION_REFERENCE')
        if m['kind'] == 'section_span':
            axes = next(s['axes'] for s in p['sections'] if s['id'] == m['section'])
            require(any(m['axis'] == a for a in axes), 'MEASURE_SECTION_AXIS')
    for x in p['inputs']:
        closed(x, ['id', 'scene', 'sceneHash', 'units', 'pose', 'meshes', 'rig',
                   'landmarks', 'regions', 'visibleMeshes'])
        require(isinstance(x['scene'], str) and x['scene'].lower().endswith('.blend')
                and len(x['scene']) <= 1024, 'MEASURE_SCENE')
        digest(x['sceneHash'])
        require(x['units'] == f['units'], 'MEASURE_UNITS_MISMATCH')
        require(x['pose'] == f['pose'], 'MEASURE_POSE_MISMATCH')
        keyed(x['meshes'])
        for mesh in x['meshes'].values():
            closed(mesh, ['object', 'geometryHash'])
            name(mesh['object'])
            digest(mesh['geometryHash'])
        unique([m['object'] for m in x['meshes'].values()], 1, 1024)
        closed(x['rig'], ['object', 'restHash'])
        name(x['rig']['object'])
        digest(x['rig']['restHash'])
        unique(x['visibleMeshes'], 1, 1024)
        require(set(x['visibleMeshes']) <= set(x['meshes']), 'MEASURE_VISIBLE_MESH')
        keyed(x['landmarks'])
        for landmark in x['landmarks'].values():
            closed(landmark, ['kind', 'mesh', 'bone', 'endpoint', 'ids', 'weights', 'origin', 'note'])
            origin(landmark['origin'])
            name(landmark['note'])
            unique(landmark['ids'], 0, 128, True)
            require(isinstance(landmark['weights'], list)
                    and len(landmark['weights']) == len(landmark['ids']), 'MEASURE_WEIGHTS')
            for w in landmark['weights']:
                number(w, 0, 1)
            if landmark['kind'] == 'surface':
                require(isinstance(landmark['mesh'], str) and landmark['mesh'] in x['meshes'] and landmark['bone'] is None
                        and landmark['endpoint'] is None and landmark['ids']
                        and abs(sum(landmark['weights'])-1) < 1e-8
                        and landmark['origin'] not in ['source_skeleton', 'unavailable'],
                        'MEASURE_SURFACE_REFERENCE')
            elif landmark['kind'] == 'bone':
                name(landmark['bone'])
                require(landmark['mesh'] is None and landmark['endpoint'] in ['head', 'tail']
                        and not landmark['ids'] and landmark['origin'] == 'source_skeleton',
                        'MEASURE_BONE_REFERENCE')
            else:
                require(landmark['kind'] == 'unavailable' and landmark['mesh'] is None
                        and landmark['bone'] is None and landmark['endpoint'] is None
                        and not landmark['ids'] and landmark['origin'] == 'unavailable',
                        'MEASURE_UNAVAILABLE_REFERENCE')
        keyed(x['regions'])
        for region in x['regions'].values():
            closed(region, ['mesh', 'faceIds', 'origin', 'note'])
            origin(region['origin'])
            name(region['note'])
            unique(region['faceIds'], 0, 500000, True)
            require((region['origin'] == 'unavailable' and region['mesh'] is None
                     and not region['faceIds']) or (region['origin'] not in ['unavailable',
                     'source_skeleton'] and isinstance(region['mesh'], str)
                     and region['mesh'] in x['meshes'] and region['faceIds']),
                    'MEASURE_REGION_REFERENCE')
        for m in p['measurements']:
            require(set(m['landmarks']) <= set(x['landmarks'])
                    and set(m['regions']) <= set(x['regions']), 'MEASURE_MISSING_REFERENCE')
        require(all(s['region'] in x['regions'] for s in p['sections']),
                'MEASURE_SECTION_REGION')
    require(isinstance(p['views'], list) and 1 <= len(p['views']) <= 32, 'MEASURE_VIEWS')
    for v in p['views']:
        closed(v, ['id', 'center', 'scale', 'direction', 'landmarks', 'sections'])
    unique([v['id'] for v in p['views']], 1, 32)
    for v in p['views']:
        identifier(v['id'])
        closed(v, ['id', 'center', 'scale', 'direction', 'landmarks', 'sections'])
        vector(v['center'])
        number(v['scale'], .000001, 10000)
        vector(v['direction'], True)
        require(abs(dot(v['direction'], f['axes'][2])) < .99, 'MEASURE_CAMERA_UP')
        unique(v['landmarks'], 0, 32)
        unique(v['sections'], 0, 64)
        require(set(v['sections']) <= section_ids
                and all(set(v['landmarks']) <= set(x['landmarks']) for x in p['inputs']),
                'MEASURE_VIEW_REFERENCE')
    return p
