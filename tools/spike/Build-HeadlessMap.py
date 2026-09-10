"""Create a diagnostic headless scene in a fresh directory; never modify the source."""
import argparse
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path


def build(source: Path, output: Path):
    if output.resolve().is_relative_to(source.resolve()):
        raise ValueError('Output must be outside the source scene directory')
    tree = ET.parse(source / 'scene.xscene')
    root = tree.getroot()
    bounds = {}
    for name in ('border_min', 'border_max'):
        nodes = root.findall(f".//game_entity[@name='{name}']/transform")
        if len(nodes) != 1:
            raise ValueError(f'Expected exactly one {name} transform')
        bounds[name] = nodes[0].attrib['position']
    # Match the official headless scene projection: retain paths/environment, omit
    # client entities and rendered terrain definitions. Binary terrain stays intact.
    entities = root.find('entities')
    if entities is None:
        raise ValueError('Missing entities element')
    removed = len(list(entities.iter('game_entity')))
    entities.clear()
    terrain = root.find('terrain')
    if terrain is None:
        raise ValueError('Missing terrain dimensions')
    bounds['terrain-size'] = ','.join(str(float(terrain.attrib['node_size']) *
        int(terrain.attrib[key])) for key in ('node_dimension_x', 'node_dimension_y'))
    root.remove(terrain)
    output.mkdir(parents=True, exist_ok=False)
    tree.write(output / 'scene.xscene', encoding='utf-8', xml_declaration=True)
    copied = {}
    for name in ('navmesh.bin', 'terrain.bin', 'flora.bin', 'atmosphere.xml'):
        shutil.copyfile(source / name, output / name)
        copied[name] = hashlib.sha256((output / name).read_bytes()).hexdigest()
    metadata = ET.Element('headless-map', bounds)
    metadata.set('navmesh-sha256', copied['navmesh.bin'])
    ET.ElementTree(metadata).write(output / 'modderlords-map.xml', encoding='utf-8')
    (output / 'manifest.json').write_text(json.dumps(dict(source=str(source.resolve()),
        removed_entities=removed, bounds=bounds, copied_sha256=copied), indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    build(args.source, args.output)
