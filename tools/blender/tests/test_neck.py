import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import neck
from neck_contract import load, validate


def profile():
    return json.loads((Path(__file__).parent / 'fixtures/neck-profile.json').read_text())


class NeckTests(unittest.TestCase):
    def test_explicit_profile(self):
        self.assertEqual(validate(profile())['operation'], 'neck_author@1')

    def test_missing_unknown_nested_and_boolean_fields(self):
        for mutate in [lambda p: p.pop('factor'), lambda p: p.update(extra=0),
                       lambda p: p['fade'].update(extra=0), lambda p: p.update(version=True),
                       lambda p: p.update(iterations=True), lambda p: p.update(factor=True)]:
            p = profile()
            mutate(p)
            with self.assertRaises(ValueError):
                validate(p)

    def test_id_aliases_ranges_and_boundary_protection(self):
        for key, value in [('headBoundaryVertices', [0, 0, 2]),
                           ('headBoundaryVertices', [0, True, 2]),
                           ('headBoundaryVertices', [0, -1, 2]),
                           ('protectedHeadVertices', [0]),
                           ('protectedBodyVertices', [])]:
            p = profile()
            p[key] = value
            with self.assertRaises(ValueError):
                validate(p)

    def test_incompatible_bounds_and_unbounded_parameters(self):
        for mutate in [lambda p: p.update(cutZ=9),
                       lambda p: p['blendBounds']['min'].__setitem__(0, 9),
                       lambda p: p['fade'].update(lower=100),
                       lambda p: p.update(iterations=33),
                       lambda p: p.update(factor=float('nan')),
                       lambda p: p.update(mergeTolerance=.1),
                       lambda p: p.update(maxBlendDisplacement=0),
                       lambda p: p['shadeFade'].update(minZ=9),
                       lambda p: p['view'].update(scale=10001)]:
            p = profile()
            mutate(p)
            with self.assertRaises(ValueError):
                validate(p)

    def test_duplicate_keys_and_nonfinite_json(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'profile.json'
            for text in ['{"x":1,"x":2}', '{"x":NaN}', '{"x":Infinity}']:
                path.write_text(text)
                with self.assertRaises(ValueError):
                    load(path)

    def test_hash_mismatch_starts_no_blender_and_publishes_nothing(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            scene, blender, policy = [root / p for p in ['input.blend', 'blender.exe', 'profile.json']]
            scene.write_bytes(b'synthetic input')
            blender.write_bytes(b'synthetic executable')
            policy.write_text(json.dumps(profile()))
            args = type('Args', (), dict(scene=scene, blender=blender, profile=policy,
                                        output=root / 'result'))()
            with patch('neck.subprocess.run') as run:
                with self.assertRaisesRegex(ValueError, 'NECK_INPUT_HASH'):
                    neck.run(args)
                run.assert_not_called()
            self.assertFalse(args.output.exists())

    def test_schema_is_closed_and_covers_every_runtime_field(self):
        schema = json.loads((Path(__file__).resolve().parents[1]
                             / 'schemas/v1/neck-profile.schema.json').read_text())
        self.assertEqual(set(schema['properties']), set(profile()))
        self.assertEqual(set(schema['required']), set(profile()))

        def closed(s):
            if s.get('type') == 'object':
                self.assertIs(s['additionalProperties'], False)
                self.assertEqual(set(s['properties']), set(s['required']))
                for value in s['properties'].values():
                    closed(value)
        closed(schema)


if __name__ == '__main__':
    unittest.main()
