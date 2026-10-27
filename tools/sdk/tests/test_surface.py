import copy
import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from common import load, validate
from surface import derive_surface, compare_surface
from surface_geometry import intersects, cross_sections
from source_facts import restore_bind, restore_masks
import types
import json


def source():
    return {'positions':[[0,0,0],[2,0,0],[0,2,0],[5,0,0],[7,0,0],[5,2,0]],
            'weights':[{'root':1},{'arm':1},{'root':.5,'arm':.5}]+[{'root':1}]*3,
            'normals':[[0,0,1]]*6,'uv':[[0,0],[1,0],[0,1]]*2,'corners':list(range(6)),
            'triangles':[{'loops':[0,1,2],'material':0},{'loops':[3,4,5],'material':0}],
            'materials':['body'],'matrix':[[int(i==j) for i in range(4)] for j in range(4)]}


def policy():
    p = load(Path(__file__).parent/'fixtures/intent-v4.json')['surface']
    for face in p['faces']: face['vertices'] = [6 if v == 3 else v for v in face['vertices']]
    p['sections'] = [{'name':'middle','normal':[1,0,0],'offset':.5}]
    return p


BONES = [{'name':'root'},{'name':'arm'}]
MATERIALS = {'body':'models/body'}


class SurfaceTests(unittest.TestCase):
    def actual(self, original=None, p=None):
        original = original or source(); p = p or policy()
        result, provenance = derive_surface(original,p,BONES)
        result['materials'] = ['models/body']
        return result,provenance

    def test_free_authored_positions_and_source_attributes(self):
        original,p = source(),policy()
        actual,_ = self.actual(original,p)
        # Not a radial field: a free asymmetric displacement of the added point.
        actual['positions'][6][2] = .4
        report = compare_surface(original,actual,p,BONES,MATERIALS)
        self.assertEqual(1,report['movedVertices'])
        self.assertEqual({'root':.5,'arm':.5},actual['weights'][6])
        self.assertEqual([683/2048,683/2048],actual['uv'][2])
        self.assertTrue(report['crossSections'][0]['authored'])
        self.assertFalse(report['runtimeTested'])
        self.assertGreater(report['intersections']['checkedPairs'],0)

    def test_arbitrary_presculpt_refinement_not_only_centroid_fans(self):
        p=policy();p['addedVertices'][0]['barycentric']=[.2,.3,.5]
        actual,_=self.actual(p=p)
        actual['positions'][6][2]=.3
        compare_surface(source(),actual,p,BONES,MATERIALS)
        self.assertAlmostEqual(.55,actual['weights'][6]['arm'])

    def test_control_and_closed_v4_schema(self):
        fixture=load(Path(__file__).parent/'fixtures/intent-v4.json')
        validate(fixture,'intent-v4')
        bad=copy.deepcopy(fixture);bad['bindTransport']='automatic'
        with self.assertRaisesRegex(ValueError,'CONTRACT_ENUM'):validate(bad,'intent-v4')
        bad=copy.deepcopy(fixture);del bad['bindTransport']
        with self.assertRaisesRegex(ValueError,'CONTRACT_REQUIRED'):validate(bad,'intent-v4')
        p=policy();p.update(geometryMode='unchanged',sourceTriangles=[],editableVertices=[],addedVertices=[],faces=[])
        actual,_=self.actual(p=p)
        self.assertEqual(0,compare_surface(source(),actual,p,BONES,MATERIALS)['movedVertices'])
        for key in ('unknown','version','bound'):
            bad=copy.deepcopy(fixture)
            if key=='unknown':bad['surface']['unknown']=1
            if key=='version':bad['version']=3
            if key=='bound':bad['surface']['maxBoundaryAngleChange']=90
            with self.subTest(key=key),self.assertRaises(ValueError):validate(bad,'intent-v4')

    def test_corrupt_return_rejections(self):
        for key in ('positions','uv','weights','triangles','normals','nonfinite','displacement'):
            actual,_=self.actual()
            if key=='positions':actual['positions'][3][2]=.1
            elif key=='uv':actual['uv'][2][0]+=.01
            elif key=='weights':actual['weights'][6]={'root':1}
            elif key=='triangles':actual['triangles'][0]['loops'].reverse()
            elif key=='normals':actual['normals'][-1]=[0,1,0]
            elif key=='nonfinite':actual['positions'][6][0]=math.inf
            else:actual['positions'][6][2]=3
            with self.subTest(key=key),self.assertRaises(ValueError):
                compare_surface(source(),actual,policy(),BONES,MATERIALS)

    def test_pre_sculpt_provenance_and_coverage_rejections(self):
        for name in ('bary','missing','duplicate','winding','source','edge'):
            p=policy()
            if name=='bary':p['addedVertices'][0]['barycentric']=[.3,.3,.3]
            elif name=='missing':p['faces'].pop()
            elif name=='duplicate':p['faces'].append(p['faces'][0])
            elif name=='winding':p['faces'][0]['vertices'].reverse()
            elif name=='source':p['addedVertices'][0]['sourceTriangle']=1
            else:p['addedVertices'][0]['barycentric']=[.5,.5,0]
            with self.subTest(name=name),self.assertRaises(ValueError):derive_surface(source(),p,BONES)

    def test_no_weight_trimming_and_no_procedural_bones(self):
        original=source();original['weights'][:3]=[{'a':1},{'b':.5,'c':.5},{'d':.5,'e':.5}]
        with self.assertRaisesRegex(ValueError,'WEIGHT_BUDGET'):
            derive_surface(original,policy(),[{'name':n} for n in 'abcde']+[{'name':'root'}])
        with self.assertRaisesRegex(ValueError,'PROCEDURAL'):
            derive_surface(source(),policy(),BONES+[{'name':'$cloth'}])

    def test_fixed_external_boundary_and_actual_tangent_guard(self):
        original=source();original['positions'][3]=[2,2,0]
        original['corners'][3:]=[1,3,2]
        p=policy();p['editableVertices']=[0]
        neutral,provenance=derive_surface(original,p,BONES)
        self.assertEqual([1,2],provenance['boundary'])
        actual=copy.deepcopy(neutral);actual['materials']=['models/body']
        actual['positions'][6][2]=1
        with self.assertRaisesRegex(ValueError,'BOUNDARY_TANGENT'):
            compare_surface(original,actual,p,BONES,MATERIALS)
        actual=copy.deepcopy(neutral);actual['materials']=['models/body'];actual['positions'][1][2]=.01
        with self.assertRaisesRegex(ValueError,'PROTECTED_POSITION'):
            compare_surface(original,actual,p,BONES,MATERIALS)

    def test_intersections_are_actual_geometry_and_allow_shared_edges(self):
        a=[[0,0,0],[2,0,0],[0,2,0]]
        self.assertFalse(intersects(a,[[2,0,0],[2,2,0],[0,2,0]]))
        self.assertTrue(intersects(a,[[.5,.5,-1],[.5,.5,1],[1,.5,0]]))
        self.assertTrue(intersects(a,[[.1,.1,0],[.3,.1,0],[.1,.3,0]]))
        self.assertFalse(intersects(a,[[4,0,0],[5,0,0],[4,1,0]]))
        original=source();actual,_=self.actual(original)
        actual['positions'][6]=[5.3,.3,0]
        p=policy();p['maxDisplacement']=10
        with self.assertRaises(ValueError):compare_surface(original,actual,p,BONES,MATERIALS)
        original=source()
        original['positions'][3:]=[[.5,.3,.15],[.5,1,.15],[.5,.3,.6]]
        actual,_=self.actual(original);actual['positions'][6][2]=.4
        with self.assertRaisesRegex(ValueError,'SURFACE_INTERSECTION'):
            compare_surface(original,actual,policy(),BONES,MATERIALS)

    def test_separate_uv_charts_remain_separate(self):
        original=source();original['positions'][3]=[2,2,0];original['corners'][3:]=[1,3,2]
        original['uv'][3:]=[[.5,.5],[1,1],[.5,1]]
        p=policy();p.update(sourceTriangles=[0,1],editableVertices=[0,1,2,3],
            addedVertices=[{'sourceTriangle':0,'barycentric':[1/3]*3},{'sourceTriangle':1,'barycentric':[1/3]*3}],
            faces=[{'sourceTriangle':0,'vertices':f} for f in [[0,1,6],[1,2,6],[2,0,6]]]+
                  [{'sourceTriangle':1,'vertices':f} for f in [[1,3,7],[3,2,7],[2,1,7]]])
        actual,_=self.actual(original,p)
        compare_surface(original,actual,p,BONES,MATERIALS)
        loops=[i for i,v in enumerate(actual['corners']) if v==1]
        self.assertIn([1,0],[actual['uv'][i] for i in loops])
        self.assertIn([.5,.5],[actual['uv'][i] for i in loops])

    def test_exact_source_bind_transport_preserves_other_dmx_attributes(self):
        class Element(dict):
            def __init__(self,name,kind,uid,**attributes):
                super().__init__(attributes);self.name=name;self.type=kind;self.id=uid
        def fixture():
            transforms=[Element('root','DmeTransform',str(i),position=[.0001,0,0],orientation=[0,0,0,1]) for i in range(2)]
            joint=Element('root','DmeJoint','j',transform=transforms[0],children=[])
            mesh=Element('vertices','DmeVertexData','v',positions=[[1,2,3]],uv=[[.5,.5]])
            return types.SimpleNamespace(elements=[joint,mesh]+transforms)
        text='m_boneName=["root"] m_nParent=[-1] m_bonePosParent=[[0,0,0]] m_boneRotParent=[[0,0,0,1]]'
        dm=fixture();report=restore_bind(dm,text,types.SimpleNamespace(Vector3=list,Quaternion=list))
        self.assertTrue(report['nonBindAttributesExact'])
        self.assertEqual([0,0,0],dm.elements[-1]['position'])
        self.assertEqual([[1,2,3]],dm.elements[1]['positions'])
        dm=fixture();dm.elements[-1]['position']=[1,0,0]
        with self.assertRaisesRegex(ValueError,'BASIS_MISMATCH'):
            restore_bind(dm,text,types.SimpleNamespace(Vector3=list,Quaternion=list))
        dm=fixture();dm.elements[-1]['scale']=2
        with self.assertRaises(ValueError):restore_bind(dm,text,types.SimpleNamespace(Vector3=list,Quaternion=list))
        dm=fixture()
        scaled=text.replace('[0,0,0,1]','[0,0,0,0.9999]')
        restore_bind(dm,scaled,types.SimpleNamespace(Vector3=list,Quaternion=list))
        self.assertEqual([0,0,0,1],dm.elements[-1]['orientation'])

    def test_duplicate_vertex_uv_seams_cannot_open_or_escape_external_boundary(self):
        original=source()
        original['positions'][3:]=[[2,0,0],[2,2,0],[0,2,0]]
        original['weights'][3:]=[{'arm':1},{'root':1},{'root':.5,'arm':.5}]
        p=policy();p.update(sourceTriangles=[0,1],editableVertices=list(range(6)),addedVertices=[],
                           faces=[{'sourceTriangle':i,'vertices':list(range(3*i,3*i+3))} for i in range(2)])
        actual,provenance=self.actual(original,p)
        self.assertEqual([(1,3),(2,5)],provenance['seamPairs'])
        actual['positions'][1][2]=.1
        with self.assertRaisesRegex(ValueError,'SEAM_OPENED'):compare_surface(original,actual,p,BONES,MATERIALS)
        actual['positions'][3][2]=.1
        compare_surface(original,actual,p,BONES,MATERIALS)
        p.update(sourceTriangles=[0],faces=[p['faces'][0]],editableVertices=[1])
        with self.assertRaisesRegex(ValueError,'FIXED_BOUNDARY'):derive_surface(original,p,BONES)

    def test_source_mask_morph_restoration_rejects_changed_bone_weights(self):
        mask=lambda name:dict(m_sName=name,m_nLocalBoneArray=[0],m_flBoneWeightArray=[1],
                             m_flDefaultMorphCtrlWeight=1,m_morphCtrlWeightArray=[])
        from kv import emit
        text=emit(dict(m_localBoneNameArray=['root'],m_localBoneMaskArray=[mask('default'),mask('upper')]))
        text=text.replace('"m_localBoneNameArray"','m_localBoneNameArray').replace('"m_localBoneMaskArray"','m_localBoneMaskArray')
        nodes=[{'_class':'WeightListList','children':[{'_class':'WeightList','name':'upper',
                'weights':[{'bone':'root','weight':1}]}]}]
        restore_masks(nodes,text)
        self.assertEqual(1,nodes[0]['children'][0]['master_morph_weight'])
        self.assertEqual([],nodes[0]['children'][0]['morph_weights'])
        nodes[0]['children'][0]['weights'][0]['weight']=.5
        with self.assertRaisesRegex(ValueError,'WEIGHTS_CHANGED'):restore_masks(nodes,text)


if __name__=='__main__': unittest.main()
