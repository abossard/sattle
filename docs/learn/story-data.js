const story = [
  {
    "id": "origins",
    "chapter": "The shop on Azure",
    "title": "Where does the health model come from?",
    "beats": [
      {
        "focus": "Dependency",
        "text": "A customer checks out. The shop’s web service calls its API, which reaches a database server called pg-shop through the database service. The arrows show what checkout depends on. It takes several sources to piece together that short path.",
        "start": 0.0,
        "end": 17.083333333333332
      },
      {
        "focus": "Master graph",
        "text": "Now the map pulls back. At the end of this replay, Sattle’s master graph holds seventy-three entities and sixty-nine links. An entity can be a pod, a service or an Azure resource. The links record facts such as which pods called each other and where a pod ran.",
        "start": 17.083333333333332,
        "end": 35.041666666666664
      },
      {
        "focus": "Projection",
        "text": "The checkout path is one view of that larger graph. Sattle calls the query that builds a view a projection. One projection maps the shop’s dependencies. Another combines that map with meter readings and rules to show how an outage can reach checkout. That second view is the health model.",
        "start": 35.041666666666664,
        "end": 55.125
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
        "text": "The box encloses services in the shop namespace, a named group in Kubernetes. Its database runs outside that box on Azure. The scope says which services belong to the shop. Their dependencies can still lead outside it.",
        "start": 55.125,
        "end": 71.33333333333333
      },
      {
        "focus": "Signal rule",
        "text": "For pg-shop, one bad minute opens the alarm. The first good minute keeps it open. The second closes it. That memory has a name, hysteresis. People can choose and refine rules like this to express what counts as failure and recovery for a particular service.",
        "start": 71.33333333333333,
        "end": 89.95833333333333
      },
      {
        "focus": "Required call",
        "text": "The API needs the database to finish checkout. A cache could be optional if the API can serve the request without it. A network record shows that a call happened. The health rules decide whether trouble at the far end should count against the caller.",
        "start": 89.95833333333333,
        "end": 107.29166666666667
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
        "text": "One report lists pg-shop in Azure. Another carries its meter reading. Both contain the same resource ID. That shared identity lets the model attach a new reading to the server it already knows.",
        "start": 107.29166666666667,
        "end": 122.125
      },
      {
        "focus": "Projection",
        "text": "Three web pods call two API pods. A pod is a running copy of a program. At the service level, those calls become one arrow from web to API. This projection groups the callers and their targets by the service they belong to.",
        "start": 122.125,
        "end": 139.20833333333334
      },
      {
        "focus": "Ontology",
        "text": "The database service points to pg-shop. They’re two different things, and the link says the server backs the service. An ontology defines that vocabulary. People can maintain it as the system grows, giving each new kind of thing and relationship a precise meaning.",
        "start": 139.20833333333334,
        "end": 158.04166666666666
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
        "text": "At 10:11, the meter reports that pg-shop stopped answering. The recorder gets that reading a minute and a second later. The chart places the zero at 10:11, the sample time. That keeps the outage in the minute it happened.",
        "start": 158.04166666666666,
        "end": 173.95833333333334
      },
      {
        "focus": "Impact",
        "text": "Down that column, the database service turns red, then the API that needs it. Web and the gateway both need the API. The cache stays green. A failure can reach a caller several steps away. The health model follows the dependencies to find those callers.",
        "start": 173.95833333333334,
        "end": 192.33333333333334
      },
      {
        "focus": "Recovery",
        "text": "At 10:14 the database answers again. Its meter rises while its alarm stays red. At 10:15, the second good minute clears the alarm for the database and its callers. A reading describes one moment. A health state can depend on several readings in a row.",
        "start": 192.33333333333334,
        "end": 211.16666666666666
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
        "text": "Earlier that morning, the cache was under heavy load. Web already depended on it. Both show amber, the warning state called Degraded. The gateway and API were healthy.",
        "start": 211.16666666666666,
        "end": 223.375
      },
      {
        "focus": "Evidence",
        "text": "In a second run, the API’s settings name the existing cache. Sattle matches the address to its Azure resource and adds a dependency. That one setting is evidence for an arrow the original run didn’t have.",
        "start": 223.375,
        "end": 238.25
      },
      {
        "focus": "Controlled comparison",
        "text": "Both runs stop at the same time. The cache meter reads eighty-five percent in each. Only the second gateway turns amber, because its API now depends on the cache. Holding the readings and rules fixed makes the effect of that one new link visible.",
        "start": 238.25,
        "end": 255.95833333333334
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
        "text": "In another replay, the API names a new host, offers-cache. Its address is on the left. The Azure inventory on the right has no match yet. The setting tells us where the API intends to connect. The resource at that address is still an open question.",
        "start": 255.95833333333334,
        "end": 273.875
      },
      {
        "focus": "Inventory match",
        "text": "Now Azure reports a Redis cache whose name and type match that address. It appears in the overlap. A query can join the two records and connect the API to the new resource. The same rule can recognize another cache when it appears.",
        "start": 273.875,
        "end": 290.4583333333333
      },
      {
        "focus": "Unknown",
        "text": "The new cache appears on the map with a question mark. Azure supplied evidence that it exists. To judge its health, Sattle also needs readings that a health rule can use. Until then, the state is Unknown. A different resource type may call for a different rule or meter.",
        "start": 290.4583333333333,
        "end": 309.2083333333333
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
        "text": "The Canvas starts with a saved graph. As pg-shop’s state changes, the engine sends a patch listing the edits. Here seven edits carry the map from revision fifteen to sixteen. A gap in reporting leaves the last known resource in place.",
        "start": 309.2083333333333,
        "end": 326.25
      },
      {
        "focus": "Revision",
        "text": "Revision sixteen shows the outage. By eighteen the database has recovered. A revision counts changes to the graph, so the distance between those numbers says nothing about how many minutes passed. The meter keeps time in minutes.",
        "start": 326.25,
        "end": 342.0833333333333
      },
      {
        "focus": "Replay",
        "text": "One path applies the edits as each record arrives. The other builds the answer again from the same recorded inputs, clock events and rules. Both end at the same graph. That agreement makes a past result reproducible and gives Sattle a way to test its updates.",
        "start": 342.0833333333333,
        "end": 360.6666666666667
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
        "text": "At revision sixteen, web is red. Its dependency path leads through the API and the database service to pg-shop. There, a meter reading tripped the server’s own alarm. The Canvas marks that server as an origin. Web shows the effect.",
        "start": 360.6666666666667,
        "end": 378.6666666666667
      },
      {
        "focus": "Lineage",
        "text": "Why does the database service point to that particular server? The health view takes the link from the structure query. That query matched the service’s host name to the Azure resource. The white paper calls this trail lineage. Each step leads back to the records behind the result.",
        "start": 378.6666666666667,
        "end": 396.7083333333333
      },
      {
        "focus": "Query draft",
        "text": "The Canvas can also preview a different rule. A draft runs against the recorded case and shows which rows it returns before the rule changes. The records still describe the same outage. A different answer shows which conclusion depends on the rule.",
        "start": 396.7083333333333,
        "end": 414.125
      }
    ]
  }
];
