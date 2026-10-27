"""Run bounded neck authoring with the caller's explicitly configured Blender."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from neck_contract import load, require, sha, validate


def run(args):
    scene = Path(args.scene).resolve()
    blender = Path(args.blender).resolve()
    output = Path(args.output).resolve()
    require(scene.is_file() and scene.suffix.lower() == '.blend', 'NECK_SCENE')
    require(blender.is_file(), 'NECK_BLENDER')
    require(output.parent.is_dir() and not output.exists(), 'NECK_OUTPUT_EXISTS')
    profile_hash = sha(args.profile)
    profile = validate(load(args.profile))
    require(sha(args.profile) == profile_hash, 'NECK_PROFILE_CHANGED')
    require(sha(scene) == profile['sceneHash'], 'NECK_INPUT_HASH')
    here = Path(__file__).resolve().parent
    toolpaths = [here / name for name in
                 ['neck.py', 'neck_contract.py', 'neck_job.py', 'neck_verify.py']]
    toolpaths += [here.parent / 'sdk' / name for name in
                  ['surface_geometry.py', 'common.py', 'skinned.py']]
    tools = {str(p.relative_to(here.parent)): sha(p) for p in toolpaths}
    output.mkdir()
    request = {'scene': str(scene), 'profile': profile, 'output': str(output)}
    (output / 'request.json').write_text(json.dumps(request, indent=2), encoding='utf-8')
    env = dict(os.environ, TEMP=str(output), TMP=str(output))
    command = [str(blender), '--background', '--factory-startup', '--disable-autoexec',
               '--python-exit-code', '1', '--python', str(here / 'neck_job.py'), '--',
               '--request', str(output / 'request.json')]
    start = time.monotonic()
    with (output / 'blender.log').open('wb') as log:
        process = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT,
                                 timeout=240, check=False)
    require(process.returncode == 0, 'NECK_BLENDER_FAILED:' + str(output / 'blender.log'))
    require(sha(scene) == profile['sceneHash'], 'NECK_INPUT_CHANGED')
    require(sha(args.profile) == profile_hash, 'NECK_PROFILE_CHANGED')
    require(all(sha(p) == tools[str(p.relative_to(here.parent))] for p in toolpaths),
            'NECK_TOOL_DRIFT')
    verification = load(output / 'verification.json')
    require(verification['passed'] is True, 'NECK_VERIFICATION')
    manifest = {'operation': 'neck_author_result@1', 'version': 1, 'sceneHash': sha(scene),
                'profileHash': profile_hash, 'toolHashes': tools,
                'blenderVersion': verification['blenderVersion'],
                'elapsedSeconds': round(time.monotonic() - start, 3),
                'files': {p.name: sha(p) for p in output.iterdir()
                          if p.suffix in ['.blend', '.json', '.png']},
                'gameReady': False, 'compiled': False}
    with (output / 'bundle.json').open('x', encoding='utf-8') as stream:
        json.dump(manifest, stream, indent=2)
    print(json.dumps({'result': str(output / 'bundle.json'),
                      'elapsedSeconds': manifest['elapsedSeconds'], 'gameReady': False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blender', required=True)
    parser.add_argument('--scene', required=True)
    parser.add_argument('--profile', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        run(args)
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
