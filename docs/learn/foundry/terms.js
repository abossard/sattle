// One definition stays beside the case while the corresponding passage plays.
const ShopTerms = {
  estate: [
    ["Source records", "Each dot is a node. Each line is a typed edge that says how two records relate."],
    ["Match identities", "A shared resource ID ties a reading to its account. DNS and endpoint records supply separate links along the route."],
    ["Technical groups", "A projection builds a graph from its inputs. This one groups records by technology and keeps the links between groups."],
    ["Capabilities", "Group software by its job. Payout reads return records and calls the payment API. Both dependencies can be traced to individual links."],
    ["Five views", "Each view selects evidence for a question. The health rule uses call outcomes, while the DNS view uses names and addresses."]
  ],
  rollup:[
    ["Health roll-up","A dependency’s health can affect the service that relies on it."],
    ["Signal rule","One bad minute opens the database alarm. Two good minutes in a row clear it."],
    ["Affects paths","Reachability asks where a path leads. This query keeps the worst state it can reach along up to 16 Affects links."],
    ["Recovery","A positive sample arrives before the alarm clears. The rule waits for a second one."]
  ],
  discovery:[
    ["New call","Hubble records traffic from a Kubernetes pod to an IP address."],
    ["Service dependency","The pod backs the API service. The destination matches a private endpoint for Redis."],
    ["Revision patch","A patch lists the edits between two versions of a graph. Here it adds the API’s dependency on Redis."],
    ["Evidence","Provenance traces a result back to its inputs. The endpoint and the DNS name both identify the Redis cache."]
  ]
};
