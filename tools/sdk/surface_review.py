"""Matched offline Blender geometry views, roles, poses and true section curves."""
import html
import math
from pathlib import Path

from common import require, validate, write
from surface import derive_surface, compare_surface


def section_svg(section, path):
    from mathutils import Vector
    n = Vector(section['normal'])
    u = n.cross(Vector((0,0,1)))
    if u.length < .01: u = n.cross(Vector((0,1,0)))
    u.normalize(); v = n.cross(u).normalized()
    projected = {}
    for key in ('source','authored'):
        projected[key] = [[[Vector(p).dot(u),Vector(p).dot(v)] for p in s['points']] for s in section[key]]
    points = [p for segments in projected.values() for segment in segments for p in segment]
    if not points: points = [[0,0],[1,1]]
    low = [min(p[k] for p in points) for k in range(2)]
    high = [max(p[k] for p in points) for k in range(2)]
    scale = 500/max(max(b-a for a,b in zip(low,high)),1)
    content = ['<svg xmlns="http://www.w3.org/2000/svg" width="600" height="600" viewBox="0 0 600 600">',
               '<rect width="600" height="600" fill="#17212b"/>',
               '<text x="20" y="25" fill="white">'+html.escape(section['name'])+'</text>',
               '<text x="20" y="48" fill="#a6b0ba">Source: grey | Return: orange | actual triangle sections</text>']
    for key,color in (('source','#b8c8d8'),('authored','#ff993c')):
        for segment in projected[key]:
            coords = [(50+(p[0]-low[0])*scale,550-(p[1]-low[1])*scale) for p in segment]
            content.append(f'<line x1="{coords[0][0]}" y1="{coords[0][1]}" x2="{coords[1][0]}" y2="{coords[1][1]}" stroke="{color}" stroke-width="1.5"/>')
    content.append('</svg>')
    with path.open('x',encoding='utf-8') as stream: stream.write('\n'.join(content))


def render(request):
    import bpy
    from mathutils import Vector
    from blender_job import snapshot, verify_geometry
    from authoring import compare_authored
    views = validate(request['views'],'surface-views')
    out = Path(request['output']); intent = request['intent']; policy = intent['surface']
    require(len({v['name'] for v in views['directions']}) == len(views['directions']) and
            len({p['name'] for p in views['poses']}) == len(views['poses']), 'SURFACE_VIEW_NAMES_AMBIGUOUS')
    bpy.ops.wm.open_mainfile(filepath=request['baseline'],use_scripts=False)
    baseline = snapshot()
    neutral, provenance = derive_surface(baseline['meshes'][policy['mesh']],policy,baseline['bones'])
    bpy.ops.wm.open_mainfile(filepath=request['returnedBlend'],use_scripts=False)
    authored = snapshot()
    mapping = {m:m for unit in baseline['meshes'].values() for m in unit['materials']}
    compare_authored(baseline,authored,dict(intent,selectedMeshes=list(baseline['meshes'])),mapping)
    report = compare_surface(baseline['meshes'][policy['mesh']],authored['meshes'][policy['mesh']],policy,baseline['bones'],mapping)
    for i,section in enumerate(report['crossSections']): section_svg(section,out/f'section-{i}.svg')
    write(out/'geometry-report.json',report)
    variants = [('source',request['baseline']),('authored',request['returnedBlend'])]
    if request.get('compiledBlend'):
        bpy.ops.wm.open_mainfile(filepath=request['compiledBlend'],use_scripts=False)
        expected = {'meshes':{n:authored['meshes'][n] for n in intent['selectedMeshes']}}
        write(out/'compiled-corner-report.json',verify_geometry(expected,snapshot()))
        variants.append(('compiled',request['compiledBlend']))
    products = []
    for label, blend in variants:
        for pose in [None]+views['poses']:
            bpy.ops.wm.open_mainfile(filepath=blend,use_scripts=False)
            if label != 'compiled':
                for name in intent['excludedMeshes']:
                    bpy.data.objects.remove(bpy.data.objects[name],do_unlink=True)
            rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
            if pose:
                require(pose['bone'] in rig.pose.bones and abs(Vector(pose['axis']).length-1) < 1e-6,
                        'SURFACE_POSE_INVALID')
                bone = rig.pose.bones[pose['bone']]
                axis = bone.bone.matrix_local.to_3x3().inverted() @ Vector(pose['axis'])
                bone.rotation_mode = 'AXIS_ANGLE'
                bone.rotation_axis_angle = (math.radians(pose['angleDegrees']),*axis)
            bpy.context.view_layer.update()
            scene = bpy.context.scene
            scene.render.engine = 'BLENDER_WORKBENCH'
            scene.render.resolution_x = scene.render.resolution_y = 700
            scene.render.resolution_percentage = 100
            scene.display.shading.light = 'STUDIO'
            scene.display.shading.color_type = 'SINGLE'
            scene.display.shading.single_color = (.55,.62,.68)
            scene.display.shading.show_shadows = True
            scene.display.shading.show_cavity = True
            scene.display.shading.cavity_type = 'BOTH'
            scene.display.shading.background_type = 'WORLD'
            if scene.world is None: scene.world = bpy.data.worlds.new('surface_review_world')
            scene.world.color = (.04,.055,.07)
            camera = bpy.data.cameras.new('surface_review')
            obj = bpy.data.objects.new('surface_review',camera); scene.collection.objects.link(obj)
            camera.type = 'ORTHO'; scene.camera = obj
            pose_name = pose['name'] if pose else 'rest'
            directions = views['directions'] if not pose else views['directions'][:2]
            for view in directions:
                direction = Vector(view['direction'])
                require(direction.length > 1e-6,'SURFACE_CAMERA_DIRECTION')
                direction.normalize()
                for frame in ('full','region'):
                    center = Vector(views[frame+'Center'])
                    obj.location = center+direction*200
                    obj.rotation_euler = (center-obj.location).to_track_quat('-Z','Y').to_euler()
                    camera.ortho_scale = views[frame+'Scale']
                    file = f'{label}-{pose_name}-{frame}-{view["name"]}.png'
                    scene.render.filepath = str(out/file)
                    bpy.ops.render.render(write_still=True)
                    products.append({'file':file,'variant':label,'pose':pose_name,'frame':frame,
                                     'view':view['name'],'poseEvidence':'synthetic' if pose else 'source_bind'})
                    # A separate role view never changes the returned artifact.
                    if label == 'authored' and not pose and frame == 'region':
                        target = bpy.data.objects[policy['mesh']]
                        colors = target.data.color_attributes.new('review_roles','BYTE_COLOR','CORNER')
                        palette = ((.25,.45,.7,1),(.95,.5,.15,1),(.8,.15,.6,1))
                        for poly in target.data.polygons:
                            role = 2 if poly.index in provenance['collar'] else 1 if provenance['faceSources'][poly.index] in policy['sourceTriangles'] else 0
                            for loop in poly.loop_indices: colors.data[loop].color = palette[role]
                        target.data.color_attributes.active_color = colors
                        scene.display.shading.color_type = 'VERTEX'
                        scene.render.filepath = str(out/f'roles-{view["name"]}.png')
                        bpy.ops.render.render(write_still=True)
                        scene.display.shading.color_type = 'SINGLE'
                        target.data.color_attributes.remove(colors)
    write(out/'view-index.json', {'views':products,'roles':{'blue':'protected source','orange':'reconstructed / editable',
          'magenta':'transition collar at fixed interface'},'poseSource':'explicit synthetic bone rotations',
          'runtimeTested':False,'productionAdmission':False})
