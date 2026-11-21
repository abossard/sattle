import type { Rel } from "../../src/core/model";

const networkTypes = new Map([
  ["PrivatelyConnectsTo", "Private link"],
  ["AttachedNetworkInterface", "Endpoint interface"],
  ["HasIpAddress", "IP address"],
  ["HasDnsName", "Configured DNS name"],
  ["ConfiguredAddress", "DNS name to configured IP"],
  ["HasDnsZone", "Private DNS zone"],
  ["LinksVirtualNetwork", "DNS zone network link"],
]);
const nameOf = (type: string) => type.slice(type.lastIndexOf(".") + 1);

/** Prefer edges of one endpoint and its interface so the examples can be followed in the master graph. */
export function masterLinks(relationships: Iterable<Rel>) {
  const examples = new Map<string, { edge: Rel; count: number }>();
  const outgoing = new Map<string, Rel[]>();
  const interfaces: Rel[] = [];
  for (const edge of relationships) {
    const type = nameOf(edge.type);
    if (!networkTypes.has(type)) continue;
    const example = examples.get(type);
    if (example) example.count++;
    else examples.set(type, { edge, count: 1 });
    const edges = outgoing.get(edge.source);
    if (edges) edges.push(edge);
    else outgoing.set(edge.source, [edge]);
    if (type === "AttachedNetworkInterface") interfaces.push(edge);
  }
  const from = (source: string | undefined, type: string, target?: string) =>
    source ? outgoing.get(source)?.find((edge) => nameOf(edge.type) === type && (!target || edge.target === target)) : undefined;
  const nic = interfaces.find((edge) => from(edge.source, "PrivatelyConnectsTo") && from(edge.target, "HasDnsName"))
    ?? interfaces.find((edge) => from(edge.source, "PrivatelyConnectsTo")) ?? interfaces[0];
  const dns = nic && (outgoing.get(nic.target)?.find((edge) => nameOf(edge.type) === "HasDnsName" && from(edge.target, "ConfiguredAddress"))
    ?? from(nic.target, "HasDnsName"));
  const address = from(dns?.target, "ConfiguredAddress");
  const ip = from(nic?.target, "HasIpAddress", address?.target) ?? from(nic?.target, "HasIpAddress");
  const vnet = examples.get("LinksVirtualNetwork")?.edge;
  for (const edge of [from(nic?.source, "PrivatelyConnectsTo"), nic, ip, dns, address, from(vnet?.source, "HasDnsZone")]) {
    if (edge) examples.get(nameOf(edge.type))!.edge = edge;
  }

  return [...networkTypes].flatMap(([type, label]) => {
    const example = examples.get(type);
    return example ? [{ label, type, ...example }] : [];
  });
}
