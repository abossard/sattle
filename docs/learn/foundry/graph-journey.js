// Illustrative shop records. Azure, Kubernetes and Hubble fields follow the shapes in
// Adapters.cs and acme.ops.ingest.kql. DNS zone exports, app spans and catalog bindings
// are extra inputs in this example. The pg-shop companion is the separate recorded replay.
const ShopGraphJourney = (() => {
  const nodes = [], rawEdges = [], identityEdges = [], joinedEdges = [];
  const byId = new Map(), byKey = new Map(), references = [];
  const windowLabel = "10:11–10:13 UTC";
  const vnet = "vnet-shop";
  const cluster = "aks-shop";
  const namespace = "retail";
  const armPrefix = "/subscriptions/00000000-0000-4000-8000-000000000104/resourceGroups/rg-shop/providers/";
  const armKey = id => "arm:" + id.toLowerCase();
  const catalogKey = id => "catalog:shop/" + id;
  const podUID = (workload, replica) => "uid-" + workload + "-" + replica;
  const uidKey = uid => "pod-uid:" + cluster + "/" + uid;
  const podNameKey = name => ["pod-name", cluster, namespace, name, windowLabel].join("/");
  const dnsKey = host => ["dns", vnet, host.toLowerCase().replace(/\.$/, ""), windowLabel].join("/");
  const addressKey = ip => ["address", vnet, ip, windowLabel].join("/");
  const capability = id => RetailProjection.parentOf[id];

  function record(id, label, source, group, cap, kind, detail, identity) {
    if (byId.has(id)) throw Error("Duplicate shop record: " + id);
    const node = {id, label, source, group, capability:cap, kind, detail, identity, entity:id};
    nodes.push(node);
    byId.set(id, node);
    if (identity) {
      const other = byKey.get(identity);
      if (other) {
        node.entity = other;
        identityEdges.push({from:id, to:other, kind:"describes the same resource", evidence:[id,other], key:identity});
      } else byKey.set(identity, id);
    }
    return node;
  }

  function reference(from, field, key, kind) {
    references.push({from, field, key, kind});
  }

  function native(from, to, kind, field) {
    rawEdges.push({from,to,kind,field,evidence:[from,to]});
  }

  // Catalogs describe the existing 46 shop components. Their own links are visible
  // immediately. Across catalogs, resolve the referenced component ID to an endpoint.
  const catalogs = {
    customer:"Client configuration", agents:"Agent definitions", tools:"Tool registrations",
    systems:"Merchant service catalog", jobs:"Job definitions", data:"Data catalog",
    commerce:"Order service catalog", fulfillment:"Logistics API catalog", engagement:"Integration definitions"
  };
  for (const n of FoundryCase.nodes) {
    record(n.id,n.label,catalogs[n.group],"applications",capability(n.id),"Component",
      catalogs[n.group] + " record for " + n.label + ".",catalogKey(n.id));
  }
  for (const [from,to,kind] of FoundryCase.edges) {
    if (byId.get(from).source === byId.get(to).source) native(from,to,kind,"component binding");
    else reference(from,"target component ID",catalogKey(to),kind);
  }

  // A metric has a resource descriptor and a series definition. Measurements refer
  // to the descriptor; they are never used as a resource's identity or graph version.
  const azureRows = [
    ["arg-receipts","shopreceipts","Microsoft.Storage/storageAccounts","Storage account","receipt-blob","Transactions"],
    ["arg-cluster",cluster,"Microsoft.ContainerService/managedClusters","AKS cluster",null,"Node CPU usage"],
    ["arg-bus","shop-events","Microsoft.ServiceBus/namespaces","Service Bus namespace","bus","Incoming messages"],
    ["arg-cosmos","shop-dedupe","Microsoft.DocumentDB/databaseAccounts","Cosmos DB account","dedupe","Total requests"],
    ["arg-function","shop-event-reader","Microsoft.Web/sites","Function app","event-reader","Function executions"],
    ["arg-lake","shopreports","Microsoft.Storage/storageAccounts","Storage account","data-lake","Transactions"]
  ];
  for (const [id,name,type,kind,logical,meter] of azureRows) {
    const arm = armPrefix + type + "/" + name, cap = logical ? capability(logical) : "infrastructure";
    record(id,name,"Azure Resource Graph","azure",cap,kind,arm,armKey(arm));
    const suffix = id.slice(4), descriptor = "monitor-" + suffix, series = "series-" + suffix;
    record(descriptor,name + " · metric scope","Azure Monitor","azure",cap,"Resource descriptor",
      "The metric response names its resource by ARM ID.",armKey(arm.toUpperCase()));
    record(series,meter + " · " + name,"Azure Monitor","telemetry",cap,"Metric series",
      "Series definition for " + name + ".");
    native(series,descriptor,"measures","resourceId on the series");
    if (logical) reference(logical,"configured ARM resource ID",armKey(arm),"uses Azure resource");
  }

  // The observed Deployment → ReplicaSet → Pod owner chain is local to a cluster.
  // The component annotation is an explicit binding to the shop catalog.
  const workloads = ["storefront","checkout","orders","returns-app","refund-job","tool-handler","review-job"];
  for (const workload of workloads) {
    const cap = capability(workload), prefix = "k8s-" + workload;
    const deploy = prefix+"-deploy", rs = prefix+"-rs", svc = prefix+"-svc";
    const workloadName = workload === "refund-job" ? "refund" : workload;
    record(deploy,workloadName,"Kubernetes API","kubernetes",cap,"Deployment",
      cluster + " / " + namespace + " / deployment/" + workloadName);
    record(rs,workloadName+"-749","Kubernetes API","kubernetes",cap,"ReplicaSet","Owner UID refers to the deployment.");
    record(svc,workloadName+".retail","Kubernetes API","kubernetes",cap,"Service","An EndpointSlice lists this service’s pod targets.");
    native(rs,deploy,"controlled by","metadata.ownerReferences.uid");
    reference(deploy,"shop component annotation",catalogKey(workload),"runs component");
    reference(deploy,"capture's cluster ARM ID",armKey(armPrefix+"Microsoft.ContainerService/managedClusters/"+cluster),"runs in");
    for (const replica of ["a","b"]) {
      const name = workloadName+"-749-"+replica, id = prefix+"-pod-"+replica, uid = podUID(workload,replica);
      record(id,name,"Kubernetes API","kubernetes",cap,"Pod","UID " + uid + " in " + cluster + "/" + namespace,uidKey(uid));
      byKey.set(podNameKey(name),id);
      native(id,rs,"controlled by","metadata.ownerReferences.uid");
      native(id,svc,"backs","EndpointSlice targetRef.uid");
      const descriptor = "otel-"+workload+"-"+replica;
      record(descriptor,name+" · trace scope","Application telemetry","kubernetes",cap,"Pod descriptor",
        "The trace resource attributes include k8s.pod.uid and the cluster.",uidKey(uid));
    }
  }

  const networkRows = [
    ["receipts","receipts.shop.internal","10.42.2.8","arg-receipts","receipt-blob"],
    ["events","events.shop.internal","10.42.2.9","arg-bus","bus"],
    ["reports","reports.shop.internal","10.42.2.10","arg-lake","data-lake"]
  ];
  const subnetId = armPrefix + "Microsoft.Network/virtualNetworks/" + vnet + "/subnets/endpoints";
  const subnet = record("shop-subnet","10.42.2.0/24","Azure Resource Graph","network","infrastructure","Subnet",
    subnetId,armKey(subnetId));
  for (const [short,host,ip,resource,component] of networkRows) {
    const dns = "dns-"+short, address = "ip-"+short, pe = "pe-"+short;
    record(dns,host,"Private DNS zone export","dns","infrastructure","A record",host+" → "+ip,dnsKey(host));
    record(address,ip,"Azure Resource Graph","network","infrastructure","Scoped IP address",
      ip+" in "+vnet+" during "+windowLabel,addressKey(ip));
    const endpointId = armPrefix + "Microsoft.Network/privateEndpoints/pe-" + short;
    record(pe,"pe-"+short,"Azure Resource Graph","network","infrastructure","Private endpoint",
      endpointId,armKey(endpointId));
    native(pe,address,"uses private IP","endpoint IP configuration");
    native(address,subnet.id,"allocated in","subnet resource ID");
    reference(dns,"A record answer and linked network",addressKey(ip),"resolves to");
    reference(pe,"privateLinkServiceId",byId.get(resource).identity,"connects to");
    reference(component,"configured hostname in "+vnet,dnsKey(host),"uses hostname");
  }

  // These illustrative Hubble rows are observations within a common capture window.
  // A pod name is scoped to cluster, namespace and a known pod lifetime.
  const flowPairs = [["storefront","checkout"],["checkout","orders"],["returns-app","tool-handler"],["tool-handler","orders"]];
  for (const [caller,callee] of flowPairs) {
    for (const replica of ["a","b"]) {
      const id = "flow-"+caller+"-"+callee+"-"+replica;
      record(id,caller+" → "+callee,"Hubble","network",capability(caller),"Flow record",
        "Forwarded connection in "+cluster+"/"+namespace+" at 10:12 UTC.");
      for (const [workload,field,kind] of [[caller,"srcPod","observed caller"],[callee,"dstPod","observed destination"]]) {
        const pod = byId.get("k8s-"+workload+"-pod-"+replica);
        reference(id,field+" with cluster, namespace and time",podNameKey(pod.label),kind);
      }
    }
  }

  // Each result is a client span with an observed outcome, not a probe of the provider.
  const samples = [
    ["10:11","refund-job","processor","reply"],["10:11","refund-job","processor","reply"],
    ["10:12","refund-job","processor","timeout"],["10:12","refund-job","processor","reply"],
    ["10:13","refund-job","processor","timeout"],["10:13","refund-job","processor","timeout"],
    ["10:11","tool-handler","orders","reply"],["10:12","tool-handler","orders","reply"],
    ["10:13","tool-handler","orders","reply"],["10:13","tool-handler","orders","reply"]
  ].map(([time,caller,target,result],i)=>{
    const id = "span-"+i, replica = i%2 ? "b" : "a";
    record(id,caller+" · "+result,"Application traces","telemetry",capability(caller),"Client span",
      time+" UTC · "+caller+" → "+byId.get(target).label+" · "+result);
    reference(id,"span resource k8s.pod.uid",uidKey(podUID(caller,replica)),"emitted by pod");
    reference(id,"instrumented client component ID",catalogKey(caller),"recorded by");
    reference(id,"instrumented peer component ID",catalogKey(target),"reports call to");
    return {id,time,caller,target,result};
  });

  const unmatched = record("unknown-dns","archive.shop.internal","Application configuration","dns","infrastructure",
    "Configured hostname","The capture contains this setting but no DNS answer for it.");
  reference(unmatched.id,"configured hostname",dnsKey(unmatched.label),"uses hostname");

  const unresolved = [];
  for (const {from,field,key,kind} of references) {
    const to = byKey.get(key);
    if (to) joinedEdges.push({from,to,kind,field,key,evidence:[from,to]});
    else unresolved.push({from,field,key,kind});
  }

  // Fold both endpoints of each directed edge. Identifier matches explain why
  // descriptions coalesce; they do not become operational dependencies.
  const semanticEdges = [...rawEdges,...joinedEdges];
  function partition(property, labels) {
    const groups = labels.map(([id,label])=>{
      const records = nodes.filter(n=>n[property]===id);
      return {id,label,members:records.map(n=>n.id),entities:[...new Set(records.map(n=>n.entity))],internal:[]};
    });
    const grouped = new Map(groups.map(g=>[g.id,g])), pairs = new Map();
    for (const edge of semanticEdges) {
      const from = byId.get(edge.from)[property], to = byId.get(edge.to)[property];
      if (from === to) { grouped.get(from).internal.push(edge); continue; }
      const pair = from+"/"+to;
      if (!pairs.has(pair)) pairs.set(pair,{from,to,count:0,evidence:[]});
      const link = pairs.get(pair);
      link.count++;
      link.evidence.push(edge);
    }
    return {groups,edges:[...pairs.values()]};
  }
  const technical = partition("group",[
    ["azure","Azure resources"],["kubernetes","Kubernetes workloads"],["network","Network"],
    ["dns","DNS records"],["telemetry","Application telemetry"],["applications","Shop applications"]
  ]);
  const capabilities = partition("capability",[
    ...RetailProjection.groups.map(g=>[g.id,g.label]),["infrastructure","Shared infrastructure"]
  ]);

  function minuteSeries(caller,target) {
    const byMinute = new Map();
    for (const span of samples.filter(s=>s.caller===caller&&s.target===target)) {
      if (!byMinute.has(span.time)) byMinute.set(span.time,{time:span.time,ok:0,total:0,evidence:[]});
      const minute = byMinute.get(span.time);
      minute.ok+=Number(span.result==="reply");
      minute.total++;
      minute.evidence.push(span.id);
    }
    return [...byMinute.values()];
  }
  // The Payout rule in the illustrated job definition requires a payment reply.
  // A call edge alone has no such implication.
  byId.get("refund-job").healthRule = {requiredReplyFrom:"processor", period:"last closed minute"};
  const requiredTarget = byId.get("refund-job").healthRule.requiredReplyFrom;
  const series = minuteSeries("refund-job",requiredTarget), payment = series.at(-1);
  const lookup = minuteSeries("tool-handler","orders").at(-1);
  const callState = m => m.total===0 ? "Unknown" : m.ok===m.total ? "Healthy" : m.ok===0 ? "Unhealthy" : "Degraded";
  const row = (label,value,evidence,extra={}) => ({label,value,evidence,...extra});
  const recordRow = (id,evidence=[],extra={}) => {
    const n = byId.get(id);
    return row(n.kind,n.label,[id,...evidence],extra);
  };
  const refundDeployment = joinedEdges.find(e=>e.to==="refund-job"&&e.kind==="runs component").from;
  const controllers = rawEdges.filter(e=>e.kind==="controlled by");
  const workloadRows = [recordRow(refundDeployment,["refund-job"],{depth:0})];
  for (const rs of controllers.filter(e=>e.to===refundDeployment)) {
    workloadRows.push(recordRow(rs.from,[rs.to],{depth:1}));
    for (const pod of controllers.filter(e=>e.to===rs.from)) {
      workloadRows.push(recordRow(pod.from,[pod.to],{depth:2}));
    }
  }
  const requiredPayment = semanticEdges.find(e=>e.from==="refund-job"&&e.to===requiredTarget&&e.kind==="calls");
  const payoutState = requiredPayment ? callState(payment) : "Unknown";
  const armResources = nodes.filter(n=>n.source==="Azure Resource Graph"&&n.identity?.startsWith("arm:"));
  const primaryDns = byId.get("dns-receipts");
  const dnsAnswer = joinedEdges.find(e=>e.from===primaryDns.id&&e.kind==="resolves to");
  const endpointEdge = rawEdges.find(e=>e.to===dnsAnswer.to&&e.kind==="uses private IP");
  const targetEdge = joinedEdges.find(e=>e.from===endpointEdge.from&&e.kind==="connects to");
  const views = [
    {id:"dashboard",label:"Live dashboard",summary:"Refund API replies per minute · sample window "+windowLabel,
      series,rows:series.map(m=>row(m.time+" UTC",m.ok+" / "+m.total+" replies",m.evidence,{state:callState(m)}))},
    {id:"health",label:"Health model tree",summary:"Payout requires the payment call. Order lookup has its own readings.",
      rows:[
        row("Payout",payoutState,["refund-job",...payment.evidence],{state:payoutState,depth:0}),
        row("Refund job call",payment.total-payment.ok+" timeouts",payment.evidence,{state:callState(payment),depth:1}),
        row("Payment API internals","Unknown",["processor"],{state:"Unknown",depth:2}),
        row("Order lookup",callState(lookup),lookup.evidence,{state:callState(lookup),depth:0})
      ]},
    {id:"kubernetes",label:"Kubernetes view",summary:"The Kubernetes API reports owner references in "+cluster+" / "+namespace+".",
      rows:workloadRows},
    {id:"azure",label:"Azure resource view",summary:"Azure Resource Graph reports resources in rg-shop. Each row carries an ARM ID.",
      rows:armResources.map(n=>recordRow(n.id))},
    {id:"dns",label:"DNS / IP management",summary:"Private DNS answers and endpoint configuration for "+vnet+" during "+windowLabel+".",
      rows:[row(primaryDns.label,byId.get(dnsAnswer.to).label,dnsAnswer.evidence),
        row("Private endpoint",byId.get(endpointEdge.from).label,endpointEdge.evidence),
        row("Target account",byId.get(targetEdge.to).label,targetEdge.evidence),
        row(unmatched.label,"No DNS answer",[unmatched.id],{state:"Unknown"})]}
  ];
  const rawConnected = new Set(rawEdges.flatMap(e=>[e.from,e.to]));
  const stats = {records:nodes.length,raw:rawEdges.length,identities:identityEdges.length,joined:joinedEdges.length,
    isolated:nodes.length-rawConnected.size,entities:new Set(nodes.map(n=>n.entity)).size,
    technicalGroups:technical.groups.length,capabilities:capabilities.groups.length};
  const example = {record:"arg-receipts",peer:"monitor-receipts",key:byId.get("arg-receipts").detail,
    match:"same ARM resource ID",dns:primaryDns.id,address:dnsAnswer.to,endpoint:endpointEdge.from};
  // The guide and film use the sentence timings from the same local recording.
  function viewAt(progress) {
    return shopViewStarts.reduce((active,start,i)=>progress>=start ? i : active,0);
  }
  function viewMoment(i) {
    return (shopViewStarts[i]+(shopViewStarts[i+1] ?? 1))/2;
  }
  return {nodes,rawEdges,identityEdges,joinedEdges,technical,capabilities,views,stats,example,unresolved,viewAt,viewMoment};
})();
