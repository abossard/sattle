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

## 02:14 · Health Tree

**Listener**

Why is my webshop failing, and when did it start?

**Guide**

The first failed database check was at 10:11 UTC. Follow the branch from the web service through the API to the database server, pg-shop. That’s how a database problem reaches the shop. Redis and storage sit on other branches. A Health Tree lets you start with the service people use and work back to the part that failed.

**Listener**

How do we know that’s the database my shop uses?

**Guide**

Look at the name on the Kubernetes service. It points to a PostgreSQL host. The structure query builds that host name from the Azure server’s name and type. The names match, so Sattle connects the service to that server. The health query combines those connections with measurements and signal rules. As the source records change, those connections can change too.

**Listener**

How long was the database in trouble?

**Guide**

It failed three checks in a row. At 10:11 the database returned its first zero. Sattle raised the alarm at 10:13. At 10:14 the server answered again. The rule waits for two good minutes in a row. At 10:15 that second good check met the rule. The tree showed the recovery at 10:17.

**Listener**

Why does the whole shop show a problem if only the database failed?

**Guide**

Web depends on that database. Start at web itself, so its own readings count. Then follow the links called Affects, up to sixteen steps from web. Three links down, pg-shop has severity three, Unhealthy. That outranks the other branches. The query keeps the worst severity it finds, which is why the database alarm appears at the top of the tree.

## 03:45 · System Changes

**Listener**

Our API just started using Redis. Will that show up in its health model?

**Guide**

Yes. Web already uses Redis, and the API already calls the database. At fifteen seconds past ten oh seven, Hubble records an API pod sending traffic to a new address. That one call gives Sattle something new to investigate. It can use the records it already has to work out what’s at the other end.

**Listener**

All I see is an IP address. How do we know that’s Redis?

**Guide**

Look at the two ends of the call. Kubernetes lists the sending pod under the API service. The destination address matches a private endpoint connected to the Redis cache. The host name on the call also identifies that cache. Put those matches together and you get a dependency from the API to Redis.

**Listener**

Did someone have to add Redis to the API’s tree?

**Guide**

No. Look at the API branch before the call. The database is already there. Now look after the call. Redis appears alongside the database, and web still has its own link to the same cache. The existing rules picked up a new dependency. Nobody had to draw that branch or change the rules.

**Listener**

If I find that branch tomorrow, how can I tell why it’s there?

**Guide**

Start with the new link in the health model. It points back to the API’s dependency in the structure graph. One record matches the destination address to the private endpoint. Another matches the host name to Redis. Both lead back to the pod’s recorded call. That trail from a result to the records behind it is called provenance.

## 05:14 · Reading a GKQL program

**Listener**

Can we see the query that builds a health graph?

**Guide**

Yes. This is a GKQL program from Sattle’s deployment health experiment. The dots in the master graph are pods. Web has two, and the database has one. The query itself is a graph, too. Its boxes are operations. A wire tells us which operation uses another’s result. The target graph shows each workload and the traffic between them.

**Listener**

What does the little arrow inside graph-match look for?

**Guide**

A ready pod and the deployment that controls it. The letters p and d hold their places in the pattern. Watch the first web pod light up. In this match, p stands for that pod, and d stands for web. That pairing is called a binding. The other web pod fits too. So does the database pod. Three matches.

**Listener**

How do those matches give web a health state?

**Guide**

Start each deployment at zero. Add one for every ready pod. The summarize step groups those numbers by deployment and adds them up. Web gets two. The database gets one. The threshold here is zero, so any count above zero means Healthy. Web appears once in the target graph, with its deployment ID and a count of two.

**Listener**

What happens to the connection if the database pod stops being ready?

**Guide**

Follow the two pod calls. Both reach the same database pod, so their matches build one Talks edge from web to the database. At ten oh three, the database pod stops being ready. Its count falls to zero. The edge takes the worse state of its two ends, so it turns red with the database. Web still has two ready pods and stays green.
