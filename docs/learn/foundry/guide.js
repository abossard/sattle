"use strict";

const architecture = "https://learn.microsoft.com/en-us/dynamics365/commerce/dev-itpro/commerce-architecture";
const journeys = "https://learn.microsoft.com/en-us/dynamics365/guidance/resources/data-integration-commerce-customer-insights-journeys";
const returns = "https://learn.microsoft.com/en-us/dynamics365/supply-chain/sales-marketing/sales-returns";
const agents = "https://learn.microsoft.com/en-us/azure/foundry/agents/";
const sources = ["Architecture and teaching choices", "docs/learn/foundry/sources.md"];
const lessons = {
  estate:["The whole shop","An $80 purchase reaches the warehouse, a support agent, the marketing tool and the books. Explore all 46 entities in this worked example of Microsoft’s Dynamics 365 Commerce architecture.",exploreGraph,[["Microsoft’s retail architecture",architecture],sources]],
  dependencies:["The business dependency graph","A warehouse manager thinks in terms of orders, shipments and returns. This projection groups the whole technical graph at that level. Open a branch to see which original links explain a business dependency.",()=>$("#activity").replaceChildren(projectionExplorer()),[["White paper · Levels of abstraction","paper/ch05-projection-language.md#58-levels-of-abstraction-a-projection-lifts-the-edges-with-the-nodes"],["Illustrated projection","docs/learn/foundry/projection.js"],["Sattle workload projection","engine/definitions/acme.workload.workloadStructure.kql"]]],
  commerce:["The order journey","One order has a physical trail, a customer record and a financial trail. A problem on one of those paths can leave the others running.",commerceExperiment,[["Commerce architecture",architecture],["Marketing integration",journeys]]],
  logistics:["Inside the warehouse","Two items come back. The receiving dock, the stock ledger and the refund worker can give three different answers about them.",warehouseExperiment,[["Dynamics 365 sales returns",returns],["Disposition codes","https://learn.microsoft.com/en-us/dynamics365/supply-chain/sales-marketing/disposition-and-return-reason-codes"]]],
  engagement:["CRM, marketing and analytics","The CRM case gets a delivery update. A retry reaches marketing. Finance waits for a payment receipt. See what each system knows at that moment.",eventExperiment,[["Commerce to Customer Insights",journeys],["Consent controls","https://learn.microsoft.com/en-us/dynamics365/customer-insights/journeys/real-time-marketing-compliance-settings"]]],
  live:["The support conversation","The customer asks where her refund is. Her agent can request a lookup. The shop’s application has the connection to the order system.",liveExperiment,[["Foundry Agent Service",agents+"overview"],["How function calls work",agents+"how-to/tools/function-calling"]]],
  sandbox:["Inside the sandbox","A receipt calculation takes place in a temporary session. What can a reviewer still open once that session closes?",sandboxExperiment,[["Foundry Code Interpreter",agents+"how-to/tools/code-interpreter"],["Blob Storage","https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-introduction"]]],
  jobs:["Work after the chat","One job uses a model to summarize the return. The payout worker checks records and follows a fixed procedure. Their dependencies are different.",jobsExperiment,[["Managed agents",agents+"overview"],sources]],
  failure:["Who feels a failure?","A payment timeout can block a refund while support still answers the customer. Change the evidence to see what each health rule can conclude.",healthExperiment,[["The health model","paper/ch07-telemetry-plane.md"],sources]],
  evolution:["When storage moves","A new hostname in a setting, an Azure inventory match and a successful file write answer three different questions about the new store.",storageExperiment,[["Ontology and types","paper/ch02-ontologies-as-type-graphs.md"],["Sources and adapters","paper/ch10-sources-and-adapters.md"]]],
  proof:["Check the evidence","The larger store illustrates the architecture. The pg-shop companion gives the same questions a recorded answer, down to a meter reading and the engine’s graph revision.",proofExperiment,[["Recorded pg-shop replay","docs/learn/pg-shop.html#incident"],["Actual workload patches","canvas/public/demo/estate/workload.patches.ndjson"]]]
};

function experiment(title, content) {
  setActivity('<div class="case-inspect"><h2>'+title+'</h2>'+content+'</div>');
}
function cases(title, options) {
  experiment(title,radioArea()+'<p id="case-answer" class="note" role="status"></p>');
  let first=true;
  choices(options.map(option=>option[0]),i=>{
    const [,answer,beat]=options[i];
    $("#case-answer").textContent=answer;
    if(!first)player.jump(beat,.9);
    first=false;
  },$("#choices"));
}
function exploreGraph() {
  experiment("Inspect a relationship",'<label for="entity">Entity in the master graph</label><select id="entity"></select><div id="links" aria-live="polite"></div>');
  const select=$("#entity"), names=new Map(FoundryCase.nodes.map(n=>[n.id,n.label]));
  for(const group of FoundryCase.groups) {
    const optgroup=document.createElement("optgroup");optgroup.label=group.label;
    for(const n of FoundryCase.nodes.filter(n=>n.group===group.id))optgroup.append(new Option(n.label,n.id));
    select.append(optgroup);
  }
  function draw() {
    const id=select.value;
    const rows=FoundryCase.edges.filter(([a,b])=>a===id||b===id);
    const ul=document.createElement("ul");
    for(const [a,b,relation] of rows) {
      const li=document.createElement("li");
      li.textContent=names.get(a)+" "+relation+" "+names.get(b)+".";
      ul.append(li);
    }
    const count=document.createElement("p");
    count.textContent=rows.length+" links touch this entity in the illustrated graph.";
    $("#links").replaceChildren(count,ul);
  }
  select.value="orders";draw();
  select.onchange=()=>{draw();player.jump(1,.94);};
}
function commerceExperiment() {
  cases("Which record answers the question?",[
    ["Can the parcel ship?","The stock reservation sets aside O-104’s items. The warehouse pack scan and carrier handoff record what physically left the building.",0],
    ["Will marketing send an offer?","The email address has opted out of promotions. Customer Insights suppresses the offer. The warehouse keeps the same packing instruction.",1],
    ["What did the shop keep?","A settled $30 refund reduces the $80 merchandise sale to $50. Freight and tax are outside this calculation.",2]
  ]);
}
function warehouseExperiment() {
  experiment("Inspect the two returned items",'<label for="disposition">Disposition of the $12 item</label><select id="disposition"><option value="scrap">Damaged · scrap and credit</option><option value="stock">Sellable · restock and credit</option></select><p id="stock-result" class="note" role="status"></p>');
  const select=$("#disposition");
  function show() {
    const sellable=select.value==="stock"?2:1;
    $("#stock-result").textContent="Received 2 · back in sellable stock "+sellable+" · scrapped "+(2-sellable)+". Both dispositions allow credit. The two eligible lines still total $30. A payment receipt records settlement.";
  }
  select.onchange=()=>{show();player.jump(1,.94);};show();
}
function eventExperiment() {
  experiment("Deliver the same order event again",'<p>The CRM case already has delivery scan D-7. This customer opted out of promotional email.</p><div class="options"><button type="button" class="pick" id="deliver">Deliver E-81</button><button type="button" class="pick" id="settle">Record receipt P-30</button></div><p id="event-result" role="status" class="note"></p>');
  let deliveries=0,settled=false;
  function show() {
    $("#event-result").textContent="E-81 deliveries: "+deliveries+". Unique events queued: "+Math.min(deliveries,1)+". Promotions sent: 0. Sales after settled refunds: $"+(settled?50:80)+".";
  }
  $("#deliver").onclick=()=>{deliveries++;show();player.jump(1,.91);};
  $("#settle").onclick=()=>{settled=true;$("#settle").disabled=true;show();player.jump(2,.92);};show();
}
function liveExperiment() {
  cases("What could support tell the shopper?",[
    ["Customer message","The customer says two items were returned. The conversation supplies context and the order ID O-104.",0],
    ["Requested function","lookup_order(order_id = O-104) asks for a fact. The shop application executes the tool against its order system.",1],
    ["Warehouse reply","The lookup returns “Return received.” Support can confirm arrival. A settled refund would need a payment receipt.",2]
  ]);
}
function sandboxExperiment() {
  cases("Follow the lifetime of the receipt",[
    ["Attached","The return case identifies the receipt as evidence for R-104. Two eligible merchandise lines show $18 and $12.",0],
    ["Calculated","The isolated session totals the two lines at $30. The number describes the receipt. The payout still depends on approval.",1],
    ["Session closed","The working session has ended. The Blob result and its link in the case store remain available to the reviewer.",2]
  ]);
}
function jobsExperiment() {
  cases("Which work can proceed?",[
    ["Chat ends","The queued message still names R-104. The background review can start after the customer leaves.",0],
    ["Summary delayed","The reviewer can still see the original receipt. A later summary can help read the case, but adds no payment authority.",1],
    ["Approval recorded","The ordinary payout worker uses the approval and refund ID to request payment. A later scheduled job compares the processor receipt with the merchant ledger.",2]
  ]);
}
function healthExperiment() {
  experiment("Change the observed signals",'<div class="options"><label><input type="checkbox" id="delay" checked> Summary is late</label><label><input type="checkbox" id="timeout" checked> Payout call times out</label></div><div class="flow-states" id="flow-states" role="status"></div><p id="health-reason"></p>');
  const signals=[["Healthy","✓"],["Degraded","△"],["Unhealthy","×"],["Unknown","?"]];
  function show() {
    const delay=$("#delay").checked, failed=$("#timeout").checked;
    const states=[["Order lookup",0],["Case review",delay?1:0],["Payout",failed?2:0],["Processor internals",3]];
    const cells=states.map(([label,i])=>{const cell=document.createElement("div");cell.dataset.state=signals[i][0];cell.textContent=signals[i][1]+" "+label+" · "+signals[i][0];return cell;});
    $("#flow-states").replaceChildren(...cells);
    $("#health-reason").textContent=failed?"The failed call is evidence from the payout worker. The shop has no direct meter inside the processor.":"The worker observes a successful call. The processor’s internal health still has no independent measurement.";
  }
  $("#delay").onchange=()=>{show();player.jump(0,.9);};
  $("#timeout").onchange=()=>{show();player.jump(2,.9);};show();
}
function storageExperiment() {
  cases("What evidence has arrived?",[
    ["New endpoint only","The returns app’s setting names the destination. The corresponding Azure storage account has yet to be identified.",0],
    ["Inventory match","Type and endpoint now identify the replacement account. The graph has enough evidence for a writes-to link. Health remains Unknown.",1],
    ["Successful write","An application write succeeds. A rule can use that observation to assess receipt storage. The ontology gives the new link its meaning.",2]
  ]);
}
function proofExperiment() {
  cases("Which explanation can be checked?",[
    ["Earlier map","A revision preserves which account received the case files at that time. The patch names the changed storage link.",0],
    ["Payout trace","The illustrated timeout and required payment dependency explain the payout alarm. There is no observation of an internal outage at the processor.",1],
    ["Engine recording","In pg-shop the zero belongs to the 10:11 sample. Workload revision 16 shows its effect, and 18 records recovery. The revision number counts graph changes.",2]
  ]);
  const a=document.createElement("a");a.href="pg-shop.html#incident";a.textContent="Inspect the recorded pg-shop outage →";$("#activity").append(a);
}
