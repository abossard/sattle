const story = [
  {
    "id": "origins",
    "chapter": "The whole shop",
    "title": "Where does the health model come from?",
    "beats": [
      {
        "focus": "Start with the shop",
        "text": "This is a small online shop running on Azure. A gateway uses the API. The web service also calls the API. The API reaches PostgreSQL through a service called database. Web also uses a cache and storage.",
        "start": 0.0,
        "end": 14.375
      },
      {
        "focus": "Assemble the evidence",
        "text": "No single source sees that whole picture. Kubernetes describes the services. Network flows show calls. Azure inventory names the resources, and Azure Monitor supplies readings. Adapters translate each report into rows with named fields.",
        "start": 14.375,
        "end": 29.458333333333332
      },
      {
        "focus": "Compute a health model",
        "text": "Sattle builds a master graph from those observations. Two queries turn it into a health model. The first finds the workload and its dependencies. The second applies signal rules and works out who is affected.",
        "start": 29.458333333333332,
        "end": 41.75
      }
    ]
  },
  {
    "id": "rules",
    "chapter": "What you contribute",
    "title": "What does the team have to decide?",
    "beats": [
      {
        "focus": "Choose the boundary",
        "text": "The example already ships with working definitions. For your own workload, connect the sources you have permission to read. Choose the namespace and cluster. Here the namespace is shop, and the cluster is aks-synth.",
        "start": 41.75,
        "end": 54.791666666666664
      },
      {
        "focus": "Define a bad reading",
        "text": "Your team decides what unhealthy means. For pg-shop, the supplied database rule trips after one low minute and clears after two good minutes. It applies to servers of this type, so each server can use the same rule.",
        "start": 54.791666666666664,
        "end": 68.08333333333333
      },
      {
        "focus": "Decide what carries impact",
        "text": "The workload query also encodes a judgment. Here a caller depends on the service it calls. If a call is optional in your system, change that rule to represent your design. Sattle can then find matching instances as new evidence arrives.",
        "start": 68.08333333333333,
        "end": 82.33333333333333
      }
    ]
  },
  {
    "id": "layers",
    "chapter": "The theory",
    "title": "How do observations become a model?",
    "beats": [
      {
        "focus": "Resolve identity",
        "text": "Start with identity. The inventory record and the meter target share the same Azure resource ID. Their evidence meets at one server. The Kubernetes service has a different key. A link says the server backs it.",
        "start": 82.33333333333333,
        "end": 94.83333333333333
      },
      {
        "focus": "Lift the view",
        "text": "The paper calls the vocabulary of types and links an ontology. A projection is a query that builds a new view. Watch several pod calls group into one link between web and API. You can follow that link back to its evidence.",
        "start": 94.83333333333333,
        "end": 108.625
      },
      {
        "focus": "Keep the clocks separate",
        "text": "The structure has versions. The readings form a time series. New readings can leave the structure unchanged. When a declared rule changes state, its result can change the health graph. That separation keeps a busy meter from redrawing the shop at every sample.",
        "start": 108.625,
        "end": 124.33333333333333
      }
    ]
  },
  {
    "id": "incident",
    "chapter": "The pg-shop replay",
    "title": "How does one failure reach the shop?",
    "beats": [
      {
        "focus": "The meter falls",
        "text": "At ten eleven, the database meter falls from one to zero. The sample reaches the capture at ten twelve and one second. The first low minute trips the authored rule.",
        "start": 124.33333333333333,
        "end": 134.20833333333334
      },
      {
        "focus": "Follow impact back to callers",
        "text": "pg-shop now has an unhealthy signal of its own. The database service relies on it. API relies on database, and both the gateway and web rely on API. Health changes travel back toward those callers. The cache and storage stay healthy in this incident.",
        "start": 134.20833333333334,
        "end": 150.41666666666666
      },
      {
        "focus": "Wait for recovery",
        "text": "The meter rises at ten fourteen. That is one good minute. At ten fifteen the second good minute clears the signal. The same dependency paths now carry recovery.",
        "start": 150.41666666666666,
        "end": 160.45833333333334
      }
    ]
  },
  {
    "id": "dependency",
    "chapter": "A new dependency",
    "title": "What if the API starts using the cache?",
    "beats": [
      {
        "focus": "Replay the original shop",
        "text": "Rewind to the earlier cache incident. In the original run, web uses the overloaded cache. Web and the shop are degraded. The API and gateway stay healthy.",
        "start": 160.45833333333334,
        "end": 170.79166666666666
      },
      {
        "focus": "Change one recorded fact",
        "text": "For a second run, we added one deployment update. The API now has a cache host setting naming the existing Azure cache. The shipped structure query matches that host to inventory and adds an API to cache dependency with configuration evidence.",
        "start": 170.79166666666666,
        "end": 185.875
      },
      {
        "focus": "Compare the two runs",
        "text": "Compare both runs at ten oh six and thirty seconds. With that extra link, the API and its gateway also become degraded. The meter and health rules are identical in both runs. The different result comes from the new path.",
        "start": 185.875,
        "end": 199.0
      }
    ]
  },
  {
    "id": "azure",
    "chapter": "A change on Azure",
    "title": "When does a new Azure resource appear?",
    "beats": [
      {
        "focus": "A name is waiting",
        "text": "Here is a separate replay experiment. The API configuration names a new cache called offers-cache. At first Azure inventory has no matching resource. The structure query cannot complete that link yet.",
        "start": 199.0,
        "end": 211.58333333333334
      },
      {
        "focus": "Inventory supplies the match",
        "text": "Now add an Azure Resource Graph record for offers-cache. Its type and name produce the host that the API configuration already mentions. The same query adds the resource and the dependency. No new query was needed for this supported Redis type.",
        "start": 211.58333333333334,
        "end": 226.20833333333334
      },
      {
        "focus": "Know what is still missing",
        "text": "The new cache shows up with unknown health. We supplied inventory, but no meter readings for it. A new resource type or a different naming convention may need a new matching rule. A new signal needs an adapter or meter mapping and a health rule when the existing ones do not cover it.",
        "start": 226.20833333333334,
        "end": 242.04166666666666
      }
    ]
  },
  {
    "id": "replay",
    "chapter": "Change and replay",
    "title": "How does the Canvas stay in step?",
    "beats": [
      {
        "focus": "Keep a revision",
        "text": "In a live deployment the engine learns about changes when its sources report them. It updates the affected results and sends a patch, a list of graph edits. A collector falling silent leaves a gap. Silence alone never proves a resource was deleted.",
        "start": 242.04166666666666,
        "end": 256.9166666666667
      },
      {
        "focus": "Reconstruct the answer",
        "text": "The Canvas starts from a snapshot and applies patches in order. In the bundled static workload export, revision sixteen shows the database outage. Revision eighteen shows recovery. Those are graph versions. The meter uses minutes.",
        "start": 256.9166666666667,
        "end": 271.5833333333333
      },
      {
        "focus": "Check the calculation",
        "text": "The white paper explains why updating just the changed parts should agree with rebuilding from scratch under its assumptions. Tests run an independent evaluator and compare results. The same recorded inputs and definitions let you repeat the investigation.",
        "start": 271.5833333333333,
        "end": 286.25
      }
    ]
  },
  {
    "id": "inspect",
    "chapter": "Check the evidence",
    "title": "Can you explain this answer to a teammate?",
    "beats": [
      {
        "focus": "Find the origin",
        "text": "In the Canvas, open the workload tree and select pg-shop. The origin marker identifies a component with its own failing signal. The Inspector shows its earlier state, its new state, and the graphs that support the result.",
        "start": 286.25,
        "end": 300.1666666666667
      },
      {
        "focus": "Follow a rule home",
        "text": "Follow the answer back through the workload health query, the structure query, and the source records. To test a different interpretation, edit a draft in the projection editor and preview its rows on a running engine.",
        "start": 300.1666666666667,
        "end": 312.375
      },
      {
        "focus": "Try the changes yourself",
        "text": "The lab collects observations. The engine joins them with your definitions. The Canvas explains the result. In the walkthrough, switch on the new dependency, then add the missing Azure inventory. Watch which parts of the same shop change.",
        "start": 312.375,
        "end": 326.875
      }
    ]
  }
];
