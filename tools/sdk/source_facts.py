"""Restore only exact source rest transforms/morph defaults lost by export transport."""
import math
from common import canonical, require
from kv import field
from verify import animation_masks, matrix


def restore_masks(nodes, source_text):
    masks = animation_masks(source_text)
    groups = [n for n in nodes if n['_class'] == 'WeightListList']
    require(len(groups) == 1, 'SOURCE_MASK_GROUP_INVALID')
    children = groups[0]['children']
    require({n['name'] for n in children} == set(masks)-{'default'}, 'SOURCE_MASK_NAMES_CHANGED')
    for node in children:
        expected = masks[node['name']]
        require(not expected['morph'], 'SOURCE_EXPLICIT_MORPH_ARRAY_UNSUPPORTED')
        require(dict((w['bone'],w['weight']) for w in node['weights']) == expected['bones'],
                'SOURCE_MASK_WEIGHTS_CHANGED')
        node['master_morph_weight'] = expected['defaultMorph']
        node['morph_weights'] = []


def restore_bind(dm, native_text, datamodel):
    names, parents, positions, rotations = [field(native_text,k) for k in
            ('m_boneName','m_nParent','m_bonePosParent','m_boneRotParent')]
    require(len(set(names)) == len(names) == len(parents) == len(positions) == len(rotations),
            'SOURCE_BIND_ARRAYS_INVALID')
    joints = [e for e in dm.elements if e.type == 'DmeJoint']
    require({j.name for j in joints} == set(names) and len(joints) == len(names), 'DMX_SOURCE_JOINTS_CHANGED')
    named = dict(zip(names,zip(parents,positions,rotations)))
    actual_parents = {}
    for joint in joints:
        for child in joint.get('children',[]):
            if child.type == 'DmeJoint':
                require(child.name not in actual_parents,'DMX_DUPLICATE_PARENT')
                actual_parents[child.name] = joint.name
    require(all(actual_parents.get(n) == (names[p] if p >= 0 else None)
                for n,(p,_,_) in named.items()), 'DMX_SOURCE_PARENTS_CHANGED')
    transforms = [e for e in dm.elements if e.type == 'DmeTransform' and e.name in named]
    require(len(transforms) == 2*len(names) and all(sum(t.name == n for t in transforms) == 2 for n in names),
            'DMX_SOURCE_BIND_STATES_CHANGED')
    allowed = {str(t.id) for t in transforms}
    def value(v):
        if hasattr(v,'id') and hasattr(v,'type'): return {'ref':str(v.id),'type':v.type}
        if isinstance(v,(list,tuple)) or type(v).__name__.endswith('Array'):
            return [value(x) for x in v]
        # datamodel vectors/arrays are list subclasses; bytes are not admitted attributes.
        if isinstance(v,(str,int,float,bool)) or v is None: return v
        try:return [value(x) for x in v]
        except TypeError:raise ValueError('DMX_ATTRIBUTE_LAYOUT_UNSUPPORTED')
    def protected():
        return canonical({str(e.id):{'type':e.type,'name':e.name,'attributes':
                {k:value(v) for k,v in e.items() if not(str(e.id) in allowed and k in ('position','orientation'))}}
                          for e in dm.elements})
    before = protected(); max_position,max_rotation = 0,0
    for t in transforms:
        _,p,q = named[t.name]
        require(set(t) <= {'position','orientation','scale'} and
                t.get('scale',1) == 1, 'DMX_SOURCE_BIND_LAYOUT_UNSUPPORTED')
        delta = math.dist(t['position'],p)
        a,b = matrix([0,0,0],t['orientation']),matrix([0,0,0],q)
        trace = sum(a[i][j]*b[i][j] for i in range(3) for j in range(3))
        rotation = math.degrees(math.acos(max(-1,min(1,(trace-1)/2))))
        require(delta < .002 and rotation < .002,'DMX_SOURCE_BIND_BASIS_MISMATCH')
        max_position = max(max_position,delta);max_rotation = max(max_rotation,rotation)
        t['position'] = datamodel.Vector3(p)
        # Compiled source quaternions are quantized and may not have unit length.
        # DMX consumers assume unit rotations; carrying the quantization scale into
        # a bind matrix accumulates positional error down a chain. Match matrix()
        # semantics exactly, without changing the source rotation.
        length = math.sqrt(sum(v*v for v in q))
        require(length > 1e-8,'SOURCE_BIND_QUATERNION_INVALID')
        t['orientation'] = datamodel.Quaternion([v/length for v in q])
    require(protected() == before,'DMX_UNDECLARED_TRANSPORT_CHANGE')
    return {'operation':'sdk_source_bind_transport@1','bones':len(names),
            'maxExportLocalPositionError':max_position,'maxExportLocalRotationError':max_rotation,
            'nonBindAttributesExact':True,'runtimeTested':False}
