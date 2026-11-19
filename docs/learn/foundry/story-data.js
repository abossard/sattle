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
    "chapter": "Health Tree",
    "title": "Why is my webshop failing, and when did it start?",
    "beats": [
      {
        "focus": "Health Tree",
        "question": "Why is my webshop failing, and when did it start?",
        "answer": "The first failed database check was at 10:11 UTC. Follow the branch from the web service through the API to the database server, pg-shop. That’s how a database problem reaches the shop. Redis and storage sit on other branches. A Health Tree lets you start with the service people use and work back to the part that failed.",
        "start": 134.375,
        "end": 156.41666666666666,
        "answerStarts": [
          137.54
        ],
        "spokenCues": [
          [
            137.915,
            "The first failed database check was at 10:11 UTC."
          ],
          [
            141.265,
            "Follow the branch from the web service through the API"
          ],
          [
            143.69,
            "to the database server, pg-shop."
          ],
          [
            146.09,
            "That’s how a database problem reaches the shop."
          ],
          [
            148.59,
            "Redis and storage sit on other branches."
          ],
          [
            150.89,
            "A Health Tree lets you start with the service people use"
          ],
          [
            153.49,
            "and work back to the part that failed."
          ]
        ]
      },
      {
        "focus": "From the master graph",
        "question": "How do we know that’s the database my shop uses?",
        "answer": "Look at the name on the Kubernetes service. It points to a PostgreSQL host. The structure query builds that host name from the Azure server’s name and type. The names match, so Sattle connects the service to that server. The health query combines those connections with measurements and signal rules. As the source records change, those connections can change too.",
        "start": 156.41666666666666,
        "end": 180.08333333333334,
        "answerStarts": [
          159.68166666666667
        ],
        "spokenCues": [
          [
            160.082,
            "Look at the name on the Kubernetes service."
          ],
          [
            162.157,
            "It points to a PostgreSQL host."
          ],
          [
            164.432,
            "The structure query builds that host name"
          ],
          [
            166.282,
            "from the Azure server’s name and type."
          ],
          [
            168.332,
            "The names match, so Sattle connects the service to that server."
          ],
          [
            171.657,
            "The health query combines those connections"
          ],
          [
            173.657,
            "with measurements and signal rules."
          ],
          [
            175.732,
            "As the source records change, those connections can change too."
          ]
        ]
      },
      {
        "focus": "Signal rule",
        "question": "How long was the database in trouble?",
        "answer": "It failed three checks in a row. At 10:11 the database returned its first zero. Sattle raised the alarm at 10:13. At 10:14 the server answered again. The rule waits for two good minutes in a row. At 10:15 that second good check met the rule. The tree showed the recovery at 10:17.",
        "start": 180.08333333333334,
        "end": 201.33333333333334,
        "answerStarts": [
          182.79833333333335
        ],
        "spokenCues": [
          [
            183.198,
            "It failed three checks in a row."
          ],
          [
            185.073,
            "At 10:11 the database returned its first zero."
          ],
          [
            188.123,
            "Sattle raised the alarm at 10:13."
          ],
          [
            190.373,
            "At 10:14 the server answered again."
          ],
          [
            192.623,
            "The rule waits for two good minutes in a row."
          ],
          [
            195.023,
            "At 10:15 that second good check met the rule."
          ],
          [
            197.723,
            "The tree showed the recovery at 10:17."
          ]
        ]
      },
      {
        "focus": "Health roll-up",
        "question": "Why does the whole shop show a problem if only the database failed?",
        "answer": "Web depends on that database. Start at web itself, so its own readings count. Then follow the links called Affects, up to sixteen steps from web. Three links down, pg-shop has severity three, Unhealthy. That outranks the other branches. The query keeps the worst severity it finds, which is why the database alarm appears at the top of the tree.",
        "start": 201.33333333333334,
        "end": 225.83333333333334,
        "answerStarts": [
          205.49833333333333
        ],
        "spokenCues": [
          [
            205.923,
            "Web depends on that database."
          ],
          [
            207.748,
            "Start at web itself, so its own readings count."
          ],
          [
            210.323,
            "Then follow the links called Affects, up to sixteen steps from web."
          ],
          [
            213.823,
            "Three links down, pg-shop has severity three, Unhealthy."
          ],
          [
            217.473,
            "That outranks the other branches."
          ],
          [
            219.198,
            "The query keeps the worst severity it finds,"
          ],
          [
            221.523,
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
        "start": 225.83333333333334,
        "end": 248.79166666666666,
        "answerStarts": [
          230.84833333333333
        ],
        "spokenCues": [
          [
            231.273,
            "Yes."
          ],
          [
            231.773,
            "Web already uses Redis, and the API already calls the database."
          ],
          [
            235.898,
            "At fifteen seconds past ten oh seven,"
          ],
          [
            238.023,
            "Hubble records an API pod sending traffic to a new address."
          ],
          [
            241.648,
            "That one call gives Sattle something new to investigate."
          ],
          [
            244.548,
            "It can use the records it already has to work out what’s at the other end."
          ]
        ]
      },
      {
        "focus": "Identify the dependency",
        "question": "All I see is an IP address. How do we know that’s Redis?",
        "answer": "Look at the two ends of the call. Kubernetes lists the sending pod under the API service. The destination address matches a private endpoint connected to the Redis cache. The host name on the call also identifies that cache. Put those matches together and you get a dependency from the API to Redis.",
        "start": 248.79166666666666,
        "end": 270.6666666666667,
        "answerStarts": [
          252.80666666666667
        ],
        "spokenCues": [
          [
            253.207,
            "Look at the two ends of the call."
          ],
          [
            254.732,
            "Kubernetes lists the sending pod under the API service."
          ],
          [
            258.182,
            "The destination address matches a private"
          ],
          [
            260.107,
            "endpoint connected to the Redis cache."
          ],
          [
            262.432,
            "The host name on the call also identifies that cache."
          ],
          [
            265.557,
            "Put those matches together and you get a dependency from the API to Redis."
          ]
        ]
      },
      {
        "focus": "The new branch",
        "question": "Did someone have to add Redis to the API’s tree?",
        "answer": "No. Look at the API branch before the call. The database is already there. Now look after the call. Redis appears alongside the database, and web still has its own link to the same cache. The existing rules picked up a new dependency. Nobody had to draw that branch or change the rules.",
        "start": 270.6666666666667,
        "end": 290.9166666666667,
        "answerStarts": [
          273.9316666666667
        ],
        "spokenCues": [
          [
            274.357,
            "No."
          ],
          [
            274.732,
            "Look at the API branch before the call."
          ],
          [
            276.882,
            "The database is already there."
          ],
          [
            278.557,
            "Now look after the call."
          ],
          [
            280.007,
            "Redis appears alongside the database,"
          ],
          [
            281.957,
            "and web still has its own link to the same cache."
          ],
          [
            284.707,
            "The existing rules picked up a new dependency."
          ],
          [
            287.307,
            "Nobody had to draw that branch or change the rules."
          ]
        ]
      },
      {
        "focus": "Trace the evidence",
        "question": "If I find that branch tomorrow, how can I tell why it’s there?",
        "answer": "Start with the new link in the health model. It points back to the API’s dependency in the structure graph. One record matches the destination address to the private endpoint. Another matches the host name to Redis. Both lead back to the pod’s recorded call. That trail from a result to the records behind it is called provenance.",
        "start": 290.9166666666667,
        "end": 314.2916666666667,
        "answerStarts": [
          294.8066666666667
        ],
        "spokenCues": [
          [
            295.207,
            "Start with the new link in the health model."
          ],
          [
            297.357,
            "It points back to the API’s dependency in the structure graph."
          ],
          [
            300.857,
            "One record matches the destination address to the private endpoint."
          ],
          [
            304.432,
            "Another matches the host name to Redis."
          ],
          [
            306.732,
            "Both lead back to the pod’s recorded call."
          ],
          [
            309.357,
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
        "start": 314.2916666666667,
        "end": 338.0,
        "answerStarts": [
          317.13166666666666
        ],
        "spokenCues": [
          [
            317.557,
            "Yes."
          ],
          [
            317.957,
            "This is a GKQL program from Sattle’s deployment health experiment."
          ],
          [
            322.057,
            "The dots in the master graph are pods."
          ],
          [
            324.282,
            "Web has two, and the database has one."
          ],
          [
            326.632,
            "The query itself is a graph, too."
          ],
          [
            328.732,
            "Its boxes are operations."
          ],
          [
            330.657,
            "A wire tells us which operation uses another’s result."
          ],
          [
            333.782,
            "The target graph shows each workload and the traffic between them."
          ]
        ]
      },
      {
        "focus": "Match a pattern",
        "question": "What does the little arrow inside graph-match look for?",
        "answer": "A ready pod and the deployment that controls it. The letters p and d hold their places in the pattern. Watch the first web pod light up. In this match, p stands for that pod, and d stands for web. That pairing is called a binding. The other web pod fits too. So does the database pod. Three matches.",
        "start": 338.0,
        "end": 359.2916666666667,
        "answerStarts": [
          341.365
        ],
        "spokenCues": [
          [
            341.765,
            "A ready pod and the deployment that controls it."
          ],
          [
            344.265,
            "The letters p and d hold their places in the pattern."
          ],
          [
            347.065,
            "Watch the first web pod light up."
          ],
          [
            349.065,
            "In this match, p stands for that pod, and d stands for web."
          ],
          [
            352.39,
            "That pairing is called a binding."
          ],
          [
            354.24,
            "The other web pod fits too."
          ],
          [
            355.94,
            "So does the database pod."
          ],
          [
            357.34,
            "Three matches."
          ]
        ]
      },
      {
        "focus": "Count ready pods",
        "question": "How do those matches give web a health state?",
        "answer": "Start each deployment at zero. Add one for every ready pod. The summarize step groups those numbers by deployment and adds them up. Web gets two. The database gets one. The threshold here is zero, so any count above zero means Healthy. Web appears once in the target graph, with its deployment ID and a count of two.",
        "start": 359.2916666666667,
        "end": 381.1666666666667,
        "answerStarts": [
          362.20666666666665
        ],
        "spokenCues": [
          [
            362.582,
            "Start each deployment at zero."
          ],
          [
            364.432,
            "Add one for every ready pod."
          ],
          [
            366.207,
            "The summarize step groups those numbers by deployment and adds them up."
          ],
          [
            369.782,
            "Web gets two."
          ],
          [
            370.857,
            "The database gets one."
          ],
          [
            372.357,
            "The threshold here is zero, so any count above zero means Healthy."
          ],
          [
            376.132,
            "Web appears once in the target graph,"
          ],
          [
            377.932,
            "with its deployment ID and a count of two."
          ]
        ]
      },
      {
        "focus": "Two calls, one edge",
        "question": "What happens to the connection if the database pod stops being ready?",
        "answer": "Follow the two pod calls. Both reach the same database pod, so their matches build one Talks edge from web to the database. At ten oh three, the database pod stops being ready. Its count falls to zero. The edge takes the worse state of its two ends, so it turns red with the database. Web still has two ready pods and stays green.",
        "start": 381.1666666666667,
        "end": 405.7916666666667,
        "answerStarts": [
          385.58166666666665
        ],
        "spokenCues": [
          [
            385.982,
            "Follow the two pod calls."
          ],
          [
            387.707,
            "Both reach the same database pod,"
          ],
          [
            389.707,
            "so their matches build one Talks edge from web to the database."
          ],
          [
            393.107,
            "At ten oh three, the database pod stops being ready."
          ],
          [
            395.982,
            "Its count falls to zero."
          ],
          [
            397.682,
            "The edge takes the worse state of its two ends,"
          ],
          [
            399.807,
            "so it turns red with the database."
          ],
          [
            401.757,
            "Web still has two ready pods and stays green."
          ]
        ]
      }
    ]
  }
];
