import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

SCRIPT = Path(__file__).parents[1] / 'scripts' / 'distribution.py'


class DistributionTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.exists(), 'distribution helper has not been implemented')
        spec = importlib.util.spec_from_file_location('distribution', SCRIPT)
        self.dist = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.dist)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def archive(self, version='1.2.3.4'):
        path = self.root / f'ASS-Fonts_{version}.zip'
        with zipfile.ZipFile(path, 'w') as z:
            z.writestr('meta.json', json.dumps({
                'guid': 'cb963c2d-9d76-48b1-9f90-5c0cd5b25eae',
                'name': 'ASS Fonts', 'version': version, 'targetAbi': '12.4.0.0',
                'description': 'Subset fonts', 'overview': 'Fonts',
                'owner': 'tokataka', 'category': 'General',
            }))
            z.writestr('Jellyfin.Plugin.AssFonts.dll', b'test fixture')
        return path

    def entry(self, version='1.2.3.4'):
        return self.dist.make_entry(self.archive(version), 'tokataka/jellyfin-plugin-assfonts',
                                    '2026-10-30T00:00:00Z', 'Release notes')

    def test_version_is_read_from_single_property(self):
        (self.root / 'Directory.Build.props').write_text(
            '<Project><PropertyGroup><Version>2.3.4.5</Version></PropertyGroup></Project>')
        self.assertEqual(self.dist.read_version(self.root), '2.3.4.5')

    def test_invalid_versions_fail_before_publishing(self):
        for version in ['1.2.3', '1.2.3.4-preview', '../bad', '1.2.3.999999', '01.2.3.4']:
            with self.subTest(version=version):
                (self.root / 'Directory.Build.props').write_text(
                    f'<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>')
                with self.assertRaises(ValueError):
                    self.dist.read_version(self.root)

    def test_catalog_points_at_immutable_version_and_actual_zip_checksum(self):
        path = self.archive()
        result = self.dist.make_entry(path, 'tokataka/jellyfin-plugin-assfonts',
                                      '2026-10-30T00:00:00Z', 'Release notes')
        version = result['versions'][0]
        self.assertEqual(version['checksum'], hashlib.md5(path.read_bytes()).hexdigest())
        self.assertEqual(version['sourceUrl'], 'https://github.com/tokataka/jellyfin-plugin-assfonts/releases/download/v1.2.3.4/ASS-Fonts_1.2.3.4.zip')
        self.assertEqual(version['targetAbi'], '12.4.0.0')
        self.assertNotIn('autoUpdate', result)

    def test_index_merge_preserves_other_plugins_and_orders_versions_numerically(self):
        old = self.entry('1.2.0.0')
        other = {'guid': 'another-plugin', 'versions': [{'version': '9.0.0.0'}]}
        merged = self.dist.merge_manifest([other, old], self.entry('1.10.0.0'))
        self.assertEqual(merged[0], other)
        self.assertEqual([v['version'] for v in merged[1]['versions']], ['1.10.0.0', '1.2.0.0'])

    def test_same_release_retry_is_idempotent(self):
        entry = self.entry()
        original = [entry]
        self.assertEqual(self.dist.merge_manifest(original, copy.deepcopy(entry)), original)

    def test_same_version_changed_payload_is_rejected(self):
        entry = self.entry()
        changed = copy.deepcopy(entry)
        changed['versions'][0]['checksum'] = '0' * 32
        with self.assertRaisesRegex(ValueError, 'immutable'):
            self.dist.merge_manifest([entry], changed)

    def test_archive_missing_plugin_dll_is_rejected(self):
        path = self.archive()
        with zipfile.ZipFile(path) as z:
            metadata = z.read('meta.json')
        with zipfile.ZipFile(path, 'w') as z:
            z.writestr('meta.json', metadata)
        with self.assertRaises(ValueError):
            self.dist.make_entry(path, 'tokataka/jellyfin-plugin-assfonts', '2026-10-30T00:00:00Z', '')

    def test_interrupted_draft_upload_is_replaced_before_retry(self):
        self.assertTrue(hasattr(self.dist, 'draft_asset_action'), 'draft upload recovery is missing')
        self.assertEqual(self.dist.draft_asset_action(
            [{'name': 'package.zip', 'state': 'starter'}], 'package.zip'), 'replace-incomplete')

    def test_completed_draft_upload_is_verified_not_overwritten(self):
        self.assertTrue(hasattr(self.dist, 'draft_asset_action'), 'draft upload recovery is missing')
        self.assertEqual(self.dist.draft_asset_action(
            [{'name': 'package.zip', 'state': 'uploaded'}], 'package.zip'), 'verify')

    def test_unknown_upload_state_fails_closed(self):
        self.assertTrue(hasattr(self.dist, 'draft_asset_action'), 'draft upload recovery is missing')
        with self.assertRaises(ValueError):
            self.dist.draft_asset_action([{'name': 'package.zip', 'state': 'unknown'}], 'package.zip')

    def test_missing_draft_asset_is_uploaded(self):
        self.assertTrue(hasattr(self.dist, 'draft_asset_action'), 'draft upload recovery is missing')
        self.assertEqual(self.dist.draft_asset_action([], 'package.zip'), 'upload')

    def test_downloaded_asset_matches_github_digest(self):
        path = self.archive()
        release = {'assets': [{'name': path.name, 'digest': 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest()}]}
        self.dist.verify_asset(path, release)
        path.write_bytes(path.read_bytes() + b'changed')
        with self.assertRaisesRegex(ValueError, 'SHA-256'):
            self.dist.verify_asset(path, release)

    def test_missing_github_digest_is_rejected(self):
        path = self.archive()
        for assets in [[], [{'name': path.name}], [{'name': path.name, 'digest': None}]]:
            with self.subTest(assets=assets), self.assertRaisesRegex(ValueError, 'SHA-256'):
                self.dist.verify_asset(path, {'assets': assets})


if __name__ == '__main__':
    unittest.main()
