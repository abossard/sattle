// Entra-only Event Hubs access. No keys, no connection strings.
import { AzureCliCredential } from '@azure/identity';
import { EventHubConsumerClient, EventHubProducerClient, earliestEventPosition, latestEventPosition } from '@azure/event-hubs';

const credential = new AzureCliCredential();
const host = (ns) => (ns.includes('.') ? ns : `${ns}.servicebus.windows.net`);

export const producer = (ns, hub) => new EventHubProducerClient(host(ns), hub, credential);
export const consumer = (ns, hub, group = 'sattle-capture') => new EventHubConsumerClient(group, host(ns), hub, credential);
export { earliestEventPosition, latestEventPosition };
