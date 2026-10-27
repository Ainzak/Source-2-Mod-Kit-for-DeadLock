"""Installed Blender adapter for labeled preparation and an actual authored return."""
import copy
import math
from pathlib import Path

from common import load, require, sha, write
from surface import derive_surface, compare_surface, ids


def labels(obj, neutral, provenance, policy):
    mesh = obj.data
    seam_vertices = {v for pair in provenance['seamPairs'] for v in pair}
    for name, domain, values in (
            ('s2_vertex_id', 'POINT', range(len(mesh.vertices))),
            ('s2_source_face', 'FACE', provenance['faceSources']),
            ('s2_seam', 'POINT', [int(i in seam_vertices)
                                for i in range(len(mesh.vertices))]),
            ('s2_vertex_role', 'POINT', [2 if i in provenance['boundary'] else
             1 if i in policy['editableVertices'] or i >= len(neutral['positions'])-len(policy['addedVertices'])
             else 0 for i in range(len(mesh.vertices))])):
        attribute = mesh.attributes.get(name) or mesh.attributes.new(name, 'INT', domain)
        for element, value in zip(attribute.data, values): element.value = value
    role = mesh.attributes.get('s2_face_role') or mesh.attributes.new('s2_face_role','INT','FACE')
    for ti, element in enumerate(role.data):
        element.value = 2 if ti in provenance['collar'] else 1 if provenance['faceSources'][ti] in policy['sourceTriangles'] else 0


def rebuild(obj, expected, provenance):
    import bpy
    original = obj.data
    original.calc_loop_triangles()
    source_polygons = [t.polygon_index for t in original.loop_triangles]
    mesh = bpy.data.meshes.new('authored_surface')
    mesh.from_pydata(expected['positions'], [list(e.vertices) for e in original.edges],
                     [ids(expected, tri) for tri in expected['triangles']])
    for mat in original.materials: mesh.materials.append(mat)
    obj.data = mesh
    for bone in {b for weights in expected['weights'] for b in weights}:
        if bone not in obj.vertex_groups: obj.vertex_groups.new(name=bone)
    for i, weights in enumerate(expected['weights']):
        for bone, weight in weights.items(): obj.vertex_groups[bone].add([i],weight,'REPLACE')
    uv = mesh.uv_layers.new(name=original.uv_layers.active.name)
    for loop, coords in zip(uv.data,expected['uv']): loop.uv = coords
    sharp = {tuple(sorted(e.vertices)) for e in original.edges if e.use_edge_sharp}
    for edge in mesh.edges: edge.use_edge_sharp = tuple(sorted(edge.vertices)) in sharp
    for poly, source in zip(mesh.polygons,provenance['faceSources']):
        poly.use_smooth = original.polygons[source_polygons[source]].use_smooth
    mesh.update()
    mesh.normals_split_custom_set(expected['normals'])
    for edge in mesh.edges: edge.use_edge_sharp = tuple(sorted(edge.vertices)) in sharp
    # Copy packed fan values rather than silently recoding protected source normals.
    if original.attributes.get('custom_normal'):
        attribute = mesh.attributes.get('custom_normal')
        require(attribute is not None, 'SURFACE_PACKED_NORMAL_STORAGE_MISSING')
        for i, ref in enumerate(provenance['cornerSources']):
            if len(ref['loops']) == 1:
                attribute.data[i].value = original.attributes['custom_normal'].data[ref['loops'][0]].value
    for src in original.attributes:
        if src.domain != 'CORNER' or src.data_type not in ('FLOAT_COLOR','BYTE_COLOR'): continue
        dst = mesh.attributes.new(src.name,src.data_type,'CORNER')
        for i, ref in enumerate(provenance['cornerSources']):
            dst.data[i].color = [sum(src.data[l].color[k]*w for l,w in zip(ref['loops'],ref['weights'])) for k in range(4)]
    mesh.update()


def restore_normals(obj, neutral, provenance, halo, source_mesh):
    mesh = obj.data
    sharp = {tuple(e) for e in source_mesh['sharp']}
    for edge in mesh.edges: edge.use_edge_sharp = tuple(sorted(edge.vertices)) in sharp
    # Remove stale custom normals for calculating normals from actual geometry.
    if mesh.attributes.get('custom_normal'):
        mesh.attributes.remove(mesh.attributes['custom_normal'])
    mesh.update()
    calculated = [list(n.vector) for n in mesh.corner_normals]
    mesh.normals_split_custom_set([calculated[i] if neutral['corners'][i] in halo else n
                                  for i,n in enumerate(neutral['normals'])])
    for edge in mesh.edges: edge.use_edge_sharp = tuple(sorted(edge.vertices)) in sharp
    if source_mesh['packed']:
        for i, ref in enumerate(provenance['cornerSources']):
            if neutral['corners'][i] not in halo:
                require(len(ref['loops']) == 1, 'SURFACE_PROTECTED_ADDED_NORMAL')
                mesh.attributes['custom_normal'].data[i].value = source_mesh['packed'][ref['loops'][0]]
    mesh.update()


def execute(request):
    import bpy
    from blender_job import snapshot
    from authoring import compare_authored
    out = Path(request['output'])
    bpy.context.preferences.filepaths.use_scripts_auto_execute = False
    bpy.context.preferences.filepaths.save_version = 0
    if request['mode'] == 'surface-review':
        from surface_review import render
        render(request)
        return
    bpy.ops.wm.open_mainfile(filepath=request['baseline'],use_scripts=False)
    baseline = snapshot()
    intent = copy.deepcopy(request['intent']); policy = intent['surface']
    obj = bpy.data.objects[policy['mesh']]
    source_shading = {'sharp': [tuple(sorted(e.vertices)) for e in obj.data.edges if e.use_edge_sharp],
                      'packed': [tuple(x.value) for x in obj.data.attributes['custom_normal'].data]
                      if obj.data.attributes.get('custom_normal') else []}
    colors = {a.name: {'type':a.data_type, 'values':[list(x.color) for x in a.data]}
              for a in obj.data.attributes if a.domain == 'CORNER' and a.data_type in ('BYTE_COLOR','FLOAT_COLOR')}
    rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    rig_context = {'matrix':[list(r) for r in rig.matrix_world],
                   'pose':{b.name:[list(r) for r in b.matrix_basis] for b in rig.pose.bones}}
    neutral, provenance = derive_surface(baseline['meshes'][policy['mesh']],policy,baseline['bones'])
    if request['mode'] == 'surface-prepare':
        if policy['geometryMode'] != 'unchanged': rebuild(obj,neutral,provenance)
        labels(obj,neutral,provenance,policy)
        # Geometry-only output still retains all excluded source context for guards.
        mapping = {m:m for unit in baseline['meshes'].values() for m in unit['materials']}
        full_intent = dict(intent, selectedMeshes=list(baseline['meshes']))
        compare_authored(baseline,snapshot(),full_intent,mapping)
        write(out/'neutral.json',snapshot())
        write(out/'provenance.json',provenance)
        bpy.ops.wm.save_as_mainfile(filepath=str(out/'surface.blend'))
    else:
        bpy.ops.wm.open_mainfile(filepath=request['returnedBlend'],use_scripts=False)
        candidate = snapshot()
        require(candidate['bones'] == baseline['bones'] and set(candidate['meshes']) == set(baseline['meshes']),
                'SURFACE_SCENE_CHANGED')
        obj = bpy.data.objects[policy['mesh']]
        rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
        require(rig_context == {'matrix':[list(r) for r in rig.matrix_world],
                'pose':{b.name:[list(r) for r in b.matrix_basis] for b in rig.pose.bones}}, 'SURFACE_RIG_POSE_CHANGED')
        require(not rig.animation_data and not rig.constraints and not any(b.constraints for b in rig.pose.bones),
                'SURFACE_RIG_ANIMATION_CHANGED')
        require(not obj.constraints and not obj.animation_data and not obj.data.shape_keys and
                all(m.type == 'ARMATURE' and m.object == rig for m in obj.modifiers), 'RETURNED_MODIFIER_UNSUPPORTED')
        require(obj.data.attributes.get('s2_vertex_id') is not None and
                [x.value for x in obj.data.attributes['s2_vertex_id'].data] == list(range(len(neutral['positions']))),
                'SURFACE_STABLE_IDS_CHANGED')
        require(obj.data.attributes.get('s2_source_face') is not None and
                [x.value for x in obj.data.attributes['s2_source_face'].data] == provenance['faceSources'],
                'SURFACE_FACE_IDS_CHANGED')
        for name, facts in colors.items():
            attr = obj.data.attributes.get(name)
            require(attr is not None and attr.data_type == facts['type'] and attr.domain == 'CORNER' and
                    len(attr.data) == len(provenance['cornerSources']), 'SURFACE_COLOR_LAYOUT_CHANGED')
            for i, ref in enumerate(provenance['cornerSources']):
                expected_color = [sum(facts['values'][l][k]*w for l,w in zip(ref['loops'],ref['weights'])) for k in range(4)]
                epsilon = .01 if facts['type'] == 'BYTE_COLOR' and len(ref['loops']) > 1 else 1e-6
                require(all(abs(a-b) <= epsilon for a,b in zip(attr.data[i].color,expected_color)),
                        'SURFACE_SOURCE_COLOR_CHANGED')
        # Validate geometry/attributes before normalization; normals must be unit and bounded.
        mapping = {m:m for unit in baseline['meshes'].values() for m in unit['materials']}
        report = compare_surface(baseline['meshes'][policy['mesh']],candidate['meshes'][policy['mesh']],policy,
                                 baseline['bones'],mapping)
        restore_normals(obj,neutral,provenance,set(report['normalHaloVertices']),source_shading)
        full_intent = dict(intent, selectedMeshes=list(baseline['meshes']))
        compare_authored(baseline,snapshot(),full_intent,mapping)
        bpy.ops.wm.save_as_mainfile(filepath=str(out/'returned.blend'))
        policy['blendPath'] = str(out/'returned.blend'); policy['blendHash'] = sha(out/'returned.blend')
        write(out/'intent.json',intent)
        write(out/'authored.json',snapshot())
        write(out/'surface-report.json',report)
