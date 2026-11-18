// Term, definition, and the shot where the viewer can see it happen.
const ShopTerms = {
  origins: [
    ["Dependency", "An arrow points from a service to something it needs.", "Follow a request from web to pg-shop"],
    ["Observation", "One source reports what it saw and when it saw it.", "Where did those arrows come from?"],
    ["Health model", "A map that combines readings with your rules to show where trouble starts and which services depend on that part.", "Build the shop map, then calculate its health"]
  ],
  rules: [
    ["Scope", "The cluster and namespace tell Sattle which services belong to this shop.", "Choose which services to include"],
    ["Signal rule", "Your team chooses the reading and how long it must last to raise or clear an alarm.", "Set the database alarm"],
    ["Required call", "If this call fails, the caller can’t complete its work. Your team chooses which calls count.", "Can the API still serve a request?"]
  ],
  layers: [
    ["Identity", "The same Azure resource ID ties the inventory record and the meter reading to one server.", "Match two reports to one server"],
    ["Projection", "A query groups details into a new view. Here, many pod calls become one service link.", "Turn several calls into one arrow"],
    ["Ontology", "The vocabulary of the map names the kinds of things it can show and the links between them.", "A database service and a database server"]
  ],
  incident: [
    ["Sample time", "A time series places each reading at the time the meter took it.", "Place the first zero on the clock"],
    ["Impact", "A caller can turn unhealthy because something it needs has failed.", "Follow the alarm back to the gateway"],
    ["Recovery", "The first good minute starts the count. The second clears the database alarm.", "Why is the shop still red at 10:14?"]
  ],
  dependency: [
    ["Degraded", "Amber marks a warning. In this replay, the cache has been under heavy load for two minutes.", "Rewind to the overloaded cache"],
    ["Evidence", "The API’s cache setting is a record you can follow back from the new arrow.", "Change one address in the API settings"],
    ["Comparison", "Both replays use the same meter readings and rules. Only the API’s cache setting changes.", "Compare the gateways at 10:06:30"]
  ],
  azure: [
    ["Unmatched address", "The API names a host. The inventory has yet to identify a resource at that address.", "An address is waiting for a match"],
    ["Inventory match", "The cache type and host address match a resource that Azure reports.", "Give the new arrow a destination"],
    ["Unknown", "Sattle knows the cache exists. It needs health readings to judge how the cache is doing.", "What can we say about the new cache?"]
  ],
  replay: [
    ["Patch", "A list of edits that takes the Canvas from one version of the map to the next.", "Send the edits to the Canvas"],
    ["Revision", "A numbered version of the graph. You can return to it to inspect the shop at that point.", "Step from the outage to recovery"],
    ["Replay", "Run the same recorded inputs and rules again to check the result.", "Do two ways of building the map agree?"]
  ],
  inspect: [
    ["Origin", "A component whose own signal is failing. Its callers can show the effects of that failure.", "Find the server that raised the alarm"],
    ["Lineage", "The trail from a result, through the queries that produced it, back to the source records.", "Show a teammate why this arrow exists"],
    ["Query draft", "A copy you can edit and preview before saving a rule change.", "Try a rule before you save it"]
  ]
};
