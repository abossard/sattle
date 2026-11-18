const story = [
  {
    "id": "origins",
    "chapter": "The whole shop",
    "title": "Why would a database outage turn the gateway red?",
    "beats": [
      {
        "focus": "Follow a request",
        "text": "This shop runs on Azure. Start at the gateway. It sends requests to the API, and so does the web service below it. Follow either path to the right and you’ll reach pg-shop, the shop’s database server.",
        "start": 0.0,
        "end": 13.708333333333334
      },
      {
        "focus": "Read the records",
        "text": "How did Sattle draw those arrows? Kubernetes lists the services. Network records show who called whom. Azure lists the database server, and Azure Monitor tells us whether it answered.",
        "start": 13.708333333333334,
        "end": 25.791666666666668
      },
      {
        "focus": "Build the health model",
        "text": "Sattle puts those records together in a map called the master graph. One query finds the parts of the shop and the links between them. A second query uses your rules to work out which parts are healthy.",
        "start": 25.791666666666668,
        "end": 38.375
      }
    ]
  },
  {
    "id": "rules",
    "chapter": "What you contribute",
    "title": "Which decisions belong to your team?",
    "beats": [
      {
        "focus": "Choose the scope",
        "text": "The replay comes with a working model. For your own shop, give Sattle read access to your sources and tell it where to look. Here, that’s one Kubernetes cluster and the services in its shop namespace.",
        "start": 38.375,
        "end": 51.125
      },
      {
        "focus": "Set the alarm",
        "text": "You decide when to raise an alarm and when to clear it. For pg-shop, one bad minute trips the database alarm. It takes two good minutes in a row to clear it. The same rule covers other servers of this type.",
        "start": 51.125,
        "end": 64.875
      },
      {
        "focus": "Choose which calls matter",
        "text": "You also know which calls your service can do without. If the API needs the database to answer a request, its health should reflect a database outage. If it can work without a cache, you can leave that call out of the health calculation.",
        "start": 64.875,
        "end": 79.79166666666667
      }
    ]
  },
  {
    "id": "layers",
    "chapter": "The theory",
    "title": "How does Sattle know it’s the same server?",
    "beats": [
      {
        "focus": "Match the server ID",
        "text": "Azure inventory names pg-shop. The meter reading carries the same resource ID, so Sattle can attach both records to one server. The Kubernetes service called database has its own ID. An arrow connects the service to the server it uses.",
        "start": 79.79166666666667,
        "end": 96.29166666666667
      },
      {
        "focus": "Group the calls",
        "text": "The white paper calls the names for things and their links an ontology. A projection gives you a simpler map. Several copies of the web service call the API. On the shop map those calls become one arrow, and you can trace it back to the records.",
        "start": 96.29166666666667,
        "end": 112.125
      },
      {
        "focus": "Keep the readings",
        "text": "Each minute adds a reading to the database chart. Most minutes leave the map just as it was. When a reading trips or clears the alarm, Sattle records the change in a new version of the health map.",
        "start": 112.125,
        "end": 124.54166666666667
      }
    ]
  },
  {
    "id": "incident",
    "chapter": "The pg-shop outage",
    "title": "What happened at 10:11?",
    "beats": [
      {
        "focus": "Read the first zero",
        "text": "At 10:11 the database meter drops to zero. The recorder gets that sample a minute and a second later. The alarm belongs to 10:11, the minute on the chart.",
        "start": 124.54166666666667,
        "end": 135.54166666666666
      },
      {
        "focus": "Trace the outage",
        "text": "Start at pg-shop and follow the dots back to the left. The database service needs that server. The API needs the service, and both the gateway and the web service use the API. The cache and storage stay healthy.",
        "start": 135.54166666666666,
        "end": 150.375
      },
      {
        "focus": "Wait for two good minutes",
        "text": "At 10:14 the database answers again. The alarm stays on. At 10:15 we have two good minutes in a row. The alarm clears, and the services that depend on the database turn green again.",
        "start": 150.375,
        "end": 163.70833333333334
      }
    ]
  },
  {
    "id": "dependency",
    "chapter": "A new dependency",
    "title": "What changes if the API starts using the cache?",
    "beats": [
      {
        "focus": "Rewind to the cache incident",
        "text": "Let’s go back a few minutes, to when the cache was under heavy load. In the original replay the web service uses the cache. The shop and its web service show Degraded. The API and gateway stay healthy.",
        "start": 163.70833333333334,
        "end": 176.95833333333334
      },
      {
        "focus": "Give the API a cache address",
        "text": "Now replay the same minutes with one change. We put the cache address in the API’s settings. Sattle finds that address in Azure inventory and draws a new arrow from the API to the cache. Sattle keeps the setting as evidence for that arrow.",
        "start": 176.95833333333334,
        "end": 192.70833333333334
      },
      {
        "focus": "Compare the gateways",
        "text": "Pause both runs at 10:06:30. In the second run the API and its gateway both show Degraded. The cache readings and alarm rules are the same. The gateway now depends on the cache through the API.",
        "start": 192.70833333333334,
        "end": 208.29166666666666
      }
    ]
  },
  {
    "id": "azure",
    "chapter": "A change on Azure",
    "title": "When does a new cache appear on the map?",
    "beats": [
      {
        "focus": "Name the new cache",
        "text": "This time, point the API at a new cache called offers-cache. So far, all Sattle has is an address in a setting. Until Azure reports a matching resource, the map has no cache at that address.",
        "start": 208.29166666666666,
        "end": 221.5
      },
      {
        "focus": "Add the inventory record",
        "text": "Now Azure Resource Graph lists offers-cache. Its address matches the one in the API settings. Both caches are the same type, so the rule that found the first one can draw this new link too.",
        "start": 221.5,
        "end": 234.29166666666666
      },
      {
        "focus": "Check its health",
        "text": "The new cache shows up as Unknown. Azure told us it exists. To tell whether it’s healthy, Sattle needs readings for that cache. For a different kind of resource, you may need a new rule to match its address, or a way to collect and interpret its readings.",
        "start": 234.29166666666666,
        "end": 249.875
      }
    ]
  },
  {
    "id": "replay",
    "chapter": "Change and replay",
    "title": "How does the shop map stay up to date?",
    "beats": [
      {
        "focus": "Send the changes",
        "text": "When a source reports a change, the engine updates the parts of the map that depend on it. It sends those edits to the Canvas, Sattle’s viewer. If a source stops reporting, Sattle keeps the last evidence. A quiet source can’t tell us the database is gone.",
        "start": 249.875,
        "end": 266.2083333333333
      },
      {
        "focus": "Look up a past version",
        "text": "The Canvas opens a saved map and applies the edits in order. In this recording, version 16 shows the database outage. Skip to version 18 to see it recover. The version numbers count changes to the map. The times on the chart belong to the readings.",
        "start": 266.2083333333333,
        "end": 283.5416666666667
      },
      {
        "focus": "Check the replay",
        "text": "The white paper explains when updating the map one change at a time gives the same answer as rebuilding it from the beginning. Sattle tests this with a second program. Both programs get the same records and rules, and the test compares their maps.",
        "start": 283.5416666666667,
        "end": 299.5416666666667
      }
    ]
  },
  {
    "id": "inspect",
    "chapter": "Check the evidence",
    "title": "How can you tell where the outage started?",
    "beats": [
      {
        "focus": "Find the first alarm",
        "text": "Open the workload tree in the Canvas at version 16 and select pg-shop. The origin marker tells you the server’s own meter tripped the alarm. Select the web service and you can see why it turned red even though the alarm came from the database.",
        "start": 299.5416666666667,
        "end": 315.6666666666667
      },
      {
        "focus": "Trace a result",
        "text": "In the Inspector, follow the health result back to the rule, then follow the database link back to its source records. On a running engine, you can edit a copy of the query and preview the rows it returns.",
        "start": 315.6666666666667,
        "end": 328.1666666666667
      },
      {
        "focus": "Try it in the guide",
        "text": "Try the two cache changes in the walkthrough. First give the API the existing cache address and watch the gateway. Then give it the new address and add the Azure record. This time, look at the new cache’s health.",
        "start": 328.1666666666667,
        "end": 341.75
      }
    ]
  }
];
