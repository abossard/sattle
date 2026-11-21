import { describe, expect, it } from "vitest";
import type { Rel } from "../src/core/model";
import { masterLinks } from "../examples/developer-guide/master-links";

const edge = (id: string, type: string, source: string, target: string): Rel => ({ id, type, source, target, props: {} });

describe("captured master links in the guide", () => {
  it("selects a connected private endpoint, interface and configured DNS address", () => {
    const links = masterLinks([
      edge("old-ip", "HasIpAddress", "other-interface", "other-ip"),
      edge("old-link", "PrivatelyConnectsTo", "other-endpoint", "other-service"),
      edge("old-nic", "AttachedNetworkInterface", "other-endpoint", "other-interface"),
      edge("new-link", "PrivatelyConnectsTo", "endpoint", "service"),
      edge("new-nic", "AttachedNetworkInterface", "endpoint", "interface"),
      edge("additional-address", "HasIpAddress", "interface", "secondary-ip"),
      edge("configured-ip", "tutorial.HasIpAddress", "interface", "ip"),
      edge("additional-name", "HasDnsName", "interface", "another-name"),
      edge("dns", "HasDnsName", "interface", "name"),
      edge("address", "ConfiguredAddress", "name", "ip"),
    ]);
    expect(links.map((link) => [link.edge.id, link.count])).toEqual([
      ["new-link", 2], ["new-nic", 2], ["configured-ip", 3], ["dns", 2], ["address", 1],
    ]);
  });

  it("lists only evidence present in the current revision", () => {
    const links = masterLinks([
      edge("contains", "AzureContains", "group", "resource"),
      edge("link", "LinksVirtualNetwork", "zone-link", "virtual-network"),
      edge("zone", "HasDnsZone", "zone-link", "dns-zone"),
    ]);
    expect(links.map((link) => link.type)).toEqual(["HasDnsZone", "LinksVirtualNetwork"]);
  });
});
