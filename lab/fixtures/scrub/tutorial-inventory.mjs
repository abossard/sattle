import { GENERIC_NAMES, isRegion } from './core.mjs';

const ref = { id: true };
const ipConfig = {
  id: true,
  properties: {
    privateIPAddress: true,
    subnet: ref,
    publicIPAddress: ref,
    loadBalancerBackendAddressPools: [ref],
    privateLinkConnectionProperties: { fqdns: ['dns'] },
  },
};
const connection = { properties: { privateLinkServiceId: true } };
const azureIdentity = /^(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;

// Preserve paths consumed by the Azure Resource Graph adapter. Properties outside this list stay in the raw capture.
const propertyShapes = {
  'microsoft.containerservice/managedclusters': {
    nodeResourceGroup: 'group',
  },
  'microsoft.compute/virtualmachinescalesets': {
    virtualMachineProfile: { networkProfile: { networkInterfaceConfigurations: [{ properties: { ipConfigurations: [ipConfig] } }] } },
  },
  'microsoft.network/privateendpoints': {
    subnet: ref,
    networkInterfaces: [ref],
    privateLinkServiceConnections: [connection],
    manualPrivateLinkServiceConnections: [connection],
    customDnsConfigs: [{ fqdn: 'dns', ipAddresses: [true] }],
    ipConfigurations: [ipConfig],
  },
  'microsoft.network/privateendpoints/privatednszonegroups': {
    privateDnsZoneConfigs: [{ properties: { privateDnsZoneId: true } }],
  },
  'microsoft.network/networkinterfaces': {
    privateEndpoint: ref,
    virtualMachine: ref,
    networkSecurityGroup: ref,
    dnsSettings: { internalFqdn: 'dns', internalDomainNameSuffix: 'dns', dnsServers: [true], appliedDnsServers: [true] },
    ipConfigurations: [ipConfig],
  },
  'microsoft.network/loadbalancers': {
    frontendIPConfigurations: [ipConfig],
    backendAddressPools: [ref],
  },
  'microsoft.network/publicipaddresses': {
    ipAddress: true,
    ipConfiguration: ref,
    dnsSettings: { fqdn: 'dns' },
  },
  'microsoft.network/virtualnetworks': {
    addressSpace: { addressPrefixes: [true] },
    subnets: [{
      id: true,
      properties: {
        addressPrefix: true, addressPrefixes: [true], networkSecurityGroup: ref,
        ipConfigurations: [ref], privateEndpoints: [ref],
      },
    }],
  },
  'microsoft.network/privatednszones/virtualnetworklinks': {
    virtualNetwork: ref,
  },
  'microsoft.apimanagement/service/backends': { url: 'url' },
  'microsoft.managedidentity/userassignedidentities': { clientId: 'identity', principalId: 'identity' },
  'microsoft.authorization/roleassignments': { principalId: 'identity', scope: 'scope' },
};

export const dnsAlias = (host, alias) => `${alias.name(host.trim().replace(/\.$/, ''))}.example.invalid`;

function select(value, shape, alias) {
  if (Array.isArray(shape)) {
    const entries = Array.isArray(value) ? value.map((item) => select(item, shape[0], alias)).filter(Boolean) : [];
    return entries.length ? entries : undefined;
  }
  if (typeof shape === 'object') {
    if (!value || typeof value !== 'object') return undefined;
    const entries = Object.entries(shape)
      .map(([key, field]) => [key, select(value[key], field, alias)])
      .filter(([, item]) => item !== undefined);
    return entries.length ? Object.fromEntries(entries) : undefined;
  }
  if (typeof value !== 'string' || !value.trim()) return undefined;
  if (shape === 'identity') return azureIdentity.test(value) ? value : undefined;
  if (shape === 'scope') return /^\/subscriptions\//i.test(value) ? value : undefined;
  if (shape === 'group') return GENERIC_NAMES.has(value.toLowerCase()) || isRegion(value.toLowerCase()) ? value : alias.name(value);
  // Use the same opaque name when Azure spells the hostname differently across sources.
  if (shape === 'dns') return dnsAlias(value, alias);
  if (shape === 'url') {
    const host = URL.parse(value)?.hostname;
    return host ? `https://${dnsAlias(host, alias)}` : undefined;
  }
  return value;
}

export function tutorialInventoryPayload(row, alias) {
  const key = row.type.toLowerCase();
  return {
    ...select(row, { id: true, name: true, type: true, location: true, resourceGroup: true, subscriptionId: 'identity' }, alias),
    properties: Object.hasOwn(propertyShapes, key) ? select(row.properties, propertyShapes[key], alias) : undefined,
  };
}
