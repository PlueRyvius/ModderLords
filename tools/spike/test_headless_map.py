import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('headless_map', Path(__file__).with_name('Build-HeadlessMap.py'))
maps = importlib.util.module_from_spec(spec)
spec.loader.exec_module(maps)


class MapTests(unittest.TestCase):
    def test_retains_navigation_and_bounds_without_render_entities(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / 'source'
            source.mkdir()
            xml = b'''<scene><entities>
              <game_entity name="border_min"><transform position="1,2,3"/></game_entity>
              <game_entity name="border_max"><transform position="9,8,7"/></game_entity>
              <game_entity name="render"><mesh/></game_entity></entities>
              <terrain node_dimension_x="16" node_dimension_y="12" node_size="100"/>
              <Paths><path name="river"/></Paths></scene>'''
            (source / 'scene.xscene').write_bytes(xml)
            for name in ('navmesh.bin', 'terrain.bin', 'flora.bin', 'atmosphere.xml'):
                (source / name).write_bytes(name.encode())
            output = Path(temp) / 'output'
            maps.build(source, output)
            root = ET.parse(output / 'scene.xscene').getroot()
            self.assertEqual(len(root.findall('.//game_entity')), 0)
            self.assertIsNone(root.find('terrain'))
            self.assertEqual(root.find('Paths/path').attrib['name'], 'river')
            self.assertEqual((source / 'scene.xscene').read_bytes(), xml)
            for name in ('navmesh.bin', 'terrain.bin', 'flora.bin', 'atmosphere.xml'):
                self.assertEqual((output / name).read_bytes(), (source / name).read_bytes())
            metadata = ET.parse(output / 'modderlords-map.xml').getroot()
            self.assertEqual(metadata.attrib['terrain-size'], '1600.0,1200.0')
            self.assertEqual(metadata.attrib['border_max'], '9,8,7')
            with self.assertRaises(FileExistsError):
                maps.build(source, output)

    def test_missing_bounds_rejected_before_output_creation(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / 'source'
            source.mkdir()
            (source / 'scene.xscene').write_text('<scene/>')
            with self.assertRaises(ValueError):
                maps.build(source, Path(temp) / 'output')
            self.assertFalse((Path(temp) / 'output').exists())


if __name__ == '__main__':
    unittest.main()
