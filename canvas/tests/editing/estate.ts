// The catalog of the synthetic estate as a current engine serves it, with the palette of that engine: every module and projection
// of it, for tests that should hold on real definitions. `node scripts/sync-catalog.mjs` writes the two files. Read with node (the
// snapshot is gzipped), so this file is for tests only, not for the page under tests/editors/page.
import { Graph } from "../../src/core/graph";
import type { Snapshot } from "../../src/core/model";
import { checkDescriptor } from "../../src/core/validate";
import { snapshotFromWire } from "../../src/core/wire";
import type { GraphExtentWire } from "../../src/core/wire-types";
import { parsePalette, type Palette } from "../../src/editor/palette";
import estatePaletteJson from "../fixtures/catalog.estate.palette.json";
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";

export const estatePalette: Palette = parsePalette(estatePaletteJson);
let read: Snapshot | null = null;
/** A fresh copy of the catalog (read once, from the project folder, where the tests run). */
export function estateCatalog(): Graph {
  if (!read) {
    const file = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/catalog.estate.json.gz")))) as { descriptor: unknown; extent: GraphExtentWire };
    const d = checkDescriptor(file.descriptor);
    if (!d.ok) throw new Error(d.errors.join());
    read = snapshotFromWire(d.value, file.extent);
  }
  return Graph.fromSnapshot(read);
}
