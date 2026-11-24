// Six cuts of the deploy-health fixture, each replayed with the X12 program.
// Time is the last included observation (UTC). Workload and Talks states are engine outputs.
const ShopGkqlExample = {
  deployments: ["web", "db"],
  pods: [
    {"id": "web-7d4-aaaaa", "deployment": "web"},
    {"id": "web-7d4-bbbbb", "deployment": "web"},
    {"id": "db-5c8-ccccc", "deployment": "db"}
  ],
  calls: [
    {"from": "web-7d4-aaaaa", "to": "db-5c8-ccccc", "at": "10:00:10"},
    {"from": "web-7d4-bbbbb", "to": "db-5c8-ccccc", "at": "10:00:11"}
  ],
  minReady: 0,
  identities: {"workloads": {"web": "e:56tkbmrbuy4uehkcbelomwuduy", "db": "e:cjudsjuk5jbebmhz2q3uc72t46"}, "talks": "r:gnc2kjt3w53om2asbqftm5g4rg"},
  frames: [
    {
      time: "10:00:11",
      ready: {"web-7d4-aaaaa": true, "web-7d4-bbbbb": true, "db-5c8-ccccc": true},
      counts: {"web": 2, "db": 1},
      states: {"web": "Healthy", "db": "Healthy"},
      talks: "Healthy"
    },
    {
      time: "10:03:00",
      ready: {"web-7d4-aaaaa": true, "web-7d4-bbbbb": true, "db-5c8-ccccc": false},
      counts: {"web": 2, "db": 0},
      states: {"web": "Healthy", "db": "Unhealthy"},
      talks: "Unhealthy"
    },
    {
      time: "10:04:00",
      ready: {"web-7d4-aaaaa": true, "web-7d4-bbbbb": false, "db-5c8-ccccc": false},
      counts: {"web": 1, "db": 0},
      states: {"web": "Healthy", "db": "Unhealthy"},
      talks: "Unhealthy"
    },
    {
      time: "10:05:00",
      ready: {"web-7d4-aaaaa": false, "web-7d4-bbbbb": false, "db-5c8-ccccc": false},
      counts: {"web": 0, "db": 0},
      states: {"web": "Unhealthy", "db": "Unhealthy"},
      talks: "Unhealthy"
    },
    {
      time: "10:06:00",
      ready: {"web-7d4-aaaaa": false, "web-7d4-bbbbb": false, "db-5c8-ccccc": true},
      counts: {"web": 0, "db": 1},
      states: {"web": "Unhealthy", "db": "Healthy"},
      talks: "Unhealthy"
    },
    {
      time: "10:07:00",
      ready: {"web-7d4-aaaaa": true, "web-7d4-bbbbb": false, "db-5c8-ccccc": true},
      counts: {"web": 1, "db": 1},
      states: {"web": "Healthy", "db": "Healthy"},
      talks: "Healthy"
    }
  ],
  verbs: [
    {"id": "deployments", "verb": "graph-match"},
    {"id": "readyPods", "verb": "graph-match"},
    {"id": "zero", "verb": "project"},
    {"id": "one", "verb": "project"},
    {"id": "rows", "verb": "union"},
    {"id": "count", "verb": "summarize"},
    {"id": "status", "verb": "extend"},
    {"id": "workloads", "verb": "nodes"},
    {"id": "workloadState", "verb": "nodes"},
    {"id": "calls", "verb": "graph-match"},
    {"id": "apart", "verb": "where"},
    {"id": "srcStatus", "verb": "project"},
    {"id": "dstStatus", "verb": "project"},
    {"id": "withSrc", "verb": "join"},
    {"id": "withDst", "verb": "join"},
    {"id": "talks", "verb": "edges"}
  ],
  wires: [
    {"from": "deployments", "to": "zero"},
    {"from": "readyPods", "to": "one"},
    {"from": "zero", "to": "rows"},
    {"from": "one", "to": "rows"},
    {"from": "rows", "to": "count"},
    {"from": "count", "to": "status"},
    {"from": "deployments", "to": "workloads"},
    {"from": "status", "to": "workloadState"},
    {"from": "calls", "to": "apart"},
    {"from": "status", "to": "srcStatus"},
    {"from": "status", "to": "dstStatus"},
    {"from": "apart", "to": "withSrc", "port": "left"},
    {"from": "srcStatus", "to": "withSrc", "port": "right"},
    {"from": "withSrc", "to": "withDst", "port": "left"},
    {"from": "dstStatus", "to": "withDst", "port": "right"},
    {"from": "withDst", "to": "talks"}
  ],
  sources: {
    "program": "https://github.com/abossard/sattle/blob/fcf7725/engine/experiments/X12-picture-c/acme.deployhealth.deploymentHealth.gkql.json",
    "kubernetes": "https://github.com/abossard/sattle/blob/fcf7725/engine/tests/Sattle.Tests/fixtures/deploy-health/kubernetes/watch.ndjson",
    "hubble": "https://github.com/abossard/sattle/blob/fcf7725/engine/tests/Sattle.Tests/fixtures/deploy-health/hubble/flows.ndjson",
    "tests": "https://github.com/abossard/sattle/blob/fcf7725/engine/experiments/X12-picture-c/PictureCTests.cs",
    "patchTests": "https://github.com/abossard/sattle/blob/fcf7725/engine/tests/Sattle.Tests/DeployHealthTests.cs"
  }
};
