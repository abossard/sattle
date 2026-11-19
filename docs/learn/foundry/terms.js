// One definition stays beside the case while the corresponding passage plays.
const ShopTerms = {
  estate: [
    ["Source records", "One dot is one source record. A line here means a record already names a related record."],
    ["Match identities", "A shared resource ID ties a reading to its account. DNS and endpoint records supply separate links along the route."],
    ["Technical groups", "Group entities by technology. Links between groups still show where an application calls a service or resolves a name."],
    ["Capabilities", "Group software by its job. Payout reads return records and calls the payment API. Both dependencies can be traced to individual links."],
    ["Five views", "Each view selects evidence for a question. The health rule uses call outcomes, while the DNS view uses names and addresses."]
  ],
  rollup:[
    ["Health roll-up","A dependency’s health can affect the service that relies on it."],
    ["Signal rule","One bad minute opens the database alarm. Two good minutes in a row clear it."],
    ["Affects paths","The workload query takes the worst severity it can reach along up to 16 Affects links."],
    ["Recovery","A positive sample arrives before the alarm clears. The rule waits for a second one."]
  ],
  discovery:[
    ["New call","Hubble records traffic from a Kubernetes pod to an IP address."],
    ["Service dependency","The pod backs the API service. The destination matches a private endpoint for Redis."],
    ["Revision patch","Compare two revisions to see which relationships the engine added."],
    ["Evidence","The dependency carries a reason. Follow it to the pod, destination address and private endpoint."]
  ]
};
