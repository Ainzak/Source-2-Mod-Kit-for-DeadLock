"""Experimental installed-tool workflow; not a production model mutation command."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys

sys.dont_write_bytecode = True
from common import HERE, artifact_path, canonical, check_pins, fresh, load, logical, pins, require, run, sha, validate, write
from kv import emit, field, metadata, parse, refs
from verify import compare, animation_masks, compare_animation_masks
from authoring import compare_authored
import lifecycle

ARCHIVE = HERE / 'archive/bin/Release/net10.0/ArchiveTool.dll'
RETAINED = {'BoneMarkupList', 'AttachmentList', 'HitboxSetList', 'PhysicsShapeList', 'AnimationList', 'WeightListList'}
REPLACED = {'RenderMeshList', 'LodGroupList', 'LODGroupList',   # VRF 20 spelling
            'BodyGroupList', 'MaterialGroupList', 'Skeleton',
            'GameDataList', 'AnimConstraintList', 'NmSkeletonList', 'AnimGraph2List'}
LIMITATIONS = ['single_lod', 'sdk_regenerated_blocks', 'native_frames_unverified',
               'source_export_fidelity_unverified', 'runtime_unverified']


def configuration(path):
    value = validate(load(path), 'config')
    for tool in value['tools'].values():
        require(Path(tool).is_file(), 'TOOL_MISSING:' + tool)
    for kind, game in value['gameRoots'].items():
        require(Path(game).is_dir() and (Path(game) / 'core').is_dir(), 'SDK_ENVIRONMENT_MISSING:' + kind)
        require(Path(value['tools'][kind]).resolve().is_relative_to(Path(game).resolve()),
                'SDK_EXECUTABLE_ENVIRONMENT_MISMATCH')
    require(Path(value['gameRoots']['csdk']).resolve() != Path(value['gameRoots']['cs2']).resolve(),
            'SDK_ENVIRONMENTS_MUST_DIFFER')
    require(ARCHIVE.is_file(), 'ARCHIVE_HELPER_NOT_BUILT')
    return value


def workspace(config, output, *immutable, archive_path=None):
    target = Path(output).resolve()
    protected = [Path(p).resolve() for p in config['gameRoots'].values()]
    protected += [Path(p).resolve().parent for p in config['tools'].values()]
    protected += [Path(p).resolve() for p in immutable]
    if archive_path is not None and (Path(archive_path).parent / 'gameinfo.gi').is_file():
        protected.append(Path(archive_path).resolve().parent)
    require(not any(target.is_relative_to(p) for p in protected), 'OUTPUT_INSIDE_PROTECTED_ROOT')
    return fresh(output)


def tool_pins(config):
    result = {key: sha(path) for key, path in config['tools'].items()}
    # Pin the executable helper and its runtime dependencies, not just the Python wrapper.
    result.update({'archive:' + p.name: sha(p) for p in ARCHIVE.parent.glob('*.dll')})
    result.update({'adapter:' + p.name: sha(p) for p in HERE.glob('*.py')})
    result.update({'schema:' + p.relative_to(HERE).as_posix(): sha(p)
                   for p in (HERE / 'schemas').rglob('*.json')})
    return result


def archive(config, action, args, log):
    return run([config['tools']['dotnet'], ARCHIVE, action, *args], log)


def blender(config, mode, out, **kwargs):
    request = out / ('request-' + mode + '.json')
    write(request, {'mode': mode, 'output': str(out), **kwargs})
    run([config['tools']['blender'], '--background', '--python-exit-code', '20',
         '--python', HERE / 'blender_job.py', '--', '--request', request], out / ('blender-' + mode + '.log'))


def probe(config_path, output):
    config = configuration(config_path)
    out = workspace(config, output)
    blender(config, 'probe', out)
    versions = {}
    for name in ('vrf', 'dotnet'):
        versions[name] = run([config['tools'][name], '--version'], out / (name + '-version.log')).strip()
    result = {'operation': 'sdk_capabilities@1', 'tools': tool_pins(config),
              'versions': versions, 'addons': load(out / 'addons.json'),
              'sdkVersionIdentity': 'binary_sha256', 'productionAdmission': False}
    write(out / 'capabilities.json', result)
    return result


def source(root):
    root = Path(root).resolve()
    manifest = validate(load(root / 'manifest.json'), 'manifest')
    check_pins(root, manifest['files'])
    config = configuration(root / 'config.json')
    require(sha(root / 'config.json') == manifest['configHash'], 'CONFIG_DRIFT')
    require(tool_pins(config) == manifest['toolPins'], 'TOOL_DRIFT')
    return root, manifest, config


def extract(config, archive_path, requests, destination, stage, name):
    spec = stage / (name + '.json')
    write(spec, requests)
    archive(config, 'extract', [archive_path, spec, destination], stage / (name + '.log'))


def prepare(args):
    config = configuration(args.config)
    out = workspace(config, args.output, archive_path=args.archive)
    model, skeleton = logical(args.model), logical(args.skeleton)
    graphs = [logical(args.default_graph), logical(args.ui_graph)]
    require(model.endswith('.vmdl_c') and skeleton.endswith('.vnmskel'), 'SOURCE_EXTENSION_INVALID')
    require(args.sha256 and len(args.sha256) == 64, 'SOURCE_HASH_REQUIRED')
    shutil.copyfile(args.config, out / 'config.json')
    inputs = out / 'inputs'
    inputs.mkdir()
    extract(config, args.archive, {model: args.sha256}, inputs, out, 'source-extract')
    model_path = inputs / model
    data = run([config['tools']['vrf'], '-i', model_path, '-b', 'DATA'], out / 'source-data.log')
    require(metadata(data), 'EMPTY_HERO_METADATA')
    references = refs(data)
    require(skeleton in references and all(g in references for g in graphs), 'SOURCE_REFERENCES_MISMATCH')
    require(not field(data, 'm_refMeshes'), 'EXTERNAL_MESH_UNSUPPORTED')
    decompiled = out / 'decompiled'
    doc = decompiled / model.removesuffix('_c')
    doc.parent.mkdir(parents=True)
    run([config['tools']['vrf'], '-i', model_path, '-o', doc, '-d'], out / 'decompile.log')
    parsed = parse(doc.read_text())
    nodes = parsed['rootNode']['children']
    kinds = [n['_class'] for n in nodes]
    require(len(set(kinds)) == len(kinds), 'DUPLICATE_SOURCE_NODE')
    require(set(kinds) <= RETAINED | REPLACED, 'UNKNOWN_MODELDOC_NODE')
    require({'BoneMarkupList', 'AnimationList', 'RenderMeshList'} <= set(kinds), 'REQUIRED_SOURCE_NODE_MISSING')
    if 'WeightListList' in kinds:
        masks = run([config['tools']['vrf'], '-i', model_path, '-b', 'ASEQ'], out / 'source-masks.log')
        compiled = animation_masks(masks)
        lists = next(n for n in nodes if n['_class'] == 'WeightListList')['children']
        require(len(lists) == len({n['name'] for n in lists}) and
                {n['name'] for n in lists} == set(compiled) - {'default'}, 'SOURCE_MASK_INVENTORY_CHANGED')
        bones = set(field(data, 'm_boneName'))
        for n in lists:
            require(set(n) == {'_class', 'name', 'weights'} and n['_class'] == 'WeightList',
                    'UNSUPPORTED_WEIGHT_LIST')
            require(len(n['weights']) == len({w['bone'] for w in n['weights']}), 'DUPLICATE_MASK_BONE')
            for w in n['weights']:
                require(set(w) == {'bone', 'weight'} and w['bone'] in bones and
                        type(w['weight']) in (int, float) and 0 <= w['weight'] <= 1, 'INVALID_MASK_WEIGHT')
    deps = out / 'dependencies'
    deps.mkdir()
    # Exact archive entries, never loader auto-discovery of unrelated installed addon overrides.
    extract(config, args.archive, {logical(x + '_c'): '' for x in references}, deps, out, 'dependencies')
    mats = [x for x in references if x.endswith('.vmat')]
    textures = set()
    for i, mat in enumerate(mats):
        text = run([config['tools']['vrf'], '-i', deps / (mat + '_c'), '-b', 'RERL'],
                   out / ('material-' + str(i) + '.log'))
        textures.update(x for x in refs(text) if x.endswith('.vtex'))
    if textures:
        extract(config, args.archive, {logical(x + '_c'): '' for x in sorted(textures)}, deps, out, 'textures')
    blender(config, 'prepare', out, source=str(model_path))
    baseline = load(out / 'baseline.json')
    require(len(baseline['bones']) == len(field(data, 'm_boneName')), 'IMPORTED_SOURCE_BONES_MISSING')
    manifest = {'operation': 'sdk_source@1', 'version': 1, 'sourcePath': model,
                'sourceHash': sha(model_path), 'skeletonPath': skeleton,
                'skeletonHash': sha(deps / (skeleton + '_c')), 'defaultGraph': graphs[0], 'uiGraph': graphs[1],
                'archive': str(Path(args.archive).resolve()), 'configHash': sha(out / 'config.json'),
                'toolPins': tool_pins(config), 'addonHash': sha(out / 'addons.json'),
                'sourceNodes': kinds, 'sourceRefs': references,
                'sourceLodMasks': field(data, 'm_refLODGroupMasks'),
                'sourceBodygroups': field(data, 'm_meshGroups'),
                'meshNames': sorted(baseline['meshes']), 'baselineHash': sha(out / 'baseline.json'),
                'files': pins(out)}
    validate(manifest, 'manifest')
    write(out / 'manifest.json', manifest)
    return {'operation': 'sdk_prepare@1', 'manifestHash': sha(out / 'manifest.json'),
            'meshes': manifest['meshNames'], 'sourceLodMasks': manifest['sourceLodMasks']}


def repin(args):
    """Re-record tool pins after a deliberate tool update (inputs and config must still match their pins)."""
    root = Path(args.source).resolve()
    manifest = validate(load(root / 'manifest.json'), 'manifest')
    check_pins(root, manifest['files'])
    config = configuration(root / 'config.json')
    require(sha(root / 'config.json') == manifest['configHash'], 'CONFIG_DRIFT')
    old, new = manifest['toolPins'], tool_pins(config)
    changed = sorted(k for k in old.keys() | new.keys() if old.get(k) != new.get(k))
    if not changed:
        return {'operation': 'sdk_repin@1', 'changed': []}
    previous = sha(root / 'manifest.json')
    backup = root / ('manifest.' + previous[:16] + '.json')
    require(not backup.exists(), 'REPIN_BACKUP_EXISTS')
    backup.write_bytes((root / 'manifest.json').read_bytes())
    manifest['toolPins'] = new
    validate(manifest, 'manifest')
    staged = root / ('manifest.' + previous[:16] + '.new')
    write(staged, manifest)
    os.replace(staged, root / 'manifest.json')
    with (root / 'tool-repins.jsonl').open('a', encoding='utf-8', newline='\n') as stream:
        stream.write(json.dumps({'previousManifestHash': previous, 'manifestHash': sha(root / 'manifest.json'),
                                 'changed': changed}, separators=(',', ':')) + '\n')
    return {'operation': 'sdk_repin@1', 'changed': changed, 'previousManifestHash': previous}


def export(args):
    root, manifest, config = source(args.source)
    intent = read_intent(args.intent)
    if intent['version'] == 4:
        surface_control(root, intent)
    require(intent['manifestHash'] == sha(root / 'manifest.json'), 'INTENT_SOURCE_MISMATCH')
    selected, excluded = set(intent['selectedMeshes']), set(intent['excludedMeshes'])
    require(selected and not selected & excluded and selected | excluded == set(manifest['meshNames']),
            'SELECTION_INVENTORY_INVALID')
    require(len({x['mesh'] for x in intent['scales']}) == len(intent['scales']) and
            {x['mesh'] for x in intent['scales']} <= selected, 'SCALE_SELECTION_INVALID')
    out = workspace(config, args.output, root, archive_path=manifest['archive'])
    shutil.copyfile(args.intent, out / 'intent.json')
    returned = {}
    if intent['version'] in (2, 3, 4):
        if intent['version'] in (2, 3):
            require('WeightListList' not in manifest['sourceNodes'], 'AUTHORED_DEPENDENCY_UNSUPPORTED')
        replacement = intent.get('replacement', intent.get('patch', intent.get('surface')))
        candidate = Path(replacement['blendPath']).resolve()
        require(candidate.is_file() and candidate.stat().st_size <= 256 * 1024 * 1024 and
                sha(candidate) == replacement['blendHash'], 'AUTHORING_INPUT_DRIFT')
        require(not out.is_relative_to(candidate.parent), 'OUTPUT_INSIDE_AUTHORING_ROOT')
        shutil.copyfile(candidate, out / 'authoring-input.blend')
        require(sha(out / 'authoring-input.blend') == replacement['blendHash'], 'AUTHORING_INPUT_DRIFT')
        returned = {'returnedBlend': str(out / 'authoring-input.blend')}
    mats = [p for p in manifest['sourceRefs'] if p.endswith('.vmat')]
    mapping = {Path(p).stem: p.removesuffix('.vmat') for p in mats}
    require(len(mapping) == len(mats), 'MATERIAL_NAME_AMBIGUOUS')
    blender(config, 'export', out, baseline=str(root / 'baseline.blend'), intent=intent,
            materials=mapping, sourceData=str(root / 'source-data.log'), **returned)
    require(sha(out / 'addons.json') == manifest['addonHash'], 'ADDON_DRIFT')
    authored = load(out / 'authored.json')
    require(all(m in mapping.values() for x in authored['meshes'].values() for m in x['materials']),
            'MATERIAL_PATH_UNRESOLVED')
    compare_authored(load(root / 'baseline.json'), authored, intent, mapping)
    plan = {'operation': 'sdk_plan@1', 'version': 1, 'manifestHash': sha(root / 'manifest.json'),
            'intentHash': sha(out / 'intent.json'), 'sourceRoot': str(root), 'exportRoot': str(out),
            'configHash': manifest['configHash'], 'files': pins(out), 'compiledPath': manifest['sourcePath'],
            'stages': ['csdk_raw_dmx', 'cs2_raw_dmx'], 'limitations': LIMITATIONS}
    validate(plan, 'plan')
    write(out / 'plan.json', plan)
    return {'operation': 'sdk_export@1', 'planHash': sha(out / 'plan.json')}


def plan_source(export_root):
    export_root = Path(export_root).resolve()
    plan = validate(load(export_root / 'plan.json'), 'plan')
    require(Path(plan['exportRoot']) == export_root, 'PLAN_ROOT_MISMATCH')
    check_pins(export_root, plan['files'])
    root, manifest, config = source(plan['sourceRoot'])
    require(plan['manifestHash'] == sha(root / 'manifest.json') and
            plan['configHash'] == manifest['configHash'], 'PLAN_SOURCE_DRIFT')
    intent = read_intent(export_root / 'intent.json')
    if intent['version'] == 4:
        surface_control(root,intent)
    if intent['version'] in (2, 3, 4):
        require(sha(export_root / 'authoring-input.blend') == intent.get('replacement', intent.get('patch', intent.get('surface')))['blendHash'],
                'AUTHORING_INPUT_DRIFT')
    require(plan['intentHash'] == sha(export_root / 'intent.json') and
            intent['manifestHash'] == sha(root / 'manifest.json'), 'PLAN_INTENT_DRIFT')
    mats = {Path(p).stem: p.removesuffix('.vmat') for p in manifest['sourceRefs'] if p.endswith('.vmat')}
    compare_authored(load(root / 'baseline.json'), load(export_root / 'authored.json'), intent, mats)
    return export_root, plan, root, manifest, config


def read_intent(path):
    value = load(path)
    require(isinstance(value, dict), 'INTENT_OBJECT_REQUIRED')
    return validate(value, 'intent-v' + str(value['version']) if value.get('version') in (2, 3, 4) else 'intent')


def surface_control(root, intent):
    policy = intent['surface']
    if policy['geometryMode'] == 'unchanged':
        require(not policy['unchangedControlPath'] and not policy['unchangedControlHash'],
                'SURFACE_UNCHANGED_CONTROL_INVALID')
        return
    control = Path(policy['unchangedControlPath']).resolve()
    require((control / 'evidence.json').is_file() and
            sha(control / 'evidence.json') == policy['unchangedControlHash'], 'SURFACE_CONTROL_REQUIRED')
    evidence = validate(load(control / 'evidence.json'),'evidence')
    check_pins(control,evidence['files'])
    require(evidence['manifestHash'] == sha(root / 'manifest.json'), 'SURFACE_CONTROL_SOURCE_MISMATCH')
    binding = load(control / 'binding.json')
    require(binding['evidenceHash'] == sha(control / 'evidence.json'), 'SURFACE_CONTROL_DRIFT')
    built_root = Path(binding['buildRoot'])
    check_pins(built_root,load(built_root / 'build.json')['files'])
    plan = validate(load(built_root / 'plan.json'),'plan')
    require(plan['manifestHash'] == evidence['manifestHash'] and
            sha(built_root / 'plan.json') == evidence['planHash'], 'SURFACE_CONTROL_PLAN_DRIFT')
    exported = Path(plan['exportRoot'])
    check_pins(exported,plan['files'])
    unchanged = read_intent(exported / 'intent.json')
    require((unchanged['version'] == 4 and unchanged['bindTransport'] == intent['bindTransport']) or
            (unchanged['version'] == 1 and intent['bindTransport'] == 'exporter' and
             'WeightListList' not in load(root / 'manifest.json')['sourceNodes']),
            'SURFACE_CONTROL_TRANSPORT_MISMATCH')
    require(sha(exported / 'intent.json') == evidence['intentHash'] and
            unchanged['selectedMeshes'] == intent['selectedMeshes'] and
            unchanged['excludedMeshes'] == intent['excludedMeshes'] and
            (unchanged['version'] == 4 and unchanged['surface']['geometryMode'] == 'unchanged' or
             unchanged['version'] == 1 and all(s['axes'] == [1,1,1] for s in unchanged['scales'])),
            'SURFACE_CONTROL_NOT_UNCHANGED')
    manifest = load(root / 'manifest.json')
    require(sha(built_root / 'cs2/game/lab' / manifest['sourcePath']) == evidence['modelHash'],
            'SURFACE_CONTROL_MODEL_DRIFT')


def surface_prepare(args):
    root, manifest, config = source(args.source)
    intent = read_intent(args.intent)
    require(intent['version'] == 4 and intent['manifestHash'] == sha(root / 'manifest.json'),
            'SURFACE_SOURCE_MISMATCH')
    surface_control(root,intent)
    selected, excluded = set(intent['selectedMeshes']), set(intent['excludedMeshes'])
    require(selected and not selected & excluded and selected | excluded == set(manifest['meshNames']) and
            intent['surface']['mesh'] in selected, 'SELECTION_INVENTORY_INVALID')
    out = workspace(config, args.output, root, archive_path=manifest['archive'])
    write(out / 'blueprint.json', intent)
    blender(config, 'surface-prepare', out, baseline=str(root / 'baseline.blend'), intent=intent)
    require(sha(out / 'addons.json') == manifest['addonHash'], 'ADDON_DRIFT')
    write(out / 'binding.json', {'sourceRoot':str(root), 'manifestHash':sha(root / 'manifest.json'),
                                'files':pins(out)})
    return {'operation':'sdk_surface_prepare@1', 'scene':str(out / 'surface.blend'),
            'bindingHash':sha(out / 'binding.json'), 'runtimeTested':False}


def surface_return(args):
    prepared = Path(args.prepared).resolve()
    binding = load(prepared / 'binding.json')
    require(set(binding) == {'sourceRoot','manifestHash','files'}, 'SURFACE_BINDING_INVALID')
    check_pins(prepared,binding['files'])
    root, manifest, config = source(binding['sourceRoot'])
    require(binding['manifestHash'] == sha(root / 'manifest.json'), 'SURFACE_SOURCE_DRIFT')
    candidate = Path(args.blend).resolve()
    require(candidate.is_file() and candidate.stat().st_size <= 256*1024*1024 and
            sha(candidate) == args.sha256, 'AUTHORING_INPUT_DRIFT')
    out = workspace(config,args.output,root,prepared,candidate.parent,archive_path=manifest['archive'])
    shutil.copyfile(candidate,out / 'input.blend')
    require(sha(out / 'input.blend') == args.sha256, 'AUTHORING_INPUT_DRIFT')
    intent = read_intent(prepared / 'blueprint.json')
    surface_control(root,intent)
    blender(config,'surface-return',out,baseline=str(root / 'baseline.blend'),intent=intent,
            returnedBlend=str(out / 'input.blend'))
    require(sha(out / 'addons.json') == manifest['addonHash'], 'ADDON_DRIFT')
    write(out / 'binding.json', {'preparedHash':sha(prepared / 'binding.json'),
                                'inputHash':args.sha256, 'files':pins(out)})
    return {'operation':'sdk_surface_return@1', 'intent':str(out / 'intent.json'),
            'report':str(out / 'surface-report.json'), 'runtimeTested':False}


def surface_review(args):
    root, manifest, config = source(args.source)
    intent = read_intent(args.intent)
    require(intent['version'] == 4 and intent['manifestHash'] == sha(root / 'manifest.json'),
            'SURFACE_SOURCE_MISMATCH')
    candidate = Path(intent['surface']['blendPath']).resolve()
    require(sha(candidate) == intent['surface']['blendHash'], 'AUTHORING_INPUT_DRIFT')
    review = validate(load(args.views),'surface-views')
    compiled = {}
    if args.compiled_verification:
        verification = Path(args.compiled_verification).resolve()
        evidence = validate(load(verification / 'evidence.json'),'evidence')
        check_pins(verification,evidence['files'])
        require(evidence['manifestHash'] == sha(root / 'manifest.json') and
                evidence['intentHash'] == sha(args.intent), 'COMPILED_REVIEW_SOURCE_MISMATCH')
        binding = load(verification / 'binding.json')
        require(binding['evidenceHash'] == sha(verification / 'evidence.json'), 'COMPILED_REVIEW_EVIDENCE_DRIFT')
        built = Path(binding['buildRoot'])
        check_pins(built,load(built / 'build.json')['files'])
        require(sha(built / 'plan.json') == evidence['planHash'] and
                sha(built / 'cs2/game/lab' / manifest['sourcePath']) == evidence['modelHash'],
                'COMPILED_REVIEW_BUILD_DRIFT')
        require((verification / 'reopened.blend').is_file(), 'COMPILED_REVIEW_MISSING')
        compiled = {'compiledBlend':str(verification / 'reopened.blend')}
    out = workspace(config,args.output,root,candidate.parent,archive_path=manifest['archive'])
    write(out / 'views.json',review)
    blender(config,'surface-review',out,baseline=str(root / 'baseline.blend'),
            returnedBlend=str(candidate), intent=intent, views=review, **compiled)
    require(sha(out / 'addons.json') == manifest['addonHash'], 'ADDON_DRIFT')
    write(out / 'review-evidence.json', {'operation':'sdk_surface_review@1','manifestHash':sha(root / 'manifest.json'),
            'blendHash':sha(candidate),'intentHash':sha(args.intent),'files':pins(out),
            'runtimeTested':False,'productionAdmission':False})
    return {'operation':'sdk_surface_review@1', 'evidence':str(out / 'review-evidence.json')}


def environment(config, which, out, manifest, decompiled, dmx, restore_source=False):
    base = out / which
    content, game = base / 'content/lab', base / 'game/lab'
    content.mkdir(parents=True)
    game.mkdir(parents=True)
    private_core = base / 'game/core'
    private_core.mkdir()
    sdk = Path(config['gameRoots'][which])
    shaders = list((sdk / 'core').glob('shaders_pc*.vpk'))
    require(shaders, 'SDK_SHADERS_MISSING')
    for p in shaders:
        shutil.copyfile(p, private_core / p.name)
    mounts = [os.path.relpath(p, base / 'game').replace('\\', '/') for p in
              (sdk / ('csgo' if which == 'cs2' else 'citadel'), sdk / 'core')]
    require(all((base / 'game' / p).is_dir() for p in mounts), 'SDK_MOUNT_MISSING')
    (game / 'gameinfo.gi').write_text('"GameInfo" { game "S2ModKit isolated SDK diagnostic" '
        'FileSystem { SearchPaths { Mod "lab" Write "lab" Game "lab" ' +
        ' '.join('Game ' + json.dumps(p) for p in mounts) + ' } } }')
    source_root = decompiled.parents[len(Path(manifest['sourcePath']).parent.parts)].parent
    shutil.copytree(source_root / 'dependencies', game, dirs_exist_ok=True)
    folder = Path(manifest['sourcePath']).parent
    dest = content / folder
    dest.mkdir(parents=True)
    shutil.copyfile(dmx, dest / 'sdkcontrol.dmx')
    doc = parse(decompiled.read_text())
    retained = [n for n in doc['rootNode']['children'] if n['_class'] in RETAINED]
    if restore_source and 'WeightListList' in manifest['sourceNodes']:
        from source_facts import restore_masks
        restore_masks(retained,(source_root / 'source-masks.log').read_text())
    # Copy only referenced decompiled animation inputs, preserving virtual paths.
    def copy_files(node):
        if isinstance(node, dict):
            if 'source_filename' in node or 'filename' in node:
                p = artifact_path(node.get('source_filename', node.get('filename')))
                require(p.endswith('.dmx') and (decompiled.parents[len(folder.parts)] / p).is_file(),
                        'ANIMATION_INPUT_MISSING:' + p)
                target = content / p
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(decompiled.parents[len(folder.parts)] / p, target)
            for v in node.values():
                copy_files(v)
        if isinstance(node, list):
            for v in node:
                copy_files(v)
    for node in retained:
        copy_files(node)
    source_data = (decompiled.parents[len(folder.parts)].parent / 'source-data.log').read_text()
    retained += [{'_class': 'GenericGameData', 'game_class': k, 'game_keys': v}
                 for k, v in metadata(source_data).items()]
    retained += [{'_class': 'RenderMeshList', 'children': [{'_class': 'RenderMeshFile', 'name': 'body',
                  'filename': (folder / 'sdkcontrol.dmx').as_posix()}]}]
    if restore_source:
        # Markup modifies its parent RenderMeshFile. A root node or wrapper is ignored.
        # Preserve source UNORM UVs rather than widening the strict UV tolerance.
        retained[-1]['children'][0]['children'] = [
            {'_class':'RenderMeshMarkup','high_high_precision_texcoords':True}]
    if which == 'cs2':
        retained += [{'_class': 'NmSkeletonList', 'children': [{'_class': 'NmSkeletonReference',
                      'filename': manifest['skeletonPath'], 'do_not_discard_skeleton_bones': False}]},
                     {'_class': 'AnimGraph2List', 'children': [
                         {'_class': 'DefaultAnimGraph2', 'filename': manifest['defaultGraph']},
                         {'_class': 'AnimGraph2', 'name': 'ui', 'filename': manifest['uiGraph']}]}]
    final = content / manifest['sourcePath'].removesuffix('_c')
    final.write_text(decompiled.read_text().splitlines()[0] + '\n' +
                     emit({'rootNode': {'_class': 'RootNode', 'children': retained}}))
    return final, game


def build(args):
    exp, plan, root, manifest, config = plan_source(args.export)
    out = workspace(config, args.output, root, exp, archive_path=manifest['archive'])
    doc = root / 'decompiled' / manifest['sourcePath'].removesuffix('_c')
    for which in ('csdk', 'cs2'):
        modeldoc, game = environment(config, which, out, manifest, doc, exp / 'sdkcontrol.dmx',
                                    restore_source=read_intent(exp / 'intent.json')['version'] == 4)
        text = run([config['tools'][which], '-i', modeldoc, '-game', game], out / (which + '-compile.log'))
        require('1 compiled, 0 failed' in text and (game / manifest['sourcePath']).is_file(), 'SDK_COMPILE_FAILED')
        if which == 'cs2':
            require('RESOURCE COMPILE WARNING' not in text and 'RESOURCE COMPILE ERROR' not in text,
                    'FINAL_RESOURCE_DIAGNOSTIC')
    shutil.copyfile(exp / 'plan.json', out / 'plan.json')
    write(out / 'build.json', {'planHash': sha(exp / 'plan.json'), 'files': pins(out)})
    return {'operation': 'sdk_build@1', 'modelHash': sha(out / 'cs2/game/lab' / manifest['sourcePath'])}


def verify(args):
    build_root = Path(args.build).resolve()
    built = load(build_root / 'build.json')
    check_pins(build_root, built['files'])
    plan = validate(load(build_root / 'plan.json'), 'plan')
    exp, _, root, manifest, config = plan_source(plan['exportRoot'])
    require(sha(exp / 'plan.json') == built['planHash'] == sha(build_root / 'plan.json'), 'BUILD_PLAN_DRIFT')
    out = workspace(config, args.output, root, exp, build_root, archive_path=manifest['archive'])
    model = build_root / 'cs2/game/lab' / manifest['sourcePath']
    actual = run([config['tools']['vrf'], '-i', model, '-b', 'DATA'], out / 'compiled-data.log')
    position, rotation = compare((root / 'source-data.log').read_text(), actual)
    if 'WeightListList' in manifest['sourceNodes']:
        masks = run([config['tools']['vrf'], '-i', model, '-b', 'ASEQ'], out / 'compiled-masks.log')
        compare_animation_masks((root / 'source-masks.log').read_text(), masks)
    actual_refs = refs(actual)
    require(all(x in actual_refs for x in (manifest['skeletonPath'], manifest['defaultGraph'],
                                          manifest['uiGraph'])), 'COMPILED_RUNTIME_REFERENCES_MISSING')
    require(set(actual_refs) <= set(manifest['sourceRefs']), 'NEW_UNDECLARED_REFERENCE')
    require(field(actual, 'm_refLODGroupMasks') == [255] and
            field(actual, 'm_lodGroupSwitchDistances') == [] and
            field(actual, 'm_meshGroups') == [], 'COMPILED_LOD_PROFILE_INVALID')
    blender(config, 'verify', out, source=str(model), authored=str(exp / 'authored.json'))
    require(sha(out / 'addons.json') == manifest['addonHash'], 'ADDON_DRIFT')
    evidence = {'operation': 'sdk_evidence@1', 'version': 1, 'planHash': sha(exp / 'plan.json'),
                'manifestHash': plan['manifestHash'], 'intentHash': plan['intentHash'],
                'modelHash': sha(model), 'metadataExact': True, 'boneMapExact': True,
                'maxBindPositionError': position, 'maxBindRotationErrorDegrees': rotation,
                'geometryReportHash': sha(out / 'geometry.json'), 'files': pins(out),
                'limitations': LIMITATIONS, 'runtimeTested': False, 'productionAdmission': False}
    validate(evidence, 'evidence')
    write(out / 'evidence.json', evidence)
    write(out / 'binding.json', {'buildRoot': str(build_root), 'evidenceHash': sha(out / 'evidence.json')})
    return {'operation': 'sdk_verify@1', 'modelHash': evidence['modelHash'], 'geometry': load(out / 'geometry.json'),
            'runtimeTested': False, 'productionAdmission': False}


def package(args):
    ver = Path(args.verification).resolve()
    evidence = validate(load(ver / 'evidence.json'), 'evidence')
    binding = load(ver / 'binding.json')
    require(binding['evidenceHash'] == sha(ver / 'evidence.json'), 'EVIDENCE_DRIFT')
    check_pins(ver, evidence['files'])
    build_root = Path(binding['buildRoot'])
    built = load(build_root / 'build.json')
    check_pins(build_root, built['files'])
    plan = validate(load(build_root / 'plan.json'), 'plan')
    _, _, root, manifest, config = plan_source(plan['exportRoot'])
    model = build_root / 'cs2/game/lab' / manifest['sourcePath']
    require(evidence['planHash'] == sha(build_root / 'plan.json') and evidence['modelHash'] == sha(model),
            'VERIFIED_BUILD_MISMATCH')
    # Recompute the parsed metadata/bind checks at publication; saved flags grant no permission.
    require(compare((root / 'source-data.log').read_text(), (ver / 'compiled-data.log').read_text()) ==
            (evidence['maxBindPositionError'], evidence['maxBindRotationErrorDegrees']), 'EVIDENCE_VALUES_CHANGED')
    out = workspace(config, args.output, root, plan['exportRoot'], build_root, ver,
                    archive_path=manifest['archive'])
    actual = run([config['tools']['vrf'], '-i', model, '-b', 'DATA'], out / 'publication-data.log')
    if 'WeightListList' in manifest['sourceNodes']:
        masks = run([config['tools']['vrf'], '-i', model, '-b', 'ASEQ'], out / 'publication-masks.log')
        compare_animation_masks((root / 'source-masks.log').read_text(), masks)
    require(compare((root / 'source-data.log').read_text(), actual) ==
            (evidence['maxBindPositionError'], evidence['maxBindRotationErrorDegrees']),
            'PUBLICATION_RESOURCE_CHANGED')
    recompute = out / 'geometry-recheck'
    recompute.mkdir()
    blender(config, 'verify', recompute, source=str(model), authored=str(Path(plan['exportRoot']) / 'authored.json'))
    require(sha(recompute / 'addons.json') == manifest['addonHash'], 'ADDON_DRIFT')
    require(load(recompute / 'geometry.json') == load(ver / 'geometry.json'), 'GEOMETRY_EVIDENCE_CHANGED')
    files = {manifest['sourcePath']: str(model),
             manifest['skeletonPath'] + '_c': str(root / 'dependencies' / (manifest['skeletonPath'] + '_c'))}
    write(out / 'entries.json', files)
    archive(config, 'package', [out / 'entries.json', out / 'diagnostic_dir.vpk'], out / 'native-package.log')
    run([config['tools']['vrf'], '-i', out / 'diagnostic_dir.vpk', '--vpk_verify'], out / 'vpk-checksums.log')
    expected = {manifest['sourcePath']: manifest['sourceHash'], manifest['skeletonPath'] + '_c': manifest['skeletonHash']}
    expected.update({x: sha(root / 'dependencies' / x) for x in pins(root / 'dependencies')})
    write(out / 'source-pins.json', expected)
    write(out / 'paths.json', list(files))
    report = {'operation': 'sdk_package@1', 'packageHash': sha(out / 'diagnostic_dir.vpk'),
              'evidenceHash': sha(ver / 'evidence.json'), 'verificationRoot': str(ver),
              'archive': manifest['archive'], 'files': pins(out), 'config': str(root / 'config.json'),
              'runtimeTested': False, 'productionAdmission': False}
    write(out / 'package.json', report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('probe')
    p.add_argument('--config', required=True)
    p.add_argument('--output', required=True)
    p = sub.add_parser('prepare')
    for key in ('config', 'archive', 'model', 'sha256', 'skeleton', 'default-graph', 'ui-graph', 'output'):
        p.add_argument('--' + key, required=True)
    p = sub.add_parser('repin')
    p.add_argument('--source', required=True)
    p = sub.add_parser('export')
    for key in ('source', 'intent', 'output'):
        p.add_argument('--' + key, required=True)
    for command, keys in (
            ('prepare-surface',('source','intent','output')),
            ('return-surface',('prepared','blend','sha256','output')),
            ('review-surface',('source','intent','views','output'))):
        p = sub.add_parser(command)
        for key in keys: p.add_argument('--' + key, required=True)
        if command == 'review-surface': p.add_argument('--compiled-verification')
    for command, key in (('build', 'export'), ('verify', 'build'), ('package', 'verification')):
        p = sub.add_parser(command)
        p.add_argument('--' + key, required=True)
        p.add_argument('--output', required=True)
    p = sub.add_parser('install')
    for key in ('package', 'addons', 'name', 'receipts'):
        p.add_argument('--' + key, required=True)
    p = sub.add_parser('rollback')
    p.add_argument('--receipt', required=True)
    p.add_argument('--addons', required=True)
    args = parser.parse_args()
    try:
        functions = {'probe': lambda a: probe(a.config, a.output), 'prepare': prepare, 'repin': repin,
                     'prepare-surface':surface_prepare, 'return-surface':surface_return,
                     'review-surface':surface_review,
                     'export': export, 'build': build, 'verify': verify, 'package': package,
                     'install': install, 'rollback': lambda a: lifecycle.rollback(a.receipt, a.addons)}
        result = functions[args.command](args)
        print(json.dumps({'ok': True, 'result': result}, allow_nan=False))
    except (ValueError, OSError, KeyError, IndexError, TypeError, TimeoutError) as error:
        print(json.dumps({'ok': False, 'error': str(error)}), file=sys.stderr)
        return 20
    return 0


def install(args):
    def preflight(report, package_root, addons):
        require(report['operation'] == 'sdk_package@1' and report['productionAdmission'] is False,
                'PACKAGE_PROFILE_INVALID')
        config = configuration(report['config'])
        logs = fresh(Path(args.receipts) / ('preflight-' + __import__('uuid').uuid4().hex))
        ver = Path(report['verificationRoot'])
        require(sha(ver / 'evidence.json') == report['evidenceHash'], 'PACKAGE_EVIDENCE_DRIFT')
        evidence = validate(load(ver / 'evidence.json'), 'evidence')
        check_pins(ver, evidence['files'])
        build_root = Path(load(ver / 'binding.json')['buildRoot'])
        plan = validate(load(build_root / 'plan.json'), 'plan')
        _, _, _, manifest, _ = plan_source(plan['exportRoot'])
        expected = {manifest['sourcePath']: evidence['modelHash'],
                    manifest['skeletonPath'] + '_c': manifest['skeletonHash']}
        write(logs / 'expected-entries.json', expected)
        archive(config, 'verify', [package_root / 'diagnostic_dir.vpk', logs / 'expected-entries.json'],
                logs / 'package-verify.log')
        archive(config, 'check-source', [report['archive'], package_root / 'source-pins.json'], logs / 'current-source.log')
        archive(config, 'collisions', [addons, package_root / 'paths.json'], logs / 'collisions.log')
    return lifecycle.install(args.package, args.addons, args.name, args.receipts, preflight)


if __name__ == '__main__':
    sys.exit(main())
