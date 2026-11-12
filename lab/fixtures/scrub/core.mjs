// The pure core of the fixture scrubber: aliases, shape detectors and a walk over parsed JSON records.
// Nothing here reads files. Every alias is an HMAC of the lower-cased input under a secret salt, so the same real
// value gets the same alias in every source and in every spelling (upper or lower case), and an alias keeps the
// shape of its input (a GUID stays a GUID, an e-mail stays an e-mail).
import { createHmac } from 'node:crypto';
import { actionFor } from './rules.mjs';

// ---------- shapes ----------
const HEX = '[0-9a-f]';
export const GUID = new RegExp(`(?<!${HEX})${HEX}{8}-${HEX}{4}-${HEX}{4}-${HEX}{4}-${HEX}{12}(?!${HEX})`, 'gi');
// 32 hex digits without dashes (role assignment ids, internal ids, machine ids); a content hash after `sha256:` is not an identity
export const HEX32 = new RegExp(`(?<![0-9a-z])(?<!sha256:)${HEX}{32}(?![0-9a-z])`, 'gi');
export const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
export const MAC = /(?<![0-9a-f:])(?:[0-9a-f]{2}:){5}[0-9a-f]{2}(?![0-9a-f:])/gi;
// NIC MACs in Network Watcher records are 12 bare hex digits next to a macAddress field name
const MAC_BARE = /(macaddress(?:"\s*:\s*"|=))([0-9a-f]{12})/gi;
export const IPV4 = /(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.]|\w)/g;
export const IPV6 = /(?<![:\w])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?)(?![:\w])/gi;
const AKS_HOST = /[a-z0-9-]+\.((?:portal\.)?hcp\.[a-z0-9]+\.azmk8s\.io)/gi;
export const KEEP_IP = (ip) => /^(0\.|127\.|169\.254\.|255\.)/.test(ip) || ip === '168.63.129.16';
// Azure region names are public and stay readable
export const isRegion = (n) => /^[a-z]+(?:central|east|west|north|south)[a-z0-9]*$/.test(n);
// Names the platform itself creates: not identifying
// and what the lab itself creates (its four hubs and its demo namespace)
export const GENERIC_NAMES = new Set(['kubernetes', 'default', 'systempool', 'agentpool', 'networkwatcherrg', 'sattle-activity', 'sattle-aks', 'sattle-hubble', 'sattle-ingest', 'sattle-demo']);
// Resource types whose "name" is a vocabulary (metric names), never a resource
const VOCABULARY_TYPES = new Set(['microsoft.insights/metrics', 'microsoft.insights/diagnosticsettings']);
const WORD = /[A-Za-z0-9_]/;

const lc = (s) => s.toLowerCase();
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const isIdShape = (s) => new RegExp(`^(?:${GUID.source}|${HEX32.source})$`, 'i').test(s);

// A name that can be found inside free text without risk: long enough and not an ordinary word.
// Plain words (a VM called `memory`) are only replaced where they stand as a name in a resource id.
export const distinctive = (n) => n.length >= 6 && /[-_.0-9 ]/.test(n);

// What else a name looks like in the capture: `:2D` for dashes (Activity Log operation ids), and without a short
// type prefix (`rg-`, `aks-`) as labels and annotations use it. (The cluster name without dashes, as Cilium writes
// it, is not derived: it is read from the flows, see harvest.)
export const variantsOf = (name) => {
  const out = new Set([name]);
  if (name.includes('-')) out.add(name.replaceAll('-', ':2D'));
  const bare = name.replace(/^[a-z]{2,5}[-_]/i, '');
  if (bare !== name && distinctive(bare) && bare.length >= 8) out.add(bare);
  return [...out];
};

// ---------- aliases ----------
export function aliases(salt) {
  const h = (kind, s) => createHmac('sha256', salt).update(`${kind}:${lc(s)}`).digest('hex');
  const bare = (s) => lc(s).replaceAll('-', '');
  return {
    // a GUID and the same 32 digits without dashes share one alias, written in the shape they came in
    guid: (g) => { const x = h('id', bare(g)); return `${x.slice(0, 8)}-${x.slice(8, 12)}-4${x.slice(13, 16)}-a${x.slice(17, 20)}-${x.slice(20, 32)}`; },
    hex32: (g) => h('id', bare(g)).slice(0, 32),
    macBare: (m) => h('macbare', m).slice(0, 12).toUpperCase(),
    mac: (m) => { const x = h('mac', m); return `02:${x.slice(0, 2)}:${x.slice(2, 4)}:${x.slice(4, 6)}:${x.slice(6, 8)}:${x.slice(8, 10)}`; },
    ipv4: (ip) => {
      const x = h('ip', ip); const b = (i) => parseInt(x.slice(i * 2, i * 2 + 2), 16);
      return /^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)/.test(ip) ? `10.${b(0)}.${b(1)}.${b(2)}` : `198.${18 + (b(0) & 1)}.${b(1)}.${b(2)}`;
    },
    ipv6: (ip) => { const x = h('ip6', ip); return `fd00:${x.slice(0, 4)}:${x.slice(4, 8)}::${x.slice(8, 12)}`; },
    email: (e) => `user-${h('email', e).slice(0, 8)}@example.com`,
    // a name keeps nothing of its text; `mc_` marks the AKS node resource group and carries no information
    name: (n) => `${/^mc_/i.test(n) ? 'mc_' : 'res-'}${h('name', n).slice(0, 8)}`,
    value: (v) => `redacted-${h('value', v).slice(0, 8)}`,
  };
}

// ---------- resource ids ----------
// Segment indexes that hold a resource name inside an id split on `/`: after resourceGroups, and every second segment
// after `providers/<namespace>`. Types and well-known extension names (metrics) are not names.
export function nameSlots(parts) {
  const slots = [];
  const low = parts.map(lc);
  for (let i = 0; i < parts.length; i++) {
    if (low[i] === 'resourcegroups' && parts[i + 1]) slots.push(i + 1);
    if (low[i] === 'providers' && parts[i + 1]) {
      for (let j = i + 2; j < parts.length && low[j] !== 'providers'; j += 2) {
        if (parts[j + 1] === undefined) break;
        if (!VOCABULARY_TYPES.has(`${low[i + 1]}/${low[j]}`)) slots.push(j + 1);
      }
    }
  }
  return slots.filter((i) => !isIdShape(parts[i]));
}
// An id as it appears in a string, ending before quotes, white space and closing brackets.
export const ARM_REGION = /\/subscriptions\/[^\s"'<>\\]*/gi;
const trimEnd = (s) => { const m = s.match(/[).,;\]}]+$/); return m ? [s.slice(0, -m[0].length), m[0]] : [s, '']; };
export const namesInArmId = (region) => { const [id] = trimEnd(region); const p = id.split('/'); return nameSlots(p).map((i) => p[i]); };

// ---------- the scrubber ----------
// names, persons and clusters are harvested from the capture; salt: secret string.
// clusters are the cluster names on Hubble flows: always looked for in free text, also when they look like a word.
export function makeScrubber({ salt, names = [], persons = [], clusters = [], literals = [] }) {
  const a = aliases(salt);
  const generic = (n) => GENERIC_NAMES.has(lc(n)) || isRegion(lc(n));
  // a distinctive name is looked for in free text in every spelling; a name that is a plain word is not
  const nameSet = new Set([...names].filter((n) => n && !generic(n) && !isIdShape(n) && distinctive(n)).flatMap(variantsOf).map(lc));
  for (const c of clusters) if (c && !generic(c)) nameSet.add(lc(c));
  const personSet = new Set([...persons, ...literals].filter((p) => p && p.length >= 4).map(lc));
  // free-text patterns as whole words, longest first
  const free = [...[...nameSet].map((t) => [t, 'name']), ...[...personSet].map((t) => [t, 'person'])]
    .sort((x, y) => y[0].length - x[0].length);
  const kind = new Map(free.map(([t, k]) => [t, k]));
  // a whole word: no letter, digit or underscore before it, unless that is the end of an escape such as `:2F`
  // (Activity Log writes `/` as `:2F` in job ids)
  const freeRe = free.length ? new RegExp(`(?:(?<![A-Za-z0-9_])|(?<=:[0-9A-Fa-f]{2}))(?:${free.map(([t]) => escapeRe(t)).join('|')})(?![A-Za-z0-9_])`, 'gi') : null;
  const personAlias = (p) => (/^[^@\s]+@[^@\s]+$/.test(p) ? a.email(p) : isIdShape(p) ? (p.includes('-') ? a.guid(p) : a.hex32(p)) : a.value(p));

  const armRegion = (region) => {
    const [id, tail] = trimEnd(region);
    const parts = id.split('/');
    for (const i of nameSlots(parts)) if (!generic(parts[i])) parts[i] = a.name(parts[i]);
    return parts.join('/') + tail;
  };

  const text = (s) => {
    let t = s;
    t = t.replace(EMAIL, (e) => a.email(e));
    if (t.includes('/') && /\/subscriptions\//i.test(t)) t = t.replace(ARM_REGION, armRegion);
    if (freeRe) t = t.replace(freeRe, (m) => (kind.get(lc(m)) === 'name' ? a.name(lc(m)) : personAlias(m)));
    t = t.replace(GUID, (g) => a.guid(g));
    t = t.replace(HEX32, (g) => a.hex32(g));
    t = t.replace(MAC_BARE, (_, pre, m) => `${pre}${a.macBare(m)}`);
    t = t.replace(MAC, (m) => a.mac(m));
    t = t.replace(IPV6, (ip) => a.ipv6(ip));
    t = t.replace(IPV4, (ip) => (KEEP_IP(ip) ? ip : a.ipv4(ip)));
    t = t.replace(AKS_HOST, (_, rest) => `${a.name(rest).slice(4)}.${rest}`);
    return t;
  };

  // one value by its rule: keep, text (detectors and names), alias (the whole value), name (the value is a name)
  const value = (v, path, source) => {
    if (typeof v !== 'string') return v;
    switch (actionFor(path, source)) {
      case 'keep': return v;
      case 'alias': return isIdShape(v) || /@/.test(v) ? text(v) : a.value(v);
      case 'name': return generic(v) || isIdShape(v) ? text(v) : a.name(v);
      default: return text(v);
    }
  };

  const walk = (v, path, source) => {
    if (Array.isArray(v)) return v.map((x) => walk(x, `${path}[]`, source));
    if (v && typeof v === 'object') {
      return Object.fromEntries(Object.entries(v).filter(([k]) => k !== 'managedFields').map(([k, x]) => [k, walk(x, `${path}.${k}`, source)]));
    }
    return value(v, path, source);
  };
  return (record) => walk(record, '', record.source ?? '');
}

export const wordBoundary = (text, i, len) => !WORD.test(text[i - 1] ?? '') && !WORD.test(text[i + len] ?? '');
