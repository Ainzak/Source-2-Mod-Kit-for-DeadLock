"""Run immutable scene measurements and matched reports using configured Blender."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from measure_contract import load, sha, validate
from measure_math import require
from measure_report import write


def run(args):
    output, blender, policy = [Path(p).resolve() for p in [args.output, args.blender, args.profile]]
    require(not output.exists() and output.parent.is_dir(), 'MEASURE_OUTPUT_EXISTS')
    require(blender.is_file(), 'MEASURE_BLENDER')
    profile_hash = sha(policy)
    profile = validate(load(policy))
    require(sha(policy) == profile_hash, 'MEASURE_PROFILE_CHANGED')
    for spec in profile['inputs']:
        scene = (policy.parent / spec['scene']).resolve()
        require(scene.is_file() and sha(scene) == spec['sceneHash'], 'MEASURE_SCENE_HASH')
        spec['scene'] = str(scene)
    here = Path(__file__).resolve().parent
    paths = [here / f for f in ['measure.py', 'measure_contract.py', 'measure_math.py',
                                'measure_blender.py', 'measure_report.py', 'neck_contract.py']]
    tools = {p.name: sha(p) for p in paths}
    output.mkdir()
    request = output / 'request.json'
    with request.open('x', encoding='utf-8') as f:
        json.dump({'profile': profile, 'output': str(output)}, f, indent=2, allow_nan=False)
    env = dict(os.environ, TEMP=str(output), TMP=str(output))
    command = [str(blender), '--background', '--factory-startup', '--disable-autoexec',
               '--python-exit-code', '1', '--python', str(here / 'measure_blender.py'), '--', str(request)]
    start = time.monotonic()
    with (output / 'blender.log').open('xb') as log:
        process = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                 env=env, timeout=240, check=False)
    require(process.returncode == 0, 'MEASURE_BLENDER_FAILED:' + str(output / 'blender.log'))
    require(all(sha(x['scene']) == x['sceneHash'] for x in profile['inputs']), 'MEASURE_INPUT_CHANGED')
    require(sha(policy) == profile_hash and all(sha(p) == tools[p.name] for p in paths),
            'MEASURE_PROFILE_TOOL_CHANGED')
    report = load(output / 'measurements.json')
    require(report['operation'] == 'character_measure_result@1'
            and report['inputFilesUnchanged'] is True, 'MEASURE_RESULT')
    write(report, profile, output)
    manifest = {'operation': 'character_measure_bundle@1', 'version': 1,
                'profileHash': profile_hash, 'toolHashes': tools,
                'inputSceneHashes': {s['id']: s['sceneHash'] for s in profile['inputs']},
                'blenderVersion': report['blenderVersion'], 'offline': True, 'gameReady': False,
                'elapsedSeconds': round(time.monotonic()-start, 3),
                'files': {p.name: sha(p) for p in sorted(output.iterdir()) if p.is_file()}}
    with (output / 'bundle.json').open('x', encoding='utf-8') as f:
        json.dump(manifest, f, indent=2)
    print(json.dumps({'report': str(output / 'report.html'),
                      'elapsedSeconds': manifest['elapsedSeconds'], 'gameReady': False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ['blender', 'profile', 'output']:
        parser.add_argument('--' + arg, required=True)
    try:
        run(parser.parse_args())
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
