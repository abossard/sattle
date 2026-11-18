# How the shop gets its health model

Transcript of the narrated Sattle field guide.

## 00:00 · The shop on Azure

A dependency is something a service needs to do its job. Follow the arrows from the shop’s web service to the API, then through the database service to the server called pg-shop.

An observation is one report from one source. Kubernetes names the shop’s services. Network records show calls between them. Azure lists the database server, and its meter tells us whether the server answered.

A health model is a map of what’s healthy and who depends on it. Sattle joins the observations in a master graph. One query draws the shop. A second applies your health rules.

## 00:41 · Your team’s decisions

Scope tells Sattle where to look. The replay comes with working rules. For your own shop, connect the sources you can read, then choose the cluster and the namespace that holds your services.

A signal rule turns readings into an alarm. Your team sets the threshold and the wait. For servers like pg-shop, one bad minute raises the alarm. Two good minutes clear it.

Your team also chooses which failures should affect a caller. The API needs its database. If your own API can serve a request without its cache, you can leave that call out of its health rule.

## 01:22 · From records to a map

Identity tells Sattle when two records describe the same thing. Compare the Azure resource ID in the inventory with the ID on the meter reading. They match. Both records belong to pg-shop.

A projection is a query that builds a new view. Here, several copies of the web service call the API. The projection groups those calls into one arrow between the services.

An ontology is the map’s vocabulary. It names kinds of things, like a service or a server, and the links they can have. Here the database service uses a server called pg-shop.

## 02:03 · The pg-shop outage

A time series keeps readings in the order of their sample times. At 10:11, pg-shop reports zero. The recorder gets that sample a minute and a second later. The chart puts it at 10:11.

Impact travels back toward the callers. Start at pg-shop and follow the dots to the left. The alarm reaches the database service, the API, then the gateway and web service. The cache stays healthy.

At 10:14 the meter rises to one. That’s the first good minute. Pause there and the map is still red. At 10:15 the next good minute clears the alarm, and the callers turn green.

## 02:46 · A new dependency

Degraded means the model sees trouble that falls short of an outage. Earlier in the replay, the cache comes under heavy load. The web service uses it and turns amber. The gateway stays green.

Evidence is a record that supports something on the map. Give the API the existing cache address. Sattle matches that setting to Azure inventory and adds an arrow from the API to the cache.

Compare the gateways at 10:06:30. In the original run, the gateway is healthy. In the changed run, it depends on the busy cache through the API. The same readings now give the gateway an amber light.

## 03:30 · A change on Azure

Suppose the API now names a different cache, offers-cache. The setting gives Sattle an address to look for. Until Azure reports a resource at that address, the shop map has no target for the new arrow.

Now Azure Resource Graph lists the new cache. Its address matches the API setting. Since the shop already uses this kind of cache, the existing query adds the new link.

Unknown means the model has too little evidence to judge health. Azure tells us offers-cache exists. We still need its readings. A new kind of resource may also need a new matching rule or meter.

## 04:11 · A map you can rewind

A patch is a list of changes. When a source reports an update, the engine sends the affected parts to the Canvas, Sattle’s viewer. A gap in reporting leaves the last known resource on the map.

A revision marks a point in the history of the map. The Canvas can return to it by taking a saved map and applying the next few patches. In this replay, revision 16 shows the outage. Revision 18 shows recovery.

To test a replay, Sattle feeds the same records and rules to two programs. One updates the graph as records arrive. The other rebuilds it from scratch. The white paper explains when their answers should agree.

## 04:55 · Check the answer

An origin has a failing signal of its own. In the Canvas workload tree, select pg-shop at revision 16. Its origin marker singles out the database. The web service is red because it depends on that database.

Select the database link in the Inspector. Follow it through the health and structure queries to the recorded names and IDs. Those records let a teammate check why Sattle connected the service to this particular server.

To try a different rule, open the projection editor on a running engine. Edit a draft, then preview its rows. You can see what your query would return before you save it.
