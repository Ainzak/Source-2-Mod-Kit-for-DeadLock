"""Report geometric head-boundary candidates without authoring a surface.

Run with Blender --background --factory-startup --disable-autoexec --python-exit-code 1
--python neck_inspect.py -- --scene INPUT --head NAME --body NAME --output ABSENT_JSON.
"""

import argparse
from collections import defaultdict
import json
from pathlib import Path
import sys

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from neck_contract import require, sha


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['scene', 'head', 'body', 'output']:
        parser.add_argument('--' + name, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    output = Path(args.output)
    require(not output.exists() and output.parent.is_dir(), 'NECK_OUTPUT_EXISTS')
    bpy.ops.wm.open_mainfile(filepath=str(Path(args.scene).resolve()), use_scripts=False)
    head, body = [bpy.data.objects.get(name) for name in [args.head, args.body]]
    require(head is not None and body is not None and head.type == body.type == 'MESH',
            'NECK_OBJECT_SELECTOR')
    indices = {tuple(v.co): v.index for v in head.data.vertices}
    counts = defaultdict(int)
    for face in head.data.polygons:
        coords = [tuple(head.data.vertices[i].co) for i in face.vertices]
        for a, b in zip(coords, coords[1:] + coords[:1]):
            counts[frozenset((a, b))] += 1
    graph = defaultdict(set)
    for edge, count in counts.items():
        if count == 1 and len(edge) == 2:
            a, b = list(edge)
            graph[a].add(b)
            graph[b].add(a)
    pending = set(graph)
    candidates = []
    while pending:
        seed = min(pending)
        found, work = set(), [seed]
        while work:
            p = work.pop()
            if p in found:
                continue
            found.add(p)
            work.extend(graph[p] - found)
        pending -= found
        candidates.append({'vertexIds': sorted(indices[p] for p in found),
                           'closed': all(len(graph[p]) == 2 for p in found),
                           'bounds': {'min': [min(p[k] for p in found) for k in range(3)],
                                      'max': [max(p[k] for p in found) for k in range(3)]}})
    report = {'operation': 'neck_author_inspection@1', 'version': 1,
              'sceneHash': sha(args.scene), 'head': args.head, 'body': args.body,
              'boundaryCandidates': candidates, 'selectedBoundary': None,
              'requiresExplicitProfile': True, 'gameReady': False}
    with output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print('Boundary candidates:', len(candidates), '; no selection made', flush=True)


if __name__ == '__main__':
    main()
