"""Machine settings (s2mk_settings@1): one private file names every tool, game and addon path of this machine."""
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True
from common import HERE, load, require, validate

REPO = HERE.parent.parent
S2MOD = 'src/S2ModKit.Cli/bin/Release/net10.0/s2mod.exe'


class SettingsError(ValueError):
    """Every missing or invalid entry, not just the first."""
    def __init__(self, problems):
        super().__init__('SETTINGS_INVALID: ' + '; '.join(problems))
        self.problems = problems


def settings_path():
    """S2MK_SETTINGS when set, else <repo>/.workspaces/settings.json (private, git-ignored)."""
    return Path(os.environ.get('S2MK_SETTINGS') or REPO / '.workspaces/settings.json')


def load_settings(path=None):
    """The schema-checked settings with the default s2mod filled in; paths are not touched (see check_settings)."""
    path = Path(path or settings_path())
    require(path.is_file(), 'SETTINGS_MISSING:' + str(path))
    value = validate(load(path), 'settings')
    value.setdefault('s2mod', str(REPO / S2MOD))
    return value


def problems(value):
    """Every path problem of settings, as CODE:entry=path strings (empty when all is well)."""
    bad = []
    def need(ok, code, name, path):
        if not ok:
            bad.append(code + ':' + name + '=' + str(path))
    entries = {'tools.' + k: p for k, p in value['tools'].items()}
    entries.update({'gameRoots.' + k: p for k, p in value['gameRoots'].items()})
    entries.update({'deadlock.' + k: p for k, p in value['deadlock'].items()})
    entries.update({'blenderAddons': value['blenderAddons'], 's2mod': value['s2mod']})
    for name, path in entries.items():
        need(Path(path).is_absolute(), 'PATH_NOT_ABSOLUTE', name, path)
    for name, path in value['tools'].items():
        need(Path(path).is_file(), 'TOOL_MISSING', 'tools.' + name, path)
    for kind, game in value['gameRoots'].items():
        need(Path(game).is_dir() and (Path(game) / 'core').is_dir(), 'SDK_ENVIRONMENT_MISSING', 'gameRoots.' + kind, game)
        need(Path(value['tools'][kind]).resolve().is_relative_to(Path(game).resolve()),
             'SDK_EXECUTABLE_ENVIRONMENT_MISMATCH', 'tools.' + kind, value['tools'][kind])
    need(Path(value['gameRoots']['csdk']).resolve() != Path(value['gameRoots']['cs2']).resolve(),
         'SDK_ENVIRONMENTS_MUST_DIFFER', 'gameRoots', value['gameRoots']['csdk'])
    need(Path(value['deadlock']['archive']).is_file(), 'ARCHIVE_MISSING', 'deadlock.archive', value['deadlock']['archive'])
    need(Path(value['deadlock']['addons']).is_dir(), 'ADDONS_FOLDER_MISSING', 'deadlock.addons', value['deadlock']['addons'])
    need(Path(value['blenderAddons']).is_dir(), 'BLENDER_ADDONS_MISSING', 'blenderAddons', value['blenderAddons'])
    need(Path(value['s2mod']).is_file(), 'S2MOD_NOT_BUILT', 's2mod', value['s2mod'])
    return bad


def check_settings(path=None):
    """load_settings plus every path check; raises SettingsError listing all problems."""
    value = load_settings(path)
    bad = problems(value)
    if bad:
        raise SettingsError(bad)
    return value


def sdk_config(value):
    """The sdk_config@1 a prepared source keeps, in a fixed key order."""
    return {'operation': 'sdk_config@1', 'version': 1,
            'tools': {k: value['tools'][k] for k in ('blender', 'vrf', 'csdk', 'cs2', 'dotnet')},
            'gameRoots': {k: value['gameRoots'][k] for k in ('csdk', 'cs2')}}
