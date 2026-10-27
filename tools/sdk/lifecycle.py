"""Receipt-owned experimental installation, separate from build/publication."""
import os
from pathlib import Path
import re
import uuid
from common import check_pins, load, require, sha, write


def install(package, addons, name, receipts, preflight):
    package, addons, receipts = map(lambda p: Path(p).resolve(), (package, addons, receipts))
    require(re.fullmatch(r'pak[0-9]{2}_dir\.vpk', name), 'INSTALL_NAME_INVALID')
    require(addons.is_dir() and receipts.is_dir(), 'INSTALL_ROOT_MISSING')
    report = load(package / 'package.json')
    check_pins(package, report['files'])
    candidate = package / 'diagnostic_dir.vpk'
    require(sha(candidate) == report['packageHash'], 'PACKAGE_HASH_CHANGED')
    target = addons / name
    require(not target.exists() and not target.is_symlink(), 'INSTALL_TARGET_OCCUPIED')
    preflight(report, package, addons)
    identity = uuid.uuid4().hex
    receipt = receipts / ('installation-' + identity + '.json')
    record = {'operation': 'sdk_install@1', 'id': identity, 'addons': str(addons),
              'name': name, 'packageHash': report['packageHash'],
              'packageRoot': str(package), 'packageReportHash': sha(package / 'package.json')}
    # Ownership is recorded first; a failed copy cannot replace an existing artifact.
    write(receipt, record)
    staging = addons / (name + '.sdk-' + identity + '.staging')
    copied = False
    try:
        with staging.open('xb') as output:
            copied = True
            with candidate.open('rb') as source:
                for chunk in iter(lambda: source.read(1048576), b''):
                    output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        require(sha(staging) == record['packageHash'], 'INSTALLED_HASH_MISMATCH')
        preflight(report, package, addons)
        require(not target.exists() and not target.is_symlink(), 'INSTALL_TARGET_OCCUPIED')
        if os.name == 'nt':
            staging.rename(target)
        else:
            os.link(staging, target)
            staging.unlink()
        copied = False
        require(sha(target) == record['packageHash'], 'INSTALLED_HASH_MISMATCH')
    except BaseException:
        # Only a just-created incomplete copy belongs to this attempt. Never remove an
        # occupied destination when exclusive creation failed.
        if copied:
            staging.unlink()
        raise
    return {'receipt': str(receipt), 'target': str(target), 'packageHash': record['packageHash']}


def rollback(receipt, addons):
    receipt, addons = Path(receipt).resolve(), Path(addons).resolve()
    record = load(receipt)
    require(set(record) == {'operation', 'id', 'addons', 'name', 'packageHash', 'packageRoot', 'packageReportHash'}
            and record['operation'] == 'sdk_install@1', 'RECEIPT_INVALID')
    require(re.fullmatch(r'[a-f0-9]{32}', record['id']) and
            re.fullmatch(r'pak[0-9]{2}_dir\.vpk', record['name']), 'RECEIPT_PATH_INVALID')
    require(Path(record['addons']).resolve() == addons, 'RECEIPT_ROOT_MISMATCH')
    target = addons / record['name']
    require(not target.is_symlink() and sha(target) == record['packageHash'], 'INSTALLED_ARTIFACT_CHANGED')
    disabled = addons / (record['name'] + '.sdk-' + record['id'] + '.disabled')
    require(not disabled.exists(), 'ROLLBACK_DESTINATION_EXISTS')
    if os.name == 'nt':
        target.rename(disabled)  # Windows refuses replacement of an existing destination.
    else:
        os.link(target, disabled)
        target.unlink()
    return {'disabled': str(disabled), 'receipt': str(receipt)}
