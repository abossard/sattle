const story = [
  {
    "id": "estate",
    "chapter": "From source records to five views",
    "title": "How does Sattle build the shop’s master graph?",
    "beats": [
      {
        "focus": "Source records",
        "question": "Why is the refund for order one oh four stuck?",
        "answer": "The payment call timed out. That trace tells us what the refund job saw. Each dot here is a node, one source record in this picture. In Kubernetes, the owner link tells us which deployment owns a ReplicaSet. A network flow names the two ends of a connection. We call a link a typed edge because its type tells us what the connection means.",
        "start": 0.0,
        "end": 22.5,
        "answerStarts": [
          3.065
        ],
        "spokenCues": [
          [
            3.415,
            "The payment call timed out."
          ],
          [
            4.965,
            "That trace tells us what the refund job saw."
          ],
          [
            7.365,
            "Each dot here is a node, one source record in this picture."
          ],
          [
            10.79,
            "In Kubernetes, the owner link tells us which deployment owns a ReplicaSet."
          ],
          [
            15.015,
            "A network flow names the two ends of a connection."
          ],
          [
            17.665,
            "We call a link a typed edge"
          ],
          [
            19.04,
            "because its type tells us what the connection means."
          ]
        ]
      },
      {
        "focus": "Match identities",
        "question": "What ties this reading to that storage account?",
        "answer": "They share a full Azure resource ID. Azure Resource Graph names the storage account, and Azure Monitor puts that same ID on its reading. That’s an identity match. Sattle’s ontology supplies the naming rule that ties both records to one account. Now follow the DNS name. It resolves to an address, and the private endpoint names the account. These links connect different things.",
        "start": 22.5,
        "end": 46.958333333333336,
        "answerStarts": [
          25.365
        ],
        "spokenCues": [
          [
            25.74,
            "They share a full Azure resource ID."
          ],
          [
            27.99,
            "Azure Resource Graph names the storage account,"
          ],
          [
            30.34,
            "and Azure Monitor puts that same ID on its reading."
          ],
          [
            33.09,
            "That’s an identity match."
          ],
          [
            34.515,
            "Sattle’s ontology supplies the naming rule"
          ],
          [
            36.665,
            "that ties both records to one account."
          ],
          [
            38.94,
            "Now follow the DNS name."
          ],
          [
            40.49,
            "It resolves to an address, and the private endpoint names the account."
          ],
          [
            44.115,
            "These links connect different things."
          ]
        ]
      },
      {
        "focus": "Technical groups",
        "question": "What happens to that path when we group the dots?",
        "answer": "It stays connected. Follow the DNS name to the address, the private endpoint and the storage account. The dots move into technical groups, but the links still cross those boundaries. A projection is a rule for building one graph from another. This one groups records by technology so you can see how DNS, networking and Azure resources fit together.",
        "start": 46.958333333333336,
        "end": 70.08333333333333,
        "answerStarts": [
          49.748333333333335
        ],
        "spokenCues": [
          [
            50.148,
            "It stays connected."
          ],
          [
            51.548,
            "Follow the DNS name to the address,"
          ],
          [
            53.373,
            "the private endpoint and the storage account."
          ],
          [
            55.873,
            "The dots move into technical groups,"
          ],
          [
            57.848,
            "but the links still cross those boundaries."
          ],
          [
            60.073,
            "A projection is a rule for building one graph from another."
          ],
          [
            63.148,
            "This one groups records by technology so you can see how DNS,"
          ],
          [
            66.748,
            "networking and Azure resources fit together."
          ]
        ]
      },
      {
        "focus": "Capabilities",
        "question": "Where does that leave the refund job?",
        "answer": "It belongs to Payout. A capability groups software by the job it does for the shop. The job reads a case from Returns Intake. Its call to the payment API becomes the blue arrow from Payout to Payment API. That arrow between two groups stands for a call you can trace back to the refund job.",
        "start": 70.08333333333333,
        "end": 90.5,
        "answerStarts": [
          72.47333333333333
        ],
        "spokenCues": [
          [
            72.873,
            "It belongs to Payout."
          ],
          [
            74.298,
            "A capability groups software by the job it does for the shop."
          ],
          [
            77.673,
            "The job reads a case from Returns Intake."
          ],
          [
            80.248,
            "Its call to the payment API becomes the blue arrow from Payout to Payment API."
          ],
          [
            85.423,
            "That arrow between two groups stands for a"
          ],
          [
            87.248,
            "call you can trace back to the refund job."
          ]
        ]
      },
      {
        "focus": "Five views",
        "question": "Which view would help me investigate the timeout?",
        "answer": [
          "Start with the dashboard. It counts successful replies. Two out of two payment calls succeeded in the first minute. By the last minute, neither got a reply.",
          "The health tree shows what that means for Payout. Its rule needs a successful payment call. Payout is blocked, and the order lookup still works.",
          "The Kubernetes view answers a different question. Which pods run the refund job? Follow the owner links down from the deployment.",
          "The Azure resource view lists the storage account in the shop’s resource group.",
          "The DNS and IP view shows how its name reaches a private address. Each view selects what it needs from the same records. That’s why you can follow one problem across all five."
        ],
        "start": 90.5,
        "end": 134.375,
        "answerStarts": [
          93.565,
          102.37,
          110.525,
          118.105,
          122.61
        ],
        "spokenCues": [
          [
            93.94,
            "Start with the dashboard."
          ],
          [
            95.315,
            "It counts successful replies."
          ],
          [
            97.04,
            "Two out of two payment calls succeeded in the first minute."
          ],
          [
            99.94,
            "By the last minute, neither got a reply."
          ],
          [
            102.745,
            "The health tree shows what that means for Payout."
          ],
          [
            105.145,
            "Its rule needs a successful payment call."
          ],
          [
            107.545,
            "Payout is blocked, and the order lookup still works."
          ],
          [
            110.9,
            "The Kubernetes view answers a different question."
          ],
          [
            113.525,
            "Which pods run the refund job?"
          ],
          [
            115.45,
            "Follow the owner links down from the deployment."
          ],
          [
            118.455,
            "The Azure resource view lists the storage"
          ],
          [
            120.455,
            "account in the shop’s resource group."
          ],
          [
            122.985,
            "The DNS and IP view shows how its name reaches a private address."
          ],
          [
            127.31,
            "Each view selects what it needs from the same records."
          ],
          [
            129.985,
            "That’s why you can follow one problem across all five."
          ]
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
        "answer": "Web needs the API, and the API needs the database. Look at the two arrows. The blue one points from the database service to the server it needs. The red one points back. A failure can affect the caller. That’s health roll-up, and it explains why the database, API and web all read Unhealthy in this revision.",
        "start": 134.375,
        "end": 157.0,
        "answerStarts": [
          138.565
        ],
        "spokenCues": [
          [
            138.99,
            "Web needs the API, and the API needs the database."
          ],
          [
            142.465,
            "Look at the two arrows."
          ],
          [
            143.765,
            "The blue one points from the database service to the server it needs."
          ],
          [
            146.94,
            "The red one points back."
          ],
          [
            148.415,
            "A failure can affect the caller."
          ],
          [
            150.165,
            "That’s health roll-up, and it explains why the database,"
          ],
          [
            152.915,
            "API and web all read Unhealthy in this revision."
          ]
        ]
      },
      {
        "focus": "Signal rule",
        "question": "What makes the database red in the first place?",
        "answer": "One bad minute opens the alarm. The chart shows the meter dropping to zero at ten eleven. Azure Monitor delivers that reading a minute later. Zero is a measurement. The database rule gives it meaning for health. On this rule, one failed sample makes the database Unhealthy.",
        "start": 157.0,
        "end": 176.29166666666666,
        "answerStarts": [
          159.865
        ],
        "spokenCues": [
          [
            160.29,
            "One bad minute opens the alarm."
          ],
          [
            162.34,
            "The chart shows the meter dropping to zero at ten eleven."
          ],
          [
            165.44,
            "Azure Monitor delivers that reading a minute later."
          ],
          [
            168.015,
            "Zero is a measurement."
          ],
          [
            169.465,
            "The database rule gives it meaning for health."
          ],
          [
            171.865,
            "On this rule, one failed sample makes the database Unhealthy."
          ]
        ]
      },
      {
        "focus": "Affects paths",
        "question": "Why does the alarm reach web and leave the cache green?",
        "answer": "Web has a path to the failing database through the API. The cache has no such path. Follow the arrows to see what each service depends on. The structure query builds those links. The health query follows up to sixteen Affects links and takes the worst state it can reach. That’s why the cache stays green.",
        "start": 176.29166666666666,
        "end": 197.66666666666666,
        "answerStarts": [
          179.63166666666666
        ],
        "spokenCues": [
          [
            180.057,
            "Web has a path to the failing database through the API."
          ],
          [
            183.157,
            "The cache has no such path."
          ],
          [
            184.957,
            "Follow the arrows to see what each service depends on."
          ],
          [
            187.882,
            "The structure query builds those links."
          ],
          [
            190.082,
            "The health query follows up to sixteen Affects links"
          ],
          [
            192.757,
            "and takes the worst state it can reach."
          ],
          [
            194.857,
            "That’s why the cache stays green."
          ]
        ]
      },
      {
        "focus": "Recovery",
        "question": "The meter is back at one. Why is the database still red?",
        "answer": "The rule remembers how many good minutes came in a row. At ten fourteen the count is one, and the alarm stays open. Ten fifteen makes two. The alarm clears and the callers recover. The times across the top come from the meter. Revision eighteen names the version of the graph that records the recovery.",
        "start": 197.66666666666666,
        "end": 219.45833333333334,
        "answerStarts": [
          201.45666666666668
        ],
        "spokenCues": [
          [
            201.832,
            "The rule remembers how many good minutes came in a row."
          ],
          [
            204.757,
            "At ten fourteen the count is one, and the alarm stays open."
          ],
          [
            208.182,
            "Ten fifteen makes two."
          ],
          [
            209.582,
            "The alarm clears and the callers recover."
          ],
          [
            211.957,
            "The times across the top come from the meter."
          ],
          [
            214.357,
            "Revision eighteen names the version of the graph that records the recovery."
          ]
        ]
      }
    ]
  },
  {
    "id": "discovery",
    "chapter": "When the system changes",
    "title": "How does the health model keep up when something changes?",
    "beats": [
      {
        "focus": "New call",
        "question": "Can the health model pick up a new dependency on its own?",
        "answer": "Yes. Hubble recorded one flow from the API pod to this address. The new line means the pod sent traffic there. When Sattle processes that observation, the existing rules connect the pod to the API and the address to Redis. The health model picks up the new dependency without anyone editing it.",
        "start": 219.45833333333334,
        "end": 240.75,
        "answerStarts": [
          223.07333333333332
        ],
        "spokenCues": [
          [
            223.498,
            "Yes."
          ],
          [
            223.948,
            "Hubble recorded one flow from the API pod to this address."
          ],
          [
            227.423,
            "The new line means the pod sent traffic there."
          ],
          [
            229.948,
            "When Sattle processes that observation,"
          ],
          [
            232.023,
            "the existing rules connect the pod to the API and the address to Redis."
          ],
          [
            236.298,
            "The health model picks up the new dependency without anyone editing it."
          ]
        ]
      },
      {
        "focus": "Service dependency",
        "question": "How does that address become a Redis dependency in the health model?",
        "answer": "Kubernetes ties the pod to the API service. At the other end, the address matches a private endpoint for Redis. The DNS name points to the same cache. That gives the structure query both ends of a service dependency. It derives the API to Redis link from those records. The health model can now follow it using its existing rule.",
        "start": 240.75,
        "end": 265.1666666666667,
        "answerStarts": [
          244.865
        ],
        "spokenCues": [
          [
            245.24,
            "Kubernetes ties the pod to the API service."
          ],
          [
            248.365,
            "At the other end, the address matches a private endpoint for Redis."
          ],
          [
            252.015,
            "The DNS name points to the same cache."
          ],
          [
            254.34,
            "That gives the structure query both ends of a service dependency."
          ],
          [
            257.89,
            "It derives the API to Redis link from those records."
          ],
          [
            261.04,
            "The health model can now follow it using its existing rule."
          ]
        ]
      },
      {
        "focus": "Revision patch",
        "question": "Redis was already there. Where can I find the new link?",
        "answer": "Look for the plus sign. A patch lists the edits from one graph revision to the next. In the master graph, the added line means the pod sent traffic. In the structure graph, it means the API depends on Redis. In the health graph, revision fourteen adds that dependency. The cache was there all along.",
        "start": 265.1666666666667,
        "end": 286.375,
        "answerStarts": [
          268.6566666666667
        ],
        "spokenCues": [
          [
            269.057,
            "Look for the plus sign."
          ],
          [
            270.407,
            "A patch lists the edits from one graph revision to the next."
          ],
          [
            273.532,
            "In the master graph, the added line means the pod sent traffic."
          ],
          [
            276.882,
            "In the structure graph, it means the API depends on Redis."
          ],
          [
            280.457,
            "In the health graph, revision fourteen adds that dependency."
          ],
          [
            283.757,
            "The cache was there all along."
          ]
        ]
      },
      {
        "focus": "Evidence",
        "question": "How could I check that the API really started using that cache?",
        "answer": "Follow the dependency back through the graphs. The structure query kept two matches. One links the pod’s destination to a private endpoint. The other identifies Redis by its DNS name. That trail from a conclusion back to its inputs is called provenance. Now compare the two runs. Redis has the same load warning in both. The added dependency explains why the API and gateway turn amber.",
        "start": 286.375,
        "end": 313.5,
        "answerStarts": [
          290.515
        ],
        "spokenCues": [
          [
            290.915,
            "Follow the dependency back through the graphs."
          ],
          [
            293.115,
            "The structure query kept two matches."
          ],
          [
            295.24,
            "One links the pod’s destination to a private endpoint."
          ],
          [
            298.165,
            "The other identifies Redis by its DNS name."
          ],
          [
            300.84,
            "That trail from a conclusion back to its inputs is called provenance."
          ],
          [
            304.44,
            "Now compare the two runs."
          ],
          [
            305.99,
            "Redis has the same load warning in both."
          ],
          [
            308.19,
            "The added dependency explains why the API and gateway turn amber."
          ]
        ]
      }
    ]
  },
  {
    "id": "gkql",
    "chapter": "Reading a GKQL program",
    "title": "How does GKQL turn pods and calls into a health graph?",
    "beats": [
      {
        "focus": "Program graph",
        "question": "Can we see the query that builds a health graph?",
        "answer": "Yes. This is a GKQL program from Sattle’s deployment health experiment. The dots in the master graph are pods. Web has two, and the database has one. The query itself is a graph, too. Its boxes are operations. A wire tells us which operation uses another’s result. The target graph shows each workload and the traffic between them.",
        "start": 313.5,
        "end": 337.2083333333333,
        "answerStarts": [
          316.34
        ],
        "spokenCues": [
          [
            316.765,
            "Yes."
          ],
          [
            317.165,
            "This is a GKQL program from Sattle’s deployment health experiment."
          ],
          [
            321.265,
            "The dots in the master graph are pods."
          ],
          [
            323.49,
            "Web has two, and the database has one."
          ],
          [
            325.84,
            "The query itself is a graph, too."
          ],
          [
            327.94,
            "Its boxes are operations."
          ],
          [
            329.865,
            "A wire tells us which operation uses another’s result."
          ],
          [
            332.99,
            "The target graph shows each workload and the traffic between them."
          ]
        ]
      },
      {
        "focus": "Match a pattern",
        "question": "What does the little arrow inside graph-match look for?",
        "answer": "A ready pod and the deployment that controls it. The letters p and d hold their places in the pattern. Watch the first web pod light up. In this match, p stands for that pod, and d stands for web. That pairing is called a binding. The other web pod fits too. So does the database pod. Three matches.",
        "start": 337.2083333333333,
        "end": 358.5,
        "answerStarts": [
          340.5733333333333
        ],
        "spokenCues": [
          [
            340.973,
            "A ready pod and the deployment that controls it."
          ],
          [
            343.473,
            "The letters p and d hold their places in the pattern."
          ],
          [
            346.273,
            "Watch the first web pod light up."
          ],
          [
            348.273,
            "In this match, p stands for that pod, and d stands for web."
          ],
          [
            351.598,
            "That pairing is called a binding."
          ],
          [
            353.448,
            "The other web pod fits too."
          ],
          [
            355.148,
            "So does the database pod."
          ],
          [
            356.548,
            "Three matches."
          ]
        ]
      },
      {
        "focus": "Count ready pods",
        "question": "How do those matches give web a health state?",
        "answer": "Start each deployment at zero. Add one for every ready pod. The summarize step groups those numbers by deployment and adds them up. Web gets two. The database gets one. The threshold here is zero, so any count above zero means Healthy. Web appears once in the target graph, with its deployment ID and a count of two.",
        "start": 358.5,
        "end": 380.375,
        "answerStarts": [
          361.415
        ],
        "spokenCues": [
          [
            361.79,
            "Start each deployment at zero."
          ],
          [
            363.64,
            "Add one for every ready pod."
          ],
          [
            365.415,
            "The summarize step groups those numbers by deployment and adds them up."
          ],
          [
            368.99,
            "Web gets two."
          ],
          [
            370.065,
            "The database gets one."
          ],
          [
            371.565,
            "The threshold here is zero, so any count above zero means Healthy."
          ],
          [
            375.34,
            "Web appears once in the target graph,"
          ],
          [
            377.14,
            "with its deployment ID and a count of two."
          ]
        ]
      },
      {
        "focus": "Two calls, one edge",
        "question": "What happens to the connection if the database pod stops being ready?",
        "answer": "Follow the two pod calls. Both reach the same database pod, so their matches build one Talks edge from web to the database. At ten oh three, the database pod stops being ready. Its count falls to zero. The edge takes the worse state of its two ends, so it turns red with the database. Web still has two ready pods and stays green.",
        "start": 380.375,
        "end": 405.0,
        "answerStarts": [
          384.79
        ],
        "spokenCues": [
          [
            385.19,
            "Follow the two pod calls."
          ],
          [
            386.915,
            "Both reach the same database pod,"
          ],
          [
            388.915,
            "so their matches build one Talks edge from web to the database."
          ],
          [
            392.315,
            "At ten oh three, the database pod stops being ready."
          ],
          [
            395.19,
            "Its count falls to zero."
          ],
          [
            396.89,
            "The edge takes the worse state of its two ends,"
          ],
          [
            399.015,
            "so it turns red with the database."
          ],
          [
            400.965,
            "Web still has two ready pods and stays green."
          ]
        ]
      }
    ]
  }
];
