# From the master graph to system health

Transcript of the narrated Sattle field guide.

## 00:00 · From source records to five views

**Listener**

Why is the refund for order one oh four stuck?

**Guide**

The refund job called the payment API. It waited for a reply and timed out. See those dots? Each is a record from one system. Kubernetes lists workloads. A network flow names two endpoints. A trace records a call. The refund job’s call is our starting point.

**Listener**

What ties this reading to that storage account?

**Guide**

They share a full Azure resource ID. Azure Resource Graph lists the storage account, and Azure Monitor puts that ID on its reading. Now follow the DNS name. It resolves to an address. The private endpoint at that address names the storage account. That gives us a path from the name to storage.

**Listener**

What happens to that path when we group the dots?

**Guide**

Watch the DNS name, the private endpoint and the storage account. They move into different groups and stay connected. This projection groups records by technology. Kubernetes workloads cluster together. Azure resources, network connections, DNS, application telemetry and shop software each get a group.

**Listener**

Where does that leave the refund job?

**Guide**

It belongs to Payout. The job reads the case store in Returns Intake and calls the payment API. Now we’re grouping software by the job it does. Those links show what Payout needs from the other groups. Shared infrastructure has a group too, so we can still trace the path to storage.

**Listener**

Which view would help me investigate the timeout?

**Guide**

Start with the dashboard. Successful payment calls drop to zero.

Open the health tree and you can see why that blocks Payout.

Want to find the pods? The Kubernetes view starts at the refund deployment and follows it to them.

The Azure resource view lists the storage account.

In the DNS and IP view, its name resolves to a private address. All five views draw on the same shop records.

## 01:49 · How health rolls up

**Listener**

If the database stops answering, why does web turn red too?

**Guide**

Web needs the API, and the API needs the database. Watch the recorded pg-shop outage. First the database signal turns red. Then you see that failure on the API and web. That’s a health roll-up. A caller’s state reflects what happened to a service it depends on.

**Listener**

What makes the database red in the first place?

**Guide**

At ten eleven, its availability meter reads zero. Azure Monitor reports the sample a minute later. That’s enough for the shop’s database rule. One bad minute opens the alarm. The reading tells us what the meter saw. The rule decides when that reading means Unhealthy.

**Listener**

Why does the alarm reach web and leave the cache green?

**Guide**

Start at web and follow the arrows marked Affects. There’s a path through the API to the database. The structure query builds those links. The health query follows up to sixteen of them and keeps the worst severity it finds. Start at the cache, though, and no path leads to the failing server. The cache stays green.

**Listener**

The meter is back at one. Why is the database still red?

**Guide**

That’s one good minute. The rule needs two in a row. Ten fourteen is the first. At ten fifteen, the alarm clears and the callers recover. You can see the failure in graph revision sixteen and the recovery in eighteen. The revision numbers count changes. The meter gives us the times.

## 03:11 · A pod starts using Redis

**Listener**

Suppose an API pod starts sending traffic to Redis. What does Sattle see first?

**Guide**

One new Hubble flow. In this copy of the pg-shop recording, an API pod sends traffic to an existing Redis cache. The pod and the destination are already on the map. The new line on the master graph connects the pod to that destination.

**Listener**

How does that address become a Redis dependency in the health model?

**Guide**

Kubernetes tells us which service the pod belongs to. It’s the API. At the other end, the address matches a private endpoint for Redis, and the host name identifies the same cache. The existing structure query links the API to Redis. Now when the cache is Degraded, so are the API and its gateway.

**Listener**

Redis was already there. Where can I find the new link?

**Guide**

Compare the two revisions in the Canvas. The patch lists the link it added, from the API to Redis. That’s the change you see highlighted. The existing health rule follows that dependency. A warning on the cache can now reach the API.

**Listener**

How could I check that the API really started using that cache?

**Guide**

Open the evidence for the link. The structure query kept two matches. One names the pod, the destination address and the private endpoint. The other names the Redis host. Both lead back to the flow and Azure records. The traffic explains the new dependency. The cache’s own load readings explain the warning.
