import copy
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import artifact_path, below, canonical, check_pins, fresh, load, pins, run, sha, validate, write
from kv import emit, field, metadata, parse
from verify import compare, rig, animation_masks, compare_animation_masks
from authoring import compare_authored
from skinned import derive, compare_skinned
from lifecycle import install, rollback
from blender_job import verify_geometry
from workflow import workspace


def data(parent=-1, position=0, camera=1):
    return ('m_keyValueText="""{camera={value=' + str(camera) + '}}"""\n'
            'm_boneName=["root"] m_nParent=[' + str(parent) + '] '
            'm_bonePosParent=[[' + str(position) + ',0,0]] m_boneRotParent=[[0,0,0,1]]')


def mesh():
    return {'positions': [[0, 0, 0], [1, 0, 0], [0, 1, 0]], 'weights': [{'root': 1}]*3,
            'normals': [[0, 0, 1]]*3, 'uv': [[0, 0], [1, 0], [0, 1]], 'corners': [0, 1, 2],
            'triangles': [{'loops': [0, 1, 2], 'material': 0}], 'materials': ['body'],
            'matrix': [[1 if i == j else 0 for j in range(4)] for i in range(4)]}


class Tree:
    def __init__(self, size):
        self.points = []

    def insert(self, point, index):
        self.points.append((point, index))

    def balance(self):
        pass

    def find_range(self, point, distance):
        import math
        return [(p, i, math.dist(point, p)) for p, i in self.points if math.dist(point, p) <= distance]


sys.modules['mathutils.kdtree'] = types.SimpleNamespace(KDTree=Tree)


class Tests(unittest.TestCase):
    def test_skinned_source_interpolation_and_protected_edges(self):
        original = mesh()
        original['weights'] = [{'root': 1}, {'arm': 1}, {'root': 0.5, 'arm': 0.5}]
        bones = [{'name': 'root'}, {'name': 'arm'}]
        policy = {'geometryMode': 'triangle_fan', 'sourceTriangles': [0],
                  'center': [0, 0, 0], 'axis': [0, 0, 1], 'radius': 2, 'scale': 1.25}
        target, mutable = derive(original, policy, bones)
        self.assertEqual({'root': 0.5, 'arm': 0.5}, target['weights'][3])
        self.assertEqual([[0, 1, 3], [1, 2, 3], [2, 0, 3]],
                         [[target['corners'][i] for i in t['loops']] for t in target['triangles']])
        self.assertEqual(original['weights'], target['weights'][:3])
        self.assertEqual([683/2048, 683/2048], target['uv'][2])
        self.assertEqual(set(range(9)), mutable)
        target['materials'] = ['models/body']
        compare_skinned(original, target, policy, bones, {'body': 'models/body'})
        for key in ('positions', 'weights', 'uv', 'triangles', 'normals'):
            bad = copy.deepcopy(target)
            if key == 'weights': bad[key][3] = {'root': 1}
            elif key == 'triangles': bad[key][0]['loops'].reverse()
            else: bad[key][0][0] += 0.1
            with self.subTest(key=key), self.assertRaises(ValueError):
                compare_skinned(original, bad, policy, bones, {'body': 'models/body'})
        unchanged = dict(policy, geometryMode='unchanged', sourceTriangles=[], scale=1)
        control, mutable = derive(original, unchanged, bones)
        self.assertEqual(original, control)
        self.assertFalse(mutable)
        control['materials'] = ['models/body']
        compare_skinned(original, control, unchanged, bones, {'body': 'models/body'})

    def test_skinned_support_and_closed_contract_reject_corruption(self):
        original = mesh()
        original['weights'][1] = {'arm': 1}
        bones = [{'name': 'root'}, {'name': 'arm'}]
        policy = {'geometryMode': 'triangle_fan', 'sourceTriangles': [0],
                  'center': [0, 0, 0], 'axis': [0, 0, 1], 'radius': 2, 'scale': 1.25}
        for change in ({'axis': [0, 0, 0]}, {'radius': 0.5}, {'sourceTriangles': [0, 0]},
                       {'sourceTriangles': [1]}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                derive(original, dict(policy, **change), bones)
        # A disconnected, protected source triangle must retain its attributes exactly.
        protected = copy.deepcopy(original)
        protected['positions'] += [[10, 0, 0], [11, 0, 0], [10, 1, 0]]
        protected['weights'] += [{'root': 1} for _ in range(3)]
        protected['corners'] += [3, 4, 5]
        protected['uv'] += [[0, 0], [1, 0], [0, 1]]
        protected['normals'] += [[0, 0, 1] for _ in range(3)]
        protected['triangles'] += [{'loops': [3, 4, 5], 'material': 0}]
        target, _ = derive(protected, policy, bones)
        self.assertEqual(protected['positions'][3:], target['positions'][3:6])
        target['materials'] = ['models/body']
        target['normals'][9] = [0, 1, 0]
        with self.assertRaisesRegex(ValueError, 'PROTECTED_NORMALS_CHANGED'):
            compare_skinned(protected, target, policy, bones, {'body': 'models/body'})
        fixture = load(Path(__file__).parent / 'fixtures/intent-v3.json')
        validate(fixture, 'intent-v3')
        for change in ('scale', 'extra', 'triangles'):
            bad = copy.deepcopy(fixture)
            if change == 'scale': bad['patch']['scale'] = 2
            if change == 'extra': bad['patch']['unknown'] = True
            if change == 'triangles': bad['patch']['sourceTriangles'] = [0, 0]
            with self.subTest(change=change), self.assertRaises(ValueError): validate(bad, 'intent-v3')

    def test_skinned_preserves_shared_edge_uv_seams_and_rejects_weight_trimming(self):
        original = mesh()
        original['positions'].append([1, 1, 0])
        original['weights'] = [{'root': 1}, {'arm': 1}, {'root': 0.5, 'arm': 0.5}, {'root': 1}]
        original['corners'] += [1, 3, 2]
        original['uv'] += [[0, 0], [1, 0], [1, 1]]
        original['normals'] += [[0, 0, 1] for _ in range(3)]
        original['triangles'] += [{'loops': [3, 4, 5], 'material': 0}]
        policy = {'geometryMode': 'triangle_fan', 'sourceTriangles': [0, 1],
                  'center': [0, 0, 0], 'axis': [0, 0, 1], 'radius': 3, 'scale': 1}
        target, _ = derive(original, policy, [{'name': 'root'}, {'name': 'arm'}])
        self.assertEqual(original['positions'], target['positions'][:4])
        self.assertEqual([683/2048, 683/2048], target['uv'][2])
        self.assertEqual([1365/2048, 683/2048], target['uv'][11])
        self.assertEqual([1, 2], [target['corners'][i] for i in [3, 4]])
        self.assertEqual([2, 1], [target['corners'][i] for i in [15, 16]])
        overflow = mesh()
        overflow['weights'] = [{'a': 0.5, 'b': 0.5}, {'c': 0.5, 'd': 0.5}, {'e': 1}]
        with self.assertRaisesRegex(ValueError, 'SKINNED_INTERPOLATED_WEIGHT_BUDGET'):
            derive(overflow, dict(policy, sourceTriangles=[0]), [{'name': c} for c in 'abcde'])

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[3] / '.workspaces')
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_closed_nested_schema(self):
        intent = {'operation': 'sdk_edit@1', 'version': 1, 'manifestHash': 'a'*64,
                  'profile': 'lod0_diagnostic', 'selectedMeshes': ['body_LOD0'], 'excludedMeshes': [],
                  'scales': [{'mesh': 'body_LOD0', 'axes': [1, 1.35, 1]}],
                  'acknowledge': ['single_lod', 'sdk_regenerated_blocks', 'native_frames_unverified',
                                  'source_export_fidelity_unverified']}
        validate(intent, 'intent')
        for change in ('unknown', 'bool', 'negative', 'ack', 'hash'):
            wrong = copy.deepcopy(intent)
            if change == 'unknown': wrong['scales'][0]['surprise'] = True
            if change == 'bool': wrong['scales'][0]['axes'][0] = True
            if change == 'negative': wrong['scales'][0]['axes'][0] = -1
            if change == 'ack': wrong['acknowledge'] = ['single_lod']
            if change == 'hash': wrong['manifestHash'] = 'not a hash'
            with self.subTest(change=change), self.assertRaises(ValueError):
                validate(wrong, 'intent')

    def test_duplicate_json_and_nonfinite_reject(self):
        p = self.root / 'input.json'
        for text in ('{"a":1,"a":2}', '{"a":NaN}'):
            p.write_text(text)
            with self.assertRaises(ValueError): load(p)

    def test_paths_and_exclusive_publication(self):
        for path in ('../x', '/x', 'a/../b', 'C:/x', 'a//b'):
            with self.subTest(path=path), self.assertRaises(ValueError): below(self.root, path)
        output = fresh(self.root / 'new')
        write(output / 'x.json', {'a': 1})
        with self.assertRaises(FileExistsError): write(output / 'x.json', {})
        with self.assertRaises(ValueError): fresh(output)

    def test_pin_drift(self):
        (self.root / 'x').write_bytes(b'input')
        expected = pins(self.root)
        (self.root / 'x').write_bytes(b'changed')
        with self.assertRaises(ValueError): check_pins(self.root, expected)

    def test_sdk_and_input_roots_cannot_receive_build_outputs(self):
        sdk = fresh(self.root / 'sdk')
        source = fresh(self.root / 'source')
        config = {'gameRoots': {'sdk': str(sdk)}, 'tools': {'compiler': str(sdk / 'compiler')}}
        for output in (sdk / 'generated', source / 'generated'):
            with self.subTest(output=output), self.assertRaises(ValueError): workspace(config, output, source)
        self.assertTrue(workspace(config, self.root / 'output', source).is_dir())

    def test_process_failure_and_timeout(self):
        with self.assertRaises(ValueError):
            run([sys.executable, '-c', 'raise SystemExit(4)'], self.root / 'bad.log')
        with self.assertRaises(TimeoutError):
            run([sys.executable, '-c', 'import time; time.sleep(10)'], self.root / 'slow.log', timeout=0.2)

    def test_kv_roundtrip_and_duplicate_reject(self):
        value = {'node': {'resource': {'$type': 'resource', '$value': 'models/a.vmdl'}},
                 'array': [1, -2.5, True, 'brace}'], 'camera': 'quoted " text'}
        self.assertEqual(value, parse(emit(value)))
        self.assertEqual({'url': 'https://example.test/path'}, parse('{url="https://example.test/path" // comment\n}'))
        with self.assertRaises(ValueError): parse('{x=1 x=2}')
        self.assertEqual({'camera': {'value': 1}}, metadata(data()))
        self.assertEqual(['root'], field(data(), 'm_boneName'))

    def test_metadata_and_bone_corruption(self):
        self.assertEqual((0, 0), compare(data(), data()))
        for actual in (data(camera=2), data(parent=0), data(parent=4), data(position=0.01),
                       data().replace('"root"', '"renamed"'), data().replace('value=1', 'value=true')):
            with self.assertRaises(ValueError): compare(data(), actual)

    def test_animation_masks_use_bone_names_and_reject_corruption(self):
        self.assertEqual('models/pose_ASTRO.dmx', artifact_path('models/pose_ASTRO.dmx'))
        for path in ('../pose.dmx', 'C:/pose.dmx', '/pose.dmx', 'models//pose.dmx'):
            with self.assertRaises(ValueError): artifact_path(path)
        value = {'m_localBoneNameArray': ['root', 'arm'], 'm_localBoneMaskArray': [
            {'m_sName': 'upper', 'm_nLocalBoneArray': [0, 1], 'm_flBoneWeightArray': [0.1, 1.0],
             'm_flDefaultMorphCtrlWeight': 1.0, 'm_morphCtrlWeightArray': []}]}
        reordered = copy.deepcopy(value)
        reordered['m_localBoneNameArray'].reverse()
        reordered['m_localBoneMaskArray'][0]['m_nLocalBoneArray'] = [1, 0]
        def dump(v): return '\n'.join(k + '=' + emit(x) for k, x in v.items())
        compare_animation_masks(dump(value), dump(reordered))
        two = copy.deepcopy(value)
        two['m_localBoneMaskArray'].append(copy.deepcopy(two['m_localBoneMaskArray'][0]))
        two['m_localBoneMaskArray'][1]['m_sName'] = 'other'
        swapped = copy.deepcopy(two)
        swapped['m_localBoneMaskArray'].reverse()
        with self.assertRaises(ValueError): compare_animation_masks(dump(two), dump(swapped))
        for key, replacement in [('m_flBoneWeightArray', [0.2, 1.0]),
                                 ('m_nLocalBoneArray', [0, 0]), ('m_sName', 'other'),
                                 ('m_flDefaultMorphCtrlWeight', 0.0), ('extra', True)]:
            bad = copy.deepcopy(value)
            bad['m_localBoneMaskArray'][0][key] = replacement
            with self.subTest(key=key), self.assertRaises(ValueError):
                compare_animation_masks(dump(value), dump(bad))

    def test_authored_change_contract(self):
        before = {'bones': ['root'], 'meshes': {'body_LOD0': mesh()}}
        authored = copy.deepcopy(before)
        authored['meshes']['body_LOD0']['positions'][2][1] = 1.35
        authored['meshes']['body_LOD0']['materials'] = ['models/body']
        intent = {'selectedMeshes': ['body_LOD0'], 'scales': [{'mesh': 'body_LOD0', 'axes': [1, 1.35, 1]}]}
        self.assertEqual(0, compare_authored(before, authored, intent, {'body': 'models/body'}))
        for key in ('uv', 'weights', 'triangles', 'positions'):
            bad = copy.deepcopy(authored)
            bad['meshes']['body_LOD0'][key] = []
            with self.subTest(key=key), self.assertRaises(ValueError):
                compare_authored(before, bad, intent, {'body': 'models/body'})

    def test_closed_rigid_replacement_and_protected_context(self):
        before = {'bones': [{'name': 'root'}], 'meshes': {'body_LOD0': mesh(), 'context_LOD0': mesh()}}
        intent = load(Path(__file__).parent / 'fixtures/intent-v2.json')
        validate(intent, 'intent-v2')
        intent['selectedMeshes'] = ['body_LOD0', 'context_LOD0']
        authored = copy.deepcopy(before)
        for m in authored['meshes'].values(): m['materials'] = ['models/body']
        target = authored['meshes']['body_LOD0']
        target['positions'] = [[0,0,0],[1,0,0],[0,1,0],[0,0,1]]
        target['weights'] = [{'root':1}]*4
        target['corners'] = [0,2,1, 0,1,3, 0,3,2, 1,2,3]
        target['uv'] = [[0,0]]*12
        target['normals'] = [[0,0,1]]*12
        target['triangles'] = [{'loops':[i,i+1,i+2],'material':0} for i in range(0,12,3)]
        self.assertEqual(0, compare_authored(before, authored, intent, {'body':'models/body'}))
        for mutation in ('position','weight','uv','normal','open','duplicate','index','context','bone'):
            bad = copy.deepcopy(authored)
            t = bad['meshes']['body_LOD0']
            if mutation == 'position': t['positions'][0][0] = 3
            elif mutation == 'weight': t['weights'][0] = {'missing':1}
            elif mutation == 'uv': t['uv'][0][0] = 2
            elif mutation == 'normal': t['normals'][0] = [0,0,0]
            elif mutation == 'open': t['triangles'].pop()
            elif mutation == 'duplicate': t['triangles'].append(t['triangles'][0])
            elif mutation == 'index': t['corners'][0] = 99
            elif mutation == 'context': bad['meshes']['context_LOD0']['normals'][0] = [1,0,0]
            else: bad['bones'].append({'name':'new'})
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                compare_authored(before, bad, intent, {'body':'models/body'})
        unchanged = copy.deepcopy(before)
        for m in unchanged['meshes'].values(): m['materials'] = ['models/body']
        intent['replacement']['geometryMode'] = 'unchanged'
        self.assertEqual(0, compare_authored(before, unchanged, intent, {'body':'models/body'}))

    def test_compiled_corner_corruption(self):
        expected = {'meshes': {'input': mesh()}}
        actual = {'meshes': {'reordered_output': mesh()}}
        self.assertEqual(1, verify_geometry(expected, actual)['triangles'])
        for key in ('uv', 'weights', 'positions'):
            bad = copy.deepcopy(actual)
            if key == 'weights': bad['meshes']['reordered_output'][key][0] = {'other': 1}
            else: bad['meshes']['reordered_output'][key][0][0] = 0.02
            with self.subTest(key=key), self.assertRaises(ValueError): verify_geometry(expected, bad)
        bad = copy.deepcopy(actual)
        bad['meshes']['reordered_output']['triangles'][0]['loops'] = [2, 1, 0]
        with self.assertRaises(ValueError): verify_geometry(expected, bad)

    def test_duplicate_face_multiplicity(self):
        expected = {'meshes': {'input': mesh()}}
        expected['meshes']['input']['triangles'] *= 2
        self.assertEqual(2, verify_geometry(expected, expected)['triangles'])
        actual = {'meshes': {'output': mesh()}}
        with self.assertRaises(ValueError): verify_geometry(expected, actual)

    def test_tight_guards_distinguish_nearby_vertices_and_uv_seams(self):
        for mode in ('position', 'uv'):
            expected = {'meshes': {'first': mesh(), 'second': mesh()}}
            second = expected['meshes']['second']
            if mode == 'position':
                for point in second['positions']: point[2] += 0.00065
            else:
                for uv in second['uv']: uv[0] += 0.00048828125
            with self.subTest(mode=mode):
                self.assertEqual(2, verify_geometry(expected, expected)['triangles'])

    def test_receipt_lifecycle_and_failed_preflight(self):
        package, addons, receipts = [fresh(self.root / name) for name in ('package', 'addons', 'receipts')]
        (package / 'diagnostic_dir.vpk').write_bytes(b'fixture package')
        write(package / 'package.json', {'packageHash': sha(package / 'diagnostic_dir.vpk'), 'files': pins(package)})
        def reject(*args): raise ValueError('SOURCE_CHANGED')
        with self.assertRaises(ValueError): install(package, addons, 'pak98_dir.vpk', receipts, reject)
        self.assertFalse((addons / 'pak98_dir.vpk').exists())
        calls = []
        def reject_after_copy(*args):
            calls.append(True)
            if len(calls) == 2: raise ValueError('SOURCE_CHANGED_AFTER_COPY')
        with self.assertRaises(ValueError): install(package, addons, 'pak98_dir.vpk', receipts, reject_after_copy)
        self.assertFalse((addons / 'pak98_dir.vpk').exists())
        self.assertEqual([], list(addons.glob('*.staging')))
        installed = install(package, addons, 'pak98_dir.vpk', receipts, lambda *args: None)
        with self.assertRaises(ValueError): install(package, addons, 'pak98_dir.vpk', receipts, lambda *args: None)
        target = addons / 'pak98_dir.vpk'
        target.write_bytes(b'foreign change')
        with self.assertRaises(ValueError): rollback(installed['receipt'], addons)
        target.write_bytes(b'fixture package')
        rolled = rollback(installed['receipt'], addons)
        self.assertTrue(Path(rolled['disabled']).exists())
        self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main()
