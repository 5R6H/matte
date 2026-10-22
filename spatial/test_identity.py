"""Shared, independently produced identity vectors; no model or third-party imports."""
import datetime as dt
import json
import os
from pathlib import Path
import re
import tempfile
import unittest

import identity


VECTORS = json.loads((Path(__file__).resolve().parents[1] / 'tests' / 'identity-vectors.json').read_text('utf-8'))


class IdentityTests(unittest.TestCase):
    def test_canonical_vectors(self):
        for case in VECTORS['canonical']:
            with self.subTest(case=case['name']):
                value = identity.parse_strict(case['json'])
                self.assertEqual(case['expected'], identity.canonical(value))
                self.assertEqual(case['sha256'], identity.sha256(identity.canonical(value).encode('utf-8')))

    def test_parser_rejections(self):
        for case in VECTORS['parseRejects']:
            with self.subTest(case=case['name']):
                with self.assertRaises(ValueError):
                    identity.parse_strict(case['json'])

    def test_canonical_rejections(self):
        for case in VECTORS['canonicalRejects']:
            with self.subTest(case=case['name']):
                with self.assertRaises(ValueError):
                    identity.canonical(identity.parse_strict(case['json']))
        for value in [1.5, True, None, '\ud800', '\udc00', {1: 'value'}, {'a': 1.0}]:
            with self.subTest(value=repr(value)):
                with self.assertRaises(ValueError):
                    identity.canonical(value)
        with self.assertRaises(ValueError):
            identity.parse_strict('"\ud800"')

    def test_recipe_vectors(self):
        for case in VECTORS['recipes']:
            with self.subTest(case=case['name']):
                if case['outcome'] == 'Invalid':
                    with self.assertRaises(ValueError):
                        identity.validate_recipe(identity.parse_strict(case['json']))
                else:
                    fields = identity.parse_strict(case['json'])
                    self.assertEqual(case['sha256'], identity.recipe_sha256(fields))
                    self.assertEqual(case['expected'], identity.canonical(fields))
                    self.assertEqual(bool(case['supported']), identity.supported(fields))

    def test_sidecar_vectors(self):
        for case in VECTORS['sidecars']:
            with self.subTest(case=case['name']), tempfile.TemporaryDirectory(prefix='matte-id-') as store:
                source, recipe = VECTORS['sourceSha'], VECTORS['recipeSha']
                location = case['location']
                if location == 'wrong-source':
                    source = '0' * 64
                if location == 'wrong-recipe':
                    recipe = '0' * 64
                folder = Path(store) / ('v2' if location == 'wrong-version-dir' else 'v1')
                folder /= source.upper() if location == 'uppercase-source' else source
                folder.mkdir(parents=True)
                artifact = folder / ((recipe.upper() if location == 'uppercase-recipe' else recipe) + '.spatial')
                sidecar = Path(str(artifact) + '.json')
                mode = case['mode']
                if mode != 'missing-artifact':
                    artifact.write_bytes(VECTORS['artifactText'].encode('utf-8'))
                if mode != 'missing-sidecar':
                    sidecar.write_bytes(b'\xff' if mode == 'bad-utf8' else case['json'].encode('utf-8'))
                handle = None
                if mode.startswith('locked-'):
                    if os.name != 'nt':
                        continue  # Mandatory on the target OS; Windows share-deny has no POSIX equivalent.
                    import ctypes
                    from ctypes import wintypes
                    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
                    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                                  wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
                    kernel.CreateFileW.restype = wintypes.HANDLE
                    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
                    locked = sidecar if mode == 'locked-sidecar' else artifact
                    handle = kernel.CreateFileW(str(locked), 0x80000000, 0, None, 3, 0, None)
                    self.assertNotEqual(ctypes.c_void_p(-1).value, handle, 'fixture lock failed')
                try:
                    self.assertEqual(case['expected'], identity.classify(sidecar, bool(case['verify'])))
                    if case['expected'] == 'Valid':
                        data = identity.parse_strict(case['json'])
                        self.assertEqual(identity.canonical(data), identity.sidecar_json(data))
                finally:
                    if handle is not None:
                        kernel.CloseHandle(handle)

    def test_names_and_boundaries(self):
        source, recipe = VECTORS['sourceSha'], VECTORS['recipeSha']
        store = Path.cwd() / 'store'
        artifact = identity.artifact_path(store, source, recipe)
        self.assertEqual(store / 'v1' / source / (recipe + '.spatial'), artifact)
        self.assertEqual(str(artifact) + '.json', str(identity.sidecar_path(store, source, recipe)))
        names = {identity.temp_name(artifact) for _ in range(32)}
        self.assertEqual(32, len(names))
        for name in names:
            self.assertRegex(name, '^' + re.escape(str(artifact)) + r'\.tmp-[0-9a-f]{16}$')
        now = dt.datetime(2026, 10, 22, 1, 2, 3, tzinfo=dt.timezone.utc)
        self.assertEqual(store / 'v1' / 'quarantine' / ('20261022T010203Z-' + source + '-' + recipe + '.spatial'),
                         identity.quarantine_path(store, source, recipe, now))
        root = Path.cwd().anchor
        # The independently calculated suffix lengths are 168 (quarantine) and 166 (sidecar temp), plus separator.
        for length in (258, 259, 260):
            for astral in (False, True):
                prefix = '\U0001f600' if astral else ''
                store = root + prefix + 's' * (length - 169 - len(root) - (2 if astral else 0))
                self.assertEqual(length > 259, identity.store_too_long(store), (length, astral))
        self.assertTrue(identity.store_too_long(root + 's' * 400))
        with self.assertRaises(ValueError):
            identity.artifact_path(store, source.upper(), recipe)
        with self.assertRaises(ValueError):
            identity.quarantine_path(store, source, recipe, dt.datetime(2026, 1, 1))


if __name__ == '__main__':
    unittest.main(verbosity=2)
