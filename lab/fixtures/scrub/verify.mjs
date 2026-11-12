// Independent check of finished fixtures. It shares no detector and no rule with the scrubber: it builds its own deny
// list from the capture (every id, address and e-mail written anywhere in it, the names in resource ids, name fields
// and node objects, the people behind claims and callers, the values of tags) and looks for each in the fixtures.
// It reports counts per kind, never a value.
import { createReadStream, readdirSync, readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { join } from 'node:path';

const lc = (s) => s.toLowerCase();
const GUID_RE = /(?<![0-9a-fA-F])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![0-9a-fA-F])/g;
const HEX32_RE = /(?<![0-9a-zA-Z:])[0-9a-fA-F]{32}(?![0-9a-zA-Z])/g;
const MAIL_RE = /[\w.+%-]+@[\w-]+(?:\.[\w-]+)+/g;
const IP4_RE = /(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])/g;
const IP6_RE = /(?<![:\w])(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{0,4}(?![:\w])/g;
const MAC_RE = /(?<![0-9a-fA-F:])(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}(?![0-9a-fA-F:])/g;
const PLATFORM_IP = /^(0\.|127\.|169\.254\.|255\.|168\.63\.129\.16$)/;
// names the platform or the lab itself creates (the lab's four hubs and its demo namespace): not identifying
const PLATFORM_NAMES = new Set(['kubernetes', 'default', 'systempool', 'agentpool', 'networkwatcherrg', 'sattle-activity', 'sattle-aks', 'sattle-hubble', 'sattle-ingest', 'sattle-demo']);
const SAFE_CLAIMS = new Set(['aud', 'iss', 'ver', 'idtyp', 'appidacr', 'acr', 'amr', 'exp', 'iat', 'nbf', 'scp', 'xms_act_fct', 'appid', 'wids', 'groups', 'sid', 'xms_aud_guid', 'xms_mirid', 'correlationid', 'http://schemas.microsoft.com/identity/claims/identityprovider', 'http://schemas.microsoft.com/identity/claims/objectidentifier', 'http://schemas.microsoft.com/identity/claims/tenantid']);
const SAFE_TAGS = new Set(['project']);
const wordChar = /[a-z0-9_]/;

const files = (d) => readdirSync(d, { withFileTypes: true }).flatMap((x) => (x.isDirectory() ? files(join(d, x.name)) : x.name.endsWith('.ndjson') ? [join(d, x.name)] : []));
const leaves = (v, path, f) => {
  if (Array.isArray(v)) v.forEach((x) => leaves(x, `${path}[]`, f));
  else if (v && typeof v === 'object') for (const [k, x] of Object.entries(v)) leaves(x, `${path}.${k}`, f);
  else if (typeof v === 'string') f(v, path);
};
const isPlainWord = (n) => /^[a-z]+$/i.test(n);

// ---------- the deny list, from the capture ----------
// Three kinds of entries:
//   ids     ids, addresses and e-mails written anywhere: looked for as text, anywhere in the fixtures
//   names   distinctive names and people: looked for as whole words in the fixtures
//   where   values that are also ordinary words (a VM called `memory`, a tag value, a claim): only an error at the place
//           they came from (a name position in a resource id, a tag, a claim, a Resource Graph name), because the same
//           word is vocabulary elsewhere
const REGION = /^[a-z]+(?:central|east|west|north|south)[a-z0-9]*$/;
const distinctive = (n) => n.length >= 6 && /[-_.0-9@ ]/.test(n);

// resource names inside the ids of one string: after resourceGroups and in every type/name pair after a provider
function* armNames(s) {
  for (const region of s.match(/\/subscriptions\/[^\s"'<>\\]*/gi) ?? []) {
    const [head, ...providers] = region.split(/\/providers\//i);
    const rg = head.match(/\/resourcegroups\/([^/]+)/i)?.[1];
    if (rg) yield rg;
    for (const p of providers) {
      const seg = p.replace(/[).,;\]}]+$/, '').split('/'); // namespace, then type and name pairs
      for (let i = 2; i < seg.length; i += 2) if (!/^(metrics|diagnosticsettings)$/i.test(seg[i - 1])) yield seg[i];
    }
  }
}
const claimOf = (path) => path.match(/\.claims\.(.+)$/)?.[1];
const tagOf = (path) => path.match(/\.tags\.([^.[]+)$/)?.[1];

export async function denyList(captureDir, { literals = (process.env.SCRUB_LITERALS ?? '').split('\n') } = {}) {
  const ids = new Set(); const names = new Set(); const where = { slot: new Set(), tag: new Set(), claim: new Set(), arg: new Set() };
  const skip = (n) => !n || PLATFORM_NAMES.has(lc(n)) || REGION.test(lc(n));
  const addName = (n, place) => { if (skip(n)) return; where[place].add(lc(n)); if (distinctive(n)) names.add(lc(n)); };
  const addPerson = (p) => { if (!p) return; names.add(lc(p)); if (p.includes('@')) { const l = lc(p.split('@')[0]); names.add(l); names.add(l.split('_')[0]); } };
  for (const f of files(captureDir)) {
    const raw = f.slice(captureDir.length + 1);
    for await (const line of createInterface({ input: createReadStream(f), crlfDelay: Infinity })) {
      if (!line) continue;
      for (const m of line.match(GUID_RE) ?? []) { ids.add(lc(m)); ids.add(lc(m).replaceAll('-', '')); }
      for (const m of line.match(HEX32_RE) ?? []) ids.add(lc(m));
      for (const m of line.match(MAIL_RE) ?? []) ids.add(lc(m));
      for (const m of line.match(IP4_RE) ?? []) if (!PLATFORM_IP.test(m)) ids.add(m);
      for (const m of line.match(IP6_RE) ?? []) if (m.length > 4 && (m.match(/:/g) ?? []).length >= 3) ids.add(lc(m));
      for (const m of line.match(MAC_RE) ?? []) ids.add(lc(m));
      if (raw.startsWith('hubble/')) { // flows: the address scan above, and the cluster name in Cilium's own spelling
        for (const m of line.matchAll(/"cluster_name"\s*:\s*"([^"]+)"/g)) if (!skip(m[1])) names.add(lc(m[1]));
        continue;
      }
      const r = JSON.parse(line);
      leaves(r, '', (s, path) => {
        for (const n of armNames(s)) addName(n, 'slot');
        const claim = claimOf(path);
        if (claim && !SAFE_CLAIMS.has(lc(claim)) && s) { where.claim.add(lc(s)); if (distinctive(s)) names.add(lc(s)); if (s.includes('@')) addPerson(s); }
        const tag = tagOf(path);
        if (tag && !SAFE_TAGS.has(tag) && s) { where.tag.add(lc(s)); if (distinctive(s) && /\d/.test(s)) names.add(lc(s)); } // Kubernetes tags such as namespace/service are workload names, not owner text
        if (/\.caller$/.test(path) && s.includes('@')) addPerson(s);
        if (r.source === 'arg' && /^\.payload\.(name|resourceGroup)$/.test(path)) addName(s, 'arg');
        if (/\.managementGroupAncestorsChain\[\]\.displayName$/.test(path) && s !== 'Tenant Root Group') names.add(lc(s));
      });
      const obj = r.payload?.object ?? r.payload;
      if ((obj?.kind === 'Node' || r.meta?.list?.kind === 'NodeList') && obj?.metadata?.name) addName(obj.metadata.name, 'slot');
    }
  }
  for (const l of literals.map((x) => x.trim()).filter(Boolean)) addPerson(l);
  // a name also appears with its dashes written out the way Activity Log operation ids write them
  for (const n of [...names]) if (n.includes('-')) names.add(n.replaceAll('-', ':2d'));
  return { ids, names, where };
}

// ---------- the check ----------
function occurs(text, t) {
  for (let i = text.indexOf(t); i !== -1; i = text.indexOf(t, i + 1)) {
    const startsWord = !wordChar.test(text[i - 1] ?? '') || /:[0-9a-f]{2}$/.test(text.slice(Math.max(0, i - 3), i)); // after an escape such as :2f
    if (startsWord && !wordChar.test(text[i + t.length] ?? '')) return true;
  }
  return false;
}

export function checkFixtures(fixturesDir, deny) {
  const values = []; const keys = []; let positional = 0;
  for (const f of files(fixturesDir)) {
    for (const line of readFileSync(f, 'utf8').split('\n')) {
      if (!line) continue;
      const rec = JSON.parse(line);
      leaves(rec, '', (v, path) => {
        values.push(lc(v));
        for (const n of armNames(v)) if (deny.where.slot.has(lc(n))) positional++;
        const tag = tagOf(path); const claim = claimOf(path);
        if (tag && !SAFE_TAGS.has(tag) && deny.where.tag.has(lc(v))) positional++;
        if (claim && !SAFE_CLAIMS.has(lc(claim)) && deny.where.claim.has(lc(v))) positional++;
        if (rec.source === 'arg' && /^\.payload\.(name|resourceGroup)$/.test(path) && deny.where.arg.has(lc(v))) positional++;
      });
      const k = (v) => { if (Array.isArray(v)) v.forEach(k); else if (v && typeof v === 'object') for (const [key, x] of Object.entries(v)) { keys.push(lc(key)); k(x); } };
      k(rec);
    }
  }
  const text = `${values.join('\n')}\n${keys.join('\n')}`;
  // every id-like token in the fixtures, then a set lookup per denied id
  const tokens = new Set();
  for (const re of [GUID_RE, HEX32_RE, MAIL_RE, IP4_RE, IP6_RE, MAC_RE]) for (const m of text.match(re) ?? []) { tokens.add(m); tokens.add(m.replaceAll('-', '')); }
  const found = { ids: 0, names: 0, positional };
  for (const t of tokens) if (deny.ids.has(t)) found.ids++;
  for (const n of deny.names) if (occurs(text, n)) found.names++;
  const strayMails = [...text.matchAll(MAIL_RE)].filter((m) => !m[0].endsWith('@example.com')).length;
  return { ...found, strayMails, checked: { values: values.length, ids: deny.ids.size, names: deny.names.size, where: Object.values(deny.where).reduce((n, s) => n + s.size, 0) } };
}
