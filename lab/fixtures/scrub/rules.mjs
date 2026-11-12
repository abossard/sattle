// Field rules: what happens to the value at a path. Keys are never touched, only values.
//   keep   the value is vocabulary (a type, an operation, a metric name): left as it is
//   alias  the whole value identifies something and is replaced (`redacted-<hash>`; ids and e-mails keep their shape)
//   name   the whole value is a resource name: replaced by the name alias, the same one an id segment gets
//   text   the default: ids, addresses and known names inside the text are replaced, the rest stays
// A path looks like `.payload.records[].identity.claims.rh`: array positions are `[]`.

// Token claims that are public vocabulary or ids that get an alias of their shape (`text`). Every other claim is `alias`.
const CLAIMS_KEPT_AS_TEXT = new Set([
  'aud', 'iss', 'ver', 'idtyp', 'appidacr', 'acr', 'amr', 'exp', 'iat', 'nbf', 'scp', 'xms_act_fct', 'appid', 'wids', 'groups', 'sid',
  'xms_aud_guid', 'xms_mirid', 'correlationid',
  'http://schemas.microsoft.com/identity/claims/identityprovider',
  'http://schemas.microsoft.com/identity/claims/objectidentifier',
  'http://schemas.microsoft.com/identity/claims/tenantid',
]);
const KEEP_TAG_KEYS = new Set(['project']);

const claim = (p) => p.match(/\.claims\.(.+)$/)?.[1];
const tagKey = (p) => p.match(/\.tags\.([^.[]+)$/)?.[1];

// First match wins. `test(path, source)`.
const RULES = [
  // the envelope and what describes the capture itself
  { do: 'keep', test: (p) => /^\.(v|source|capturedAt|seq)$/.test(p) },
  { do: 'keep', test: (p) => /^\.meta\.(name|query|kql|resource|interval|start|end|page|pageSize|via|path|hashSalt|partitionId|sequenceNumber|offset|enqueuedTimeUtc|metrics\[\]|list\.(kind|apiVersion|resourceVersion))$/.test(p) },
  // vocabularies that a real name must never rewrite
  { do: 'keep', test: (p) => /\.(kind|apiVersion|type|category|reason|unit|interval|resultType|resultSignature|level|stage|verb|status)$/.test(p) },
  { do: 'keep', test: (p) => /\.(operationName|category)(\.value|\.localizedValue)?$/.test(p) || /\.authorization\.action$/.test(p) },
  { do: 'keep', test: (p, s) => s === 'azure-metrics' && /^\.payload\.(namespace|value\[\]\.name\.(value|localizedValue))$/.test(p) },
  { do: 'keep', test: (p) => /\.metric\.__name__$/.test(p) },
  // identity of a person or a token: replaced whole
  { do: (p) => (CLAIMS_KEPT_AS_TEXT.has(claim(p)) ? 'text' : 'alias'), test: (p) => claim(p) !== undefined },
  { do: 'alias', test: (p) => /\.evidence\.role$/.test(p) },
  { do: 'alias', test: (p) => /\.(ConnectionString|InstrumentationKey)$/.test(p) },
  { do: 'alias', test: (p) => /\.managementGroupAncestorsChain\[\]\.displayName$/.test(p) },
  // tag values are free text chosen by the owner
  { do: (p) => (KEEP_TAG_KEYS.has(tagKey(p)) ? 'text' : 'alias'), test: (p) => tagKey(p) !== undefined },
  // Resource Graph rows carry the resource name and group as plain values
  { do: 'name', test: (p, s) => s === 'arg' && /^\.payload\.(name|resourceGroup)$/.test(p) },
];

export function actionFor(path, source) {
  for (const r of RULES) if (r.test(path, source)) return typeof r.do === 'function' ? r.do(path) : r.do;
  return 'text';
}
