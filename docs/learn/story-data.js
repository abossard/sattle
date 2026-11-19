const story = [
  {
    "id": "origins",
    "chapter": "The shop on Azure",
    "title": "Where does the health model come from?",
    "beats": [
      {
        "focus": "Dependency",
        "question": "What does checkout need from the rest of the shop?",
        "answer": "The web service calls the API. The API calls the database service, which uses a PostgreSQL server named pg-shop. Follow those arrows from checkout and you can see which calls have to succeed.",
        "start": 0.0,
        "end": 19.0,
        "answerStarts": [
          4.225
        ]
      },
      {
        "focus": "Master graph",
        "question": "Where did the rest of those dots come from?",
        "answer": "Kubernetes records say where the shop’s pods ran. Network records say which pods called each other. By the end of the replay, Sattle’s master graph holds seventy-three entities and sixty-nine links. Each dot stands for something a source reported.",
        "start": 19.0,
        "end": 39.166666666666664,
        "answerStarts": [
          22.75
        ]
      },
      {
        "focus": "Projection",
        "question": "How does that map become a health model?",
        "answer": "Start with the services checkout depends on. A query groups the underlying records into those services and keeps the calls between them. That transformation is a projection. A health projection also uses readings and rules to work out which services a failure affects.",
        "start": 39.166666666666664,
        "end": 61.75,
        "answerStarts": [
          43.016666666666666
        ]
      }
    ]
  },
  {
    "id": "rules",
    "chapter": "The shop’s health rules",
    "title": "What makes a reading count as an outage?",
    "beats": [
      {
        "focus": "Scope",
        "question": "Why is the database server outside the box?",
        "answer": "The box marks the shop’s Kubernetes namespace. The database service belongs to that namespace. Its PostgreSQL server runs on Azure. The boundary tells Sattle which services to start with. Following a dependency can take it outside.",
        "start": 61.75,
        "end": 83.125,
        "answerStarts": [
          66.2
        ]
      },
      {
        "focus": "Signal rule",
        "question": "How long does the database have to go quiet before an alarm opens?",
        "answer": "One bad minute opens the pg-shop alarm. It takes two good minutes in a row to close it. The rule remembers the previous minute. That’s called hysteresis. A team can choose these thresholds for its service.",
        "start": 83.125,
        "end": 103.33333333333333,
        "answerStarts": [
          88.925
        ]
      },
      {
        "focus": "Required call",
        "question": "Could a failed call leave checkout working?",
        "answer": "Checkout needs the database, so the database alarm counts against the API. For a service that can carry on without its cache, a rule could treat the cache call as optional. Network records establish that a call happened. A rule says what its failure means.",
        "start": 103.33333333333333,
        "end": 125.08333333333333,
        "answerStarts": [
          107.20833333333333
        ]
      }
    ]
  },
  {
    "id": "layers",
    "chapter": "From records to a map",
    "title": "How do many records become one map?",
    "beats": [
      {
        "focus": "Identity",
        "question": "How does Sattle know which server the meter belongs to?",
        "answer": "Azure Resource Graph lists pg-shop. Azure Monitor puts the very same resource ID on the reading. Sattle can use that ID to attach the reading to the right server, even when different sources describe it.",
        "start": 125.08333333333333,
        "end": 144.33333333333334,
        "answerStarts": [
          129.60833333333332
        ]
      },
      {
        "focus": "Projection",
        "question": "Why is there just one arrow between web and API?",
        "answer": "Three web pods call two API pods. Pods are running copies of an application. Group the pods by service and their calls become one link from web to API. The records underneath still show which copies made the calls.",
        "start": 144.33333333333334,
        "end": 165.58333333333334,
        "answerStarts": [
          149.33333333333334
        ]
      },
      {
        "focus": "Ontology",
        "question": "What does the arrow from the database service to pg-shop mean?",
        "answer": "Pg-shop backs the database service. That’s a different relationship from one pod calling another. Sattle’s ontology names the kinds of things and links the model understands. A team can maintain that vocabulary as new kinds of software appear.",
        "start": 165.58333333333334,
        "end": 188.58333333333334,
        "answerStarts": [
          171.25833333333333
        ]
      }
    ]
  },
  {
    "id": "incident",
    "chapter": "The pg-shop outage",
    "title": "How can one bad reading affect the whole shop?",
    "beats": [
      {
        "focus": "Sample time",
        "question": "The reading arrived after ten twelve. Why does the chart put it at ten eleven?",
        "answer": "Azure Monitor measured a zero at ten eleven. The recorder received it a minute and a second later. The chart uses the sample time, so the failure lands in the minute the meter observed it.",
        "start": 188.58333333333334,
        "end": 207.70833333333334,
        "answerStarts": [
          195.05833333333334
        ]
      },
      {
        "focus": "Impact",
        "question": "Why have web and the gateway turned red too?",
        "answer": "Both call the API. The API needs the database service, which depends on pg-shop. The model carries the database alarm back along those links. The cache stays green because its own work doesn’t depend on that server.",
        "start": 207.70833333333334,
        "end": 227.79166666666666,
        "answerStarts": [
          211.83333333333334
        ]
      },
      {
        "focus": "Recovery",
        "question": "At ten fourteen the database answers again. Why is it still red?",
        "answer": "That’s the first good minute after the outage. At ten fifteen the second good reading clears the alarm. Its callers recover too. The meter shows the latest sample. The alarm also remembers what happened a minute ago.",
        "start": 227.79166666666666,
        "end": 248.875,
        "answerStarts": [
          233.66666666666666
        ]
      }
    ]
  },
  {
    "id": "dependency",
    "chapter": "A new dependency",
    "title": "What changes when the API gains a dependency?",
    "beats": [
      {
        "focus": "Degraded",
        "question": "What was that amber patch earlier in the morning?",
        "answer": "The cache was under load, and web already relied on it. Both show Degraded, the warning state. In that same snapshot the API and gateway are healthy. The cache warning hasn’t reached them.",
        "start": 248.875,
        "end": 266.125,
        "answerStarts": [
          252.875
        ]
      },
      {
        "focus": "Evidence",
        "question": "Where did the new arrow from the API to the cache come from?",
        "answer": "In the second run, the API’s deployment has a setting that names the cache host. Sattle matches that address to the existing Azure cache. The deployment setting is the evidence for the new dependency.",
        "start": 266.125,
        "end": 285.1666666666667,
        "answerStarts": [
          271.425
        ]
      },
      {
        "focus": "Controlled comparison",
        "question": "Why does the gateway change color in only one run?",
        "answer": "Both runs stop at the same moment with cache load at eighty-five percent. In the second run the API depends on the cache, and the gateway depends on the API. The new link carries the warning to the gateway.",
        "start": 285.1666666666667,
        "end": 304.5,
        "answerStarts": [
          289.81666666666666
        ]
      }
    ]
  },
  {
    "id": "azure",
    "chapter": "A change on Azure",
    "title": "What can Sattle infer when Azure changes?",
    "beats": [
      {
        "focus": "Unmatched address",
        "question": "The API now points at offers-cache. Is that enough to put the cache on the map?",
        "answer": "The deployment gives a host name. At this point in the replay, Azure Resource Graph has no matching resource. The setting tells us where the API expects to connect. The resource behind that address is still unknown.",
        "start": 304.5,
        "end": 325.875,
        "answerStarts": [
          311.2
        ]
      },
      {
        "focus": "Resource match",
        "question": "Which record settles the question?",
        "answer": "Azure Resource Graph returns a Redis resource named offers-cache. Its name matches the first part of the address. Its resource type matches the Redis host suffix. The query can now link the API to that cache.",
        "start": 325.875,
        "end": 345.0416666666667,
        "answerStarts": [
          329.375
        ]
      },
      {
        "focus": "Unknown",
        "question": "Why is there a question mark on the new cache?",
        "answer": "The resource record identifies the cache, but the replay has no reading that tells the health rule how it’s doing. Sattle marks its state Unknown. To judge a different kind of resource, the model may need a different meter or rule.",
        "start": 345.0416666666667,
        "end": 364.75,
        "answerStarts": [
          349.19166666666666
        ]
      }
    ]
  },
  {
    "id": "replay",
    "chapter": "A map through time",
    "title": "Can Sattle rebuild the shop as it looked during the outage?",
    "beats": [
      {
        "focus": "Patch",
        "question": "Does the Canvas download the whole map when the database fails?",
        "answer": "It already has revision fifteen. Seven edits take it to revision sixteen. The engine sends those edits together as a patch. The Canvas can apply the patch to the map it has.",
        "start": 364.75,
        "end": 382.5,
        "answerStarts": [
          370.15
        ]
      },
      {
        "focus": "Revision",
        "question": "How far apart are revisions sixteen and eighteen?",
        "answer": "In sixteen, pg-shop is unhealthy. By eighteen it has recovered. Those numbers count changes to the graph. To find the elapsed time, look at the timestamps on the database readings.",
        "start": 382.5,
        "end": 400.5833333333333,
        "answerStarts": [
          387.075
        ]
      },
      {
        "focus": "Replay",
        "question": "How can we check what the map showed during the outage?",
        "answer": "Replay the shop records and clock events with the same rules. Applying each update as it arrives gives the same graph as computing the answer again from the recorded inputs. That gives us a way to check the result.",
        "start": 400.5833333333333,
        "end": 419.9166666666667,
        "answerStarts": [
          405.03333333333336
        ]
      }
    ]
  },
  {
    "id": "inspect",
    "chapter": "The evidence for an answer",
    "title": "What explains a red web service?",
    "beats": [
      {
        "focus": "Origin",
        "question": "Web is red. Where did the failure start?",
        "answer": "In revision sixteen, follow web through the API and database service to pg-shop. The server’s own meter tripped its alarm. The Canvas marks pg-shop as the origin, so you can tell where the failure began and which services felt it.",
        "start": 419.9166666666667,
        "end": 440.875,
        "answerStarts": [
          423.94166666666666
        ]
      },
      {
        "focus": "Lineage",
        "question": "Can we check the link between the database service and pg-shop?",
        "answer": "The structure query matched the service’s configured host to the Azure resource. The health view uses the link that query produced. Following a result back through its queries to the source records is called lineage.",
        "start": 440.875,
        "end": 460.5416666666667,
        "answerStarts": [
          446.375
        ]
      },
      {
        "focus": "Query draft",
        "question": "Could we try a different rule on this same outage?",
        "answer": "The Canvas can run a draft query against the recorded shop and show the rows it returns. That gives a team a way to try a rule on a known failure and inspect the result before adopting it.",
        "start": 460.5416666666667,
        "end": 478.125,
        "answerStarts": [
          464.76666666666665
        ]
      }
    ]
  }
];
