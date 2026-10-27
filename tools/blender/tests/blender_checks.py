"""Synthetic Blender integration, run explicitly with an absent workspace output root."""
import json
import copy
import hashlib
from pathlib import Path
import sys
sys.dont_write_bytecode=True
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
sys.path.insert(0,str(Path(__file__).resolve().parent))
import bpy
import handoff as h
import blender_bridge as bridge
from test_handoff import fixture

root=Path(sys.argv[sys.argv.index('--')+1])
h.require(not root.exists(),'OUTPUT_ALREADY_EXISTS');root.mkdir(parents=True)
b,bh,_=fixture()
bridge.create(b,bh)
delta=bridge.extract(b,bh)
h.require(all(r['changedRecords']==0 for r in h.verify(b,bh,delta)['members']),'UNCHANGED_ROUNDTRIP_FAILED')
obj=next(o for o in bpy.context.scene.objects if o.get('s2mod_member_id')==b['members'][0]['memberId'])
obj.data.shape_keys.key_blocks[bridge.EDIT_KEY].data[3].co.z=.25
delta=bridge.extract(b,bh)
h.require(h.verify(b,bh,delta)['members'][0]['changedRecords']==1,'EDITED_ROUNDTRIP_FAILED')
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=str(root/'synthetic.blend'))
bpy.ops.wm.open_mainfile(filepath=str(root/'synthetic.blend'))
h.require(bridge.extract(b,bh)==delta,'REOPEN_CHANGED_DELTA')
# Native source order is numeric, not lexical (mesh 10 must not precede mesh 2).
ordered=copy.deepcopy(b)
first=ordered['members'][0]; first['mesh']=2;first['memberId']=h.member_id(first)
second=copy.deepcopy(first);second['mesh']=10;second['memberId']=h.member_id(second)
ordered['members'].insert(1,second)
ordered_hash=hashlib.sha256(h.canonical(ordered)).hexdigest()
bridge.create(ordered,ordered_hash)
ordered_delta=bridge.extract(ordered,ordered_hash)
h.require([m['memberId'] for m in ordered_delta['members']]==[m['memberId'] for m in ordered['members'] if m['editable']],'SOURCE_ORDER_CHANGED')
h.verify(ordered,ordered_hash,ordered_delta)
passed=[]
def reject(name,change,code):
 bridge.create(b,bh)
 obj=next(o for o in bpy.context.scene.objects if o.get('s2mod_member_id')==b['members'][0]['memberId'])
 change(obj)
 try:bridge.extract(b,bh)
 except h.HandoffError as error:
  h.require(code in str(error),f'WRONG_REJECTION_{name}:{error}')
  passed.append({'case':name,'code':code});return
 raise RuntimeError(f'Accepted invalid synthetic scene: {name}')

reject('object transform',lambda o:setattr(o.location,'x',1),'OBJECT_TRANSFORM_CHANGED')
reject('modifier',lambda o:o.modifiers.new('test','SUBSURF'),'SCENE_EVALUATION_UNSUPPORTED')
reject('vertex ID',lambda o:setattr(o.data.attributes['s2mod_vertex_id'].data[1],'value',0),'STABLE_IDS_CHANGED')
reject('corner ID',lambda o:setattr(o.data.attributes['s2mod_corner_id'].data[1],'value',0),'STABLE_IDS_CHANGED')
reject('triangle indices',lambda o:setattr(o.data.loops[1],'vertex_index',2),'TOPOLOGY_CHANGED')
reject('source basis',lambda o:setattr(o.data.shape_keys.key_blocks[0].data[1].co,'x',2),'SOURCE_BASIS_CHANGED')
reject('raw source mesh',lambda o:setattr(o.data.vertices[1].co,'x',2),'RAW_SOURCE_MESH_CHANGED')
reject('protected target',lambda o:setattr(o.data.shape_keys.key_blocks[bridge.EDIT_KEY].data[0].co,'x',.1),'PROTECTED_POSITION_CHANGED')
reject('key mix',lambda o:setattr(o.data.shape_keys.key_blocks[bridge.EDIT_KEY],'value',.5),'SHAPE_KEY_EVALUATION_CHANGED')
reject('extra key',lambda o:o.shape_key_add(name='stacked',from_mix=False),'SHAPE_KEYS_CHANGED')
reject('member deletion',lambda o:bpy.data.objects.remove(o,do_unlink=True),'SCENE_MEMBER_INVENTORY_CHANGED')
reject('scene source',lambda o:bpy.context.scene.__setitem__('s2mod_bundle_sha256','d'*64),'SCENE_SOURCE_MISMATCH')
reject('context changed',lambda o:setattr(next(x for x in bpy.context.scene.objects if x.get('s2mod_member_id')==b['members'][-1]['memberId']).data.vertices[1].co,'x',2),'SOURCE_BASIS_CHANGED')
reject('protected metadata',lambda o:setattr(o.data.attributes['s2mod_protected'].data[0],'value',False),'PROTECTED_ATTRIBUTE_CHANGED')
result={'kind':'synthetic_blender_handoff_checks','blenderVersion':bpy.app.version_string,
        'unchangedRoundTrip':'passed','editedRoundTrip':'passed','freshReopen':'passed','rejections':passed,
        'compiledWriterQualified':False,'runtimeQualified':False}
h.publish_bytes(root/'checks.json',h.canonical(result))
print(f'OK: synthetic unchanged/edited/reopened scenes and {len(passed)} invalid-scene rejections passed.')
