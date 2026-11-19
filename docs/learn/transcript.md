# How the shop gets its health model

Transcript of the narrated Sattle field guide.

## 00:00 · The shop on Azure

**Listener**

What does checkout need from the rest of the shop?

**Guide**

The web service calls the API. The API calls the database service, which uses a PostgreSQL server named pg-shop. Follow those arrows from checkout and you can see which calls have to succeed.

**Listener**

Where did the rest of those dots come from?

**Guide**

Kubernetes records say where the shop’s pods ran. Network records say which pods called each other. By the end of the replay, Sattle’s master graph holds seventy-three entities and sixty-nine links. Each dot stands for something a source reported.

**Listener**

How does that map become a health model?

**Guide**

Start with the services checkout depends on. A query groups the underlying records into those services and keeps the calls between them. That transformation is a projection. A health projection also uses readings and rules to work out which services a failure affects.

## 01:01 · The shop’s health rules

**Listener**

Why is the database server outside the box?

**Guide**

The box marks the shop’s Kubernetes namespace. The database service belongs to that namespace. Its PostgreSQL server runs on Azure. The boundary tells Sattle which services to start with. Following a dependency can take it outside.

**Listener**

How long does the database have to go quiet before an alarm opens?

**Guide**

One bad minute opens the pg-shop alarm. It takes two good minutes in a row to close it. The rule remembers the previous minute. That’s called hysteresis. A team can choose these thresholds for its service.

**Listener**

Could a failed call leave checkout working?

**Guide**

Checkout needs the database, so the database alarm counts against the API. For a service that can carry on without its cache, a rule could treat the cache call as optional. Network records establish that a call happened. A rule says what its failure means.

## 02:05 · From records to a map

**Listener**

How does Sattle know which server the meter belongs to?

**Guide**

Azure Resource Graph lists pg-shop. Azure Monitor puts the very same resource ID on the reading. Sattle can use that ID to attach the reading to the right server, even when different sources describe it.

**Listener**

Why is there just one arrow between web and API?

**Guide**

Three web pods call two API pods. Pods are running copies of an application. Group the pods by service and their calls become one link from web to API. The records underneath still show which copies made the calls.

**Listener**

What does the arrow from the database service to pg-shop mean?

**Guide**

Pg-shop backs the database service. That’s a different relationship from one pod calling another. Sattle’s ontology names the kinds of things and links the model understands. A team can maintain that vocabulary as new kinds of software appear.

## 03:08 · The pg-shop outage

**Listener**

The reading arrived after ten twelve. Why does the chart put it at ten eleven?

**Guide**

Azure Monitor measured a zero at ten eleven. The recorder received it a minute and a second later. The chart uses the sample time, so the failure lands in the minute the meter observed it.

**Listener**

Why have web and the gateway turned red too?

**Guide**

Both call the API. The API needs the database service, which depends on pg-shop. The model carries the database alarm back along those links. The cache stays green because its own work doesn’t depend on that server.

**Listener**

At ten fourteen the database answers again. Why is it still red?

**Guide**

That’s the first good minute after the outage. At ten fifteen the second good reading clears the alarm. Its callers recover too. The meter shows the latest sample. The alarm also remembers what happened a minute ago.

## 04:08 · A new dependency

**Listener**

What was that amber patch earlier in the morning?

**Guide**

The cache was under load, and web already relied on it. Both show Degraded, the warning state. In that same snapshot the API and gateway are healthy. The cache warning hasn’t reached them.

**Listener**

Where did the new arrow from the API to the cache come from?

**Guide**

In the second run, the API’s deployment has a setting that names the cache host. Sattle matches that address to the existing Azure cache. The deployment setting is the evidence for the new dependency.

**Listener**

Why does the gateway change color in only one run?

**Guide**

Both runs stop at the same moment with cache load at eighty-five percent. In the second run the API depends on the cache, and the gateway depends on the API. The new link carries the warning to the gateway.

## 05:04 · A change on Azure

**Listener**

The API now points at offers-cache. Is that enough to put the cache on the map?

**Guide**

The deployment gives a host name. At this point in the replay, Azure Resource Graph has no matching resource. The setting tells us where the API expects to connect. The resource behind that address is still unknown.

**Listener**

Which record settles the question?

**Guide**

Azure Resource Graph returns a Redis resource named offers-cache. Its name matches the first part of the address. Its resource type matches the Redis host suffix. The query can now link the API to that cache.

**Listener**

Why is there a question mark on the new cache?

**Guide**

The resource record identifies the cache, but the replay has no reading that tells the health rule how it’s doing. Sattle marks its state Unknown. To judge a different kind of resource, the model may need a different meter or rule.

## 06:04 · A map through time

**Listener**

Does the Canvas download the whole map when the database fails?

**Guide**

It already has revision fifteen. Seven edits take it to revision sixteen. The engine sends those edits together as a patch. The Canvas can apply the patch to the map it has.

**Listener**

How far apart are revisions sixteen and eighteen?

**Guide**

In sixteen, pg-shop is unhealthy. By eighteen it has recovered. Those numbers count changes to the graph. To find the elapsed time, look at the timestamps on the database readings.

**Listener**

How can we check what the map showed during the outage?

**Guide**

Replay the shop records and clock events with the same rules. Applying each update as it arrives gives the same graph as computing the answer again from the recorded inputs. That gives us a way to check the result.

## 06:59 · The evidence for an answer

**Listener**

Web is red. Where did the failure start?

**Guide**

In revision sixteen, follow web through the API and database service to pg-shop. The server’s own meter tripped its alarm. The Canvas marks pg-shop as the origin, so you can tell where the failure began and which services felt it.

**Listener**

Can we check the link between the database service and pg-shop?

**Guide**

The structure query matched the service’s configured host to the Azure resource. The health view uses the link that query produced. Following a result back through its queries to the source records is called lineage.

**Listener**

Could we try a different rule on this same outage?

**Guide**

The Canvas can run a draft query against the recorded shop and show the rows it returns. That gives a team a way to try a rule on a known failure and inspect the result before adopting it.
