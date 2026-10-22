"""Spatial v1 identity contract. Pure helpers and read-only classification; no app wiring."""
import datetime as dt
import hashlib
import json
import os
import re
import secrets
from pathlib import Path


def _string(value):
    if type(value) is not str:
        raise ValueError('expected string')
    if any(0xd800 <= ord(c) <= 0xdfff for c in value):
        raise ValueError('lone surrogate')
    return value


def _values(value, depth=0, parsing=False):
    if depth > 128:
        raise ValueError('JSON too deep')
    if type(value) is str:
        _string(value)
    elif type(value) is int:
        pass
    elif type(value) is dict:
        for k, v in value.items():
            _string(k)
            _values(v, depth + 1, parsing)
    elif type(value) is list:
        for v in value:
            _values(v, depth + 1, parsing)
    elif parsing and (value is None or type(value) is bool):
        pass  # Preserve JSON types for schema checks, including future-version envelopes.
    else:
        raise ValueError('unsupported JSON value')


def canonical(value):
    _values(value)
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True)


def _pairs(items):
    result = {}
    for key, value in items:
        if key in result:
            raise ValueError('duplicate key')
        result[key] = value
    return result


def _reject_number(value):
    raise ValueError('only decimal integers allowed')


def parse_strict(text):
    _string(text)
    try:
        value = json.loads(text, object_pairs_hook=_pairs, parse_float=_reject_number,
                           parse_constant=_reject_number)
        _values(value, parsing=True)
        return value
    except RecursionError as e:
        raise ValueError('JSON too deep') from e


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def _object(value, keys):
    if type(value) is not dict or set(value) != set(keys.split()):
        raise ValueError('incorrect object fields')
    return value


def _integer(value, minimum, maximum):
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError('integer out of range or wrong type')
    return value


def _digest(value):
    if not re.fullmatch('[0-9a-f]{64}', _string(value)):
        raise ValueError('invalid SHA-256')
    return value


def validate_recipe(fields):
    _object(fields, 'exporter generator height model upright width')
    for key, prefix in [('exporter', 'spatial-photos'), ('generator', 'matte-sharp')]:
        if not re.fullmatch(prefix + r'/(0|[1-9][0-9]*)', _string(fields[key])):
            raise ValueError('invalid revision')
    for key in ('width', 'height'):
        _integer(fields[key], 64, 16384)
    _digest(fields['model'])
    if fields['upright'] != 'exif-v1':
        raise ValueError('invalid upright')
    return fields


def recipe_sha256(fields):
    return sha256(canonical(validate_recipe(fields)).encode('utf-8'))


def supported(fields):
    try:
        validate_recipe(fields)
        return fields['generator'] == 'matte-sharp/1' and fields['exporter'] == 'spatial-photos/1'
    except (ValueError, TypeError):
        return False


def artifact_path(store, source_sha, recipe_sha):
    return Path(os.path.abspath(store)) / 'v1' / _digest(source_sha) / (_digest(recipe_sha) + '.spatial')


def sidecar_path(store, source_sha, recipe_sha):
    return Path(str(artifact_path(store, source_sha, recipe_sha)) + '.json')


def temp_name(final):
    return str(final) + '.tmp-' + secrets.token_hex(8)


def quarantine_path(store, source_sha, recipe_sha, now=None):
    now = now or dt.datetime.now(dt.timezone.utc)
    if now.tzinfo is None or now.utcoffset() != dt.timedelta(0):
        raise ValueError('UTC timestamp required')
    stamp = '%04d%02d%02dT%02d%02d%02dZ' % (now.year, now.month, now.day, now.hour, now.minute, now.second)
    return Path(os.path.abspath(store)) / 'v1' / 'quarantine' / (
        stamp + '-' + _digest(source_sha) + '-' + _digest(recipe_sha) + '.spatial')


def store_too_long(store):
    digest = '0' * 64
    sidecar = str(sidecar_path(store, digest, digest))
    quarantine = str(quarantine_path(store, digest, digest))
    # Windows counts UTF-16 code units, including when this helper is tested on another OS.
    return max(len(p.encode('utf-16-le')) // 2 for p in
               (sidecar + '.tmp-' + '0' * 16, quarantine)) > 259


def validate_sidecar(data):
    _object(data, 'version source recipe artifact created')
    _integer(data['version'], 1, 1)
    source = _object(data['source'], 'sha256 size name')
    recipe = _object(data['recipe'], 'sha256 fields')
    artifact = _object(data['artifact'], 'file sha256 size')
    for item in (source, recipe, artifact):
        _digest(item['sha256'])
    for item in (source, artifact):
        _integer(item['size'], 0, 9223372036854775807)
    _string(source['name'])
    if recipe_sha256(recipe['fields']) != recipe['sha256']:
        raise ValueError('recipe digest mismatch')
    if not supported(recipe['fields']):
        raise ValueError('unsupported recipe')
    if artifact['file'] != recipe['sha256'] + '.spatial':
        raise ValueError('artifact filename mismatch')
    created = _string(data['created'])
    if not re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z', created):
        raise ValueError('invalid created')
    dt.datetime.strptime(created, '%Y-%m-%dT%H:%M:%SZ')
    return data


def sidecar_json(data):
    return canonical(validate_sidecar(data))


def classify(sidecar, verify_artifact=False):
    """Return Valid/Unsupported/Invalid/Unreadable. Missing sidecar is Unreadable;
    a readable sidecar referring to an absent artifact is Invalid. Never mutates files.
    This checks bytes only; it does not grant a subsequent playback lease.
    """
    try:
        path = Path(os.path.abspath(sidecar))
        data = parse_strict(path.read_bytes().decode('utf-8'))
        if type(data) is dict and type(data.get('version')) is int and data['version'] != 1:
            return 'Unsupported'
        validate_sidecar(data)
        if (path.name != data['artifact']['file'] + '.json' or
                path.parent.name != data['source']['sha256'] or path.parent.parent.name != 'v1'):
            return 'Invalid'
        artifact = path.with_name(data['artifact']['file'])
        try:
            with artifact.open('rb') as stream:
                if os.fstat(stream.fileno()).st_size != data['artifact']['size']:
                    return 'Invalid'
                if verify_artifact:
                    digest = hashlib.sha256()
                    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                        digest.update(chunk)
                    if digest.hexdigest() != data['artifact']['sha256']:
                        return 'Invalid'
        except FileNotFoundError:
            return 'Invalid'
        return 'Valid'
    except OSError:
        return 'Unreadable'
    except (ValueError, TypeError, OverflowError):
        return 'Invalid'
