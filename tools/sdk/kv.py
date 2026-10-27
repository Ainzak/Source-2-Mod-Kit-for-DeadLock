"""Strict reader for the characterized textual KV3 metadata/ModelDoc subset."""
import json
import math
import re
from common import require

TOKEN = re.compile(r'\s*(""".*?"""|"(?:\\.|[^"\\])*"|'
                   r'[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|'
                   r'[A-Za-z_][A-Za-z_0-9]*|[{}\[\]=,:])', re.S)


def parse(text):
    text = re.sub(r'<!--.*?-->', '', text, flags=re.S)
    tokens, pos = [], 0
    while text[pos:].strip():
        comment = re.match(r'\s*//[^\n]*(?:\n|$)', text[pos:])
        if comment:
            pos += comment.end()
            continue
        match = TOKEN.match(text, pos)
        require(match is not None, 'KV_UNSUPPORTED_TOKEN:' + str(pos))
        tokens.append(match.group(1))
        pos = match.end()
    index = 0
    def value(depth=0):
        nonlocal index
        require(depth < 64 and index < len(tokens), 'KV_DEPTH_OR_EOF')
        token = tokens[index]
        index += 1
        if token == '{':
            result = {}
            while index < len(tokens) and tokens[index] != '}':
                if tokens[index] == ',':
                    index += 1
                    continue
                key = tokens[index]
                index += 1
                key = json.loads(key) if key.startswith('"') else key
                require(key not in result and tokens[index] == '=', 'KV_KEY_INVALID')
                index += 1
                result[key] = value(depth + 1)
            require(index < len(tokens), 'KV_EOF')
            index += 1
            return result
        if token == '[':
            result = []
            while index < len(tokens) and tokens[index] != ']':
                if tokens[index] == ',':
                    index += 1
                else:
                    result.append(value(depth + 1))
            require(index < len(tokens), 'KV_EOF')
            index += 1
            return result
        if token.startswith('"""'):
            return token[3:-3]
        if token.startswith('"'):
            return json.loads(token)
        if token in ('true', 'false'):
            return token == 'true'
        if token == 'null':
            return None
        if re.fullmatch(r'[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?', token):
            result = float(token) if any(c in token for c in '.eE') else int(token)
            require(math.isfinite(result), 'KV_NONFINITE')
            return result
        require(index < len(tokens) and tokens[index] == ':', 'KV_UNSUPPORTED_VALUE')
        index += 1
        return {'$type': token, '$value': value(depth + 1)}
    result = value()
    require(index == len(tokens), 'KV_TRAILING_INPUT')
    return result


def emit(v):
    if isinstance(v, dict):
        if set(v) == {'$type', '$value'}:
            require(v['$type'] in ('resource', 'resource_name', 'subclass'), 'KV_UNSUPPORTED_TYPE')
            return v['$type'] + ':' + emit(v['$value'])
        return '{' + ' '.join(json.dumps(k) + '=' + emit(x) for k, x in v.items()) + '}'
    if isinstance(v, list):
        return '[' + ','.join(emit(x) for x in v) + ']'
    return json.dumps(v, allow_nan=False)


def field(text, name):
    match = re.search(r'\b' + re.escape(name) + r'\s*=\s*', text)
    require(match is not None, 'SOURCE_FIELD_MISSING:' + name)
    start = match.end()
    found = TOKEN.match(text, start)
    require(found is not None, 'SOURCE_FIELD_INVALID')
    token = found.group(1)
    if token not in ('[', '{'):
        return parse(token)
    # Match containers through tokenization, including quoted/multiline braces.
    depth, pos = 1, found.end()
    while depth:
        found = TOKEN.match(text, pos)
        require(found is not None, 'SOURCE_FIELD_INVALID')
        token = found.group(1)
        if token in ('[', '{'):
            depth += 1
        if token in (']', '}'):
            depth -= 1
        pos = found.end()
    return parse(text[start:pos])


def metadata(text):
    return parse(field(text, 'm_keyValueText'))


def refs(text):
    return sorted(set(re.findall(r'\b(?:models|animgraphs|scripts|materials)/[a-z0-9_+./-]+',
                                 text.split('--- Resource Blocks:')[0])))
