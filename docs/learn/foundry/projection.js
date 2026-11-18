// This quotient maps every entity to one business capability and lifts both
// endpoints of every relationship through that same map (white paper, §5.8).
const retailCapabilities = [
  ["checkout","Shopping & checkout",["shopper","storefront","checkout"]],
  ["fulfillment","Order fulfillment",["orders","inventory","order-feed"]],
  ["logistics","Logistics",["warehouse","carrier","tracking"]],
  ["support","Customer support",["chat","conversation","returns-app","lookup-order","tool-handler"]],
  ["agents","Managed agents",["project","returns-agent","review-agent","chat-model","review-model"]],
  ["intake","Returns intake",["returns-desk","inspection","case-queue","case-store","approval"]],
  ["evidence","Receipt evidence",["interpreter","sandbox","artifact-writer","receipt-blob"]],
  ["review","AI review",["review-worker","summary-store"]],
  ["payout","Payout",["refund-worker","refund-id"]],
  ["payment","Payment processing",["processor","processor-receipt"]],
  ["reconciliation","Financial reconciliation",["merchant-ledger","scheduler","reconciler"]],
  ["crm","CRM",["crm"]],
  ["marketing","Marketing",["consent","marketing","event-reader","dedupe","bus","journey-flow"]],
  ["analytics","Analytics",["data-lake","analytics"]]
];

// Roles help a reader interpret the original directed verb. A "calls" edge
// says which service was invoked. A health rule separately establishes impact.
const retailRelationRoles = {
  "Service call":["calls","requests","queries"],
  "Optional call":["can call"],
  "Required input":["requires"],
  "Runtime structure":["hosts","runs","implements","runs code in","uses","starts"],
  "Reads a record":["reads","retrieves output from","compares","references","matches original lines in",
    "reads customer case from","checks event ID in","checks before sending"],
  "Writes a record":["writes","links evidence to","writes agent output to","records","posts to","continues",
    "updates delivery on","records disposition for","opens case with","updates support case in"],
  "Event flow":["sends a message to","sends case to","sends scan to","publishes sale to","polls",
    "queues event on","consumes","triggers journey in","exports sale to","exports delivery to",
    "exports settled refund to","exports returned lines to"],
  "Business operation":["places order at","submits order to","reserves stock in","requests fulfillment from",
    "hands parcel to","receives returned items for"]
};

function projectRetailGraph(nodes,edges,capabilities=retailCapabilities,relationRoles=retailRelationRoles) {
  const source=new Map(nodes.map(node=>[node.id,node]));
  if(source.size!==nodes.length)throw new Error("Each source entity needs a unique ID.");
  const parentOf=Object.create(null), groupById=new Map();
  const roleOf=Object.create(null);
  for(const [role,kinds] of Object.entries(relationRoles)) {
    for(const kind of kinds) {
      if(roleOf[kind])throw new Error("Ambiguous relationship role: "+kind);
      roleOf[kind]=role;
    }
  }
  for(const [id,label,members] of capabilities) {
    if(groupById.has(id))throw new Error("Duplicate capability: "+id);
    const group={id,label,nodes:[],internal:[],internalSourceIndices:[]};
    for(const member of members) {
      if(!source.has(member))throw new Error("Unknown source entity: "+member);
      if(parentOf[member]!==undefined)throw new Error("Entity belongs to two capabilities: "+member);
      parentOf[member]=id;
      group.nodes.push({...source.get(member)});
    }
    groupById.set(id,group);
  }
  for(const node of nodes) {
    if(parentOf[node.id]===undefined)throw new Error("Unassigned source entity: "+node.id);
  }
  const outgoing=new Map(),links=[],typedLinks=[],internal=[];
  edges.forEach(([a,b,kind],index)=>{
    const from=parentOf[a],to=parentOf[b],role=roleOf[kind];
    if(from===undefined||to===undefined)throw new Error("Missing endpoint in source link "+index);
    if(!role)throw new Error("Missing relationship role: "+kind);
    const triple=[a,b,kind];
    if(from===to) {
      groupById.get(from).internal.push(triple);
      groupById.get(from).internalSourceIndices.push(index);
      internal.push(triple);
      return;
    }
    if(!outgoing.has(from))outgoing.set(from,new Map());
    const destinations=outgoing.get(from);
    if(!destinations.has(to)) {
      const link={from,to,kinds:[],roles:[],relations:[],evidence:[],sourceIndices:[]};
      destinations.set(to,link);
      links.push(link);
    }
    const link=destinations.get(to);
    link.evidence.push(triple);
    link.sourceIndices.push(index);
    // Lift each relationship type independently. The diagram bundles parallel
    // typed edges between the same two capabilities to keep it readable.
    let relation=link.relations.find(relation=>relation.kind===kind);
    if(!relation) {
      relation={from,to,kind,role,evidence:[],sourceIndices:[]};
      link.relations.push(relation);
      link.kinds.push(kind);
      typedLinks.push(relation);
    }
    relation.evidence.push(triple);
    relation.sourceIndices.push(index);
    if(!link.roles.includes(role))link.roles.push(role);
  });
  return {groups:[...groupById.values()],links,typedLinks,internal,parentOf,roleOf};
}

const RetailProjection=projectRetailGraph(FoundryCase.nodes,FoundryCase.edges);
