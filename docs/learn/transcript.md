# How the shop gets its health model

Transcript of the narrated Sattle field guide.

## 00:00 · The whole shop

This shop runs on Azure. Start at the gateway. It sends requests to the API, and so does the web service below it. Follow either path to the right and you’ll reach pg-shop, the shop’s database server.

How did Sattle draw those arrows? Kubernetes lists the services. Network records show who called whom. Azure lists the database server, and Azure Monitor tells us whether it answered.

Sattle puts those records together in a map called the master graph. One query finds the parts of the shop and the links between them. A second query uses your rules to work out which parts are healthy.

## 00:38 · What you contribute

The replay comes with a working model. For your own shop, give Sattle read access to your sources and tell it where to look. Here, that’s one Kubernetes cluster and the services in its shop namespace.

You decide when to raise an alarm and when to clear it. For pg-shop, one bad minute trips the database alarm. It takes two good minutes in a row to clear it. The same rule covers other servers of this type.

You also know which calls your service can do without. If the API needs the database to answer a request, its health should reflect a database outage. If it can work without a cache, you can leave that call out of the health calculation.

## 01:19 · The theory

Azure inventory names pg-shop. The meter reading carries the same resource ID, so Sattle can attach both records to one server. The Kubernetes service called database has its own ID. An arrow connects the service to the server it uses.

The white paper calls the names for things and their links an ontology. A projection gives you a simpler map. Several copies of the web service call the API. On the shop map those calls become one arrow, and you can trace it back to the records.

Each minute adds a reading to the database chart. Most minutes leave the map just as it was. When a reading trips or clears the alarm, Sattle records the change in a new version of the health map.

## 02:04 · The pg-shop outage

At 10:11 the database meter drops to zero. The recorder gets that sample a minute and a second later. The alarm belongs to 10:11, the minute on the chart.

Start at pg-shop and follow the dots back to the left. The database service needs that server. The API needs the service, and both the gateway and the web service use the API. The cache and storage stay healthy.

At 10:14 the database answers again. The alarm stays on. At 10:15 we have two good minutes in a row. The alarm clears, and the services that depend on the database turn green again.

## 02:43 · A new dependency

Let’s go back a few minutes, to when the cache was under heavy load. In the original replay the web service uses the cache. The shop and its web service show Degraded. The API and gateway stay healthy.

Now replay the same minutes with one change. We put the cache address in the API’s settings. Sattle finds that address in Azure inventory and draws a new arrow from the API to the cache. Sattle keeps the setting as evidence for that arrow.

Pause both runs at 10:06:30. In the second run the API and its gateway both show Degraded. The cache readings and alarm rules are the same. The gateway now depends on the cache through the API.

## 03:28 · A change on Azure

This time, point the API at a new cache called offers-cache. So far, all Sattle has is an address in a setting. Until Azure reports a matching resource, the map has no cache at that address.

Now Azure Resource Graph lists offers-cache. Its address matches the one in the API settings. Both caches are the same type, so the rule that found the first one can draw this new link too.

The new cache shows up as Unknown. Azure told us it exists. To tell whether it’s healthy, Sattle needs readings for that cache. For a different kind of resource, you may need a new rule to match its address, or a way to collect and interpret its readings.

## 04:09 · Change and replay

When a source reports a change, the engine updates the parts of the map that depend on it. It sends those edits to the Canvas, Sattle’s viewer. If a source stops reporting, Sattle keeps the last evidence. A quiet source can’t tell us the database is gone.

The Canvas opens a saved map and applies the edits in order. In this recording, version 16 shows the database outage. Skip to version 18 to see it recover. The version numbers count changes to the map. The times on the chart belong to the readings.

The white paper explains when updating the map one change at a time gives the same answer as rebuilding it from the beginning. Sattle tests this with a second program. Both programs get the same records and rules, and the test compares their maps.

## 04:59 · Check the evidence

Open the workload tree in the Canvas at version 16 and select pg-shop. The origin marker tells you the server’s own meter tripped the alarm. Select the web service and you can see why it turned red even though the alarm came from the database.

In the Inspector, follow the health result back to the rule, then follow the database link back to its source records. On a running engine, you can edit a copy of the query and preview the rows it returns.

Try the two cache changes in the walkthrough. First give the API the existing cache address and watch the gateway. Then give it the new address and add the Azure record. This time, look at the new cache’s health.
