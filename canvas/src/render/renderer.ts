// WebGL2 renderer: instanced nodes (SDF shapes), instanced edges (hairline quads or bezier wires), arrowheads.
// Positions live in a float texture so layout progress updates one texture, not every edge.
import { Scene, WORDS_EDGE, WORDS_NODE } from "./scene";

const TEX_W = 2048;
const BEZIER_SEGMENTS = 20;

export interface Camera { x: number; y: number; zoom: number }
export interface RenderParams {
  width: number;            // css px
  height: number;
  dpr: number;
  camera: Camera;
  dim: number;              // alpha multiplier for the base pass (focus mode); 1 = off
  edgeAlpha: number;        // 0..1 LOD alpha for edges
  edgeWidth: number;        // css px
  arrows: boolean;
  wires: boolean;           // bezier wires (node editor) instead of straight edges
  background: [number, number, number];
  colors: { selected: [number, number, number]; added: [number, number, number]; removed: [number, number, number]; changed: [number, number, number]; identity: [number, number, number]; source: [number, number, number] };
  minNodePx: number;
  /** draw every n-th edge (hash sampled); adaptive level of detail while the camera moves */
  edgeSkip: number;
  telemetry: { on: boolean; warnNode: number; warnEdge: number; flow: boolean; time: number; flowColor: [number, number, number] };
}

const COMMON = `#version 300 es
precision highp float; precision highp int;
uniform sampler2D uPos;
uniform vec2 uRes; uniform vec2 uCenter; uniform float uZoom;
vec4 posOf(int slot){ return texelFetch(uPos, ivec2(slot & ${TEX_W - 1}, slot >> 11), 0); }
vec2 toScreen(vec2 w){ return (w - uCenter) * uZoom; }
vec4 toClip(vec2 s){ return vec4(s.x / (uRes.x * 0.5), -s.y / (uRes.y * 0.5), 0.0, 1.0); }
`;

const NODE_VS = COMMON + `
layout(location=0) in int aSlot;
layout(location=1) in vec2 aSize;
layout(location=2) in vec4 aColor;
layout(location=3) in uint aMeta;
layout(location=4) in float aN0;   // telemetry: ring gauge
layout(location=5) in float aN1;   // telemetry: size
layout(location=6) in float aN2;   // telemetry: state
uniform float uMinPx; uniform int uTel; uniform float uWarnN;
out vec2 vP; out vec4 vColor; out vec2 vHalf; flat out uint vMeta; out vec3 vTel;
void main(){
  uint shape = aMeta & 255u;
  if(shape == 0u){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  vec2 corner = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1)) * 2.0 - 1.0;
  vec4 wp = posOf(aSlot);
  vec2 hext = aSize * uZoom;
  bool warn = uTel == 1 && aN2 >= uWarnN && shape != 6u;
  if(uTel == 1 && shape != 6u) hext *= 1.0 + 0.45 * aN1;       // size follows the size channel
  if(warn) hext *= 1.25;                                        // size cue for the warning state, visible even when tiny
  if((aMeta & 0x200000u) != 0u) hext = max(hext, vec2(2.5));
  else if(shape != 6u) hext = max(hext, vec2(uMinPx));
  bool gauge = uTel == 1 && shape != 6u && hext.x >= 3.0;
  float pad = (((aMeta >> 8) & 0xFFFFu) != 0u || gauge) ? 10.0 : 2.0; // room for state rings and the gauge only when needed
  vTel = vec3(aN0, aN1, warn ? 1.0 : 0.0);
  vec2 ext = hext + vec2(pad);
  vec2 s = toScreen(wp.xy) + corner * ext;
  gl_Position = toClip(s);
  vP = corner * ext; vHalf = hext; vColor = aColor; vMeta = aMeta;
}`;

const NODE_FS = `#version 300 es
precision highp float; precision highp int;
in vec2 vP; in vec4 vColor; in vec2 vHalf; flat in uint vMeta; in vec3 vTel;
uniform float uDim; uniform int uTel;
uniform vec3 uSel; uniform vec3 uAdded; uniform vec3 uRemoved; uniform vec3 uChanged; uniform vec3 uIdentity; uniform vec3 uSource;
out vec4 outColor;
float sdBox(vec2 p, vec2 b){ vec2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }
float sdTri(vec2 p, float r){
  p.y += r * 0.25; float k = 1.7320508;
  p.x = abs(p.x) - r; p.y = p.y + r / k;
  if(p.x + k * p.y > 0.0) p = vec2(p.x - k * p.y, -k * p.x - p.y) / 2.0;
  p.x -= clamp(p.x, -2.0 * r, 0.0);
  return -length(p) * sign(p.y);
}
float sdHex(vec2 p, float r){
  const vec3 k = vec3(-0.866025404, 0.5, 0.577350269);
  p = abs(p); p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
  p -= vec2(clamp(p.x, -k.z * r, k.z * r), r);
  return length(p) * sign(p.y);
}
void main(){
  uint shape = vMeta & 255u; uint st = (vMeta >> 8) & 255u; uint ex = (vMeta >> 16) & 255u;
  float r = vHalf.x;
  float d;
  if(shape == 1u) d = length(vP) - r;
  else if(shape == 2u) d = sdBox(vP, vec2(r * 0.88));
  else if(shape == 3u) d = (abs(vP.x) + abs(vP.y)) * 0.7071 - r * 0.95;
  else if(shape == 4u) d = sdTri(vP, r * 0.95);
  else if(shape == 5u) d = sdHex(vP, r * 0.95);
  else d = sdBox(vP, vHalf - vec2(6.0)) - 6.0;
  float aa = 0.75;
  float fill = 1.0 - smoothstep(-aa, aa, d);
  vec4 col = vec4(vColor.rgb, vColor.a);
  float a = fill * col.a;
  vec3 rgb = col.rgb;
  // state rings (outside the shape). added: solid, changed: double, removed: dashed, identity: thick, selected: thick halo
  float ang = atan(vP.y, vP.x);
  if((st & 16u) != 0u){ float rd = abs(d - 3.0); float m = 1.0 - smoothstep(0.6, 1.6, rd); rgb = mix(rgb, uAdded, m * (1.0 - fill)); a = max(a, m); if(fill > 0.5) rgb = mix(rgb, uAdded, 0.0); }
  if((st & 64u) != 0u){ float m1 = 1.0 - smoothstep(0.5, 1.4, abs(d - 3.0)); float m2 = 1.0 - smoothstep(0.5, 1.4, abs(d - 6.0)); float m = max(m1, m2); rgb = mix(rgb, uChanged, m * (1.0 - fill)); a = max(a, m); }
  if((st & 32u) != 0u){ float dash = step(0.0, sin(ang * 8.0 + d)); float m = (1.0 - smoothstep(0.6, 1.6, abs(d - 3.0))) * dash; rgb = mix(rgb, uRemoved, m * (1.0 - fill)); a = max(a, m); }
  // reference (a stub for an endpoint the graph does not hold): hollow, with a dashed outline
  if((ex & 64u) != 0u){ float dash = step(0.0, sin(ang * 10.0)); float m = (1.0 - smoothstep(0.5, 1.4, abs(d - 0.0))) * dash; a = a * 0.35; rgb = mix(rgb, vec3(0.78, 0.82, 0.88), m * 0.9); a = max(a, m); }
  if((st & 128u) != 0u){ float m = 1.0 - smoothstep(0.8, 1.8, abs(d - 4.0)); rgb = mix(rgb, uIdentity, m * (1.0 - fill)); a = max(a, m); }
  if((ex & 1u) != 0u){ float dash = step(0.0, sin(ang * 5.0)); float m = (1.0 - smoothstep(0.7, 1.7, abs(d - 5.0))) * dash; rgb = mix(rgb, uSource, m * (1.0 - fill)); a = max(a, m); }
  if((st & 8u) != 0u){ float m = 1.0 - smoothstep(0.7, 1.7, abs(d - 8.5)); rgb = mix(rgb, uSel, m * (1.0 - fill) * 0.7); a = max(a, m * 0.7); }
  // outline: selected thick, hover medium
  if((st & 1u) != 0u){ float m = 1.0 - smoothstep(1.2, 2.2, abs(d + 0.5)); rgb = mix(rgb, uSel, m); a = max(a, m); float halo = 1.0 - smoothstep(0.0, 6.0, d); a = max(a, halo * 0.25 * step(0.0, d)); }
  if((st & 2u) != 0u){ float m = 1.0 - smoothstep(0.8, 1.6, abs(d + 0.2)); rgb = mix(rgb, uSel, m); a = max(a, m); }
  if((ex & 8u) != 0u){ float m = 1.0 - smoothstep(0.8, 1.8, abs(d - 3.0)); rgb = mix(rgb, uRemoved, m * (1.0 - fill)); a = max(a, m); }
  if((ex & 16u) != 0u){ float m = 1.0 - smoothstep(0.8, 1.8, abs(d - 3.0)); rgb = mix(rgb, uChanged, m * (1.0 - fill)); a = max(a, m); }
  if(uTel == 1 && shape != 6u){
    // ring gauge: track plus an arc from the top, clockwise, filled to the channel value; warning state is thicker and segmented
    float warn = vTel.z;
    float inner = 2.5, outer = warn > 0.5 ? 6.5 : 4.5;
    float g = abs(d - (inner + outer) * 0.5) - (outer - inner) * 0.5;
    float track = 1.0 - smoothstep(-0.4, 0.6, g);
    float ang = (atan(vP.x, -vP.y) + 3.14159265) / 6.2831853;
    float on = step(ang, vTel.x);
    float seg = warn > 0.5 ? step(0.0, sin(ang * 90.0)) : 1.0;
    vec3 gc = warn > 0.5 ? uRemoved : uChanged;
    if(d > 0.0 && vHalf.x >= 3.0){
      rgb = mix(rgb, uSel, track * 0.22 * (1.0 - fill)); a = max(a, track * 0.22);
      float arc = track * on * seg;
      rgb = mix(rgb, gc, arc * (1.0 - fill)); a = max(a, arc);
    }
    if(warn > 0.5) rgb = mix(rgb, uRemoved, 0.55 * fill);
  }
  // light dark edge to separate shapes from edges below
  float rim = (1.0 - smoothstep(-1.4, -0.4, d)) * 0.0;
  a *= uDim;
  if(a < 0.01) discard;
  outColor = vec4(rgb - rim, a);
}`;

const EDGE_VS = COMMON + `
layout(location=0) in ivec2 aEnds;
layout(location=1) in vec4 aColor;
layout(location=2) in uint aMeta;
layout(location=3) in float aT0;   // telemetry: width
layout(location=4) in float aT1;   // telemetry: flow speed
layout(location=5) in float aT2;   // telemetry: state
uniform float uWidth; uniform float uEdgeAlpha; uniform int uWire; uniform int uSegments; uniform int uSkip; uniform int uTel; uniform float uWarnE;
out vec4 vColor; out float vDist; out float vSide; flat out uint vMeta; out vec2 vTel;
void main(){
  uint style = aMeta & 255u;
  if(style == 0u || aEnds.x < 0 || aEnds.y < 0){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  vec2 a = toScreen(posOf(aEnds.x).xy), b = toScreen(posOf(aEnds.y).xy);
  uint st = (aMeta >> 8) & 255u;
  if(uWire == 0){
    // reject edges whose endpoints are both beyond the same viewport side, and thin out dense scenes while moving
    vec2 hv = uRes * 0.5 + 8.0;
    if((a.x < -hv.x && b.x < -hv.x) || (a.x > hv.x && b.x > hv.x) || (a.y < -hv.y && b.y < -hv.y) || (a.y > hv.y && b.y > hv.y)){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    if(uSkip > 1 && st == 0u && int((uint(gl_InstanceID) * 2654435761u) >> 8) % uSkip != 0){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  }
  float w = uWidth * 0.5;
  if(uTel == 1) w = max(w, 0.3 + aT0 * 3.4);                     // half width in px follows the width channel
  if((st & 5u) != 0u) w = max(w, 1.4);
  if((st & 1u) != 0u) w = max(w, 2.0);
  if((st & 240u) != 0u) w = max(w, 1.6);
  vec2 p; vec2 n; float dist;
  int side = gl_VertexID & 1;
  float sgn = float(side) * 2.0 - 1.0;
  if(uWire == 0){
    vec2 d = b - a; float len = length(d);
    if(len < 0.01){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    vec2 dir = d / len; n = vec2(-dir.y, dir.x);
    float along = float((gl_VertexID >> 1) & 1);
    p = mix(a, b, along); dist = along * len;
  } else {
    int seg = gl_VertexID >> 1;
    float t = float(seg) / float(uSegments);
    float dx = clamp(abs(b.x - a.x) * 0.5, 24.0, 260.0);
    vec2 c1 = a + vec2(dx, 0.0), c2 = b - vec2(dx, 0.0);
    float u = 1.0 - t;
    p = u*u*u*a + 3.0*u*u*t*c1 + 3.0*u*t*t*c2 + t*t*t*b;
    vec2 tg = 3.0*u*u*(c1 - a) + 6.0*u*t*(c2 - c1) + 3.0*t*t*(b - c2);
    float tl = length(tg); tg = tl > 0.0001 ? tg / tl : vec2(1.0, 0.0);
    n = vec2(-tg.y, tg.x);
    dist = t * (length(b - a) * 1.2 + dx);
  }
  float pad = w + 1.0;
  gl_Position = toClip(p + n * sgn * pad);
  vColor = aColor; vColor.a *= uEdgeAlpha; vDist = dist; vSide = sgn * pad / max(w, 0.0001); vMeta = aMeta;
  vTel = vec2(aT1, (uTel == 1 && aT2 >= uWarnE) ? 1.0 : 0.0);
  if(uTel == 1) vColor.a = max(vColor.a, 0.12 + 0.55 * aT0);
  if((st & 5u) != 0u) vColor.a = max(vColor.a, 0.9);
}`;

const EDGE_FS = `#version 300 es
precision highp float; precision highp int;
in vec4 vColor; in float vDist; in float vSide; flat in uint vMeta; in vec2 vTel;
uniform float uDim; uniform vec3 uSel; uniform vec3 uAdded; uniform vec3 uRemoved; uniform vec3 uChanged; uniform vec3 uIdentity; uniform float uWidth;
uniform int uTel; uniform float uTime; uniform int uFlowOn; uniform vec3 uFlowColor;
out vec4 outColor;
void main(){
  uint style = vMeta & 255u; uint st = (vMeta >> 8) & 255u;
  if(style == 2u && fract(vDist / 12.0) > 0.58) discard;
  float edge = 1.0 - smoothstep(0.7, 1.0, abs(vSide));
  vec3 rgb = vColor.rgb; float a = vColor.a * edge;
  if(uTel == 1){
    if(vTel.y > 0.5){
      // warning state: coral and dashed, so the state does not rely on color
      if(fract(vDist / 14.0) > 0.6) discard;
      rgb = mix(rgb, uRemoved, 0.92); a = max(a, 0.85 * edge);
    } else if(uFlowOn == 1 && vTel.x > 0.02){
      // moving pulses; speed follows the flow channel
      float phase = fract(vDist / 22.0 - uTime * (0.25 + vTel.x * 2.4));
      float pulse = smoothstep(0.0, 0.12, phase) * (1.0 - smoothstep(0.12, 0.5, phase));
      rgb = mix(rgb, uFlowColor, pulse * 0.85); a = a * (0.6 + 0.4 * pulse);
    }
  }
  if((st & 16u) != 0u) rgb = uAdded;
  if((st & 32u) != 0u) rgb = uRemoved;
  if((st & 64u) != 0u) rgb = uChanged;
  if((st & 128u) != 0u) rgb = uIdentity;
  if((st & 5u) != 0u) rgb = mix(rgb, uSel, 0.75);
  if((st & 240u) != 0u) a = max(a, 0.85 * edge);
  a *= uDim;
  if(a < 0.01) discard;
  outColor = vec4(rgb, a);
}`;

const ARROW_VS = COMMON + `
layout(location=0) in ivec2 aEnds;
layout(location=1) in vec4 aColor;
layout(location=2) in uint aMeta;
uniform float uEdgeAlpha; uniform float uArrowSize;
out vec4 vColor; flat out uint vMeta;
void main(){
  uint style = aMeta & 255u;
  if(style == 0u || aEnds.x < 0 || aEnds.y < 0 || aEnds.x == aEnds.y){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  vec4 pa = posOf(aEnds.x), pb = posOf(aEnds.y);
  vec2 a = toScreen(pa.xy), b = toScreen(pb.xy);
  vec2 d = b - a; float len = length(d);
  float rpx = max(pb.z * uZoom, 3.0) + 2.0;
  float L = uArrowSize;
  if(len < rpx + L * 1.5){ gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
  vec2 dir = d / len; vec2 n = vec2(-dir.y, dir.x);
  vec2 tip = b - dir * rpx;
  vec2 pt;
  if(gl_VertexID == 0) pt = tip;
  else if(gl_VertexID == 1) pt = tip - dir * L + n * L * 0.42;
  else pt = tip - dir * L - n * L * 0.42;
  gl_Position = toClip(pt);
  vColor = aColor; vColor.a = max(vColor.a * uEdgeAlpha, 0.35) ; vMeta = aMeta;
}`;

const ARROW_FS = `#version 300 es
precision highp float; precision highp int;
in vec4 vColor; flat in uint vMeta;
uniform float uDim; uniform vec3 uSel; uniform vec3 uAdded; uniform vec3 uRemoved; uniform vec3 uChanged; uniform vec3 uIdentity;
out vec4 outColor;
void main(){
  uint st = (vMeta >> 8) & 255u;
  vec3 rgb = vColor.rgb; float a = vColor.a;
  if((st & 16u) != 0u) rgb = uAdded;
  if((st & 32u) != 0u) rgb = uRemoved;
  if((st & 64u) != 0u) rgb = uChanged;
  if((st & 128u) != 0u) { rgb = uIdentity; a = 1.0; }
  if((st & 5u) != 0u) { rgb = mix(rgb, uSel, 0.75); a = 1.0; }
  a *= uDim;
  outColor = vec4(rgb, a);
}`;

interface Program { prog: WebGLProgram; u: Record<string, WebGLUniformLocation | null> }

function compile(gl: WebGL2RenderingContext, vs: string, fs: string, uniforms: string[]): Program {
  const mk = (type: number, src: string) => {
    const s = gl.createShader(type)!;
    gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(`shader: ${gl.getShaderInfoLog(s)}`);
    return s;
  };
  const prog = gl.createProgram()!;
  gl.attachShader(prog, mk(gl.VERTEX_SHADER, vs));
  gl.attachShader(prog, mk(gl.FRAGMENT_SHADER, fs));
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(`link: ${gl.getProgramInfoLog(prog)}`);
  const u: Record<string, WebGLUniformLocation | null> = {};
  for (const name of uniforms) u[name] = gl.getUniformLocation(prog, name);
  return { prog, u };
}

interface InstanceBuffer { vao: WebGLVertexArrayObject; buf: WebGLBuffer; bytes: number; tel: WebGLBuffer[]; telBytes: number }

export class GraphRenderer {
  readonly gl: WebGL2RenderingContext;
  private nodeProg: Program; private edgeProg: Program; private arrowProg: Program;
  private nodes: InstanceBuffer; private edges: InstanceBuffer; private arrows: InstanceBuffer;
  private hotNodes: InstanceBuffer; private hotEdges: InstanceBuffer; private hotArrows: InstanceBuffer;
  private tex: WebGLTexture;
  private texRows = 0;
  private texData = new Float32Array(0);
  private scene: Scene | null = null;
  private hotNodeSlots: number[] = [];
  private hotEdgeSlots: number[] = [];
  private hotNodeData = new Uint32Array(0);
  private hotEdgeData = new Uint32Array(0);
  private hotNodeT: Uint8Array[] = [new Uint8Array(256), new Uint8Array(256), new Uint8Array(256)];
  private hotEdgeT: Uint8Array[] = [new Uint8Array(256), new Uint8Array(256), new Uint8Array(256)];
  private hotDirty = true;
  /** time spent in the last render call on the CPU side, ms */
  lastCpuMs = 0;

  constructor(readonly canvas: HTMLCanvasElement) {
    const gl = canvas.getContext("webgl2", { antialias: false, alpha: false, powerPreference: "high-performance", preserveDrawingBuffer: false });
    if (!gl) throw new Error("WebGL2 is not available");
    this.gl = gl;
    const common = ["uPos", "uRes", "uCenter", "uZoom", "uDim", "uSel", "uAdded", "uRemoved", "uChanged"];
    this.nodeProg = compile(gl, NODE_VS, NODE_FS, [...common, "uMinPx", "uIdentity", "uSource", "uTel", "uWarnN"]);
    this.edgeProg = compile(gl, EDGE_VS, EDGE_FS, [...common, "uIdentity", "uWidth", "uEdgeAlpha", "uWire", "uSegments", "uSkip", "uTel", "uWarnE", "uTime", "uFlowOn", "uFlowColor"]);
    this.arrowProg = compile(gl, ARROW_VS, ARROW_FS, [...common, "uIdentity", "uEdgeAlpha", "uArrowSize"]);
    this.nodes = this.makeNodeBuffer(); this.hotNodes = this.makeNodeBuffer();
    this.edges = this.makeEdgeBuffer(); this.hotEdges = this.makeEdgeBuffer();
    this.arrows = this.makeEdgeBuffer(this.edges); this.hotArrows = this.makeEdgeBuffer(this.hotEdges);
    this.tex = gl.createTexture()!;
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
  }

  private makeNodeBuffer(): InstanceBuffer {
    const { gl } = this;
    const vao = gl.createVertexArray()!; const buf = gl.createBuffer()!;
    gl.bindVertexArray(vao); gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    const stride = WORDS_NODE * 4;
    gl.enableVertexAttribArray(0); gl.vertexAttribIPointer(0, 1, gl.INT, stride, 0); gl.vertexAttribDivisor(0, 1);
    gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 2, gl.FLOAT, false, stride, 4); gl.vertexAttribDivisor(1, 1);
    gl.enableVertexAttribArray(2); gl.vertexAttribPointer(2, 4, gl.UNSIGNED_BYTE, true, stride, 12); gl.vertexAttribDivisor(2, 1);
    gl.enableVertexAttribArray(3); gl.vertexAttribIPointer(3, 1, gl.UNSIGNED_INT, stride, 16); gl.vertexAttribDivisor(3, 1);
    const tel = this.telemetryBuffers(4);
    gl.bindVertexArray(null);
    return { vao, buf, bytes: 0, tel, telBytes: 0 };
  }

  /** Three byte-per-instance telemetry attributes (normalized to 0..1 in the shader) starting at `first`. */
  private telemetryBuffers(first: number, shared?: WebGLBuffer[]): WebGLBuffer[] {
    const { gl } = this;
    const tel = shared ?? [0, 1, 2].map(() => gl.createBuffer()!);
    for (let c = 0; c < 3; c++) {
      gl.bindBuffer(gl.ARRAY_BUFFER, tel[c]);
      gl.enableVertexAttribArray(first + c); gl.vertexAttribPointer(first + c, 1, gl.UNSIGNED_BYTE, true, 0, 0); gl.vertexAttribDivisor(first + c, 1);
    }
    return tel;
  }

  /** Edge instance buffer; a second VAO over the same buffer is used for arrowheads. */
  private makeEdgeBuffer(shared?: InstanceBuffer): InstanceBuffer {
    const { gl } = this;
    const vao = gl.createVertexArray()!; const buf = shared?.buf ?? gl.createBuffer()!;
    gl.bindVertexArray(vao); gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    const stride = WORDS_EDGE * 4;
    gl.enableVertexAttribArray(0); gl.vertexAttribIPointer(0, 2, gl.INT, stride, 0); gl.vertexAttribDivisor(0, 1);
    gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 4, gl.UNSIGNED_BYTE, true, stride, 8); gl.vertexAttribDivisor(1, 1);
    gl.enableVertexAttribArray(2); gl.vertexAttribIPointer(2, 1, gl.UNSIGNED_INT, stride, 12); gl.vertexAttribDivisor(2, 1);
    const tel = this.telemetryBuffers(3, shared?.tel);
    gl.bindVertexArray(null);
    return { vao, buf, bytes: 0, tel, telBytes: 0 };
  }

  setScene(scene: Scene): void {
    this.scene = scene;
    scene.nodeDirty = { lo: 0, hi: scene.nodeCount };
    scene.edgeDirty = { lo: 0, hi: scene.edgeCount };
    scene.positionsDirty = true;
    this.nodes.bytes = 0; this.edges.bytes = 0;
    this.nodes.telBytes = 0; this.edges.telBytes = 0;
    scene.telemetryDirty = true;
    this.hotDirty = true;
  }

  /** Slots drawn again on top of everything (selection, hover, neighbours). */
  setHot(nodeSlots: number[], edgeSlots: number[]): void {
    this.hotNodeSlots = nodeSlots; this.hotEdgeSlots = edgeSlots; this.hotDirty = true;
  }

  private upload(ib: InstanceBuffer, data: Uint32Array, lo: number, hi: number, words: number): void {
    const { gl } = this;
    gl.bindBuffer(gl.ARRAY_BUFFER, ib.buf);
    const need = data.byteLength;
    if (ib.bytes !== need) { gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW); ib.bytes = need; }
    else gl.bufferSubData(gl.ARRAY_BUFFER, lo * words * 4, data, lo * words, (hi - lo) * words);
  }

  private syncScene(): void {
    const s = this.scene!;
    const { gl } = this;
    if (s.nodeDirty && s.nodeCap) {
      this.upload(this.nodes, s.nodeU32, s.nodeDirty.lo, Math.min(s.nodeDirty.hi, s.nodeCap), WORDS_NODE);
      s.nodeDirty = null;
    }
    if (s.edgeDirty && s.edgeCap) {
      this.upload(this.edges, s.edgeU32, s.edgeDirty.lo, Math.min(s.edgeDirty.hi, s.edgeCap), WORDS_EDGE);
      s.edgeDirty = null;
      this.hotDirty = true;
    }
    if (s.telemetryDirty) {
      const up = (ib: InstanceBuffer, chans: Uint8Array[]) => {
        for (let c = 0; c < 3; c++) {
          gl.bindBuffer(gl.ARRAY_BUFFER, ib.tel[c]);
          if (ib.telBytes !== chans[c].byteLength) gl.bufferData(gl.ARRAY_BUFFER, chans[c], gl.DYNAMIC_DRAW);
          else gl.bufferSubData(gl.ARRAY_BUFFER, 0, chans[c]);
        }
        ib.telBytes = chans[0].byteLength;
      };
      up(this.nodes, s.nodeT); up(this.edges, s.edgeT);
      s.telemetryDirty = false;
      this.hotDirty = true;
    }
    if (s.positionsDirty && s.nodeCap) {
      const rows = Math.ceil(s.nodeCap / TEX_W);
      const needed = rows * TEX_W * 4;
      if (this.texData.length !== needed) this.texData = new Float32Array(needed);
      const t = this.texData, p = s.positions, r = s.radius;
      for (let i = 0; i < s.nodeCap; i++) { t[4 * i] = p[2 * i]; t[4 * i + 1] = p[2 * i + 1]; t[4 * i + 2] = r[i]; }
      gl.bindTexture(gl.TEXTURE_2D, this.tex);
      if (rows !== this.texRows) { gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, TEX_W, rows, 0, gl.RGBA, gl.FLOAT, t); this.texRows = rows; }
      else gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, TEX_W, rows, gl.RGBA, gl.FLOAT, t);
      s.positionsDirty = false;
    }
    if (this.hotDirty) {
      const nn = this.hotNodeSlots.length, ne = this.hotEdgeSlots.length;
      if (this.hotNodeData.length < nn * WORDS_NODE) this.hotNodeData = new Uint32Array(Math.max(nn * WORDS_NODE * 2, 1024));
      if (this.hotEdgeData.length < ne * WORDS_EDGE) this.hotEdgeData = new Uint32Array(Math.max(ne * WORDS_EDGE * 2, 1024));
      for (let i = 0; i < nn; i++) {
        const o = this.hotNodeSlots[i] * WORDS_NODE;
        for (let k = 0; k < WORDS_NODE; k++) this.hotNodeData[i * WORDS_NODE + k] = s.nodeU32[o + k];
      }
      for (let i = 0; i < ne; i++) {
        const o = this.hotEdgeSlots[i] * WORDS_EDGE;
        for (let k = 0; k < WORDS_EDGE; k++) this.hotEdgeData[i * WORDS_EDGE + k] = s.edgeU32[o + k];
      }
      for (let c = 0; c < 3; c++) {
        if (this.hotNodeT[c].length < nn) this.hotNodeT[c] = new Uint8Array(Math.max(nn * 2, 256));
        if (this.hotEdgeT[c].length < ne) this.hotEdgeT[c] = new Uint8Array(Math.max(ne * 2, 256));
        for (let i = 0; i < nn; i++) this.hotNodeT[c][i] = s.nodeT[c][this.hotNodeSlots[i]] ?? 0;
        for (let i = 0; i < ne; i++) this.hotEdgeT[c][i] = s.edgeT[c][this.hotEdgeSlots[i]] ?? 0;
        gl.bindBuffer(gl.ARRAY_BUFFER, this.hotNodes.tel[c]); gl.bufferData(gl.ARRAY_BUFFER, this.hotNodeT[c].subarray(0, nn), gl.DYNAMIC_DRAW);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.hotEdges.tel[c]); gl.bufferData(gl.ARRAY_BUFFER, this.hotEdgeT[c].subarray(0, ne), gl.DYNAMIC_DRAW);
      }
      for (const [ib, data, n, w] of [[this.hotNodes, this.hotNodeData, nn, WORDS_NODE], [this.hotEdges, this.hotEdgeData, ne, WORDS_EDGE]] as const) {
        gl.bindBuffer(gl.ARRAY_BUFFER, ib.buf);
        gl.bufferData(gl.ARRAY_BUFFER, data.subarray(0, n * w), gl.DYNAMIC_DRAW);
        ib.bytes = n * w * 4;
      }
      this.hotDirty = false;
    }
  }

  render(p: RenderParams): void {
    const t0 = performance.now();
    const { gl, canvas } = this;
    const pxW = Math.max(1, Math.round(p.width * p.dpr)), pxH = Math.max(1, Math.round(p.height * p.dpr));
    if (canvas.width !== pxW || canvas.height !== pxH) { canvas.width = pxW; canvas.height = pxH; }
    gl.viewport(0, 0, pxW, pxH);
    gl.clearColor(p.background[0], p.background[1], p.background[2], 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    const s = this.scene;
    if (!s) return;
    this.syncScene();
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.tex);

    const setCommon = (pr: Program, dim: number) => {
      gl.useProgram(pr.prog);
      gl.uniform1i(pr.u.uPos, 0);
      gl.uniform2f(pr.u.uRes, p.width, p.height);
      gl.uniform2f(pr.u.uCenter, p.camera.x, p.camera.y);
      gl.uniform1f(pr.u.uZoom, p.camera.zoom);
      gl.uniform1f(pr.u.uDim, dim);
      gl.uniform3fv(pr.u.uSel, p.colors.selected);
      gl.uniform3fv(pr.u.uAdded, p.colors.added);
      gl.uniform3fv(pr.u.uRemoved, p.colors.removed);
      gl.uniform3fv(pr.u.uChanged, p.colors.changed);
      gl.uniform3fv(pr.u.uIdentity, p.colors.identity);
    };
    const edgeVerts = p.wires ? BEZIER_SEGMENTS * 2 + 2 : 4;
    const drawEdges = (ib: InstanceBuffer, count: number, dim: number) => {
      if (count <= 0) return;
      setCommon(this.edgeProg, dim);
      gl.uniform1f(this.edgeProg.u.uWidth, p.edgeWidth);
      gl.uniform1f(this.edgeProg.u.uEdgeAlpha, p.edgeAlpha);
      gl.uniform1i(this.edgeProg.u.uWire, p.wires ? 1 : 0);
      gl.uniform1i(this.edgeProg.u.uSegments, BEZIER_SEGMENTS);
      gl.uniform1i(this.edgeProg.u.uSkip, ib === this.edges ? p.edgeSkip : 1);
      gl.uniform1i(this.edgeProg.u.uTel, p.telemetry.on ? 1 : 0);
      gl.uniform1f(this.edgeProg.u.uWarnE, p.telemetry.warnEdge / 255 - 0.001);
      gl.uniform1f(this.edgeProg.u.uTime, p.telemetry.time);
      gl.uniform1i(this.edgeProg.u.uFlowOn, p.telemetry.flow ? 1 : 0);
      gl.uniform3fv(this.edgeProg.u.uFlowColor, p.telemetry.flowColor);
      gl.bindVertexArray(ib.vao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, edgeVerts, count);
    };
    const drawArrows = (ib: InstanceBuffer, count: number, dim: number) => {
      if (count <= 0 || !p.arrows || p.wires) return;
      setCommon(this.arrowProg, dim);
      gl.uniform1f(this.arrowProg.u.uEdgeAlpha, p.edgeAlpha);
      gl.uniform1f(this.arrowProg.u.uArrowSize, 8);
      gl.bindVertexArray(ib.vao);
      gl.drawArraysInstanced(gl.TRIANGLES, 0, 3, count);
    };
    const drawNodes = (ib: InstanceBuffer, count: number, dim: number) => {
      if (count <= 0) return;
      setCommon(this.nodeProg, dim);
      gl.uniform1f(this.nodeProg.u.uMinPx, p.minNodePx);
      gl.uniform1i(this.nodeProg.u.uTel, p.telemetry.on ? 1 : 0);
      gl.uniform1f(this.nodeProg.u.uWarnN, p.telemetry.warnNode / 255 - 0.001);
      gl.uniform3fv(this.nodeProg.u.uIdentity, p.colors.identity);
      gl.uniform3fv(this.nodeProg.u.uSource, p.colors.source);
      gl.bindVertexArray(ib.vao);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, count);
    };
    // counts can run ahead of the allocated instances when every edge or node of a graph is hidden (all endpoints missing, or all filtered)
    const edges = Math.min(s.edgeCount, s.edgeCap), nodes = Math.min(s.nodeCount, s.nodeCap);
    drawEdges(this.edges, edges, p.dim);
    drawArrows(this.arrows, edges, p.dim);
    drawNodes(this.nodes, nodes, p.dim);
    drawEdges(this.hotEdges, this.hotEdgeSlots.length, 1);
    drawArrows(this.hotArrows, this.hotEdgeSlots.length, 1);
    drawNodes(this.hotNodes, this.hotNodeSlots.length, 1);
    gl.bindVertexArray(null);
    this.lastCpuMs = performance.now() - t0;
  }

  dispose(): void {
    this.gl.getExtension("WEBGL_lose_context")?.loseContext();
  }
}
