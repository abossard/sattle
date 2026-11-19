# From the master graph to system health

Transcript of the narrated Sattle field guide.

## 00:00 · From source records to five views

**Listener**

Why is the refund for order one oh four stuck?

**Guide**

The payment call timed out. That trace tells us what the refund job saw. Each dot here is a node, one source record in this picture. In Kubernetes, the owner link tells us which deployment owns a ReplicaSet. A network flow names the two ends of a connection. We call a link a typed edge because its type tells us what the connection means.

**Listener**

What ties this reading to that storage account?

**Guide**

They share a full Azure resource ID. Azure Resource Graph names the storage account, and Azure Monitor puts that same ID on its reading. That’s an identity match. Sattle’s ontology supplies the naming rule that ties both records to one account. Now follow the DNS name. It resolves to an address, and the private endpoint names the account. These links connect different things.

**Listener**

What happens to that path when we group the dots?

**Guide**

It stays connected. Follow the DNS name to the address, the private endpoint and the storage account. The dots move into technical groups, but the links still cross those boundaries. A projection is a rule for building one graph from another. This one groups records by technology so you can see how DNS, networking and Azure resources fit together.

**Listener**

Where does that leave the refund job?

**Guide**

It belongs to Payout. A capability groups software by the job it does for the shop. The job reads a case from Returns Intake. Its call to the payment API becomes the blue arrow from Payout to Payment API. That arrow between two groups stands for a call you can trace back to the refund job.

**Listener**

Which view would help me investigate the timeout?

**Guide**

Start with the dashboard. It counts successful replies. Two out of two payment calls succeeded in the first minute. By the last minute, neither got a reply.

The health tree shows what that means for Payout. Its rule needs a successful payment call. Payout is blocked, and the order lookup still works.

The Kubernetes view answers a different question. Which pods run the refund job? Follow the owner links down from the deployment.

The Azure resource view lists the storage account in the shop’s resource group.

The DNS and IP view shows how its name reaches a private address. Each view selects what it needs from the same records. That’s why you can follow one problem across all five.

## 02:14 · How health rolls up

**Listener**

If the database stops answering, why does web turn red too?

**Guide**

Web needs the API, and the API needs the database. Look at the two arrows. The blue one points from the database service to the server it needs. The red one points back. A failure can affect the caller. That’s health roll-up, and it explains why the database, API and web all read Unhealthy in this revision.

**Listener**

What makes the database red in the first place?

**Guide**

One bad minute opens the alarm. The chart shows the meter dropping to zero at ten eleven. Azure Monitor delivers that reading a minute later. Zero is a measurement. The database rule gives it meaning for health. On this rule, one failed sample makes the database Unhealthy.

**Listener**

Why does the alarm reach web and leave the cache green?

**Guide**

Web has a path to the failing database through the API. The cache has no such path. Follow the arrows to see what each service depends on. The structure query builds those links. The health query follows up to sixteen Affects links and takes the worst state it can reach. That’s why the cache stays green.

**Listener**

The meter is back at one. Why is the database still red?

**Guide**

The rule remembers how many good minutes came in a row. At ten fourteen the count is one, and the alarm stays open. Ten fifteen makes two. The alarm clears and the callers recover. The times across the top come from the meter. Revision eighteen names the version of the graph that records the recovery.

## 03:39 · A pod starts using Redis

**Listener**

Suppose an API pod starts sending traffic to Redis. What does Sattle see first?

**Guide**

Hubble recorded one flow from the API pod to this address. In our copy of the replay, the pod and the address already exist. The new line means the pod sent traffic there. That’s what the network observed. To call Redis a dependency of the whole API service, the model needs to know who’s at each end.

**Listener**

How does that address become a Redis dependency in the health model?

**Guide**

Kubernetes ties the pod to the API service. At the other end, the address matches a private endpoint for Redis. The DNS name points to the same cache. That gives the structure query both ends of a service dependency. It derives the API to Redis link from those records. The health model can now follow it using its existing rule.

**Listener**

Redis was already there. Where can I find the new link?

**Guide**

Look for the plus sign. A patch lists the edits from one graph revision to the next. In the master graph, the added line means the pod sent traffic. In the structure graph, it means the API depends on Redis. In the health graph, revision fourteen adds that dependency. The cache was there all along.

**Listener**

How could I check that the API really started using that cache?

**Guide**

Follow the dependency back through the graphs. The structure query kept two matches. One links the pod’s destination to a private endpoint. The other identifies Redis by its DNS name. That trail from a conclusion back to its inputs is called provenance. Now compare the two runs. Redis has the same load warning in both. The added dependency explains why the API and gateway turn amber.
