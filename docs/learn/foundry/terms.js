// One definition stays beside the case while the corresponding passage plays.
const ShopTerms = {
  estate: [
    ["Source records", "Each dot is a node showing one illustrative source record, such as a pod or a storage account; it is not necessarily a final master-graph entity. Each line is an edge, a link with a type, such as owns or calls, that says how two nodes relate."],
    ["Match identities", "A shared resource ID ties a reading to its account. DNS and endpoint records supply separate links along the route."],
    ["Technical groups", "A projection is a rule that builds one graph from other graphs. This one groups records by technology and keeps the links between groups."],
    ["Capabilities", "Group software by its job. Payout reads return records and calls the payment API. Both dependencies can be traced to individual links."],
    ["Five views", "Each view selects evidence for a question. The health rule uses call outcomes, while the DNS view uses names and addresses."]
  ],
  rollup:[
    ["Health Tree","Start at a service. Its branches show the components whose health can affect it."],
    ["From the master graph","The structure query matches records to build the branches. The health query combines those branches with measurements and signal rules."],
    ["Signal rule","The database rule opens an alarm after one failed minute. Two good minutes in a row clear it. Alarm times are when the model acted, after each record arrived: the 10:11 failure raised the alarm at 10:13."],
    ["Health roll-up","Starting at each component, the query follows up to 16 Affects links and keeps the worst severity it finds, including the component’s own."]
  ],
  discovery:[
    ["New call","The API pod sends traffic to an address that web already uses. The flow records the caller and its destination."],
    ["Identify the dependency","Kubernetes identifies the caller. A private endpoint and a DNS name identify the Redis cache."],
    ["The new branch","The API now depends on the database and Redis. Web still points to that same cache."],
    ["Trace the evidence","Provenance is the trail from the new API dependency to the call and the records that identify its destination."]
  ],
  gkql:[
    ["Program graph","GKQL is one of the notations a projection can be written in, and a GKQL program is itself a graph. Each block names an operation. Its outgoing wires lead to the operations that use its result."],
    ["Match a pattern","A pattern describes a shape to look for. Each match here pairs a ready pod with the deployment that controls it."],
    ["Count ready pods","Each deployment contributes a zero. Each ready pod adds one. The deployment keeps its count even when its last ready pod drops out."],
    ["Two calls, one edge","Both calls connect the same two workloads. Their target is one Talks edge, whose state depends on the workloads at both ends."]
  ]
};
