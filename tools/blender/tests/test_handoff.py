import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import handoff as h

TEST_ROOT = Path(__file__).resolve().parents[3]/'.workspaces/blender-handoff-tests'
TEST_ROOT.mkdir(parents=True,exist_ok=True)


def fixture():
    # Fully synthetic two-LOD square plus a source-coincident collapsed face.
    members = []
    for lod in [0,1]:
        points = [[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[1.,1.,0.],[0.,1.,0.]]
        row = {"lod":lod,"mesh":lod,"buffer":0}
        members.append(row | {"memberId":h.member_id(row),"sourceDecodedHash":"b"*64,"editable":True,
                              "positions":[[h.word(v) for v in p] for p in points],
                              "triangles":[0,1,2,1,3,2,2,4,3],"protectedIds":[0]})
    context = copy.deepcopy(members[0]); context.update(buffer=1,editable=False,protectedIds=list(range(5)))
    context["memberId"] = h.member_id(context); members.append(context)
    bundle = {"schemaVersion":1,"kind":"geometry_authoring_handoff","inputHash":"a"*64,
              "snapshotSha256":"c"*64,"requiredLods":[0,1],"maximumDisplacement":2,
              "omittedAttributes":["rig","uv","weights","materials","packedFrames"],"members":members}
    bh = h.digest(h.canonical(bundle))
    delta = {"schemaVersion":1,"kind":"geometry_authoring_delta","operation":{"name":"author_positions","version":1},
             "inputHash":bundle["inputHash"],"bundleSha256":bh,"blenderVersion":"synthetic",
             "targetKey":"S2ModKit_Edit","members":[{"memberId":m["memberId"],"positions":copy.deepcopy(m["positions"])} for m in members if m["editable"]]}
    return bundle,bh,delta


class HandoffTests(unittest.TestCase):
    def reject(self,code,change):
        b,bh,d = fixture(); change(b,d)
        with self.assertRaisesRegex(h.HandoffError,code): h.verify(b,bh,d)

    def test_unchanged_and_explicit_edits(self):
        b,bh,d = fixture()
        self.assertEqual([0,0],[r['changedRecords'] for r in h.verify(b,bh,d)['members']])
        d['members'][0]['positions'][3][2] = h.word(.25)
        self.assertEqual(1,h.verify(b,bh,d)['members'][0]['changedRecords'])

    def test_missing_lod(self):
        self.reject('LOD_RETURN_INCOMPLETE',lambda b,d:d['members'].pop())

    def test_duplicate_member(self):
        self.reject('DELTA_MEMBER_INVALID',lambda b,d:d['members'].append(copy.deepcopy(d['members'][0])))

    def test_context_return_rejected(self):
        self.reject('DELTA_MEMBER_INVALID',lambda b,d:d['members'].append({'memberId':b['members'][-1]['memberId'],'positions':b['members'][-1]['positions']}))

    def test_protected_record_changed(self):
        self.reject('PROTECTED_POSITION_CHANGED',lambda b,d:d['members'][0]['positions'][0].__setitem__(0,h.word(.1)))

    def test_signed_zero_is_protected_word_change(self):
        self.reject('PROTECTED_POSITION_CHANGED',lambda b,d:d['members'][0]['positions'][0].__setitem__(0,h.word(-0.)))

    def test_coincident_record_split(self):
        self.reject('SOURCE_COINCIDENCE_SPLIT',lambda b,d:d['members'][0]['positions'][4].__setitem__(1,h.word(.8)))

    def test_orientation_flip(self):
        self.reject('TRIANGLE_HEMISPHERE_FAILED',lambda b,d:d['members'][0]['positions'][1].__setitem__(0,h.word(-1.)))

    def test_valid_source_face_collapsed(self):
        self.reject('TRIANGLE_HEMISPHERE_FAILED',lambda b,d:d['members'][0]['positions'][1].__setitem__(0,h.word(0.)))

    def test_displacement_limit_exact(self):
        def change(b,d):
            d['members'][0]['positions'][3][2] = h.word(2.000000238418579)
        self.reject('DISPLACEMENT_EXCEEDED',change)

    def test_nonfinite_float_word(self):
        self.reject('FLOAT32_NONFINITE',lambda b,d:d['members'][0]['positions'][3].__setitem__(2,'0000807f'))

    def test_bool_version(self):
        self.reject('CONTRACT_VERSION_INVALID',lambda b,d:d.__setitem__('schemaVersion',True))

    def test_unknown_property(self):
        self.reject('CONTRACT_PROPERTIES_INVALID',lambda b,d:d.__setitem__('typo',1))

    def test_input_hash(self):
        self.reject('DELTA_SOURCE_MISMATCH',lambda b,d:d.__setitem__('inputHash','d'*64))

    def test_source_collinear_face_is_fixed(self):
        b,bh,d = fixture()
        m = b['members'][0]
        m['positions'] = [[h.word(v) for v in p] for p in [[0,0,0],[1,0,0],[2,0,0]]]
        m['triangles'] = [0,1,2]
        d['members'][0]['positions'] = copy.deepcopy(m['positions'])
        h.verify(b,bh,d)
        d['members'][0]['positions'][1][1] = h.word(.1)
        with self.assertRaisesRegex(h.HandoffError,'SOURCE_COLLAPSE_CHANGED'): h.verify(b,bh,d)

    def test_subnormal_triangle_orientation(self):
        b,bh,d = fixture(); tiny = h.decode('01000000')
        m = b['members'][0]
        m['positions'] = [[h.word(v) for v in p] for p in [[0,0,0],[tiny,0,0],[0,tiny,0]]]
        m['triangles'] = [0,1,2]
        d['members'][0]['positions'] = copy.deepcopy(m['positions'])
        h.verify(b,bh,d)
        d['members'][0]['positions'][1][0] = h.word(-tiny)
        with self.assertRaisesRegex(h.HandoffError,'TRIANGLE_HEMISPHERE_FAILED'): h.verify(b,bh,d)

    def test_strict_json_duplicate_and_nonfinite(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as temp:
            path = Path(temp)/'bad.json'
            for text,code in [('{"a":1,"a":2}','DUPLICATE_JSON_PROPERTY'),('{"a":NaN}','NONFINITE_JSON_NUMBER')]:
                path.write_text(text)
                with self.assertRaisesRegex(h.HandoffError,code): h.load(path)

    def test_trusted_hash_and_no_overwrite(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as temp:
            b,bh,_ = fixture(); path = Path(temp)/'bundle.json'
            h.publish_bytes(path,h.canonical(b)); h.trusted_bundle(path,bh)
            with self.assertRaisesRegex(h.HandoffError,'OUTPUT_ALREADY_EXISTS'): h.publish_bytes(path,b'bad')
            with self.assertRaisesRegex(h.HandoffError,'BUNDLE_HASH_MISMATCH'): h.trusted_bundle(path,'f'*64)
            self.assertEqual(b,h.load(path))

    def test_prepare_requires_complete_lods(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as temp:
            b,_,_ = fixture()
            snap = {'inputHash':b['inputHash'],'rows':[{'lod':m['lod'],'mesh':m['mesh'],'buffer':m['buffer'],
                    'decodedHash':m['sourceDecodedHash'],'points':[[h.decode(v) for v in p] for p in m['positions']],
                    'calls':[{'triangles':m['triangles']}]} for m in b['members']]}
            policy = {'schemaVersion':1,'inputHash':b['inputHash'],'maximumDisplacement':2,
                      'members':[{'memberId':m['memberId'],'protectedIds':m['protectedIds']} for m in b['members'] if m['editable']]}
            sp,pp = Path(temp)/'source.json',Path(temp)/'policy.json'
            sp.write_bytes(h.canonical(snap)); pp.write_bytes(h.canonical(policy))
            self.assertEqual(3,len(h.prepare(sp,pp)['members']))
            policy['members'].pop(); pp.write_bytes(h.canonical(policy))
            with self.assertRaisesRegex(h.HandoffError,'LOD_SELECTION_INCOMPLETE'): h.prepare(sp,pp)


if __name__=='__main__': unittest.main()
