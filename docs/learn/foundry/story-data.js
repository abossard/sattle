const story = [
  {
    "id": "estate",
    "chapter": "From source records to five views",
    "title": "What is Sattle doing?",
    "beats": [
      {
        "focus": "Source records",
        "question": "What is Sattle doing?",
        "answer": "It connects the shop’s records so we can follow one order across systems. Each dot in this graph is a record, or node. This one shows why the refund for order one oh four is stuck. The refund job called the payment API and got no reply. In Kubernetes, the owner link tells us which deployment owns a ReplicaSet. A network flow names the two ends of a connection. We call a link a typed edge because its type tells us what the connection means.",
        "start": 0.0,
        "end": 26.125,
        "answerStarts": [
          1.74
        ],
        "spokenCues": [
          [
            2.14,
            "It connects the shop’s records so we can follow one order across systems."
          ],
          [
            5.865,
            "Each dot in this graph is a record, or node."
          ],
          [
            8.34,
            "This one shows why the refund for order one oh four is stuck."
          ],
          [
            11.29,
            "The refund job called the payment API and got no reply."
          ],
          [
            14.59,
            "In Kubernetes, the owner link tells us which deployment owns a ReplicaSet."
          ],
          [
            18.69,
            "A network flow names the two ends of a connection."
          ],
          [
            21.29,
            "We call a link a typed edge"
          ],
          [
            22.665,
            "because its type tells us what the connection means."
          ]
        ]
      },
      {
        "focus": "Match identities",
        "question": "What ties this reading to that storage account?",
        "answer": "They share a full Azure resource ID. Azure Resource Graph names the storage account, and Azure Monitor puts that same ID on its reading. That’s an identity match. Sattle’s ontology supplies the naming rule that ties both records to one account. Now follow the DNS name. It resolves to an address, and the private endpoint names the account. These links connect different things.",
        "start": 26.125,
        "end": 50.583333333333336,
        "answerStarts": [
          28.99
        ],
        "spokenCues": [
          [
            29.365,
            "They share a full Azure resource ID."
          ],
          [
            31.615,
            "Azure Resource Graph names the storage account,"
          ],
          [
            33.965,
            "and Azure Monitor puts that same ID on its reading."
          ],
          [
            36.715,
            "That’s an identity match."
          ],
          [
            38.14,
            "Sattle’s ontology supplies the naming rule"
          ],
          [
            40.29,
            "that ties both records to one account."
          ],
          [
            42.565,
            "Now follow the DNS name."
          ],
          [
            44.115,
            "It resolves to an address, and the private endpoint names the account."
          ],
          [
            47.74,
            "These links connect different things."
          ]
        ]
      },
      {
        "focus": "Technical groups",
        "question": "What happens to that path when we group the dots?",
        "answer": "It stays connected. Follow the DNS name to the address, the private endpoint and the storage account. The dots move into technical groups, but the links still cross those boundaries. A projection is a rule for building one graph from another. This one groups records by technology so you can see how DNS, networking and Azure resources fit together.",
        "start": 50.583333333333336,
        "end": 73.70833333333333,
        "answerStarts": [
          53.373333333333335
        ],
        "spokenCues": [
          [
            53.773,
            "It stays connected."
          ],
          [
            55.173,
            "Follow the DNS name to the address,"
          ],
          [
            56.998,
            "the private endpoint and the storage account."
          ],
          [
            59.498,
            "The dots move into technical groups,"
          ],
          [
            61.473,
            "but the links still cross those boundaries."
          ],
          [
            63.698,
            "A projection is a rule for building one graph from another."
          ],
          [
            66.773,
            "This one groups records by technology so you can see how DNS,"
          ],
          [
            70.373,
            "networking and Azure resources fit together."
          ]
        ]
      },
      {
        "focus": "Capabilities",
        "question": "Where does that leave the refund job?",
        "answer": "It belongs to Payout. A capability groups software by the job it does for the shop. The job reads a case from Returns Intake. Its call to the payment API becomes the blue arrow from Payout to Payment API. That arrow between two groups stands for a call you can trace back to the refund job.",
        "start": 73.70833333333333,
        "end": 94.125,
        "answerStarts": [
          76.09833333333333
        ],
        "spokenCues": [
          [
            76.498,
            "It belongs to Payout."
          ],
          [
            77.923,
            "A capability groups software by the job it does for the shop."
          ],
          [
            81.298,
            "The job reads a case from Returns Intake."
          ],
          [
            83.873,
            "Its call to the payment API becomes the blue arrow from Payout to Payment API."
          ],
          [
            89.048,
            "That arrow between two groups stands for a"
          ],
          [
            90.873,
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
        "start": 94.125,
        "end": 138.0,
        "answerStarts": [
          97.19,
          105.995,
          114.15,
          121.73,
          126.235
        ],
        "spokenCues": [
          [
            97.565,
            "Start with the dashboard."
          ],
          [
            98.94,
            "It counts successful replies."
          ],
          [
            100.665,
            "Two out of two payment calls succeeded in the first minute."
          ],
          [
            103.565,
            "By the last minute, neither got a reply."
          ],
          [
            106.37,
            "The health tree shows what that means for Payout."
          ],
          [
            108.77,
            "Its rule needs a successful payment call."
          ],
          [
            111.17,
            "Payout is blocked, and the order lookup still works."
          ],
          [
            114.525,
            "The Kubernetes view answers a different question."
          ],
          [
            117.15,
            "Which pods run the refund job?"
          ],
          [
            119.075,
            "Follow the owner links down from the deployment."
          ],
          [
            122.08,
            "The Azure resource view lists the storage"
          ],
          [
            124.08,
            "account in the shop’s resource group."
          ],
          [
            126.61,
            "The DNS and IP view shows how its name reaches a private address."
          ],
          [
            130.935,
            "Each view selects what it needs from the same records."
          ],
          [
            133.61,
            "That’s why you can follow one problem across all five."
          ]
        ]
      }
    ]
  },
  {
    "id": "rollup",
    "chapter": "Health Tree",
    "title": "Why is my webshop failing, and when did it start?",
    "beats": [
      {
        "focus": "Health Tree",
        "question": "Why is my webshop failing, and when did it start?",
        "answer": "The first failed database check was at 10:11 UTC. Follow the branch from the web service through the API to the database server, pg-shop. That’s how a database problem reaches the shop. Redis and storage sit on other branches. A Health Tree lets you start with the service people use and work back to the part that failed.",
        "start": 138.0,
        "end": 160.04166666666666,
        "answerStarts": [
          141.165
        ],
        "spokenCues": [
          [
            141.54,
            "The first failed database check was at 10:11 UTC."
          ],
          [
            144.89,
            "Follow the branch from the web service through the API"
          ],
          [
            147.315,
            "to the database server, pg-shop."
          ],
          [
            149.715,
            "That’s how a database problem reaches the shop."
          ],
          [
            152.215,
            "Redis and storage sit on other branches."
          ],
          [
            154.515,
            "A Health Tree lets you start with the service people use"
          ],
          [
            157.115,
            "and work back to the part that failed."
          ]
        ]
      },
      {
        "focus": "From the master graph",
        "question": "How do we know that’s the database my shop uses?",
        "answer": "Look at the name on the Kubernetes service. It points to a PostgreSQL host. The structure query builds that host name from the Azure server’s name and type. The names match, so Sattle connects the service to that server. The health query combines those connections with measurements and signal rules. As the source records change, those connections can change too.",
        "start": 160.04166666666666,
        "end": 183.70833333333334,
        "answerStarts": [
          163.30666666666667
        ],
        "spokenCues": [
          [
            163.707,
            "Look at the name on the Kubernetes service."
          ],
          [
            165.782,
            "It points to a PostgreSQL host."
          ],
          [
            168.057,
            "The structure query builds that host name"
          ],
          [
            169.907,
            "from the Azure server’s name and type."
          ],
          [
            171.957,
            "The names match, so Sattle connects the service to that server."
          ],
          [
            175.282,
            "The health query combines those connections"
          ],
          [
            177.282,
            "with measurements and signal rules."
          ],
          [
            179.357,
            "As the source records change, those connections can change too."
          ]
        ]
      },
      {
        "focus": "Signal rule",
        "question": "How long was the database in trouble?",
        "answer": "It failed three checks in a row. At 10:11 the database returned its first zero. Sattle raised the alarm at 10:13. At 10:14 the server answered again. The rule waits for two good minutes in a row. At 10:15 that second good check met the rule. The tree showed the recovery at 10:17.",
        "start": 183.70833333333334,
        "end": 204.95833333333334,
        "answerStarts": [
          186.42333333333335
        ],
        "spokenCues": [
          [
            186.823,
            "It failed three checks in a row."
          ],
          [
            188.698,
            "At 10:11 the database returned its first zero."
          ],
          [
            191.748,
            "Sattle raised the alarm at 10:13."
          ],
          [
            193.998,
            "At 10:14 the server answered again."
          ],
          [
            196.248,
            "The rule waits for two good minutes in a row."
          ],
          [
            198.648,
            "At 10:15 that second good check met the rule."
          ],
          [
            201.348,
            "The tree showed the recovery at 10:17."
          ]
        ]
      },
      {
        "focus": "Health roll-up",
        "question": "Why does the whole shop show a problem if only the database failed?",
        "answer": "Web depends on that database. Start at web itself, so its own readings count. Then follow the links called Affects, up to sixteen steps from web. Three links down, pg-shop has severity three, Unhealthy. That outranks the other branches. The query keeps the worst severity it finds, which is why the database alarm appears at the top of the tree.",
        "start": 204.95833333333334,
        "end": 229.45833333333334,
        "answerStarts": [
          209.12333333333333
        ],
        "spokenCues": [
          [
            209.548,
            "Web depends on that database."
          ],
          [
            211.373,
            "Start at web itself, so its own readings count."
          ],
          [
            213.948,
            "Then follow the links called Affects, up to sixteen steps from web."
          ],
          [
            217.448,
            "Three links down, pg-shop has severity three, Unhealthy."
          ],
          [
            221.098,
            "That outranks the other branches."
          ],
          [
            222.823,
            "The query keeps the worst severity it finds,"
          ],
          [
            225.148,
            "which is why the database alarm appears at the top of the tree."
          ]
        ]
      }
    ]
  },
  {
    "id": "discovery",
    "chapter": "System Changes",
    "title": "How does Redis show up in the API’s Health Tree?",
    "beats": [
      {
        "focus": "New call",
        "question": "Our API just started using Redis. Will that show up in its health model?",
        "answer": "Yes. Web already uses Redis, and the API already calls the database. At fifteen seconds past ten oh seven, Hubble records an API pod sending traffic to a new address. That one call gives Sattle something new to investigate. It can use the records it already has to work out what’s at the other end.",
        "start": 229.45833333333334,
        "end": 252.41666666666666,
        "answerStarts": [
          234.47333333333333
        ],
        "spokenCues": [
          [
            234.898,
            "Yes."
          ],
          [
            235.398,
            "Web already uses Redis, and the API already calls the database."
          ],
          [
            239.523,
            "At fifteen seconds past ten oh seven,"
          ],
          [
            241.648,
            "Hubble records an API pod sending traffic to a new address."
          ],
          [
            245.273,
            "That one call gives Sattle something new to investigate."
          ],
          [
            248.173,
            "It can use the records it already has to work out what’s at the other end."
          ]
        ]
      },
      {
        "focus": "Identify the dependency",
        "question": "All I see is an IP address. How do we know that’s Redis?",
        "answer": "Look at the two ends of the call. Kubernetes lists the sending pod under the API service. The destination address matches a private endpoint connected to the Redis cache. The host name on the call also identifies that cache. Put those matches together and you get a dependency from the API to Redis.",
        "start": 252.41666666666666,
        "end": 274.2916666666667,
        "answerStarts": [
          256.4316666666667
        ],
        "spokenCues": [
          [
            256.832,
            "Look at the two ends of the call."
          ],
          [
            258.357,
            "Kubernetes lists the sending pod under the API service."
          ],
          [
            261.807,
            "The destination address matches a private"
          ],
          [
            263.732,
            "endpoint connected to the Redis cache."
          ],
          [
            266.057,
            "The host name on the call also identifies that cache."
          ],
          [
            269.182,
            "Put those matches together and you get a dependency from the API to Redis."
          ]
        ]
      },
      {
        "focus": "The new branch",
        "question": "Did someone have to add Redis to the API’s tree?",
        "answer": "No. Look at the API branch before the call. The database is already there. Now look after the call. Redis appears alongside the database, and web still has its own link to the same cache. The existing rules picked up a new dependency. Nobody had to draw that branch or change the rules.",
        "start": 274.2916666666667,
        "end": 294.5416666666667,
        "answerStarts": [
          277.5566666666667
        ],
        "spokenCues": [
          [
            277.982,
            "No."
          ],
          [
            278.357,
            "Look at the API branch before the call."
          ],
          [
            280.507,
            "The database is already there."
          ],
          [
            282.182,
            "Now look after the call."
          ],
          [
            283.632,
            "Redis appears alongside the database,"
          ],
          [
            285.582,
            "and web still has its own link to the same cache."
          ],
          [
            288.332,
            "The existing rules picked up a new dependency."
          ],
          [
            290.932,
            "Nobody had to draw that branch or change the rules."
          ]
        ]
      },
      {
        "focus": "Trace the evidence",
        "question": "If I find that branch tomorrow, how can I tell why it’s there?",
        "answer": "Start with the new link in the health model. It points back to the API’s dependency in the structure graph. One record matches the destination address to the private endpoint. Another matches the host name to Redis. Both lead back to the pod’s recorded call. That trail from a result to the records behind it is called provenance.",
        "start": 294.5416666666667,
        "end": 317.9166666666667,
        "answerStarts": [
          298.4316666666667
        ],
        "spokenCues": [
          [
            298.832,
            "Start with the new link in the health model."
          ],
          [
            300.982,
            "It points back to the API’s dependency in the structure graph."
          ],
          [
            304.482,
            "One record matches the destination address to the private endpoint."
          ],
          [
            308.057,
            "Another matches the host name to Redis."
          ],
          [
            310.357,
            "Both lead back to the pod’s recorded call."
          ],
          [
            312.982,
            "That trail from a result to the records behind it is called provenance."
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
        "start": 317.9166666666667,
        "end": 341.625,
        "answerStarts": [
          320.75666666666666
        ],
        "spokenCues": [
          [
            321.182,
            "Yes."
          ],
          [
            321.582,
            "This is a GKQL program from Sattle’s deployment health experiment."
          ],
          [
            325.682,
            "The dots in the master graph are pods."
          ],
          [
            327.907,
            "Web has two, and the database has one."
          ],
          [
            330.257,
            "The query itself is a graph, too."
          ],
          [
            332.357,
            "Its boxes are operations."
          ],
          [
            334.282,
            "A wire tells us which operation uses another’s result."
          ],
          [
            337.407,
            "The target graph shows each workload and the traffic between them."
          ]
        ]
      },
      {
        "focus": "Match a pattern",
        "question": "What does the little arrow inside graph-match look for?",
        "answer": "A ready pod and the deployment that controls it. The letters p and d hold their places in the pattern. Watch the first web pod light up. In this match, p stands for that pod, and d stands for web. That pairing is called a binding. The other web pod fits too. So does the database pod. Three matches.",
        "start": 341.625,
        "end": 362.9166666666667,
        "answerStarts": [
          344.99
        ],
        "spokenCues": [
          [
            345.39,
            "A ready pod and the deployment that controls it."
          ],
          [
            347.89,
            "The letters p and d hold their places in the pattern."
          ],
          [
            350.69,
            "Watch the first web pod light up."
          ],
          [
            352.69,
            "In this match, p stands for that pod, and d stands for web."
          ],
          [
            356.015,
            "That pairing is called a binding."
          ],
          [
            357.865,
            "The other web pod fits too."
          ],
          [
            359.565,
            "So does the database pod."
          ],
          [
            360.965,
            "Three matches."
          ]
        ]
      },
      {
        "focus": "Count ready pods",
        "question": "How do those matches give web a health state?",
        "answer": "Start each deployment at zero. Add one for every ready pod. The summarize step groups those numbers by deployment and adds them up. Web gets two. The database gets one. The threshold here is zero, so any count above zero means Healthy. Web appears once in the target graph, with its deployment ID and a count of two.",
        "start": 362.9166666666667,
        "end": 384.7916666666667,
        "answerStarts": [
          365.83166666666665
        ],
        "spokenCues": [
          [
            366.207,
            "Start each deployment at zero."
          ],
          [
            368.057,
            "Add one for every ready pod."
          ],
          [
            369.832,
            "The summarize step groups those numbers by deployment and adds them up."
          ],
          [
            373.407,
            "Web gets two."
          ],
          [
            374.482,
            "The database gets one."
          ],
          [
            375.982,
            "The threshold here is zero, so any count above zero means Healthy."
          ],
          [
            379.757,
            "Web appears once in the target graph,"
          ],
          [
            381.557,
            "with its deployment ID and a count of two."
          ]
        ]
      },
      {
        "focus": "Two calls, one edge",
        "question": "What happens to the connection if the database pod stops being ready?",
        "answer": "Follow the two pod calls. Both reach the same database pod, so their matches build one Talks edge from web to the database. At ten oh three, the database pod stops being ready. Its count falls to zero. The edge takes the worse state of its two ends, so it turns red with the database. Web still has two ready pods and stays green.",
        "start": 384.7916666666667,
        "end": 409.4166666666667,
        "answerStarts": [
          389.20666666666665
        ],
        "spokenCues": [
          [
            389.607,
            "Follow the two pod calls."
          ],
          [
            391.332,
            "Both reach the same database pod,"
          ],
          [
            393.332,
            "so their matches build one Talks edge from web to the database."
          ],
          [
            396.732,
            "At ten oh three, the database pod stops being ready."
          ],
          [
            399.607,
            "Its count falls to zero."
          ],
          [
            401.307,
            "The edge takes the worse state of its two ends,"
          ],
          [
            403.432,
            "so it turns red with the database."
          ],
          [
            405.382,
            "Web still has two ready pods and stays green."
          ]
        ]
      }
    ]
  }
];
