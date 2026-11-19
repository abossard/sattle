# From the master graph to system health

Transcript of the narrated Sattle field guide.

## 00:00 · From source records to five views

**Listener**

Why is the refund for order one oh four stuck?

**Guide**

The refund job called the payment API and timed out waiting for a reply. Its call record tells us that much. To trace the request, start with what each system recorded. Kubernetes lists workloads, a network flow names two endpoints, and a trace shows a call. Each dot is one source record.

**Listener**

What tells us that two records describe the same thing?

**Guide**

These two carry the same full Azure resource ID. Azure Resource Graph lists a storage account. Azure Monitor puts that account’s ID on a reading. Matching the ID ties the reading to the account. The DNS record supplies a different link. Its name resolves to an address, and the private endpoint at that address names the storage account.

**Listener**

Can I see how the technical parts fit together?

**Guide**

Yes. The DNS answer leads to a private endpoint and then to storage. Follow that path as the dots move. This projection groups records by technology. Kubernetes workloads sit together, alongside groups for Azure resources, network connections, DNS, application telemetry and shop software. The links still cross the groups.

**Listener**

Where does that leave the refund job?

**Guide**

It belongs to Payout. It reads the case store in Returns Intake and calls the payment API. Grouping by capability puts software with the same job together. Its original links explain what each group depends on. Shared infrastructure has its own group, so the path to storage is still there.

**Listener**

Which view would help me investigate the timeout?

**Guide**

The dashboard shows successful payment calls falling to zero.

The health tree shows why that blocks Payout.

The Kubernetes view follows the refund deployment to its pods.

The Azure resource view lists the storage account.

The DNS and IP view traces its name to a private address. Each projection draws on the same shop evidence to answer a different question.

## 02:14 · How health rolls up

**Listener**

If the database stops answering, why does web turn red too?

**Guide**

Web needs the API, and the API needs the database. A health roll-up lets a problem in a dependency change the state shown for its callers. Follow the recorded pg-shop outage. The database signal turns red, and web inherits that state through the API.

**Listener**

What makes the database red in the first place?

**Guide**

Its availability meter reads zero at ten eleven. Azure Monitor reports that sample a minute later. The shop’s database rule opens an alarm after one bad minute. The reading is what the meter saw. The rule decides what it means for health.

**Listener**

How does Sattle work out which services feel that alarm?

**Guide**

The structure query records which parts can affect each service. The health query starts at the service, follows up to sixteen links called Affects, and takes the worst severity it finds. Web reaches the database along that path. The cache has no such path to the failing server, so it stays green.

**Listener**

The meter is back at one. Why is the database still red?

**Guide**

The rule waits for two good minutes in a row. Ten fourteen is the first. Ten fifteen clears the alarm, and the callers recover too. In the exported graph, revision sixteen shows the failure and eighteen shows recovery. Those numbers count graph changes. The meter supplies the times.

## 03:49 · A pod starts using Redis

**Listener**

Suppose an API pod starts sending traffic to Redis. What does Sattle see first?

**Guide**

One new Hubble flow. In a copy of the pg-shop recording, an API pod sends traffic to the address of an existing Redis cache. The master graph gains one link from the pod to the destination. Both were already on the map.

**Listener**

How does that address become a Redis dependency in the health model?

**Guide**

Kubernetes shows that the pod backs the API service. The destination address matches a private endpoint for the Redis cache. The host name identifies the same cache. The existing structure query uses that evidence to add an API dependency. During the cache warning, the health model now marks both the API and its gateway Degraded.

**Listener**

Where can I see what was new?

**Guide**

Look at the revision that adds the dependency. Its patch lists a new link from the API to Redis. Compare the two revisions in the Canvas to highlight what changed. Redis was already on the map. The new part is the API’s dependency, and the existing health rule already knows how to use it.

**Listener**

How could I check that the API really started using that cache?

**Guide**

The structure query kept two pieces of evidence. One names the pod, its destination address and the private endpoint. The other names the Redis host. You can check both against the flow and Azure records. The traffic explains the new dependency. The cache’s own load readings explain the warning.
