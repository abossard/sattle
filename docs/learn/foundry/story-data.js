const story = [
  {
    "id": "estate",
    "chapter": "From source records to five views",
    "title": "How does Sattle build the shop’s master graph?",
    "beats": [
      {
        "focus": "Source records",
        "question": "Why is the refund for order one oh four stuck?",
        "answer": "The refund job called the payment API. It waited for a reply and timed out. See those dots? Each is a record from one system. Kubernetes lists workloads. A network flow names two endpoints. A trace records a call. The refund job’s call is our starting point.",
        "start": 0.0,
        "end": 19.541666666666668,
        "answerStarts": [
          3.065
        ]
      },
      {
        "focus": "Match identities",
        "question": "What ties this reading to that storage account?",
        "answer": "They share a full Azure resource ID. Azure Resource Graph lists the storage account, and Azure Monitor puts that ID on its reading. Now follow the DNS name. It resolves to an address. The private endpoint at that address names the storage account. That gives us a path from the name to storage.",
        "start": 19.541666666666668,
        "end": 40.041666666666664,
        "answerStarts": [
          22.406666666666666
        ]
      },
      {
        "focus": "Technical groups",
        "question": "What happens to that path when we group the dots?",
        "answer": "Watch the DNS name, the private endpoint and the storage account. They move into different groups and stay connected. This projection groups records by technology. Kubernetes workloads cluster together. Azure resources, network connections, DNS, application telemetry and shop software each get a group.",
        "start": 40.041666666666664,
        "end": 61.166666666666664,
        "answerStarts": [
          42.83166666666666
        ]
      },
      {
        "focus": "Capabilities",
        "question": "Where does that leave the refund job?",
        "answer": "It belongs to Payout. The job reads the case store in Returns Intake and calls the payment API. Now we’re grouping software by the job it does. Those links show what Payout needs from the other groups. Shared infrastructure has a group too, so we can still trace the path to storage.",
        "start": 61.166666666666664,
        "end": 80.45833333333333,
        "answerStarts": [
          63.556666666666665
        ]
      },
      {
        "focus": "Five views",
        "question": "Which view would help me investigate the timeout?",
        "answer": [
          "Start with the dashboard. Successful payment calls drop to zero.",
          "Open the health tree and you can see why that blocks Payout.",
          "Want to find the pods? The Kubernetes view starts at the refund deployment and follows it to them.",
          "The Azure resource view lists the storage account.",
          "In the DNS and IP view, its name resolves to a private address. All five views draw on the same shop records."
        ],
        "start": 80.45833333333333,
        "end": 109.20833333333333,
        "answerStarts": [
          83.52333333333333,
          87.85333333333334,
          91.58333333333333,
          97.51333333333334,
          100.79333333333334
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
        "answer": "Web needs the API, and the API needs the database. Watch the recorded pg-shop outage. First the database signal turns red. Then you see that failure on the API and web. That’s a health roll-up. A caller’s state reflects what happened to a service it depends on.",
        "start": 109.20833333333333,
        "end": 130.125,
        "answerStarts": [
          113.39833333333333
        ]
      },
      {
        "focus": "Signal rule",
        "question": "What makes the database red in the first place?",
        "answer": "At ten eleven, its availability meter reads zero. Azure Monitor reports the sample a minute later. That’s enough for the shop’s database rule. One bad minute opens the alarm. The reading tells us what the meter saw. The rule decides when that reading means Unhealthy.",
        "start": 130.125,
        "end": 149.04166666666666,
        "answerStarts": [
          132.99
        ]
      },
      {
        "focus": "Affects paths",
        "question": "Why does the alarm reach web and leave the cache green?",
        "answer": "Start at web and follow the arrows marked Affects. There’s a path through the API to the database. The structure query builds those links. The health query follows up to sixteen of them and keeps the worst severity it finds. Start at the cache, though, and no path leads to the failing server. The cache stays green.",
        "start": 149.04166666666666,
        "end": 170.375,
        "answerStarts": [
          152.38166666666666
        ]
      },
      {
        "focus": "Recovery",
        "question": "The meter is back at one. Why is the database still red?",
        "answer": "That’s one good minute. The rule needs two in a row. Ten fourteen is the first. At ten fifteen, the alarm clears and the callers recover. You can see the failure in graph revision sixteen and the recovery in eighteen. The revision numbers count changes. The meter gives us the times.",
        "start": 170.375,
        "end": 191.25,
        "answerStarts": [
          174.165
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
        "answer": "One new Hubble flow. In this copy of the pg-shop recording, an API pod sends traffic to an existing Redis cache. The pod and the destination are already on the map. The new line on the master graph connects the pod to that destination.",
        "start": 191.25,
        "end": 211.875,
        "answerStarts": [
          196.79
        ]
      },
      {
        "focus": "Service dependency",
        "question": "How does that address become a Redis dependency in the health model?",
        "answer": "Kubernetes tells us which service the pod belongs to. It’s the API. At the other end, the address matches a private endpoint for Redis, and the host name identifies the same cache. The existing structure query links the API to Redis. Now when the cache is Degraded, so are the API and its gateway.",
        "start": 211.875,
        "end": 234.375,
        "answerStarts": [
          215.99
        ]
      },
      {
        "focus": "Revision patch",
        "question": "Redis was already there. Where can I find the new link?",
        "answer": "Compare the two revisions in the Canvas. The patch lists the link it added, from the API to Redis. That’s the change you see highlighted. The existing health rule follows that dependency. A warning on the cache can now reach the API.",
        "start": 234.375,
        "end": 252.08333333333334,
        "answerStarts": [
          237.865
        ]
      },
      {
        "focus": "Evidence",
        "question": "How could I check that the API really started using that cache?",
        "answer": "Open the evidence for the link. The structure query kept two matches. One names the pod, the destination address and the private endpoint. The other names the Redis host. Both lead back to the flow and Azure records. The traffic explains the new dependency. The cache’s own load readings explain the warning.",
        "start": 252.08333333333334,
        "end": 274.5416666666667,
        "answerStarts": [
          256.22333333333336
        ]
      }
    ]
  }
];
