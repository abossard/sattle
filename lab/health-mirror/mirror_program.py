"""Load the authored GKQL documents and extend their repeatable stage for deeper source DAGs."""

import json
from pathlib import Path
from shutil import copyfile

ROOT = Path(__file__).resolve().parent
DEFINITIONS = ROOT / "definitions"


def read(path):
    return json.loads(path.read_text())


module = read(DEFINITIONS / "mirror.module.gkql.json")
ROW = next(row["fields"] for row in module["rows"] if row["name"] == "CloudHealthRow")
ONTOLOGY_ROW = next(row["fields"] for row in module["rows"] if row["name"] == "WorkloadOntologyRow")


def materialize(output, depth):
    """Most builds copy byte-for-byte. Additional levels repeat only the stage declared by the author."""
    repeat = read(ROOT / "rollup-levels.json")
    for path in sorted(DEFINITIONS.glob("*.gkql.json")):
        target = output / path.name
        last = repeat["authoredLevels"]
        if path.name != repeat["projection"] or depth <= last:
            copyfile(path, target)
            continue
        doc = read(path)
        names = [pattern.format(last) for pattern in repeat["verbs"]]
        original = {v["id"]: v for v in doc["verbs"]}
        incoming = [wire for wire in doc["wires"] if wire["to"] in names]
        accumulator = repeat["accumulator"]
        outgoing = [
            wire
            for wire in doc["wires"]
            if wire["from"] == accumulator.format(last) and wire["to"] not in names
        ]
        for level in range(last + 1, depth + 1):
            ids = {pattern.format(last): pattern.format(level) for pattern in repeat["verbs"]}
            previous = {accumulator.format(last - 1): accumulator.format(level - 1)}
            doc["verbs"].extend({**original[k], "id": ids[k]} for k in names)
            doc["wires"].extend(
                {**w, "from": ids.get(w["from"], previous.get(w["from"], w["from"])), "to": ids[w["to"]]}
                for w in incoming
            )
        for w in outgoing:
            w["from"] = accumulator.format(depth)
        target.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")
