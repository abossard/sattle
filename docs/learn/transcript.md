# How the shop gets its health model

Transcript of the narrated Sattle field guide.

## 00:00 · The whole shop

This is a small online shop running on Azure. A gateway uses the API. The web service also calls the API. The API reaches PostgreSQL through a service called database. Web also uses a cache and storage.

No single source sees that whole picture. Kubernetes describes the services. Network flows show calls. Azure inventory names the resources, and Azure Monitor supplies readings. Adapters translate each report into rows with named fields.

Sattle builds a master graph from those observations. Two queries turn it into a health model. The first finds the workload and its dependencies. The second applies signal rules and works out who is affected.

## 00:41 · What you contribute

The example already ships with working definitions. For your own workload, connect the sources you have permission to read. Choose the namespace and cluster. Here the namespace is shop, and the cluster is aks-synth.

Your team decides what unhealthy means. For pg-shop, the supplied database rule trips after one low minute and clears after two good minutes. It applies to servers of this type, so each server can use the same rule.

The workload query also encodes a judgment. Here a caller depends on the service it calls. If a call is optional in your system, change that rule to represent your design. Sattle can then find matching instances as new evidence arrives.

## 01:22 · The theory

Start with identity. The inventory record and the meter target share the same Azure resource ID. Their evidence meets at one server. The Kubernetes service has a different key. A link says the server backs it.

The paper calls the vocabulary of types and links an ontology. A projection is a query that builds a new view. Watch several pod calls group into one link between web and API. You can follow that link back to its evidence.

The structure has versions. The readings form a time series. New readings can leave the structure unchanged. When a declared rule changes state, its result can change the health graph. That separation keeps a busy meter from redrawing the shop at every sample.

## 02:04 · The pg-shop replay

At ten eleven, the database meter falls from one to zero. The sample reaches the capture at ten twelve and one second. The first low minute trips the authored rule.

pg-shop now has an unhealthy signal of its own. The database service relies on it. API relies on database, and both the gateway and web rely on API. Health changes travel back toward those callers. The cache and storage stay healthy in this incident.

The meter rises at ten fourteen. That is one good minute. At ten fifteen the second good minute clears the signal. The same dependency paths now carry recovery.

## 02:40 · A new dependency

Rewind to the earlier cache incident. In the original run, web uses the overloaded cache. Web and the shop are degraded. The API and gateway stay healthy.

For a second run, we added one deployment update. The API now has a cache host setting naming the existing Azure cache. The shipped structure query matches that host to inventory and adds an API to cache dependency with configuration evidence.

Compare both runs at ten oh six and thirty seconds. With that extra link, the API and its gateway also become degraded. The meter and health rules are identical in both runs. The different result comes from the new path.

## 03:19 · A change on Azure

Here is a separate replay experiment. The API configuration names a new cache called offers-cache. At first Azure inventory has no matching resource. The structure query cannot complete that link yet.

Now add an Azure Resource Graph record for offers-cache. Its type and name produce the host that the API configuration already mentions. The same query adds the resource and the dependency. No new query was needed for this supported Redis type.

The new cache shows up with unknown health. We supplied inventory, but no meter readings for it. A new resource type or a different naming convention may need a new matching rule. A new signal needs an adapter or meter mapping and a health rule when the existing ones do not cover it.

## 04:02 · Change and replay

In a live deployment the engine learns about changes when its sources report them. It updates the affected results and sends a patch, a list of graph edits. A collector falling silent leaves a gap. Silence alone never proves a resource was deleted.

The Canvas starts from a snapshot and applies patches in order. In the bundled static workload export, revision sixteen shows the database outage. Revision eighteen shows recovery. Those are graph versions. The meter uses minutes.

The white paper explains why updating just the changed parts should agree with rebuilding from scratch under its assumptions. Tests run an independent evaluator and compare results. The same recorded inputs and definitions let you repeat the investigation.

## 04:46 · Check the evidence

In the Canvas, open the workload tree and select pg-shop. The origin marker identifies a component with its own failing signal. The Inspector shows its earlier state, its new state, and the graphs that support the result.

Follow the answer back through the workload health query, the structure query, and the source records. To test a different interpretation, edit a draft in the projection editor and preview its rows on a running engine.

The lab collects observations. The engine joins them with your definitions. The Canvas explains the result. In the walkthrough, switch on the new dependency, then add the missing Azure inventory. Watch which parts of the same shop change.
