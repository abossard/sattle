const story = [
  {
    "id": "estate",
    "chapter": "From source records to five views",
    "title": "How does Sattle build the shop’s master graph?",
    "beats": [
      {
        "focus": "Source records",
        "question": "Why is the refund for order one oh four stuck?",
        "answer": "The refund job called the payment API and timed out waiting for a reply. Its call record tells us that much. To trace the request, start with what each system recorded. Kubernetes lists workloads, a network flow names two endpoints, and a trace shows a call. Each dot is one source record.",
        "start": 0.0,
        "end": 24.625,
        "answerStarts": [
          4.4
        ]
      },
      {
        "focus": "Match identities",
        "question": "What tells us that two records describe the same thing?",
        "answer": "These two carry the same full Azure resource ID. Azure Resource Graph lists a storage account. Azure Monitor puts that account’s ID on a reading. Matching the ID ties the reading to the account. The DNS record supplies a different link. Its name resolves to an address, and the private endpoint at that address names the storage account.",
        "start": 24.625,
        "end": 52.541666666666664,
        "answerStarts": [
          29.375
        ]
      },
      {
        "focus": "Technical groups",
        "question": "Can I see how the technical parts fit together?",
        "answer": "Yes. The DNS answer leads to a private endpoint and then to storage. Follow that path as the dots move. This projection groups records by technology. Kubernetes workloads sit together, alongside groups for Azure resources, network connections, DNS, application telemetry and shop software. The links still cross the groups.",
        "start": 52.541666666666664,
        "end": 79.875,
        "answerStarts": [
          56.94166666666667
        ]
      },
      {
        "focus": "Capabilities",
        "question": "Where does that leave the refund job?",
        "answer": "It belongs to Payout. It reads the case store in Returns Intake and calls the payment API. Grouping by capability puts software with the same job together. Its original links explain what each group depends on. Shared infrastructure has its own group, so the path to storage is still there.",
        "start": 79.875,
        "end": 103.33333333333333,
        "answerStarts": [
          83.5
        ]
      },
      {
        "focus": "Five views",
        "question": "Which view would help me investigate the timeout?",
        "answer": [
          "The dashboard shows successful payment calls falling to zero.",
          "The health tree shows why that blocks Payout.",
          "The Kubernetes view follows the refund deployment to its pods.",
          "The Azure resource view lists the storage account.",
          "The DNS and IP view traces its name to a private address. Each projection draws on the same shop evidence to answer a different question."
        ],
        "start": 103.33333333333333,
        "end": 134.41666666666666,
        "answerStarts": [
          107.75833333333334,
          112.03833333333333,
          115.11833333333334,
          119.72333333333333,
          123.30333333333333
        ]
      }
    ]
  },
  {
    "id": "rollup",
    "chapter": "How health rolls up",
    "title": "Why does web turn red when the database fails?",
    "beats": [
      {
        "focus": "Health roll-up",
        "question": "If the database stops answering, why does web turn red too?",
        "answer": "Web needs the API, and the API needs the database. A health roll-up lets a problem in a dependency change the state shown for its callers. Follow the recorded pg-shop outage. The database signal turns red, and web inherits that state through the API.",
        "start": 134.41666666666666,
        "end": 159.125,
        "answerStarts": [
          139.96666666666667
        ]
      },
      {
        "focus": "Signal rule",
        "question": "What makes the database red in the first place?",
        "answer": "Its availability meter reads zero at ten eleven. Azure Monitor reports that sample a minute later. The shop’s database rule opens an alarm after one bad minute. The reading is what the meter saw. The rule decides what it means for health.",
        "start": 159.125,
        "end": 180.375,
        "answerStarts": [
          163.375
        ]
      },
      {
        "focus": "Affects paths",
        "question": "How does Sattle work out which services feel that alarm?",
        "answer": "The structure query records which parts can affect each service. The health query starts at the service, follows up to sixteen links called Affects, and takes the worst severity it finds. Web reaches the database along that path. The cache has no such path to the failing server, so it stays green.",
        "start": 180.375,
        "end": 204.70833333333334,
        "answerStarts": [
          185.1
        ]
      },
      {
        "focus": "Recovery",
        "question": "The meter is back at one. Why is the database still red?",
        "answer": "The rule waits for two good minutes in a row. Ten fourteen is the first. Ten fifteen clears the alarm, and the callers recover too. In the exported graph, revision sixteen shows the failure and eighteen shows recovery. Those numbers count graph changes. The meter supplies the times.",
        "start": 204.70833333333334,
        "end": 229.875,
        "answerStarts": [
          209.85833333333332
        ]
      }
    ]
  },
  {
    "id": "discovery",
    "chapter": "A pod starts using Redis",
    "title": "What changes when a pod starts talking to Redis?",
    "beats": [
      {
        "focus": "New call",
        "question": "Suppose an API pod starts sending traffic to Redis. What does Sattle see first?",
        "answer": "One new Hubble flow. In a copy of the pg-shop recording, an API pod sends traffic to the address of an existing Redis cache. The master graph gains one link from the pod to the destination. Both were already on the map.",
        "start": 229.875,
        "end": 253.29166666666666,
        "answerStarts": [
          237.025
        ]
      },
      {
        "focus": "Service dependency",
        "question": "How does that address become a Redis dependency in the health model?",
        "answer": "Kubernetes shows that the pod backs the API service. The destination address matches a private endpoint for the Redis cache. The host name identifies the same cache. The existing structure query uses that evidence to add an API dependency. During the cache warning, the health model now marks both the API and its gateway Degraded.",
        "start": 253.29166666666666,
        "end": 282.0833333333333,
        "answerStarts": [
          259.09166666666664
        ]
      },
      {
        "focus": "Revision patch",
        "question": "Where can I see what was new?",
        "answer": "Look at the revision that adds the dependency. Its patch lists a new link from the API to Redis. Compare the two revisions in the Canvas to highlight what changed. Redis was already on the map. The new part is the API’s dependency, and the existing health rule already knows how to use it.",
        "start": 282.0833333333333,
        "end": 304.9583333333333,
        "answerStarts": [
          285.2583333333333
        ]
      },
      {
        "focus": "Evidence",
        "question": "How could I check that the API really started using that cache?",
        "answer": "The structure query kept two pieces of evidence. One names the pod, its destination address and the private endpoint. The other names the Redis host. You can check both against the flow and Azure records. The traffic explains the new dependency. The cache’s own load readings explain the warning.",
        "start": 304.9583333333333,
        "end": 331.0,
        "answerStarts": [
          310.55833333333334
        ]
      }
    ]
  }
];
