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
    "title": "Where does the Health Tree come from?",
    "beats": [
      {
        "focus": "Health Tree",
        "question": "Web is red, but the cache is green. What happened?",
        "answer": "The database server failed a health check. Follow the branch from web through the API and the database service to pg-shop. Its failure reaches each parent on the way back up. The cache and storage stay green. A Health Tree starts at one component. Its branches show what can affect that component’s health.",
        "start": 134.375,
        "end": 155.5,
        "answerStarts": [
          137.64
        ],
        "spokenCues": [
          [
            138.015,
            "The database server failed a health check."
          ],
          [
            140.29,
            "Follow the branch from web through the API"
          ],
          [
            142.515,
            "and the database service to pg-shop."
          ],
          [
            144.94,
            "Its failure reaches each parent on the way back up."
          ],
          [
            147.74,
            "The cache and storage stay green."
          ],
          [
            149.74,
            "A Health Tree starts at one component."
          ],
          [
            151.915,
            "Its branches show what can affect that component’s health."
          ]
        ]
      },
      {
        "focus": "From the master graph",
        "question": "Where did that branch of the tree come from?",
        "answer": "From two records in the master graph. Kubernetes gives the database service an external name. The structure query builds a host name from the Azure server’s name and type. The names match, so the query records which server backs the service. The health query combines that structure with readings and signal rules. When new evidence changes a match, Sattle updates the model.",
        "start": 155.5,
        "end": 178.83333333333334,
        "answerStarts": [
          157.99
        ],
        "spokenCues": [
          [
            158.365,
            "From two records in the master graph."
          ],
          [
            160.415,
            "Kubernetes gives the database service an external name."
          ],
          [
            163.365,
            "The structure query builds a host name from the Azure server’s name and type."
          ],
          [
            167.24,
            "The names match, so the query records which server backs the service."
          ],
          [
            170.89,
            "The health query combines that structure with readings and signal rules."
          ],
          [
            174.615,
            "When new evidence changes a match, Sattle updates the model."
          ]
        ]
      },
      {
        "focus": "Signal rule",
        "question": "How does one reading turn the database red?",
        "answer": "The database rule says one failed minute opens the alarm. At ten eleven, the reading drops to zero. That gives the server its own severity, Unhealthy. The rule also sets the recovery time. At ten fourteen, the first good minute leaves the alarm open. At ten fifteen, the second good minute clears it. Requiring two good samples keeps one brief recovery from clearing the alarm.",
        "start": 178.83333333333334,
        "end": 203.58333333333334,
        "answerStarts": [
          181.74833333333333
        ],
        "spokenCues": [
          [
            182.123,
            "The database rule says one failed minute opens the alarm."
          ],
          [
            185.273,
            "At ten eleven, the reading drops to zero."
          ],
          [
            187.648,
            "That gives the server its own severity, Unhealthy."
          ],
          [
            190.423,
            "The rule also sets the recovery time."
          ],
          [
            192.723,
            "At ten fourteen, the first good minute leaves the alarm open."
          ],
          [
            196.048,
            "At ten fifteen, the second good minute clears it."
          ],
          [
            198.673,
            "Requiring two good samples keeps one brief recovery from clearing the alarm."
          ]
        ]
      },
      {
        "focus": "Health roll-up",
        "question": "How does Sattle choose the color at the top of the tree?",
        "answer": "It takes the worst severity web can reach. Start at web itself. Zero steps means a component’s own signals count too. Then follow the links called Affects, up to sixteen steps. Three links down, pg-shop has severity three, Unhealthy. That outranks the healthy cache and storage. The failed reading belongs to the server. Web turns red because it depends on that server.",
        "start": 203.58333333333334,
        "end": 228.70833333333334,
        "answerStarts": [
          206.79833333333335
        ],
        "spokenCues": [
          [
            207.198,
            "It takes the worst severity web can reach."
          ],
          [
            209.498,
            "Start at web itself."
          ],
          [
            210.773,
            "Zero steps means a component’s own signals count too."
          ],
          [
            213.873,
            "Then follow the links called Affects, up to sixteen steps."
          ],
          [
            216.873,
            "Three links down, pg-shop has severity three, Unhealthy."
          ],
          [
            220.498,
            "That outranks the healthy cache and storage."
          ],
          [
            222.773,
            "The failed reading belongs to the server."
          ],
          [
            224.873,
            "Web turns red because it depends on that server."
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
        "start": 228.70833333333334,
        "end": 250.0,
        "answerStarts": [
          232.32333333333332
        ],
        "spokenCues": [
          [
            232.748,
            "Yes."
          ],
          [
            233.198,
            "Hubble recorded one flow from the API pod to this address."
          ],
          [
            236.673,
            "The new line means the pod sent traffic there."
          ],
          [
            239.198,
            "When Sattle processes that observation,"
          ],
          [
            241.273,
            "the existing rules connect the pod to the API and the address to Redis."
          ],
          [
            245.548,
            "The health model picks up the new dependency without anyone editing it."
          ]
        ]
      },
      {
        "focus": "Service dependency",
        "question": "How does that address become a Redis dependency in the health model?",
        "answer": "Kubernetes ties the pod to the API service. At the other end, the address matches a private endpoint for Redis. The DNS name points to the same cache. That gives the structure query both ends of a service dependency. It derives the API to Redis link from those records. The health model can now follow it using its existing rule.",
        "start": 250.0,
        "end": 274.4166666666667,
        "answerStarts": [
          254.115
        ],
        "spokenCues": [
          [
            254.49,
            "Kubernetes ties the pod to the API service."
          ],
          [
            257.615,
            "At the other end, the address matches a private endpoint for Redis."
          ],
          [
            261.265,
            "The DNS name points to the same cache."
          ],
          [
            263.59,
            "That gives the structure query both ends of a service dependency."
          ],
          [
            267.14,
            "It derives the API to Redis link from those records."
          ],
          [
            270.29,
            "The health model can now follow it using its existing rule."
          ]
        ]
      },
      {
        "focus": "Revision patch",
        "question": "Redis was already there. Where can I find the new link?",
        "answer": "Look for the plus sign. A patch lists the edits from one graph revision to the next. In the master graph, the added line means the pod sent traffic. In the structure graph, it means the API depends on Redis. In the health graph, revision fourteen adds that dependency. The cache was there all along.",
        "start": 274.4166666666667,
        "end": 295.625,
        "answerStarts": [
          277.9066666666667
        ],
        "spokenCues": [
          [
            278.307,
            "Look for the plus sign."
          ],
          [
            279.657,
            "A patch lists the edits from one graph revision to the next."
          ],
          [
            282.782,
            "In the master graph, the added line means the pod sent traffic."
          ],
          [
            286.132,
            "In the structure graph, it means the API depends on Redis."
          ],
          [
            289.707,
            "In the health graph, revision fourteen adds that dependency."
          ],
          [
            293.007,
            "The cache was there all along."
          ]
        ]
      },
      {
        "focus": "Evidence",
        "question": "How could I check that the API really started using that cache?",
        "answer": "Follow the dependency back through the graphs. The structure query kept two matches. One links the pod’s destination to a private endpoint. The other identifies Redis by its DNS name. That trail from a conclusion back to its inputs is called provenance. Now compare the two runs. Redis has the same load warning in both. The added dependency explains why the API and gateway turn amber.",
        "start": 295.625,
        "end": 322.75,
        "answerStarts": [
          299.765
        ],
        "spokenCues": [
          [
            300.165,
            "Follow the dependency back through the graphs."
          ],
          [
            302.365,
            "The structure query kept two matches."
          ],
          [
            304.49,
            "One links the pod’s destination to a private endpoint."
          ],
          [
            307.415,
            "The other identifies Redis by its DNS name."
          ],
          [
            310.09,
            "That trail from a conclusion back to its inputs is called provenance."
          ],
          [
            313.69,
            "Now compare the two runs."
          ],
          [
            315.24,
            "Redis has the same load warning in both."
          ],
          [
            317.44,
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
        "start": 322.75,
        "end": 346.4583333333333,
        "answerStarts": [
          325.59
        ],
        "spokenCues": [
          [
            326.015,
            "Yes."
          ],
          [
            326.415,
            "This is a GKQL program from Sattle’s deployment health experiment."
          ],
          [
            330.515,
            "The dots in the master graph are pods."
          ],
          [
            332.74,
            "Web has two, and the database has one."
          ],
          [
            335.09,
            "The query itself is a graph, too."
          ],
          [
            337.19,
            "Its boxes are operations."
          ],
          [
            339.115,
            "A wire tells us which operation uses another’s result."
          ],
          [
            342.24,
            "The target graph shows each workload and the traffic between them."
          ]
        ]
      },
      {
        "focus": "Match a pattern",
        "question": "What does the little arrow inside graph-match look for?",
        "answer": "A ready pod and the deployment that controls it. The letters p and d hold their places in the pattern. Watch the first web pod light up. In this match, p stands for that pod, and d stands for web. That pairing is called a binding. The other web pod fits too. So does the database pod. Three matches.",
        "start": 346.4583333333333,
        "end": 367.75,
        "answerStarts": [
          349.8233333333333
        ],
        "spokenCues": [
          [
            350.223,
            "A ready pod and the deployment that controls it."
          ],
          [
            352.723,
            "The letters p and d hold their places in the pattern."
          ],
          [
            355.523,
            "Watch the first web pod light up."
          ],
          [
            357.523,
            "In this match, p stands for that pod, and d stands for web."
          ],
          [
            360.848,
            "That pairing is called a binding."
          ],
          [
            362.698,
            "The other web pod fits too."
          ],
          [
            364.398,
            "So does the database pod."
          ],
          [
            365.798,
            "Three matches."
          ]
        ]
      },
      {
        "focus": "Count ready pods",
        "question": "How do those matches give web a health state?",
        "answer": "Start each deployment at zero. Add one for every ready pod. The summarize step groups those numbers by deployment and adds them up. Web gets two. The database gets one. The threshold here is zero, so any count above zero means Healthy. Web appears once in the target graph, with its deployment ID and a count of two.",
        "start": 367.75,
        "end": 389.625,
        "answerStarts": [
          370.665
        ],
        "spokenCues": [
          [
            371.04,
            "Start each deployment at zero."
          ],
          [
            372.89,
            "Add one for every ready pod."
          ],
          [
            374.665,
            "The summarize step groups those numbers by deployment and adds them up."
          ],
          [
            378.24,
            "Web gets two."
          ],
          [
            379.315,
            "The database gets one."
          ],
          [
            380.815,
            "The threshold here is zero, so any count above zero means Healthy."
          ],
          [
            384.59,
            "Web appears once in the target graph,"
          ],
          [
            386.39,
            "with its deployment ID and a count of two."
          ]
        ]
      },
      {
        "focus": "Two calls, one edge",
        "question": "What happens to the connection if the database pod stops being ready?",
        "answer": "Follow the two pod calls. Both reach the same database pod, so their matches build one Talks edge from web to the database. At ten oh three, the database pod stops being ready. Its count falls to zero. The edge takes the worse state of its two ends, so it turns red with the database. Web still has two ready pods and stays green.",
        "start": 389.625,
        "end": 414.25,
        "answerStarts": [
          394.04
        ],
        "spokenCues": [
          [
            394.44,
            "Follow the two pod calls."
          ],
          [
            396.165,
            "Both reach the same database pod,"
          ],
          [
            398.165,
            "so their matches build one Talks edge from web to the database."
          ],
          [
            401.565,
            "At ten oh three, the database pod stops being ready."
          ],
          [
            404.44,
            "Its count falls to zero."
          ],
          [
            406.14,
            "The edge takes the worse state of its two ends,"
          ],
          [
            408.265,
            "so it turns red with the database."
          ],
          [
            410.215,
            "Web still has two ready pods and stays green."
          ]
        ]
      }
    ]
  }
];
