"""Closed contracts, immutable artifacts and bounded external processes."""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
MAX_BYTES = 256 * 1024 * 1024


def require(condition, code):
    if not condition:
        raise ValueError(code)


def sha(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1048576), b''):
            h.update(chunk)
    return h.hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def write(path, value):
    with Path(path).open('xb') as stream:
        stream.write(canonical(value))


def load(path):
    require(Path(path).stat().st_size <= MAX_BYTES, 'INPUT_TOO_LARGE')
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'DUPLICATE_KEY')
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding='utf-8'), object_pairs_hook=pairs,
                      parse_constant=lambda x: require(False, 'NONFINITE_NUMBER'))


def validate(value, kind):
    versioned = kind in ('intent-v2', 'intent-v3', 'intent-v4')
    schema = load(HERE / 'schemas' / (kind.split('-')[-1] if versioned else 'v1') /
                  (('intent' if versioned else kind) + '.schema.json'))
    def check(v, s):
        if '$ref' in s:
            s = schema['$defs'][s['$ref'].split('/')[-1]]
        if 'const' in s:
            require(v == s['const'] and type(v) is type(s['const']), 'CONTRACT_CONST')
        if 'enum' in s:
            require(v in s['enum'], 'CONTRACT_ENUM')
        types = {'object': dict, 'array': list, 'string': str, 'integer': int,
                 'number': (int, float), 'boolean': bool}
        if 'type' in s:
            require(isinstance(v, types[s['type']]) and
                    (s['type'] not in ('number', 'integer') or not isinstance(v, bool)),
                    'CONTRACT_TYPE')
        if isinstance(v, dict):
            require(set(s.get('required', [])) <= v.keys(), 'CONTRACT_REQUIRED')
            for key, item in v.items():
                child = s.get('properties', {}).get(key, s.get('additionalProperties', {}))
                require(child is not False, 'CONTRACT_UNKNOWN_FIELD:' + key)
                if isinstance(child, dict):
                    check(item, child)
        if isinstance(v, list):
            require(s.get('minItems', 0) <= len(v) <= s.get('maxItems', 10000000), 'CONTRACT_SIZE')
            if s.get('uniqueItems'):
                require(len({canonical(x) for x in v}) == len(v), 'CONTRACT_DUPLICATE')
            for item in v:
                check(item, s.get('items', {}))
        if isinstance(v, str):
            require(len(v) >= s.get('minLength', 0), 'CONTRACT_EMPTY')
            require(re.search(s.get('pattern', ''), v) is not None, 'CONTRACT_PATTERN')
        if isinstance(v, (int, float)) and not isinstance(v, bool):
            require(math.isfinite(v) and s.get('minimum', -math.inf) <= v <=
                    s.get('maximum', math.inf), 'CONTRACT_RANGE')
    check(value, schema)
    return value


def logical(value):
    require(isinstance(value, str) and re.fullmatch(r'[a-z0-9_+./-]+', value), 'UNSAFE_LOGICAL_PATH')
    require(not value.startswith('/') and all(p not in ('', '.', '..') for p in value.split('/')),
            'UNSAFE_LOGICAL_PATH')
    return value


def below(root, relative):
    root = Path(root).resolve()
    result = root / artifact_path(relative)
    require(result.resolve().is_relative_to(root), 'PATH_ESCAPE')
    return result


def artifact_path(value):
    # Decompiled source filenames can contain original uppercase animation names.
    # This is an artifact path, not a lower-case virtual archive selector.
    require(isinstance(value, str) and re.fullmatch(r'[A-Za-z0-9_+./-]+', value) and
            not value.startswith('/') and all(p not in ('', '.', '..') for p in value.split('/')),
            'UNSAFE_ARTIFACT_PATH')
    return value


def fresh(path):
    path = Path(path).absolute()
    require(not path.exists() and not path.is_symlink(), 'OUTPUT_EXISTS')
    require(path.parent.is_dir(), 'OUTPUT_PARENT_MISSING')
    path.mkdir()
    return path


def pins(root):
    root = Path(root)
    result = {}
    for path in sorted(root.rglob('*')):
        require(not path.is_symlink(), 'SYMLINK_INPUT')
        if path.is_file() and path.name != 'timings.jsonl':   # timing records grow later; never pinned
            require(path.stat().st_size <= MAX_BYTES, 'ARTIFACT_TOO_LARGE')
            result[path.relative_to(root).as_posix()] = sha(path)
    return result


def check_pins(root, files):
    for name, expected in files.items():
        require(sha(below(root, name)) == expected, 'INPUT_DRIFT:' + name)


def run(args, log, timeout=300, cwd=None):
    """Run one step and record its wall time in timings.jsonl beside the log."""
    log = Path(log)
    require(not log.exists(), 'LOG_EXISTS')
    began, clock, ok = datetime.now(timezone.utc), time.monotonic(), False
    try:
        text = _run(args, log, timeout, cwd)
        ok = True
        return text
    finally:
        line = {'step': log.stem, 'seconds': round(time.monotonic() - clock, 3), 'ok': ok,
                'started': began.strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'}
        with (log.parent / 'timings.jsonl').open('a', encoding='utf-8', newline='\n') as stream:
            stream.write(json.dumps(line, separators=(',', ':')) + '\n')


def timing_summary(folder):
    """Per-step [count, total seconds] from every timings.jsonl below folder."""
    result = {}
    for path in sorted(Path(folder).rglob('timings.jsonl')):
        for text in path.read_text(encoding='utf-8').splitlines():
            if text.strip():
                item = json.loads(text)
                entry = result.setdefault(item['step'], [0, 0.0])
                entry[0] += 1
                entry[1] += item['seconds']
    return result


def _run(args, log, timeout, cwd):
    """No shell. Kill the child tree on Windows timeout or keyboard interruption."""
    started = time.monotonic()
    if os.environ.get('S2MK_VERBOSE'):                       # step lines only on request
        print('Running ' + log.stem + ' ...', file=sys.stderr, flush=True)
    with log.open('xb') as stream:
        proc = subprocess.Popen([str(a) for a in args], cwd=cwd, stdout=stream,
                                stderr=subprocess.STDOUT)
        try:
            while proc.poll() is None:
                if time.monotonic() - started > timeout:
                    raise TimeoutError('PROCESS_TIMEOUT')
                if log.stat().st_size > MAX_BYTES:
                    raise ValueError('PROCESS_LOG_TOO_LARGE')
                time.sleep(0.1)
        except BaseException:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            proc.kill()
            proc.wait()
            raise
    require(proc.returncode == 0, 'PROCESS_FAILED:' + str(log))
    text = log.read_text(encoding='utf-8', errors='replace')
    require('Traceback (most recent call last)' not in text or 'Exception ignored in atexit' in text,
            'PROCESS_SCRIPT_FAILED:' + str(log))
    return text
