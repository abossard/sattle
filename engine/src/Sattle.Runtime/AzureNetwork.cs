using System.Net;
using System.Text.Json;

namespace Sattle.Runtime;

readonly record struct AzureNetworkFact(string Kind, string Target, string? Address);

/// <summary>Network evidence in a Resource Graph row. The relation is observed at the field that supplied it.</summary>
static class AzureNetwork
{
    public const string RowType = "AzureNetworkRow";
    public static readonly RowContract Contract = new(RowType,
        [RowDef.F("resourceId", "Text"), RowDef.F("kind", "Text"), RowDef.F("target", "Text"), RowDef.F("resolvedAddress", "Text", true)]);

    static IEnumerable<JsonElement> Items(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    public static AzureNetworkFact[] Read(JsonElement resource, string type)
    {
        if (resource.P("properties") is not { ValueKind: JsonValueKind.Object } p) return [];
        var facts = new HashSet<AzureNetworkFact>();
        void Add(string kind, string? target, string? address = null)
        {
            if (!string.IsNullOrWhiteSpace(target)) facts.Add(new(kind, target, address));
        }
        void Arm(string kind, string? id)
        {
            if (id?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true) Add(kind, id.ToLowerInvariant());
        }
        string? Ip(string? value) => IPAddress.TryParse(value, out var ip) ? ip.ToString() : null;
        void Dns(string? name, string? address = null) => Add("dnsName", name?.TrimEnd('.').ToLowerInvariant(), address);
        void Refs(string kind, JsonElement? values)
        {
            foreach (var value in Items(values)) Arm(kind, value.S("id"));
        }
        void Addresses(JsonElement? values)
        {
            foreach (var value in Items(values))
                if (value.ValueKind == JsonValueKind.String) Add("dnsServer", Ip(value.GetString()));
        }
        void IpConfigurations(JsonElement? values)
        {
            foreach (var cfg in Items(values))
            {
                if (cfg.P("properties") is not { } c) continue;
                var address = Ip(c.S("privateIPAddress"));
                Add("ip", address);
                Arm("subnet", c.PathS("subnet", "id"));
                Arm("attachment", c.PathS("publicIPAddress", "id"));
                Refs("attachment", c.P("loadBalancerBackendAddressPools"));
                foreach (var fqdn in Items(c.Path("privateLinkConnectionProperties", "fqdns")))
                    if (fqdn.ValueKind == JsonValueKind.String) Dns(fqdn.GetString(), address);
            }
        }
        void Prefixes(JsonElement? values)
        {
            foreach (var value in Items(values))
                if (value.ValueKind == JsonValueKind.String) Add("prefix", value.GetString());
        }

        switch (type.ToLowerInvariant())
        {
            case "microsoft.network/privateendpoints":
                Arm("subnet", p.PathS("subnet", "id"));
                Refs("networkInterface", p.P("networkInterfaces"));
                foreach (var name in new[] { "privateLinkServiceConnections", "manualPrivateLinkServiceConnections" })
                    foreach (var connection in Items(p.P(name))) Arm("privateLink", connection.PathS("properties", "privateLinkServiceId"));
                foreach (var dns in Items(p.P("customDnsConfigs")))
                {
                    Dns(dns.S("fqdn"));
                    foreach (var value in Items(dns.P("ipAddresses")))
                        if (value.ValueKind == JsonValueKind.String && Ip(value.GetString()) is { } address)
                        {
                            Add("ip", address);
                            Dns(dns.S("fqdn"), address);
                        }
                }
                IpConfigurations(p.P("ipConfigurations"));
                break;
            case "microsoft.network/networkinterfaces":
                Arm("attachment", p.PathS("virtualMachine", "id"));
                Arm("attachment", p.PathS("networkSecurityGroup", "id"));
                Dns(p.PathS("dnsSettings", "internalFqdn"));
                Dns(p.PathS("dnsSettings", "internalDomainNameSuffix"));
                Addresses(p.Path("dnsSettings", "dnsServers"));
                Addresses(p.Path("dnsSettings", "appliedDnsServers"));
                IpConfigurations(p.P("ipConfigurations"));
                break;
            case "microsoft.network/publicipaddresses":
                var publicAddress = Ip(p.S("ipAddress"));
                Add("ip", publicAddress);
                Dns(p.PathS("dnsSettings", "fqdn"), publicAddress);
                Arm("attachment", p.PathS("ipConfiguration", "id"));
                break;
            case "microsoft.network/privatednszones":
            case "microsoft.network/dnszones":
                Dns(resource.S("name"));
                break;
            case "microsoft.network/privatednszones/virtualnetworklinks":
                Arm("virtualNetwork", p.PathS("virtualNetwork", "id"));
                if (resource.S("id") is { } link && link.IndexOf("/virtualNetworkLinks/", StringComparison.OrdinalIgnoreCase) is > 0 and var cut)
                    Arm("dnsZone", link[..cut]);
                break;
            case "microsoft.network/privateendpoints/privatednszonegroups":
                foreach (var zone in Items(p.P("privateDnsZoneConfigs"))) Arm("dnsZone", zone.PathS("properties", "privateDnsZoneId"));
                break;
            case "microsoft.network/virtualnetworks":
                Prefixes(p.Path("addressSpace", "addressPrefixes"));
                foreach (var subnet in Items(p.P("subnets")))
                {
                    Arm("containsSubnet", subnet.S("id"));
                    Add("prefix", subnet.PathS("properties", "addressPrefix"));
                    Prefixes(subnet.Path("properties", "addressPrefixes"));
                }
                break;
            case "microsoft.network/loadbalancers":
                IpConfigurations(p.P("frontendIPConfigurations"));
                break;
            case "microsoft.compute/virtualmachinescalesets":
                foreach (var nic in Items(p.Path("virtualMachineProfile", "networkProfile", "networkInterfaceConfigurations")))
                    IpConfigurations(nic.Path("properties", "ipConfigurations"));
                break;
        }
        return [.. facts.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Target, StringComparer.Ordinal).ThenBy(x => x.Address, StringComparer.Ordinal)];
    }
}
