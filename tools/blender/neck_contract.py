"""Closed standard-library contracts for the separate offline neck authoring tool."""

import hashlib
import json
import math
import re
from pathlib import Path


def require(condition, code):
    if not condition:
        raise ValueError(code)


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1048576), b''):
            digest.update(chunk)
    return digest.hexdigest()


def load(path):
    require(Path(path).stat().st_size <= 4 * 1024 * 1024, 'NECK_JSON_SIZE')

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'NECK_DUPLICATE_PROPERTY')
            result[key] = value
        return result

    return json.loads(Path(path).read_text(encoding='utf-8'), object_pairs_hook=pairs,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('NECK_NONFINITE')))


def closed(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys), 'NECK_PROPERTIES')


def number(value, low=None, high=None):
    require(type(value) in (int, float) and math.isfinite(value), 'NECK_NUMBER')
    require(low is None or value >= low, 'NECK_RANGE')
    require(high is None or value <= high, 'NECK_RANGE')


def vector(value):
    require(isinstance(value, list) and len(value) == 3, 'NECK_VECTOR')
    for item in value:
        number(item, -1000000, 1000000)


def ids(value, minimum=1, maximum=200000):
    require(isinstance(value, list) and minimum <= len(value) <= maximum, 'NECK_IDS')
    require(all(type(x) is int and 0 <= x < 200000 for x in value), 'NECK_IDS')
    require(len(set(value)) == len(value), 'NECK_DUPLICATE_ID')


def validate(profile):
    closed(profile, ['operation', 'version', 'sceneHash', 'head', 'body', 'outputObject',
                     'headBoundaryVertices', 'protectedHeadVertices', 'protectedBodyVertices',
                     'cutZ', 'blendBounds', 'fade', 'iterations', 'factor', 'mergeTolerance',
                     'maxBlendDisplacement', 'shadeFade', 'view'])
    require(profile['operation'] == 'neck_author@1' and type(profile['version']) is int
            and profile['version'] == 1, 'NECK_VERSION')
    require(isinstance(profile['sceneHash'], str)
            and re.fullmatch('[0-9a-f]{64}', profile['sceneHash']), 'NECK_HASH')
    for field in ['head', 'body', 'outputObject']:
        require(isinstance(profile[field], str) and 0 < len(profile[field]) <= 128
                and not any(ord(c) < 32 for c in profile[field]), 'NECK_NAME')
    require(len({profile[k] for k in ['head', 'body', 'outputObject']}) == 3, 'NECK_SELECTOR')
    ids(profile['headBoundaryVertices'], 3, 512)
    ids(profile['protectedHeadVertices'])
    ids(profile['protectedBodyVertices'])
    require(not set(profile['headBoundaryVertices']) & set(profile['protectedHeadVertices']),
            'NECK_PROTECTED_INTERFACE')
    number(profile['cutZ'])
    closed(profile['blendBounds'], ['min', 'max'])
    vector(profile['blendBounds']['min'])
    vector(profile['blendBounds']['max'])
    require(all(a < b for a, b in zip(profile['blendBounds']['min'],
                                     profile['blendBounds']['max'])), 'NECK_BOUNDS')
    require(profile['blendBounds']['min'][2] < profile['cutZ']
            < profile['blendBounds']['max'][2], 'NECK_CUT')
    closed(profile['fade'], ['lower', 'upper'])
    for value in profile['fade'].values():
        number(value, 1e-6)
    require(sum(profile['fade'].values()) < profile['blendBounds']['max'][2]
            - profile['blendBounds']['min'][2], 'NECK_FADE')
    require(type(profile['iterations']) is int and 1 <= profile['iterations'] <= 32,
            'NECK_ITERATIONS')
    number(profile['factor'], 1e-6, 1)
    number(profile['mergeTolerance'], 1e-7, 1e-4)
    number(profile['maxBlendDisplacement'], 1e-6, 30)
    closed(profile['shadeFade'], ['minZ', 'maxZ'])
    number(profile['shadeFade']['minZ'])
    number(profile['shadeFade']['maxZ'])
    require(profile['shadeFade']['minZ'] < profile['shadeFade']['maxZ'], 'NECK_SHADE_FADE')
    closed(profile['view'], ['center', 'scale'])
    vector(profile['view']['center'])
    number(profile['view']['scale'], 1e-6, 10000)
    return profile
