import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location('headless_assets', Path(__file__).with_name('Build-HeadlessAssets.py'))
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


def fixture(path):
    """Opaque, future-version metadata and compressed-looking bytes must survive unchanged."""
    records = []
    for kind, name in [('a08f8b97-197c-4bea-b95b-53846cae834e', 'texture'),
                       (next(iter(assets.TYPES)), 'skeleton')]:
        metadata = b'opaque\x00metadata\xff'
        record = bytearray(uuid.UUID(kind).bytes_le + uuid.uuid4().bytes_le)
        record += struct.pack('<II', 987, len(name)) + name.encode()
        record += struct.pack('<Q', len(metadata)) + metadata + struct.pack('<Q', 0xABCDEF)
        record += struct.pack('<I', 1)
        position = len(record)
        record += struct.pack('<QQQ', 0, 999, 7) + bytes(44) + b'\x01'
        record += struct.pack('<I', 1) + uuid.uuid4().bytes_le * 3
        records.append((record, position))
    end = 36 + sum(len(r) for r, _ in records)
    header = b'TPAC' + struct.pack('<I', 2) + uuid.uuid4().bytes_le + struct.pack('<III', 2, end - 36, 19)
    for record, position in records:
        struct.pack_into('<Q', record, position, end)
        end += 7
    path.write_bytes(header + b''.join(r for r, _ in records) + b'PAYLOAD' * 2)


class SubsetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / 'input.tpac'
        fixture(self.source)

    def tearDown(self):
        self.temp.cleanup()

    def test_filters_render_assets_preserving_opaque_data_and_source(self):
        original = self.source.read_bytes()
        header, records = assets.inventory(self.source)
        target = self.root / 'subset.tpac'
        result = assets.subset(self.source, target, header, records)
        new_header, selected = assets.inventory(target)
        self.assertEqual(['skeleton'], [r['name'] for r in selected])
        self.assertEqual(original, self.source.read_bytes())
        self.assertEqual(1, len(result['assets']))
        self.assertNotEqual(header[8:24], new_header[8:24])
        self.assertEqual(header[32:36], new_header[32:36])
        self.assertTrue(target.read_bytes().endswith(b'PAYLOAD'))

    def test_refuses_to_overwrite_source(self):
        header, records = assets.inventory(self.source)
        with self.assertRaises(ValueError):
            assets.subset(self.source, self.source, header, records)

    def test_rejects_truncated_payload(self):
        self.source.write_bytes(self.source.read_bytes()[:-1])
        with self.assertRaises(ValueError):
            assets.inventory(self.source)

    def test_rejects_unknown_package_version(self):
        data = bytearray(self.source.read_bytes())
        struct.pack_into('<I', data, 4, 99)
        self.source.write_bytes(data)
        with self.assertRaises(ValueError):
            assets.inventory(self.source)


if __name__ == '__main__':
    unittest.main()
