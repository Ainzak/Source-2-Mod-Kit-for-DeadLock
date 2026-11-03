import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import workflow
from common import canonical, pins, sha, validate
from settings import REPO, SettingsError, check_settings, load_settings, problems, sdk_config, settings_path

FIXTURES = HERE / 'tests/fixtures'


def touch(path, data=b'x'):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def machine(root):
    """A complete synthetic machine below root: every path exists."""
    csdk, cs2 = root / 'csdk/game', root / 'cs2/game'
    for game in (csdk, cs2):
        (game / 'core').mkdir(parents=True)
    value = {'operation': 's2mk_settings@1', 'version': 1,
             'tools': {'blender': str(touch(root / 'blender/blender.exe')), 'vrf': str(touch(root / 'vrf/vrf.exe')),
                       'csdk': str(touch(csdk / 'bin/rc.exe')), 'cs2': str(touch(cs2 / 'bin/rc.exe')),
                       'dotnet': str(touch(root / 'dotnet/dotnet.exe'))},
             'gameRoots': {'csdk': str(csdk), 'cs2': str(cs2)},
             'deadlock': {'archive': str(touch(root / 'deadlock/pak01_dir.vpk')), 'addons': str((root / 'deadlock/addons').resolve())},
             'blenderAddons': str(root / 'blender-addons'), 's2mod': str(touch(root / 'cli/s2mod.exe'))}
    (root / 'deadlock/addons').mkdir()
    (root / 'blender-addons').mkdir()
    return value


def save(root, value, name='settings.json'):
    path = root / name
    path.write_text(json.dumps(value), encoding='utf-8')
    return path


class SettingsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.addCleanup(self.temp.cleanup)

    def test_environment_overrides_default_location(self):
        with mock.patch.dict(os.environ, {'S2MK_SETTINGS': str(self.root / 'x.json')}):
            self.assertEqual(self.root / 'x.json', settings_path())
        with mock.patch.dict(os.environ):
            os.environ.pop('S2MK_SETTINGS', None)
            self.assertEqual(REPO / '.workspaces/settings.json', settings_path())

    def test_loads_the_file_named_by_the_environment_and_defaults_s2mod(self):
        value = machine(self.root)
        del value['s2mod']
        path = save(self.root, value)
        with mock.patch.dict(os.environ, {'S2MK_SETTINGS': str(path)}):
            loaded = load_settings()
            self.assertEqual(value['deadlock'], loaded['deadlock'])
            self.assertEqual(str(REPO / 'src/S2ModKit.Cli/bin/Release/net10.0/s2mod.exe'), loaded['s2mod'])
            self.assertEqual([x for x in problems(loaded) if not x.startswith('S2MOD_NOT_BUILT')], [])
        value = machine(self.root / 'again')
        self.assertEqual(value['s2mod'], load_settings(save(self.root, value, 'again.json'))['s2mod'])

    def test_missing_file_and_closed_contract(self):
        with self.assertRaises(ValueError) as caught:
            load_settings(self.root / 'absent.json')
        self.assertTrue(str(caught.exception).startswith('SETTINGS_MISSING:'))
        value = machine(self.root)
        for edit, code in ((lambda v: v.update(extra=1), 'CONTRACT_UNKNOWN_FIELD'), (lambda v: v.pop('deadlock'), 'CONTRACT_REQUIRED'),
                           (lambda v: v['tools'].pop('vrf'), 'CONTRACT_REQUIRED'), (lambda v: v.update(version=2), 'CONTRACT_ENUM'),
                           (lambda v: v['deadlock'].update(cache='x'), 'CONTRACT_UNKNOWN_FIELD')):
            broken = json.loads(json.dumps(value))
            edit(broken)
            with self.subTest(code=code), self.assertRaises(ValueError) as caught:
                load_settings(save(self.root, broken, code + '.json'))
            self.assertTrue(str(caught.exception).startswith(code), str(caught.exception))

    def test_check_passes_for_a_complete_machine(self):
        value = machine(self.root)
        self.assertEqual(value, check_settings(save(self.root, value)))

    def test_check_names_every_problem_not_only_the_first(self):
        value = machine(self.root)
        value['tools']['vrf'] = str(self.root / 'gone/vrf.exe')
        value['tools']['dotnet'] = 'dotnet.exe'
        value['deadlock']['archive'] = str(self.root / 'gone/pak01_dir.vpk')
        value['deadlock']['addons'] = str(self.root / 'gone/addons')
        value['blenderAddons'] = str(self.root / 'gone/blender-addons')
        value['gameRoots']['cs2'] = value['gameRoots']['csdk']
        value['s2mod'] = str(self.root / 'gone/s2mod.exe')
        with self.assertRaises(SettingsError) as caught:
            check_settings(save(self.root, value))
        text = caught.exception.problems
        self.assertEqual(str(caught.exception), 'SETTINGS_INVALID: ' + '; '.join(text))
        for expected in ('TOOL_MISSING:tools.vrf=', 'TOOL_MISSING:tools.dotnet=', 'PATH_NOT_ABSOLUTE:tools.dotnet=',
                         'ARCHIVE_MISSING:deadlock.archive=', 'ADDONS_FOLDER_MISSING:deadlock.addons=',
                         'BLENDER_ADDONS_MISSING:blenderAddons=', 'S2MOD_NOT_BUILT:s2mod=',
                         'SDK_ENVIRONMENTS_MUST_DIFFER:', 'SDK_EXECUTABLE_ENVIRONMENT_MISMATCH:tools.cs2='):
            self.assertTrue(any(x.startswith(expected) for x in text), expected + ' not in ' + repr(text))

    def test_sdk_config_is_the_config_contract_in_fixed_key_order(self):
        value = machine(self.root)
        config = sdk_config(value)
        self.assertEqual(list(json.loads((FIXTURES / 'config.json').read_text())), list(config))
        self.assertEqual(list(json.loads((FIXTURES / 'config.json').read_text())['tools']), list(config['tools']))
        validate(config, 'config')
        reordered = {**value, 'tools': dict(reversed(list(value['tools'].items())))}
        self.assertEqual(json.dumps(config), json.dumps(sdk_config(reordered)))
        with mock.patch.object(workflow, 'ARCHIVE', touch(self.root / 'ArchiveTool.dll')):
            self.assertEqual(config, workflow.checked_config(config))

    def test_prepare_without_config_derives_and_writes_config_json(self):
        value = machine(self.root)
        archive = touch(self.root / 'ArchiveTool.dll')
        out = self.root / 'prepared'
        args = mock.Mock(config=None, archive=None, output=str(out), model='models/a/a.vmdl_c', skeleton='models/a/a.vnmskel',
                         default_graph='g.vnmgraph', ui_graph='u.vnmgraph', sha256='0' * 64)
        with mock.patch.dict(os.environ, {'S2MK_SETTINGS': str(save(self.root, value))}), mock.patch.object(workflow, 'ARCHIVE', archive):
            with self.assertRaises((OSError, ValueError)):        # the fake dotnet cannot extract: stops after the config is written
                workflow.prepare(args)
        self.assertEqual((json.dumps(sdk_config(value), indent=2) + '\n').encode(), (out / 'config.json').read_bytes())
        again = self.root / 'prepared2'
        args.output = str(again)
        with mock.patch.dict(os.environ, {'S2MK_SETTINGS': str(save(self.root, value))}), mock.patch.object(workflow, 'ARCHIVE', archive):
            with self.assertRaises((OSError, ValueError)):
                workflow.prepare(args)
        self.assertEqual(sha(out / 'config.json'), sha(again / 'config.json'))

    def test_prepare_with_explicit_config_still_copies_it(self):
        value = machine(self.root)
        explicit = self.root / 'explicit.json'
        explicit.write_text(json.dumps(sdk_config(value), indent=1), encoding='utf-8')
        archive = touch(self.root / 'ArchiveTool.dll')
        out = self.root / 'prepared'
        args = mock.Mock(config=str(explicit), archive=str(self.root / 'a.vpk'), output=str(out), model='models/a/a.vmdl_c',
                         skeleton='models/a/a.vnmskel', default_graph='g.vnmgraph', ui_graph='u.vnmgraph', sha256='0' * 64)
        with mock.patch.dict(os.environ, {'S2MK_SETTINGS': str(self.root / 'absent.json')}), mock.patch.object(workflow, 'ARCHIVE', archive):
            with self.assertRaises((OSError, ValueError)):
                workflow.prepare(args)       # no settings file needed when both --config and --archive are given
        self.assertEqual(explicit.read_bytes(), (out / 'config.json').read_bytes())

    def test_prepare_arguments_config_and_archive_are_optional(self):
        seen = {}
        argv = ['workflow.py', 'prepare', '--model', 'm', '--sha256', 'x', '--skeleton', 's', '--default-graph', 'd',
                '--ui-graph', 'u', '--output', 'o']
        with mock.patch.object(sys, 'argv', argv), mock.patch.object(workflow, 'prepare', lambda a: seen.update(config=a.config, archive=a.archive) or {}):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, workflow.main())
        self.assertEqual({'config': None, 'archive': None}, seen)


class RepinTests(unittest.TestCase):
    """source_repin on synthetic sources: only this repository's own pins repin automatically."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.addCleanup(self.temp.cleanup)
        self.sdk = self.root / 'sdk'
        touch(self.sdk / 'adapter_a.py', b'a = 1\n')
        touch(self.sdk / 'schemas/v1/x.schema.json', b'{}')
        archive = touch(self.root / 'archive/ArchiveTool.dll', b'helper')
        touch(self.root / 'archive/Other.dll', b'other')
        machine_value = machine(self.root / 'machine')
        self.config = sdk_config(machine_value)
        for path in (mock.patch.object(workflow, 'HERE', self.sdk), mock.patch.object(workflow, 'ARCHIVE', archive)):
            path.start()
            self.addCleanup(path.stop)
        self.tools = machine_value['tools']
        self.source = self.root / 'source'
        self.source.mkdir()
        (self.source / 'config.json').write_text(json.dumps(self.config), encoding='utf-8')
        touch(self.source / 'baseline.json', b'{}')
        manifest = json.loads((FIXTURES / 'manifest.json').read_text())
        manifest.update(configHash=sha(self.source / 'config.json'), toolPins=workflow.tool_pins(self.config),
                        files=pins(self.source))
        self.write_manifest(manifest)

    def write_manifest(self, manifest):
        (self.source / 'manifest.json').write_bytes(canonical(manifest))

    def manifest_hash(self):
        return sha(self.source / 'manifest.json')

    def test_no_drift_changes_nothing(self):
        before = self.manifest_hash()
        _, _, _, repinned = workflow.source_repin(self.source)
        self.assertEqual([], repinned)
        self.assertEqual(before, self.manifest_hash())
        self.assertFalse((self.source / 'tool-repins.jsonl').exists())

    def test_own_pin_drift_is_repinned_with_a_backup_and_a_record(self):
        before = self.manifest_hash()
        (self.sdk / 'adapter_a.py').write_bytes(b'a = 2\n')
        touch(self.sdk / 'adapter_new.py', b'b = 1\n')
        (self.sdk / 'schemas/v1/x.schema.json').write_bytes(b'{"a":1}')
        (self.root / 'archive/Other.dll').write_bytes(b'other changed')
        with self.assertRaises(ValueError) as caught:
            workflow.source(self.source)               # the strict check keeps refusing
        self.assertEqual('TOOL_DRIFT', str(caught.exception))
        _, manifest, _, repinned = workflow.source_repin(self.source)
        self.assertEqual(['adapter:adapter_a.py', 'adapter:adapter_new.py', 'archive:Other.dll', 'schema:schemas/v1/x.schema.json'], repinned)
        self.assertEqual(workflow.tool_pins(self.config), manifest['toolPins'])
        self.assertTrue((self.source / ('manifest.' + before[:16] + '.json')).is_file())
        record = json.loads((self.source / 'tool-repins.jsonl').read_text().splitlines()[-1])
        self.assertEqual({'previousManifestHash': before, 'manifestHash': self.manifest_hash(), 'changed': repinned}, record)
        workflow.source(self.source)                    # strict check passes after the repin
        self.assertEqual([], workflow.source_repin(self.source)[3])

    def test_removed_own_file_is_repinned(self):
        (self.sdk / 'adapter_a.py').unlink()
        self.assertEqual(['adapter:adapter_a.py'], workflow.source_repin(self.source)[3])

    def test_external_tool_drift_refuses_and_names_the_tools(self):
        before = self.manifest_hash()
        Path(self.tools['blender']).write_bytes(b'new blender')
        Path(self.tools['vrf']).write_bytes(b'new vrf')
        (self.sdk / 'adapter_a.py').write_bytes(b'a = 2\n')              # own drift alongside does not make it acceptable
        with self.assertRaises(ValueError) as caught:
            workflow.source_repin(self.source)
        message = str(caught.exception)
        self.assertTrue(message.startswith('TOOL_DRIFT:blender,vrf:'), message)
        self.assertIn('workflow.py repin --source', message)
        self.assertNotIn('adapter', message.split(':')[1])
        self.assertEqual(before, self.manifest_hash())
        self.assertEqual([], list(self.source.glob('manifest.*.json')))
        self.assertFalse((self.source / 'tool-repins.jsonl').exists())

    def test_deliberate_repin_command_still_handles_external_tools(self):
        Path(self.tools['dotnet']).write_bytes(b'new dotnet')
        result = workflow.repin(mock.Mock(source=str(self.source)))
        self.assertEqual(['dotnet'], result['changed'])

    def test_config_and_input_drift_still_refuse_without_repinning(self):
        before = self.manifest_hash()
        (self.sdk / 'adapter_a.py').write_bytes(b'a = 2\n')
        (self.source / 'baseline.json').write_bytes(b'{"changed":1}')
        with self.assertRaises(ValueError) as caught:
            workflow.source_repin(self.source)
        self.assertTrue(str(caught.exception).startswith('INPUT_DRIFT:baseline.json'))
        (self.source / 'baseline.json').write_bytes(b'{}')
        (self.source / 'config.json').write_text(json.dumps({**self.config, 'version': 1}, indent=3), encoding='utf-8')
        with self.assertRaises(ValueError) as caught:
            workflow.source_repin(self.source)
        self.assertTrue(str(caught.exception) in ('CONFIG_DRIFT',) or str(caught.exception).startswith('INPUT_DRIFT:config.json'))
        self.assertEqual(before, self.manifest_hash())


if __name__ == '__main__':
    unittest.main()
