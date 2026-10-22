"""Windows Spatial publication and run protocol. Standard library + identity only."""
import datetime as dt
import hashlib
import os
from pathlib import Path
import sys
import time

import identity


class StoreLock:
    """Byte 0, shared open and exclusive byte-range lock; timeout is in seconds."""
    def __init__(self, path, timeout=30):
        if timeout < 0:
            raise ValueError('negative lock timeout')
        self.path, self.timeout, self.file = Path(path), timeout, None

    def __enter__(self):
        import msvcrt
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(self.path, os.O_RDWR | os.O_CREAT | os.O_BINARY, 0o600)
        self.file = os.fdopen(fd, 'r+b', buffering=0)
        deadline = time.monotonic() + self.timeout
        try:
            while True:
                self.file.seek(0)
                try:
                    msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
                    return self
                except OSError:
                    if time.monotonic() >= deadline:
                        raise TimeoutError('store busy or unreadable')
                    time.sleep(min(0.1, max(0.001, deadline - time.monotonic())))
        except BaseException:
            self.file.close()
            self.file = None
            raise

    def __exit__(self, *exc):
        import msvcrt
        if self.file is not None:
            try:
                self.file.seek(0)
                msvcrt.locking(self.file.fileno(), msvcrt.LK_UNLCK, 1)
            finally:
                self.file.close()
                self.file = None


def rename_new(source, destination):
    # Windows os.rename refuses an occupied name. Never replace a committed file.
    if os.name != 'nt':
        raise OSError('Spatial publication requires Windows')
    os.rename(source, destination)


def destination_state(sidecar):
    # Classify deliberately calls an absent sidecar Unreadable. Only definite absence is recoverable here.
    try:
        Path(sidecar).stat()
    except FileNotFoundError:
        return 'Invalid'
    except OSError:
        return 'Unreadable'
    return identity.classify(sidecar, verify_artifact=True)


def needs(store, source_sha, recipe_sha):
    return identity.classify(identity.sidecar_path(store, source_sha, recipe_sha), verify_artifact=True) != 'Valid'


def cleanup_temps(store):
    # Caller holds generator.lock then store.lock. Live publications also keep an open handle.
    root = Path(store) / 'v1'
    try:
        for path in root.rglob('*.tmp-*'):
            try:
                path.unlink()
            except OSError:
                pass
    except OSError:
        pass


def _check_state(state):
    if state == 'Unsupported':
        raise ValueError('conflict with a newer format')
    if state == 'Unreadable':
        raise OSError('store busy or unreadable')


def _quarantine(store, source_sha, recipe_sha, artifact, sidecar, moved):
    now = dt.datetime.now(dt.timezone.utc)
    for offset in range(86400):
        target = identity.quarantine_path(store, source_sha, recipe_sha, now + dt.timedelta(seconds=offset))
        target_json = target.with_suffix('.json')  # Keep quarantine sidecars within the existing path-length budget.
        # Under the stable lock, choose a free pair of quarantine names without replacing any prior recovery.
        if not target.exists() and not target_json.exists():
            break
    else:
        raise OSError('quarantine names exhausted')
    for source, dest in ((artifact, target), (sidecar, target_json)):
        try:
            source.stat()
        except FileNotFoundError:
            continue
        dest.parent.mkdir(parents=True, exist_ok=True)
        rename_new(source, dest)
        moved.append((source, dest))


def publish_one(photo, store, fields, lock_path, make, timeout=30, hook=None):
    """make(bytes, temp_path) writes only that temp. hook(step, path) is a crash/fault test seam.
    Normal exceptions roll back this publication. Process death can leave an orphan, never a partial valid pair.
    """
    photo = Path(photo).absolute()
    try:
        data = photo.read_bytes()
    except OSError as e:
        raise OSError('unreadable') from e
    # The scan is only an estimate: identity and decoding use this fresh snapshot.
    source_sha, recipe_sha = identity.sha256(data), identity.recipe_sha256(fields)
    artifact = identity.artifact_path(store, source_sha, recipe_sha)
    sidecar = identity.sidecar_path(store, source_sha, recipe_sha)
    with StoreLock(lock_path, timeout):
        state = destination_state(sidecar)
        _check_state(state)
        if state == 'Valid':
            return
    artifact.parent.mkdir(parents=True, exist_ok=True)
    temp = Path(identity.temp_name(artifact))
    side_temp = Path(identity.temp_name(sidecar))
    moved, installed = [], []
    # Keep the temp open while make runs outside the lock; another run's cleanup must not remove live work.
    reservation = temp.open('x+b')
    try:
        make(data, temp)
        if hook:
            hook('temp_written', temp)
        with StoreLock(lock_path, timeout):
            state = destination_state(sidecar)
            _check_state(state)
            if state == 'Valid':
                return
            try:
                _quarantine(store, source_sha, recipe_sha, artifact, sidecar, moved)
                reservation.flush()
                os.fsync(reservation.fileno())
                reservation.seek(0)
                digest = hashlib.sha256()
                for chunk in iter(lambda: reservation.read(1024 * 1024), b''):
                    digest.update(chunk)
                size = os.fstat(reservation.fileno()).st_size
                reservation.close()
                rename_new(temp, artifact)
                installed.append(artifact)
                if hook:
                    hook('artifact_renamed', artifact)
                metadata = {'version': 1, 'source': {'sha256': source_sha, 'size': len(data),
                            'name': photo.name},
                            'recipe': {'sha256': recipe_sha, 'fields': fields},
                            'artifact': {'file': artifact.name, 'sha256': digest.hexdigest(), 'size': size},
                            'created': dt.datetime.now(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}
                with side_temp.open('xb') as stream:
                    stream.write(identity.sidecar_json(metadata).encode('utf-8'))
                    stream.flush()
                    os.fsync(stream.fileno())
                if hook:
                    hook('sidecar_temp_written', side_temp)
                rename_new(side_temp, sidecar)
                installed.append(sidecar)
                if identity.classify(sidecar, verify_artifact=True) != 'Valid':
                    raise OSError('published pair could not be verified')
            except Exception:
                for path in reversed(installed):
                    try:
                        path.unlink()
                    except OSError:
                        pass
                for source, dest in reversed(moved):
                    try:
                        rename_new(dest, source)
                    except OSError:
                        pass
                raise
    finally:
        failed, cleanup_error = sys.exc_info()[0] is not None, None
        try:
            reservation.close()
        except OSError as e:
            cleanup_error = e
        for path in (temp, side_temp):
            try:
                path.unlink()
            except FileNotFoundError:
                pass
            except OSError as e:
                cleanup_error = cleanup_error or e
        if cleanup_error is not None and not failed:
            raise cleanup_error


def run(photos, store, fields, lock_path, make, emit=None, timeout=30, hook=None):
    """Hold the stable generator.lock for the whole run, including cleanup and scan.
    All clients acquire generator.lock before store.lock; a second run never waits or cleans temps.
    """
    if emit is None:
        emit = lambda line: print(line, flush=True)
    held = StoreLock(Path(lock_path).with_name('generator.lock'), 0)
    try:
        held.__enter__()
    except TimeoutError:
        emit('Failed: generation: another generation is running')
        return 1
    except OSError:
        emit('Failed: generation: store busy or unreadable')
        return 1
    try:
        return _run_locked(photos, store, fields, lock_path, make, emit, timeout, hook)
    finally:
        held.__exit__(None, None, None)


def _run_locked(photos, store, fields, lock_path, make, emit, timeout, hook):
    """Scan identities without retaining photo bytes; re-read pending photos before generation.
    Processing counts input photos needing work (duplicates included), plus unreadable inputs needing attention.
    Saved is emitted for every verified photo; an already-current photo needs no Predicting progress slot.
    """
    if emit is None:
        emit = lambda line: print(line, flush=True)
    recipe_sha = identity.recipe_sha256(fields)
    if not identity.supported(fields):
        raise ValueError('unsupported recipe')
    with StoreLock(lock_path, timeout):
        cleanup_temps(store)
    failed = 0
    items = []
    for path in photos:
        photo = Path(path).absolute()
        try:
            source_sha = identity.sha256(photo.read_bytes())
            todo = needs(store, source_sha, recipe_sha)
            items.append((photo, source_sha, todo, False))
        except OSError:
            items.append((photo, None, True, True))
    count = sum(item[2] for item in items)
    emit(f'- Processing {count} valid image files -')
    for photo, source_sha, todo, unreadable in items:
        if todo:
            emit(f'Predicting gaussians for {photo}...')
        try:
            if unreadable:
                raise OSError('unreadable')
            if todo:
                publish_one(photo, store, fields, lock_path, make, timeout, hook)
            else:
                with StoreLock(lock_path, timeout):
                    if needs(store, source_sha, recipe_sha):
                        raise OSError('store changed; scan again')
            emit(f'Saved: {photo.name}')
        except Exception as e:
            failed += 1
            emit(f'Failed: {photo.name}: {e}')
    if not count:
        emit('Nothing to generate')
    return 1 if failed else 0
