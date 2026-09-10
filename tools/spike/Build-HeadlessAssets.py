"""Experimental TPAC v1/v2 subset writer for isolated dedicated-server diagnostics.

Preserves opaque metadata and compressed payloads byte-for-byte. Only table offsets and
the package header change. Layout verified against the locally installed TpacTool 0.4
AssetPackage reader; no TpacTool code or game data is embedded in this script.
"""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import struct
import uuid

TYPES = {
    'c635a3d5-eabb-45dd-883e-aa57e4196113': 'Skeleton',
    'bafab007-7e3f-453f-bac6-e7640043112b': 'SkeletalAnimation',
    '506509c8-e563-4ca4-b166-a53b92e913a7': 'AnimationClip',
    'e8528e0e-64b6-4e61-bae0-7569c0452aea': 'PhysicsShape',
}


def exact(stream, size):
    data = stream.read(size)
    if len(data) != size:
        raise ValueError('Truncated TPAC')
    return data


def number(stream, fmt):
    return struct.unpack('<' + fmt, exact(stream, struct.calcsize('<' + fmt)))[0]


def inventory(path):
    length = path.stat().st_size
    records = []
    with path.open('rb') as stream:
        header = exact(stream, 36)
        if header[:4] != b'TPAC':
            raise ValueError(f'Not TPAC: {path}')
        version = struct.unpack_from('<I', header, 4)[0]
        count = struct.unpack_from('<I', header, 24)[0]
        if version not in (1, 2) or count > 1_000_000:
            raise ValueError(f'Unsupported TPAC version/count: {version}/{count}')
        for _ in range(count):
            start = stream.tell()
            kind = str(uuid.UUID(bytes_le=exact(stream, 16)))
            asset_id = str(uuid.UUID(bytes_le=exact(stream, 16)))
            if version == 2:
                exact(stream, 4)
            name_size = number(stream, 'I')
            if name_size > 1_000_000:
                raise ValueError('Invalid name size')
            name = exact(stream, name_size).decode('utf-8')
            metadata_size = number(stream, 'Q')
            if metadata_size > length - stream.tell():
                raise ValueError('Invalid metadata size')
            stream.seek(metadata_size, 1)
            exact(stream, 8)  # opaque checksum
            segment_count = number(stream, 'I')
            if segment_count > 100_000:
                raise ValueError('Invalid segment count')
            segments = []
            for _ in range(segment_count):
                location = stream.tell() - start
                offset, actual, stored = struct.unpack('<QQQ', exact(stream, 24))
                exact(stream, 45)  # owner/type GUIDs, opaque values, compression format
                if offset + stored > length:
                    raise ValueError('Segment extends beyond source file')
                segments.append((location, offset, stored))
            dependencies = number(stream, 'I')
            if dependencies * 48 > length - stream.tell():
                raise ValueError('Invalid dependency count')
            stream.seek(dependencies * 48, 1)
            end = stream.tell()
            stream.seek(start)
            raw = exact(stream, end - start)
            records.append(dict(kind=kind, id=asset_id, name=name, raw=raw, segments=segments))
        table_end = stream.tell()
        for record in records:
            for _, offset, stored in record['segments']:
                if stored and offset < table_end:
                    raise ValueError('Payload overlaps the resource table')
    return header, records


def subset(source, target, header, records):
    selected = [r for r in records if r['kind'] in TYPES]
    if not selected:
        return None
    if target.exists() or target.resolve() == source.resolve():
        raise ValueError('Output must be a new file separate from the source')
    end = 36 + sum(len(r['raw']) for r in selected)
    new_header = bytearray(header)
    package_id = uuid.UUID(bytes_le=header[8:24])
    new_header[8:24] = uuid.uuid5(package_id, 'ModderLordsHeadlessSubset-v1').bytes_le
    struct.pack_into('<II', new_header, 24, len(selected), end - 36)
    payloads = []
    with target.open('xb') as output, source.open('rb') as original:
        output.write(new_header)
        for record in selected:
            raw = bytearray(record['raw'])
            for location, offset, size in record['segments']:
                struct.pack_into('<Q', raw, location, end)
                payloads.append((offset, size, end))
                end += size
            output.write(raw)
        for offset, size, destination in payloads:
            if output.tell() != destination:
                raise ValueError('Incorrect output offset')
            original.seek(offset)
            remaining = size
            while remaining:
                chunk = exact(original, min(remaining, 1024 * 1024))
                output.write(chunk)
                remaining -= len(chunk)
    # Reparse and compare opaque records and every stored payload against the source.
    _, verified = inventory(target)
    with source.open('rb') as original, target.open('rb') as output:
        for old, new in zip(selected, verified, strict=True):
            restored = bytearray(new['raw'])
            for (location, offset, size), (_, new_offset, new_size) in zip(old['segments'], new['segments'], strict=True):
                if size != new_size:
                    raise ValueError('Changed payload size')
                struct.pack_into('<Q', restored, location, offset)
                original.seek(offset)
                output.seek(new_offset)
                remaining = size
                while remaining:
                    count = min(remaining, 1024 * 1024)
                    if exact(original, count) != exact(output, count):
                        raise ValueError('Changed payload bytes')
                    remaining -= count
            if restored != old['raw']:
                raise ValueError('Changed asset metadata')
    with target.open('rb') as stream:
        checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
    return dict(file=target.name, bytes=target.stat().st_size,
                sha256=checksum,
                assets=[dict(id=r['id'], name=r['name'], type=TYPES[r['kind']]) for r in selected])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.output:
        if args.output.resolve().is_relative_to(args.source.resolve()):
            raise ValueError('Output must be outside the source package directory')
        args.output.mkdir(parents=True, exist_ok=False)
    results = []
    paths = [args.source] if args.source.is_file() else sorted(args.source.glob('*.tpac'))
    if not paths:
        raise ValueError('No source packages found')
    for path in paths:
        header, records = inventory(path)
        counts = collections.Counter(TYPES.get(r['kind'], r['kind']) for r in records)
        result = dict(source=str(path), types=dict(counts))
        if args.output:
            result['subset'] = subset(path, args.output / path.name, header, records)
        results.append(result)
        print(json.dumps(dict(file=path.name, types=dict(counts)), separators=(',', ':')), flush=True)
    if args.output:
        (args.output / 'manifest.json').write_text(json.dumps(results, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
