import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from measure_contract import load, validate
from measure_math import compare, geometry_hash, section, weighted, world
import measure


def profile():
    return json.loads((Path(__file__).parent / 'fixtures/measure-profile.json').read_text())


def box(offset=0):
    p = [[x+offset, y, z] for z in [0, 4] for y in [-2, 2] for x in [-1, 1]]
    faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3],
             [0, 4, 6, 2], [1, 3, 7, 5]]
    return [(i, [p[j] for j in t]) for i, f in enumerate(faces)
            for t in [[f[0], f[1], f[2]], [f[0], f[2], f[3]]]]


class MeasurementTests(unittest.TestCase):
    def test_closed_profile_and_nested_failures(self):
        validate(profile())
        changes = [lambda p: p.update(extra=0), lambda p: p.update(version=True),
                   lambda p: p['inputs'][0].pop('id'),
                   lambda p: p['inputs'][0]['rig'].update(extra=True),
                   lambda p: p['inputs'][0]['landmarks']['jaw'].update(extra=True),
                   lambda p: p['views'][0].update(id='../escape'),
                   lambda p: p['inputs'][0].update(id='source/escape')]
        for change in changes:
            p = profile()
            change(p)
            with self.assertRaises(ValueError):
                validate(p)

    def test_units_pose_and_missing_reference_are_not_coerced(self):
        for change in [lambda p: p['inputs'][1].update(units='cm'),
                       lambda p: p['frame'].update(pose='current_pose'),
                       lambda p: p['inputs'][1].update(pose='evaluated_pose'),
                       lambda p: p['inputs'][0]['landmarks'].pop('jaw'),
                       lambda p: p['measurements'][0].update(landmarks=['jaw']),
                       lambda p: p['inputs'][0]['regions']['torso'].update(origin='source_skeleton')]:
            p = profile()
            change(p)
            with self.assertRaises(ValueError):
                validate(p)

    def test_finite_ids_weights_and_section_axes(self):
        for change in [lambda p: p['inputs'][0]['landmarks']['jaw'].update(ids=[1, 1], weights=[.5, .5]),
                       lambda p: p['inputs'][0]['landmarks']['jaw'].update(ids=[True]),
                       lambda p: p['inputs'][0]['landmarks']['jaw'].update(weights=[.5]),
                       lambda p: p['views'][0].update(scale=float('nan')),
                       lambda p: p['sections'][0].update(axes=[[1, 0, 0], [1, 0, 0]]),
                       lambda p: p['sections'][0].update(expectedComponents=True),
                       lambda p: p['frame'].update(axes=[[1, 0, 0], [0, 1, 0], [0, 0, -1]])]:
            p = profile()
            change(p)
            with self.assertRaises(ValueError):
                validate(p)

    def test_duplicate_json_keys_and_nonfinite_constants(self):
        with tempfile.TemporaryDirectory() as root:
            p = Path(root) / 'bad.json'
            for text in ['{"x":0,"x":1}', '{"x":NaN}', '{"x":Infinity}']:
                p.write_text(text)
                with self.assertRaises(ValueError):
                    load(p)

    def test_world_frame_and_weighted_landmark(self):
        matrix = [[2, 0, 0, 10], [0, 3, 0, 20], [0, 0, 4, 30], [0, 0, 0, 1]]
        self.assertEqual(world([1, 2, 3], matrix), [12, 26, 42])
        rotated = [[0, -2, 0, 10], [3, 0, 0, 20], [0, 0, 4, 30], [0, 0, 0, 1]]
        self.assertEqual(world([1, 2, 3], rotated), [6, 23, 42])
        self.assertEqual(weighted([[0, 0, 0], [4, 2, 6]], [0, 1], [.25, .75]), [3, 1.5, 4.5])
        with self.assertRaisesRegex(ValueError, 'MEASURE_VERTEX_ID'):
            weighted([[0, 0, 0]], [1], [1])
        a = geometry_hash([[1, 2, 3]], [], matrix)
        matrix[0][0] = 3
        self.assertNotEqual(a, geometry_hash([[1, 2, 3]], [], matrix))

    def test_nonuniform_section_has_actual_closed_contour_and_face_ids(self):
        s = section(box(), [0, 0, 1], [0, 0, 1], [[1, 0, 0], [0, 1, 0]], 1)
        self.assertEqual(s['spans'], [2, 4])
        self.assertEqual(len(s['components']), 1)
        self.assertTrue(s['components'][0]['closed'])
        self.assertTrue(all(e['faceIds'] for e in s['segments']))

    def test_multiple_loops_are_retained_without_largest_loop_guess(self):
        s = section(box()+box(10), [0, 0, 1], [0, 0, 1], [[1, 0, 0], [0, 1, 0]], 1)
        self.assertEqual(len(s['components']), 2)
        self.assertIsNone(s['spans'])
        self.assertEqual(s['reason'], 'unexpected component count')

    def test_open_contour_span_and_coplanar_ambiguity(self):
        tris = [(0, [[0, 0, 0], [2, 0, 0], [0, 0, 2]])]
        s = section(tris, [0, 0, 1], [0, 0, 1], [[1, 0, 0], [0, 1, 0]], 1)
        self.assertEqual(s['spans'], [1, 0])
        self.assertFalse(s['components'][0]['closed'])
        self.assertNotIn('area', s)
        s = section(box(), [0, 0, 0], [0, 0, 1], [[1, 0, 0], [0, 1, 0]], 1)
        self.assertIsNone(s['spans'])
        self.assertEqual(s['reason'], 'coplanar triangles')

    def test_near_cell_boundaries_do_not_split_geometrically_shared_points(self):
        s = section(box(.00000999), [0, 0, 1.234567], [0, 0, 1], [[1, 0, 0], [0, 1, 0]], 1)
        self.assertEqual(len(s['components']), 1)
        self.assertTrue(s['components'][0]['closed'])

    def test_branched_contour_cannot_supply_a_span(self):
        triangles = [(i, [[0, 0, 0], endpoint, [0, 0, 1]])
                     for i, endpoint in enumerate([[1, 0, 0], [-1, 0, 0], [0, 1, 0]])]
        s = section(triangles, [0, 0, 0], [0, 0, 1], [[1, 0, 0], [0, 1, 0]], 1)
        self.assertEqual(s['reason'], 'branched contour')
        self.assertIsNone(s['spans'])

    def test_changed_surface_and_zero_denominator_are_honest(self):
        rows = [{'value': 0, 'origins': ['authored_anatomy']},
                {'value': 1, 'origins': ['authored_anatomy']}]
        compared = compare(rows, 'preserve', .001)
        self.assertEqual(compared[1]['review'], 'review')
        self.assertIsNone(compared[1]['percentDelta'])
        compared = compare([dict(rows[0], origins=['clothing_estimate']), rows[1]], 'preserve', .001)
        self.assertEqual(compared[1]['review'], 'estimate')
        compared = compare([dict(rows[0], value=None), rows[1]], 'preserve', .001)
        self.assertEqual(compared[1]['review'], 'unavailable')
        self.assertIsNone(compared[1]['absoluteDelta'])

    def test_stale_input_never_launches_blender_or_creates_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            scene, executable, policy = root/'fixture.blend', root/'blender', root/'profile.json'
            scene.write_bytes(b'synthetic fixture')
            executable.write_bytes(b'synthetic executable')
            policy.write_text(json.dumps(profile()))
            args = type('Args', (), dict(profile=policy, blender=executable, output=root/'result'))()
            with patch('measure.subprocess.run') as run:
                with self.assertRaisesRegex(ValueError, 'MEASURE_SCENE_HASH'):
                    measure.run(args)
                run.assert_not_called()
            self.assertFalse(args.output.exists())

    def test_schema_properties_and_closed_definitions_match_runtime(self):
        schema = json.loads((Path(__file__).resolve().parents[1]
                             / 'schemas/v1/measure-profile.schema.json').read_text())
        self.assertEqual(set(schema['properties']), set(profile()))
        for definition in [schema] + list(schema['definitions'].values()):
            if 'properties' in definition:
                self.assertIs(definition['additionalProperties'], False)
                self.assertEqual(set(definition['properties']), set(definition['required']))


if __name__ == '__main__':
    unittest.main()
