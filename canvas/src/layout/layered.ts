// Layered layout for trees and DAGs (Sugiyama style, without dummy nodes): cycle breaking, longest-path layering,
// barycenter ordering, then coordinate relaxation that keeps order and minimum separation.
// Deterministic: all ties are resolved by node index.

export interface LayeredInput {
  n: number;
  src: Int32Array;           // edge sources (node index), -1 edges are skipped
  dst: Int32Array;
  /** node box size along and across layers, in world units */
  width?: Float32Array;
  height?: Float32Array;
}
export interface LayeredOptions {
  direction: "down" | "right";
  layerGap: number;
  nodeGap: number;
  /** put roots (fewer sources than sinks) on top; "auto" decides from the degree distribution */
  roots: "auto" | "sources" | "sinks";
}
export const defaultLayered: LayeredOptions = { direction: "down", layerGap: 90, nodeGap: 36, roots: "auto" };

export interface LayeredResult { positions: Float32Array; layers: Int32Array; layerCount: number }

export function layeredLayout(g: LayeredInput, opt: LayeredOptions = defaultLayered): LayeredResult {
  const { n } = g;
  const m = g.src.length;
  // adjacency in CSR form
  const outStart = new Int32Array(n + 1), inStart = new Int32Array(n + 1);
  for (let e = 0; e < m; e++) {
    const s = g.src[e], t = g.dst[e];
    if (s < 0 || t < 0 || s === t) continue;
    outStart[s + 1]++; inStart[t + 1]++;
  }
  for (let i = 0; i < n; i++) { outStart[i + 1] += outStart[i]; inStart[i + 1] += inStart[i]; }
  const outAdj = new Int32Array(outStart[n]), inAdj = new Int32Array(inStart[n]);
  const oFill = outStart.slice(0, n), iFill = inStart.slice(0, n);
  for (let e = 0; e < m; e++) {
    const s = g.src[e], t = g.dst[e];
    if (s < 0 || t < 0 || s === t) continue;
    outAdj[oFill[s]++] = t; inAdj[iFill[t]++] = s;
  }

  // 1. cycle breaking: iterative DFS, edges to a node on the stack are reversed (ignored as forward constraints)
  const state = new Uint8Array(n); // 0 new, 1 on stack, 2 done
  const isBack = new Uint8Array(outAdj.length);
  const stackNode = new Int32Array(n), stackPos = new Int32Array(n);
  const dfsOrder = new Int32Array(n);
  let dfsCount = 0;
  const startOrder = rootsFirst(n, inStart, outStart);
  for (const root of startOrder) {
    if (state[root] !== 0) continue;
    let sp = 0;
    stackNode[0] = root; stackPos[0] = outStart[root]; state[root] = 1;
    while (sp >= 0) {
      const v = stackNode[sp];
      if (stackPos[sp] < outStart[v + 1]) {
        const ei = stackPos[sp]++;
        const w = outAdj[ei];
        if (state[w] === 1) isBack[ei] = 1;
        else if (state[w] === 0) { state[w] = 1; sp++; stackNode[sp] = w; stackPos[sp] = outStart[w]; }
      } else { state[v] = 2; dfsOrder[dfsCount++] = v; sp--; }
    }
  }

  // 2. longest-path layering over forward edges, in reverse DFS finishing order (a topological order)
  const layer = new Int32Array(n);
  for (let k = dfsCount - 1; k >= 0; k--) {
    const v = dfsOrder[k];
    for (let ei = outStart[v]; ei < outStart[v + 1]; ei++) {
      if (isBack[ei]) continue;
      const w = outAdj[ei];
      if (layer[w] < layer[v] + 1) layer[w] = layer[v] + 1;
    }
  }
  let layerCount = 0;
  for (let i = 0; i < n; i++) if (layer[i] + 1 > layerCount) layerCount = layer[i] + 1;

  // decide orientation: roots on top. In trees pointing child -> parent the sinks are the few roots.
  let sources = 0, sinks = 0;
  for (let i = 0; i < n; i++) { if (inStart[i + 1] === inStart[i]) sources++; if (outStart[i + 1] === outStart[i]) sinks++; }
  const flip = opt.roots === "sinks" || (opt.roots === "auto" && sinks > 0 && sinks < sources);
  if (flip) for (let i = 0; i < n; i++) layer[i] = layerCount - 1 - layer[i];

  // 3. order within layers. Start with DFS order, then barycenter sweeps.
  const layerStart = new Int32Array(layerCount + 1);
  for (let i = 0; i < n; i++) layerStart[layer[i] + 1]++;
  for (let l = 0; l < layerCount; l++) layerStart[l + 1] += layerStart[l];
  const order = new Int32Array(n);        // nodes grouped by layer
  const rank = new Float64Array(n);       // position index within its layer
  {
    const fill = layerStart.slice(0, layerCount);
    for (let k = dfsCount - 1; k >= 0; k--) { const v = dfsOrder[k]; order[fill[layer[v]]++] = v; }
    for (let l = 0; l < layerCount; l++) for (let p = layerStart[l]; p < layerStart[l + 1]; p++) rank[order[p]] = p - layerStart[l];
  }
  const bary = new Float64Array(n);
  const sweep = (l: number, useIn: boolean) => {
    const a = layerStart[l], b = layerStart[l + 1];
    if (b - a < 2) return;
    for (let p = a; p < b; p++) {
      const v = order[p];
      let sum = 0, cnt = 0;
      const [adj, s0, s1] = useIn ? [inAdj, inStart[v], inStart[v + 1]] : [outAdj, outStart[v], outStart[v + 1]];
      for (let k = s0; k < s1; k++) { const w = adj[k]; if (layer[w] !== l) { sum += rank[w]; cnt++; } }
      bary[v] = cnt ? sum / cnt : rank[v];
    }
    const slice = Array.from(order.subarray(a, b));
    slice.sort((x, y) => bary[x] - bary[y] || x - y);
    for (let k = 0; k < slice.length; k++) { order[a + k] = slice[k]; rank[slice[k]] = k; }
  };
  for (let pass = 0; pass < 4; pass++) {
    for (let l = 1; l < layerCount; l++) sweep(l, !flip ? true : false);
    for (let l = layerCount - 2; l >= 0; l--) sweep(l, !flip ? false : true);
  }
  // layering follows edge direction: "in" neighbours are on previous layers unless flipped; the sweeps above use that

  // 4. coordinates across the layer: start evenly spaced, relax toward neighbour means, keep order and separation
  const across = new Float64Array(n);
  const size = (v: number) => (opt.direction === "down" ? g.width?.[v] ?? 0 : g.height?.[v] ?? 0);
  for (let l = 0; l < layerCount; l++) {
    let x = 0;
    for (let p = layerStart[l]; p < layerStart[l + 1]; p++) {
      const v = order[p];
      across[v] = x + size(v) / 2;
      x += size(v) + opt.nodeGap;
    }
  }
  const sepNeighbor = (l: number, p: number) => size(order[p - 1]) / 2 + size(order[p]) / 2 + opt.nodeGap;
  const relax = (l: number, up: boolean, down: boolean) => {
    const a = layerStart[l], b = layerStart[l + 1];
    for (let p = a; p < b; p++) {
      const v = order[p];
      let sum = 0, cnt = 0;
      if (up) for (let k = inStart[v]; k < inStart[v + 1]; k++) { const w = inAdj[k]; if (layer[w] !== l) { sum += across[w]; cnt++; } }
      if (down) for (let k = outStart[v]; k < outStart[v + 1]; k++) { const w = outAdj[k]; if (layer[w] !== l) { sum += across[w]; cnt++; } }
      if (cnt) across[v] = sum / cnt;
    }
    // restore order with minimum separation: forward then backward pass, then average (keeps centred parents)
    const lo = new Float64Array(b - a), hi = new Float64Array(b - a);
    for (let p = a; p < b; p++) {
      const v = order[p];
      lo[p - a] = p > a ? Math.max(across[v], lo[p - a - 1] + sepNeighbor(l, p)) : across[v];
    }
    for (let p = b - 1; p >= a; p--) {
      const v = order[p];
      hi[p - a] = p < b - 1 ? Math.min(across[v], hi[p - a + 1] - sepNeighbor(l, p + 1)) : across[v];
    }
    // feasible placement: shift the forward-pass solution to follow the backward one on average, then fix order again
    for (let p = a; p < b; p++) across[order[p]] = (lo[p - a] + hi[p - a]) / 2;
    for (let p = a + 1; p < b; p++) {
      const minX = across[order[p - 1]] + sepNeighbor(l, p);
      if (across[order[p]] < minX) across[order[p]] = minX;
    }
  };
  for (let pass = 0; pass < 6; pass++) {
    for (let l = 1; l < layerCount; l++) relax(l, !flip, flip);
    for (let l = layerCount - 2; l >= 0; l--) relax(l, flip, !flip);
  }

  // 5. emit positions, centred on the widest layer
  let minA = Infinity, maxA = -Infinity;
  for (let i = 0; i < n; i++) { if (across[i] < minA) minA = across[i]; if (across[i] > maxA) maxA = across[i]; }
  const mid = n ? (minA + maxA) / 2 : 0;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    const a = across[i] - mid, l = layer[i] * opt.layerGap;
    if (opt.direction === "down") { positions[2 * i] = a; positions[2 * i + 1] = l; } else { positions[2 * i] = l; positions[2 * i + 1] = a; }
  }
  return { positions, layers: layer, layerCount };
}

/** Roots first so DFS trees hang from sources; remaining nodes follow in index order. */
function rootsFirst(n: number, inStart: Int32Array, outStart: Int32Array): Int32Array {
  const out = new Int32Array(n);
  let k = 0;
  for (let i = 0; i < n; i++) if (inStart[i + 1] === inStart[i]) out[k++] = i;
  for (let i = 0; i < n; i++) if (inStart[i + 1] !== inStart[i]) out[k++] = i;
  void outStart;
  return out;
}
