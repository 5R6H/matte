"""Publication/locking tests use only the stdlib and fake generation; no GPU packages required."""
import contextlib
import ctypes
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import types
import unittest
from unittest import mock

import identity
import matte_sharp
import publish


def fields(width=2560):
    return matte_sharp.recipe_for('0' * 64, width, 1600)


def fake_make(data, temp):
    Path(temp).write_bytes(b'artifact:' + data)


def child(*args):
    return subprocess.Popen([sys.executable, str(Path(__file__).resolve()), *map(str, args)],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')


def stop_child(process):
    if process.poll() is None:
        process.kill()
    process.communicate(timeout=5)


@contextlib.contextmanager
def deny_read(path, share=0):
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path), 0x80000000, share, None, 3, 0, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        yield
    finally:
        kernel.CloseHandle(handle)


class PublishTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='matte-pub-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.store, self.lock = self.root / 'store', self.root / 'stable' / 'store.lock'
        self.photo = self.root / 'photo.jpg'
        self.photo.write_bytes(b'original-photo')
        self.lines, self.made = [], []

    def make(self, data, temp):
        self.made.append(data)
        fake_make(data, temp)

    def run_photos(self, photos=None, recipe=None, make=None, hook=None):
        return publish.run(photos or [self.photo], self.store, recipe or fields(), self.lock,
                           make or self.make, emit=self.lines.append, timeout=3, hook=hook)

    def paths(self, data=b'original-photo', recipe=None):
        args = (self.store, identity.sha256(data), identity.recipe_sha256(recipe or fields()))
        return identity.artifact_path(*args), identity.sidecar_path(*args)

    def test_publication_order(self):
        artifact, sidecar = self.paths()
        steps = []
        def hook(step, path):
            steps.append(step)
            if step == 'temp_written':
                self.assertFalse(artifact.exists())
                self.assertFalse(sidecar.exists())
            if step == 'artifact_renamed':
                self.assertTrue(artifact.exists())
                self.assertFalse(sidecar.exists(), 'sidecar published before artifact event')
            if step == 'sidecar_temp_written':
                self.assertTrue(artifact.exists(), 'sidecar prepared before artifact')
                self.assertFalse(sidecar.exists())
                self.assertEqual(path.read_bytes().decode(), identity.canonical(identity.parse_strict(path.read_text())))
        self.assertEqual(0, self.run_photos(hook=hook))
        self.assertEqual(['temp_written', 'artifact_renamed', 'sidecar_temp_written'], steps)
        self.assertEqual('Valid', identity.classify(sidecar, True))
        self.assertEqual(['- Processing 1 valid image files -', f'Predicting gaussians for {self.photo}...', 'Saved: photo.jpg'], self.lines)
        self.assertFalse((self.store / 'photo.spatial').exists())

    def mark_bad(self):
        artifact, sidecar = self.paths()
        meta = identity.parse_strict(sidecar.read_text())
        digest = meta['artifact']['sha256']
        records = {'version': 1, 'artifacts': {digest: {
            'sourceSha': meta['source']['sha256'], 'recipeSha': meta['recipe']['sha256'],
            'name': self.photo.name, 'reason': 'Jpeg'}}}
        with publish.StoreLock(self.lock):
            (self.store / 'v1' / 'bad.json').write_text(json.dumps(records), encoding='utf-8')
        return digest

    def test_bad_equal_bytes_fail_without_saved_or_retry_loop(self):
        self.assertEqual(0, self.run_photos())
        artifact, sidecar = self.paths()
        before = artifact.read_bytes(), sidecar.read_bytes()
        self.mark_bad()
        # Sidecar metadata (or its serialization) cannot rehabilitate content.
        sidecar.write_bytes(before[1] + b'\n')
        self.assertTrue(publish.needs(self.store, identity.sha256(self.photo.read_bytes()), identity.recipe_sha256(fields())))
        self.lines.clear(); self.made.clear()
        self.assertEqual(1, self.run_photos())
        self.assertEqual([b'original-photo'], self.made)
        self.assertIn('Failed: photo.jpg: regenerated file is still unreadable', self.lines)
        self.assertFalse(any(line.startswith('Saved:') for line in self.lines))
        self.assertEqual(before[0], artifact.read_bytes())
        self.assertFalse(list((self.store / 'v1').rglob('*.tmp-*')))

    def test_bad_pair_quarantines_then_different_bytes_publish(self):
        self.assertEqual(0, self.run_photos())
        artifact, sidecar = self.paths()
        before = artifact.read_bytes(), sidecar.read_bytes()
        digest = self.mark_bad(); self.lines.clear()
        def make(data, temp):
            Path(temp).write_bytes(b'repaired:' + data)
        self.assertEqual(0, self.run_photos(make=make))
        self.assertIn('Saved: photo.jpg', self.lines)
        self.assertEqual(b'repaired:original-photo', artifact.read_bytes())
        quarantined = list((self.store / 'v1' / 'quarantine').iterdir())
        self.assertEqual(2, len(quarantined))
        self.assertEqual(set(before), {p.read_bytes() for p in quarantined})
        self.assertIn(digest, publish.bad_records(self.store), 'old digest was rehabilitated')
        self.assertFalse(publish.needs(self.store, identity.sha256(self.photo.read_bytes()), identity.recipe_sha256(fields())))

    def test_bad_publication_check_sees_rejection_added_during_generation(self):
        # Start absent; another writer publishes a Valid but rejected pair before this publication commits.
        def hook(step, path):
            if step == 'temp_written':
                publish.publish_one(self.photo, self.store, fields(), self.lock, fake_make)
                self.mark_bad()
        self.assertEqual(0, self.run_photos(make=lambda data, temp: Path(temp).write_bytes(b'new'), hook=hook))
        artifact, sidecar = self.paths()
        self.assertEqual(b'new', artifact.read_bytes(), 'publication discarded repair for an occupied bad pair')
        self.assertEqual(2, len(list((self.store / 'v1' / 'quarantine').iterdir())))

    def test_bad_digest_rejected_even_after_matte_quarantined_destination(self):
        self.assertEqual(0, self.run_photos()); self.mark_bad()
        artifact, sidecar = self.paths()
        with publish.StoreLock(self.lock):
            publish._quarantine(self.store, identity.sha256(self.photo.read_bytes()), identity.recipe_sha256(fields()), artifact, sidecar, [])
        self.lines.clear()
        self.assertEqual(1, self.run_photos())
        self.assertIn('Failed: photo.jpg: regenerated file is still unreadable', self.lines)
        self.assertFalse(artifact.exists()); self.assertFalse(sidecar.exists())

    def test_bad_pair_held_defers_both_files_and_retries_only_on_next_run(self):
        self.assertEqual(0, self.run_photos()); self.mark_bad()
        artifact, sidecar = self.paths()
        before = artifact.read_bytes(), sidecar.read_bytes()
        with deny_read(artifact, share=1):  # Playback permits reads, but neither writes nor renames.
            self.assertEqual(1, self.run_photos(make=lambda data, temp: Path(temp).write_bytes(b'new')))
            self.assertEqual(before[0], artifact.read_bytes())
        self.assertEqual(before[1], sidecar.read_bytes())
        self.assertEqual(0, self.run_photos(make=lambda data, temp: Path(temp).write_bytes(b'new')))

    def test_corrupt_bad_cache_is_empty_and_does_not_block_existing_pair(self):
        self.assertEqual(0, self.run_photos())
        path = self.store / 'v1' / 'bad.json'
        for content in ('{bad', '{}', 'null', '{"version":1,"artifacts":{"bad":{}}}'):
            path.write_text(content)
            self.assertEqual({}, publish.bad_records(self.store))
            self.made.clear()
            self.assertEqual(0, self.run_photos())
            self.assertEqual([], self.made)

    def test_process_death_at_each_publication_step(self):
        for step in ('temp_written', 'artifact_renamed', 'sidecar_temp_written'):
            with self.subTest(step=step):
                self.store = self.root / step
                prior = self.root / 'prior.jpg'; prior.write_bytes(b'prior')
                self.assertEqual(0, self.run_photos([prior]))
                previous = {p: p.read_bytes() for p in (self.store / 'v1').rglob('*') if p.is_file()}
                process = child('--crash', step, self.store, self.photo, self.lock)
                stdout, stderr = process.communicate(timeout=8)
                self.assertEqual(73, process.returncode, stdout + stderr)
                artifact, sidecar = self.paths()
                self.assertNotEqual('Valid', identity.classify(sidecar, True))
                for path, data in previous.items():
                    self.assertEqual(data, path.read_bytes(), 'earlier pair changed')
                self.assertEqual(0, self.run_photos())
                self.assertEqual('Valid', identity.classify(sidecar, True))
                self.assertFalse(list((self.store / 'v1').rglob('*.tmp-*')))

    def test_duplicate_photos_and_existing_pair(self):
        copy = self.root / 'copy.jpg'; copy.write_bytes(self.photo.read_bytes())
        self.store.mkdir(); legacy = self.store / 'photo.spatial'; legacy.write_bytes(b'legacy')
        self.assertEqual(0, self.run_photos([self.photo, copy]))
        self.assertEqual([b'original-photo'], self.made)
        self.assertEqual('- Processing 2 valid image files -', self.lines[0])
        self.assertEqual(2, sum(line.startswith('Saved:') for line in self.lines))
        self.lines.clear()
        self.assertEqual(0, self.run_photos())
        self.assertEqual(1, len(self.made))
        self.assertEqual('- Processing 0 valid image files -', self.lines[0])
        self.assertIn('Saved: photo.jpg', self.lines)
        self.assertEqual(b'legacy', legacy.read_bytes())

    def test_needs_includes_recipe(self):
        self.assertEqual(0, self.run_photos())
        old = self.paths()[1].read_bytes()
        self.assertEqual(0, self.run_photos(recipe=fields(1280)))
        self.assertEqual(2, len(self.made), 'different recipe incorrectly reused')
        self.assertEqual(old, self.paths()[1].read_bytes())
        self.assertEqual('Valid', identity.classify(self.paths(recipe=fields(1280))[1], True))

    def test_pending_library_makes_no_copies_outside_store(self):
        photos = [self.root / ('photo%d.jpg' % i) for i in range(8)]
        for i, photo in enumerate(photos):
            photo.write_bytes(('photo-%d' % i).encode())
        write = Path.write_bytes
        def write_inside_store(path, data):
            self.assertTrue(path.is_relative_to(self.store), 'photo copied outside the store')
            return write(path, data)
        with contextlib.ExitStack() as stack:
            for name in ('TemporaryDirectory', 'TemporaryFile', 'NamedTemporaryFile', 'mkdtemp', 'mkstemp'):
                stack.enter_context(mock.patch.object(tempfile, name, side_effect=AssertionError('photo spooling is forbidden')))
            stack.enter_context(mock.patch.object(Path, 'write_bytes', write_inside_store))
            self.assertEqual(0, self.run_photos(photos))
        self.assertEqual(8, len(self.made))
        self.assertEqual('- Processing 8 valid image files -', self.lines[0])
        self.assertEqual(8, sum(line.startswith('Saved:') for line in self.lines))
        self.assertCountEqual(photos + [self.photo, self.store, self.lock.parent], self.root.iterdir())

    def test_photo_edited_between_scan_and_generation(self):
        def emit(line):
            self.lines.append(line)
            if line.startswith('- Processing'):
                self.photo.write_bytes(b'replaced-photo')
        self.assertEqual(0, publish.run([self.photo], self.store, fields(), self.lock, self.make, emit=emit))
        self.assertEqual([b'replaced-photo'], self.made)
        artifact, sidecar = self.paths(b'replaced-photo')
        self.assertTrue(sidecar.exists(), 'publication used the scanned digest instead of the fresh digest')
        self.assertEqual('Valid', identity.classify(sidecar, True))
        self.assertEqual(identity.sha256(b'replaced-photo'), identity.parse_strict(sidecar.read_text())['source']['sha256'])
        self.assertEqual(b'artifact:replaced-photo', artifact.read_bytes())
        self.assertFalse(self.paths()[0].exists())
        self.assertIn('Saved: photo.jpg', self.lines)

    def test_photo_edited_to_existing_identity_reuses_pair(self):
        other = self.root / 'other.jpg'; other.write_bytes(b'existing')
        self.assertEqual(0, self.run_photos([other]))
        def emit(line):
            if line.startswith('- Processing'):
                self.photo.write_bytes(b'existing')
            self.lines.append(line)
        self.assertEqual(0, publish.run([self.photo], self.store, fields(), self.lock, self.make, emit=emit))
        self.assertEqual([b'existing'], self.made, 'needs was not checked for the fresh digest')
        self.assertFalse(self.paths()[0].exists())
        self.assertIn('Saved: photo.jpg', self.lines)

    def test_source_changed_after_generation_read(self):
        original_read = Path.read_bytes
        reads = []
        def read(path):
            data = original_read(path)
            if path == self.photo:
                reads.append(data)
                if len(reads) == 2:
                    self.photo.write_bytes(b'replaced-photo')
            return data
        with mock.patch.object(Path, 'read_bytes', read):
            self.assertEqual(0, self.run_photos())
        self.assertEqual([b'original-photo'], self.made, 'decoded bytes were reread from the photo path')
        artifact, sidecar = self.paths()
        self.assertEqual(b'artifact:original-photo', artifact.read_bytes())
        metadata = identity.parse_strict(sidecar.read_text())
        self.assertEqual(identity.sha256(b'original-photo'), metadata['source']['sha256'])
        self.assertEqual({'sha256', 'size', 'name'}, set(metadata['source']))
        self.assertNotIn(str(self.photo), sidecar.read_text())

    def test_photo_unreadable_after_scan_is_failed(self):
        def emit(line):
            self.lines.append(line)
            if line.startswith('- Processing'):
                self.photo.unlink()
        self.assertEqual(1, publish.run([self.photo], self.store, fields(), self.lock, self.make, emit=emit))
        self.assertIn('Failed: photo.jpg: unreadable', self.lines)
        self.assertFalse(any(line.startswith('Saved:') for line in self.lines))
        self.assertFalse(self.made)

    def test_unreadable_photo_is_failed(self):
        self.photo.unlink()
        self.assertEqual(1, self.run_photos())
        self.assertIn('Failed: photo.jpg: unreadable', self.lines)
        self.assertFalse(any(line.startswith('Saved:') for line in self.lines))
        self.assertFalse(self.made)

    def test_unsupported_and_unreadable_destinations_are_untouched(self):
        artifact, sidecar = self.paths(); artifact.parent.mkdir(parents=True)
        artifact.write_bytes(b'prior artifact'); sidecar.write_text('{"version":2}', encoding='utf-8')
        self.assertEqual(1, self.run_photos())
        self.assertIn('Failed: photo.jpg: conflict with a newer format', self.lines)
        with deny_read(sidecar):
            self.assertEqual(1, self.run_photos())
            self.assertIn('Failed: photo.jpg: store busy or unreadable', self.lines)
        self.assertEqual(b'prior artifact', artifact.read_bytes())
        self.assertEqual('{"version":2}', sidecar.read_text())
        self.assertFalse(self.made)
        self.assertFalse((self.store / 'v1' / 'quarantine').exists())

    def test_invalid_pair_orphan_and_stray_sidecar_recovery(self):
        for mode in ('invalid', 'orphan', 'stray'):
            with self.subTest(mode=mode):
                self.store = self.root / mode
                self.assertEqual(0, self.run_photos())
                artifact, sidecar = self.paths()
                if mode == 'stray':
                    artifact.unlink()
                else:
                    artifact.write_bytes(b'damaged')
                if mode == 'orphan':
                    sidecar.unlink()
                prior = [p.read_bytes() for p in (artifact, sidecar) if p.exists()]
                self.assertEqual(0, self.run_photos())
                self.assertEqual('Valid', identity.classify(sidecar, True))
                recovered = [p.read_bytes() for p in (self.store / 'v1' / 'quarantine').iterdir()]
                self.assertCountEqual(prior, recovered)

    def test_failed_publication_restores_quarantined_files(self):
        artifact, sidecar = self.paths(); artifact.parent.mkdir(parents=True)
        artifact.write_bytes(b'earlier'); sidecar.write_bytes(b'invalid-json')
        def fail(step, path):
            if step == 'sidecar_temp_written':
                raise OSError('injected failure')
        self.assertEqual(1, self.run_photos(hook=fail))
        self.assertEqual(b'earlier', artifact.read_bytes())
        self.assertEqual(b'invalid-json', sidecar.read_bytes())
        self.assertFalse(list((self.store / 'v1').rglob('*.tmp-*')))

    def test_first_valid_pair_wins_after_generation(self):
        winner = {}
        def competing(data, temp):
            fake_make(data, temp)
            publish.publish_one(self.photo, self.store, fields(), self.lock,
                                lambda data, path: path.write_bytes(b'winner'))
            winner['sidecar'] = self.paths()[1].read_bytes()
        self.assertEqual(0, self.run_photos(make=competing))
        self.assertEqual(b'winner', self.paths()[0].read_bytes())
        self.assertEqual(winner['sidecar'], self.paths()[1].read_bytes())

    def test_rollback_continues_and_preserves_original_error(self):
        for fail_unlink in (False, True):
            with self.subTest(fail_unlink=fail_unlink):
                self.store = self.root / str(fail_unlink)
                artifact, sidecar = self.paths(); artifact.parent.mkdir(parents=True)
                artifact.write_bytes(b'earlier'); sidecar.write_bytes(b'invalid-json')
                original = RuntimeError('original publication failure')
                undo, cleanup = [], []
                rename, unlink = publish.rename_new, Path.unlink
                def fail(step, path):
                    if step == 'sidecar_temp_written':
                        raise original
                def rename_failing(source, dest):
                    if source.parent.name == 'quarantine':
                        undo.append(dest)
                        if dest == sidecar:
                            raise OSError('restore failed')
                    rename(source, dest)
                def unlink_failing(path, *args, **kwargs):
                    if path == artifact and fail_unlink:
                        raise OSError('unlink failed')
                    if '.tmp-' in path.name:
                        cleanup.append(path)
                        if path.name.startswith(sidecar.name):
                            raise OSError('temp cleanup failed')
                    return unlink(path, *args, **kwargs)
                with mock.patch.object(publish, 'rename_new', rename_failing), mock.patch.object(Path, 'unlink', unlink_failing):
                    with self.assertRaises(RuntimeError) as caught:
                        publish.publish_one(self.photo, self.store, fields(), self.lock, self.make, hook=fail)
                self.assertIs(original, caught.exception, 'rollback replaced the original exception')
                self.assertEqual([sidecar, artifact], undo, 'rollback stopped after an undo failure')
                self.assertEqual(2, len(cleanup), 'temp cleanup stopped after an undo failure')
                if not fail_unlink:
                    self.assertEqual(b'earlier', artifact.read_bytes())
                else:
                    self.assertIn(b'earlier', [p.read_bytes() for p in (self.store / 'v1' / 'quarantine').iterdir()])

    def test_rename_never_overwrites_final(self):
        source, final = self.root / 'temp', self.root / 'final'
        source.write_bytes(b'new'); final.write_bytes(b'earlier')
        with self.assertRaises(FileExistsError):
            publish.rename_new(source, final)
        self.assertEqual(b'earlier', final.read_bytes())
        self.assertEqual(b'new', source.read_bytes())

    def test_publication_waits_for_other_process(self):
        held = []
        def make(data, temp):
            fake_make(data, temp)
            process = child('--hold-lock', self.lock, 800)
            held.append(process)
            self.assertEqual('LOCKED', process.stdout.readline().strip())
            held.append(time.monotonic())
        try:
            self.assertEqual(0, self.run_photos(make=make))
            self.assertGreaterEqual(time.monotonic() - held[1], 0.7, 'publication bypassed the held lock')
            self.assertEqual('Valid', identity.classify(self.paths()[1], True))
        finally:
            if held:
                stop_child(held[0])

    def test_start_cleanup_preserves_open_temps_and_legacy(self):
        folder = self.store / 'v1' / 'loose'; folder.mkdir(parents=True)
        stale, active = folder / 'stale.tmp-1', folder / 'active.tmp-2'
        stale.write_bytes(b'stale')
        legacy = self.store / 'legacy.tmp-3'; legacy.write_bytes(b'legacy')
        with active.open('xb') as stream:
            stream.write(b'active'); stream.flush()
            def hook(step, path):
                if step == 'temp_written':
                    with publish.StoreLock(self.lock):
                        publish.cleanup_temps(self.store)
                    self.assertTrue(path.exists(), 'live generation temp deleted')
            self.assertEqual(0, self.run_photos(hook=hook))
            self.assertTrue(active.exists())
        self.assertFalse(stale.exists())
        self.assertEqual(b'legacy', legacy.read_bytes())

    def test_second_run_fails_fast_without_touching_first_temps(self):
        process = child('--paused-run', self.store, self.photo, self.lock)
        try:
            self.assertEqual('RUNNING', process.stdout.readline().strip())
            temps = {p: p.read_bytes() for p in (self.store / 'v1').rglob('*.tmp-*')}
            self.assertEqual(2, len(temps), 'first run must leave an open and a closed temp')
            started = time.monotonic()
            with mock.patch.object(publish, 'cleanup_temps', side_effect=AssertionError('cleanup ran without generator.lock')):
                self.assertEqual(1, self.run_photos())
            self.assertLess(time.monotonic() - started, 1, 'second run did not fail fast')
            self.assertEqual(['Failed: generation: another generation is running'], self.lines)
            self.assertEqual(temps, {p: p.read_bytes() for p in (self.store / 'v1').rglob('*.tmp-*')})
            self.assertFalse(self.made)
            stdout, stderr = process.communicate('continue\n', timeout=8)
            self.assertEqual(0, process.returncode, stdout + stderr)
            self.assertEqual(0, self.run_photos())  # Run lock was released; stale closed temp is now removable.
            self.assertFalse(list((self.store / 'v1').rglob('*.tmp-*')))
        finally:
            stop_child(process)

    def test_cleanup_holds_generator_lock(self):
        cleanup = publish.cleanup_temps
        checked = []
        def check(store):
            with self.assertRaises(TimeoutError, msg='cleanup ran without generator.lock'):
                with publish.StoreLock(self.lock.with_name('generator.lock'), 0):
                    pass
            checked.append(True)
            cleanup(store)
        with mock.patch.object(publish, 'cleanup_temps', check):
            self.assertEqual(0, self.run_photos())
        self.assertEqual([True], checked)

    def test_run_lock_covers_scan_and_is_released_on_error(self):
        def photos():
            with self.assertRaises(TimeoutError):
                with publish.StoreLock(self.lock.with_name('generator.lock'), 0):
                    pass
            raise ValueError('scan failed')
            yield self.photo
        with self.assertRaisesRegex(ValueError, 'scan failed'):
            publish.run(photos(), self.store, fields(), self.lock, self.make)
        self.assertEqual(0, self.run_photos())


class RecipeTests(unittest.TestCase):
    def test_constants_pinned_to_revision(self):
        # Oracle computed independently from a literal dict with stdlib json.dumps + hashlib, not identity.canonical.
        pins = {'matte-sharp/1': 'ee7e2ddca559f6b671007304f88dcb3380aaf51bfdb86b92c3db9fd6798d8eae'}
        actual = identity.sha256(identity.canonical(matte_sharp.OUTPUT_CONSTANTS).encode('utf-8'))
        self.assertEqual(pins.get(matte_sharp.GENERATOR), actual,
                         'changed an output constant: bump the generator revision')

    def test_describe_without_torch_importable(self):
        with tempfile.TemporaryDirectory() as root:
            checkpoint = Path(root) / 'weights'; checkpoint.write_bytes(b'checkpoint')
            script = """import sys,importlib.abc
sys.path.insert(0,sys.argv[1])
class Block(importlib.abc.MetaPathFinder):
 def find_spec(self,fullname,*args):
  if fullname.split('.')[0] in ('torch','numpy','PIL','sharp','exporter','spatial_photos'): raise ImportError('heavy import forbidden: '+fullname)
sys.meta_path.insert(0,Block())
import matte_sharp
raise SystemExit(matte_sharp.main(['--describe-recipe','--checkpoint',sys.argv[2],'--width','1280','--height','720']))
"""
            result = subprocess.run([sys.executable, '-S', '-c', script, str(Path(__file__).parent), str(checkpoint)],
                                    capture_output=True, text=True, timeout=8)
            self.assertEqual(0, result.returncode, result.stderr)
            lines = result.stdout.splitlines(); self.assertEqual(2, len(lines))
            recipe = identity.parse_strict(lines[0].removeprefix('Recipe: '))
            self.assertEqual(identity.sha256(b'checkpoint'), recipe['model'])
            self.assertEqual((1280, 720), (recipe['width'], recipe['height']))
            self.assertEqual('RecipeSha: ' + identity.recipe_sha256(recipe), lines[1])

    def test_checkpoint_bytes_hashed_are_loaded(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'checkpoint'; path.write_bytes(b'first-checkpoint')
            read = Path.read_bytes
            def changed(p):
                data = read(p); p.write_bytes(b'replaced-checkpoint'); return data
            with mock.patch.object(Path, 'read_bytes', changed):
                data, digest = matte_sharp.read_checkpoint(path)
            loaded = []
            def load(stream, **kwargs):
                loaded.append(stream.read()); return {'weights': 1}
            predictor = mock.Mock(); predictor.eval.return_value = predictor
            torch = types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False), device=lambda d: d, load=load)
            models = types.SimpleNamespace(PredictorParams=lambda: None, create_predictor=lambda p: predictor)
            with mock.patch.dict(sys.modules, {'torch': torch, 'sharp.models': models}), mock.patch.object(matte_sharp, 'use_device'):
                matte_sharp.load_model(data)
            self.assertEqual([b'first-checkpoint'], loaded)
            self.assertEqual(identity.sha256(loaded[0]), digest, 'checkpoint hash came from a separate path read')

    def test_photo_decoder_receives_bytesio(self):
        class Decoded(Exception):
            pass
        def opened(value):
            self.assertIsInstance(value, io.BytesIO)
            self.assertEqual(b'snapshot', value.read())
            raise Decoded()
        image = types.SimpleNamespace(open=opened)
        with mock.patch.dict(sys.modules, {'numpy': types.SimpleNamespace(), 'PIL': types.SimpleNamespace(Image=image, ImageOps=None)}):
            with self.assertRaises(Decoded):
                matte_sharp.load_photo(b'snapshot', 2560, 1600)

    def test_trial_remains_plain_output(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root); checkpoint, photo, out = root / 'weights', root / 'photo', root / 'trial.spatial'
            checkpoint.write_bytes(b'checkpoint'); photo.write_bytes(b'trial-photo')
            def make(predictor, device, data, temp, width, height):
                self.assertEqual(b'trial-photo', data)
                fake_make(data, temp)
            with mock.patch.object(matte_sharp, 'be_quiet'), mock.patch.object(matte_sharp, 'load_model', return_value=(None, types.SimpleNamespace(type='cpu'))) as model, mock.patch.object(matte_sharp, 'make', make), contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(0, matte_sharp.main(['--trial', str(photo), '--out', str(out), '--checkpoint', str(checkpoint)]))
            model.assert_called_once_with(b'checkpoint')
            self.assertEqual(b'artifact:trial-photo', out.read_bytes())
            self.assertTrue(output.getvalue().startswith('Trial ok:'))
            self.assertFalse((root / 'v1').exists())
            self.assertFalse(Path(str(out) + '.part').exists())
            self.assertFalse(Path(str(out) + '.json').exists())

    def test_enumeration_keeps_same_stem_and_missing_single(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root); (root / 'a').mkdir(); (root / 'b').mkdir()
            a, b, missing = root / 'a' / 'same.jpg', root / 'b' / 'same.png', root / 'missing.jpg'
            a.write_bytes(b'a'); b.write_bytes(b'b')
            roots = root / 'roots.txt'; roots.write_text('\n'.join(map(str, (root / 'a', root / 'b', a, missing))), encoding='utf-8')
            self.assertCountEqual([a, b, missing], matte_sharp.photos_in(roots))


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] in ('--hold-lock', '--try-lock'):
        mode, path, milliseconds = sys.argv[1:]
        try:
            with publish.StoreLock(path, int(milliseconds) / 1000 if mode == '--try-lock' else 3):
                print('LOCKED', flush=True)
                if mode == '--hold-lock':
                    time.sleep(int(milliseconds) / 1000)
        except TimeoutError:
            print('TIMEOUT', flush=True); sys.exit(2)
    elif len(sys.argv) > 1 and sys.argv[1] == '--paused-run':
        _, _, store, photo, lock = sys.argv
        def paused(data, temp):
            fake_make(data, temp)
            temp.with_name('closed.tmp-0123456789abcdef').write_bytes(b'first-run-work')
            print('RUNNING', flush=True)
            sys.stdin.readline()
        sys.exit(publish.run([photo], store, fields(), lock, paused, emit=lambda line: None))
    elif len(sys.argv) > 1 and sys.argv[1] == '--crash':
        _, _, step, store, photo, lock = sys.argv
        def crash(event, path):
            if event == step:
                os._exit(73)
        sys.exit(publish.run([photo], store, fields(), lock, fake_make, hook=crash))
    else:
        unittest.main(verbosity=2)
