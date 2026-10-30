"""Package a plugin and maintain a Jellyfin catalog without replacing releases."""
import argparse
import copy
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DLL = 'Jellyfin.Plugin.AssFonts.dll'


def version_tuple(value):
    if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', value):
        raise ValueError('Use a four-part numeric version, for example 1.0.1.0')
    parts = tuple(map(int, value.split('.')))
    if any(part > 65534 for part in parts):
        raise ValueError('Assembly version components must not exceed 65534')
    return parts


def read_version(root=ROOT):
    value = ET.parse(root / 'Directory.Build.props').findtext('.//Version', '').strip()
    version_tuple(value)
    return value


def version_bumped(before, root=ROOT):
    if not re.fullmatch(r'[0-9a-f]{40}|[0-9a-f]{64}', before):
        raise ValueError('Expected the commit ID from the push event')
    current = version_tuple(read_version(root))
    if set(before) == {'0'}:
        return True
    previous = subprocess.check_output(['git', 'show', f'{before}:Directory.Build.props'], cwd=root, text=True)
    old = ET.fromstring(previous).findtext('.//Version', '').strip()
    return current > version_tuple(old)


def package(root=ROOT):
    version = read_version(root)
    metadata = json.loads((root / 'plugin.json').read_text())
    metadata.update(version=version, status='Active')
    files = [root / 'src/bin/Release/net10.0' / DLL,
             root / 'LICENSE', root / 'THIRD-PARTY-NOTICES.txt']
    for path in files:
        if not path.is_file():
            raise ValueError(f'Missing package input: {path.name}; run the build first')
    target = root / f'artifacts/ASS-Fonts_{version}.zip'
    target.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        contents = {path.name: path.read_bytes() for path in files}
        contents['meta.json'] = (json.dumps(metadata, indent=2) + '\n').encode()
        for name, data in sorted(contents.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    return target


def make_entry(archive, repository, timestamp, changelog):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Expected owner/repository')
    with zipfile.ZipFile(archive) as zip_file:
        if DLL not in zip_file.namelist() or zip_file.getinfo(DLL).file_size == 0:
            raise ValueError('Plugin DLL missing from ZIP')
        metadata = json.loads(zip_file.read('meta.json'))
    version = metadata['version']
    version_tuple(version)
    if archive.name != f'ASS-Fonts_{version}.zip':
        raise ValueError('ZIP filename and embedded version disagree')
    entry = {key: metadata[key] for key in
             ['guid', 'name', 'description', 'overview', 'owner', 'category']}
    entry['versions'] = [{
        'version': version, 'changelog': changelog, 'targetAbi': metadata['targetAbi'],
        'sourceUrl': f'https://github.com/{repository}/releases/download/v{version}/{archive.name}',
        'checksum': hashlib.md5(archive.read_bytes()).hexdigest(),
        'timestamp': timestamp,
    }]
    return entry


def merge_manifest(current, entry):
    result = copy.deepcopy(current)
    existing = next((p for p in result if p['guid'].lower() == entry['guid'].lower()), None)
    if existing is None:
        return result + [copy.deepcopy(entry)]
    new_version = entry['versions'][0]
    old = next((v for v in existing['versions'] if v['version'] == new_version['version']), None)
    if old is not None:
        for key in ['checksum', 'sourceUrl', 'targetAbi']:
            if old[key] != new_version[key]:
                raise ValueError(f'Published version {old["version"]} is immutable ({key} differs)')
        return result
    existing['versions'].append(copy.deepcopy(new_version))
    existing['versions'].sort(key=lambda v: version_tuple(v['version']), reverse=True)
    if existing['versions'][0]['version'] == new_version['version']:
        existing.update({k: v for k, v in entry.items() if k != 'versions'})
    return result


def draft_asset_action(assets, name):
    asset = next((item for item in assets if item['name'] == name), None)
    if asset is None:
        return 'upload'
    if asset['state'] == 'uploaded':
        return 'verify'
    if asset['state'] == 'starter':
        return 'replace-incomplete'
    raise ValueError(f'Unknown draft asset state for {name}')


def verify_asset(archive, release, built_archive=None):
    asset = next((item for item in release['assets'] if item['name'] == archive.name), {})
    actual = 'sha256:' + hashlib.sha256(archive.read_bytes()).hexdigest()
    if asset.get('digest') != actual:
        raise ValueError('GitHub release asset SHA-256 is unavailable or does not match the downloaded ZIP')
    if built_archive is not None and archive.read_bytes() != built_archive.read_bytes():
        raise ValueError('Published asset does not match the tested build; publish a new version')


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('version')
    bump = commands.add_parser('version-bumped')
    bump.add_argument('--before', required=True)
    commands.add_parser('package')
    asset = commands.add_parser('draft-asset-action')
    asset.add_argument('--assets', type=Path, required=True)
    asset.add_argument('--name', required=True)
    verify = commands.add_parser('verify')
    verify.add_argument('--zip', type=Path, required=True)
    verify.add_argument('--release', type=Path, required=True)
    verify.add_argument('--built-zip', type=Path)
    merge = commands.add_parser('index')
    merge.add_argument('--zip', type=Path, required=True)
    merge.add_argument('--repository', required=True)
    merge.add_argument('--release', type=Path, required=True)
    merge.add_argument('--manifest', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'version':
        print(read_version())
    elif args.command == 'version-bumped':
        print(str(version_bumped(args.before)).lower())
    elif args.command == 'package':
        print(package())
    elif args.command == 'draft-asset-action':
        print(draft_asset_action(json.loads(args.assets.read_text())['assets'], args.name))
    elif args.command == 'verify':
        verify_asset(args.zip, json.loads(args.release.read_text()), args.built_zip)
    else:
        release = json.loads(args.release.read_text())
        entry = make_entry(args.zip, args.repository, release['publishedAt'], release['body'])
        current = json.loads(args.manifest.read_text()) if args.manifest.exists() else []
        merged = merge_manifest(current, entry)
        args.manifest.write_text(json.dumps(merged, indent=2, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
