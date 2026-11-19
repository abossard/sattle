// This teaching model adapts the retail architecture mapped in sources.md.
const FoundryCase = (() => {
  const groups = [
    {id:"customer", label:"Client applications", parts:[
      ["web-client","Web client"], ["chat","Chat API"], ["returns-app","Returns app"], ["conversation","Conversation record"]
    ]},
    {id:"agents", label:"Microsoft Foundry", parts:[
      ["project","Foundry project"], ["returns-agent","Returns agent"], ["review-agent","Review agent"],
      ["chat-model","Chat model"], ["review-model","Review model"]
    ]},
    {id:"tools", label:"Tools and sandbox", parts:[
      ["lookup-order","lookup_order"], ["tool-handler","Tool handler"], ["interpreter","Code Interpreter"],
      ["sandbox","Python sandbox"], ["artifact-writer","Evidence writer"]
    ]},
    {id:"systems", label:"Merchant and payment systems", parts:[
      ["orders","Order system"], ["processor","Payment API"], ["merchant-ledger","Merchant ledger service"]
    ]},
    {id:"jobs", label:"Background work", parts:[
      ["case-queue","Case queue"], ["review-job","Review job"], ["refund-job","Refund job"],
      ["scheduler","Job schedule"], ["reconciler","Reconciliation job"]
    ]},
    {id:"data", label:"Stored records", parts:[
      ["case-store","Case store"], ["receipt-blob","Receipt storage"], ["summary-store","Case summary records"],
      ["approval","Approval record"], ["refund-id","Refund ID record"], ["processor-receipt","Payment receipt record"]
    ]},
    {id:"commerce", label:"Store and orders", parts:[
      ["storefront","Online storefront"], ["checkout","Checkout service"],
      ["inventory","Stock ledger"], ["order-feed","Order events"]
    ]},
    {id:"fulfillment", label:"Logistics and returns", parts:[
      ["warehouse-api","Warehouse API"], ["carrier-api","Carrier API"], ["tracking-service","Tracking service"],
      ["inspection-log","Inspection log"], ["returns-api","Returns API"]
    ]},
    {id:"engagement", label:"CRM, marketing and analytics", parts:[
      ["crm","Customer support CRM"], ["consent","Consent record"], ["marketing","Marketing tool"],
      ["analytics","Sales reporting service"], ["data-lake","Retail data lake"], ["event-reader","Azure Function"],
      ["dedupe","Cosmos DB event IDs"], ["bus","Service Bus"], ["journey-flow","Logic Apps workflow"]
    ]}
  ];
  const nodes = groups.flatMap(group => group.parts.map(([id,label])=>({id,label,group:group.id})));
  // Directed typed links. "Hosts" and "reads" have different meanings from "requires".
  const edges = [
    ["web-client","chat","sends a message to"], ["chat","returns-app","calls"],
    ["returns-app","conversation","continues"], ["returns-app","returns-agent","calls"],
    ["returns-app","case-store","reads"], ["project","returns-agent","hosts"],
    ["project","review-agent","hosts"], ["project","chat-model","hosts"],
    ["project","review-model","hosts"], ["returns-agent","chat-model","uses"],
    ["returns-agent","conversation","uses"], ["returns-agent","lookup-order","requests"],
    ["returns-app","tool-handler","runs"], ["tool-handler","lookup-order","implements"],
    ["tool-handler","orders","queries"], ["returns-agent","interpreter","can call"],
    ["interpreter","sandbox","runs code in"], ["returns-app","artifact-writer","calls"],
    ["artifact-writer","sandbox","retrieves output from"], ["artifact-writer","receipt-blob","writes"],
    ["artifact-writer","case-store","links evidence to"], ["returns-app","case-queue","sends case to"],
    ["review-job","case-queue","reads"], ["review-job","review-agent","calls"],
    ["review-agent","review-model","uses"], ["review-job","case-store","reads"],
    ["review-job","receipt-blob","reads"], ["review-job","summary-store","writes agent output to"],
    ["refund-job","case-store","reads"], ["refund-job","approval","requires"],
    ["refund-job","refund-id","uses"], ["refund-job","processor","calls"],
    ["refund-job","processor-receipt","records"], ["refund-job","merchant-ledger","posts to"],
    ["scheduler","reconciler","starts"], ["reconciler","merchant-ledger","reads"],
    ["reconciler","processor-receipt","compares"], ["case-store","receipt-blob","references"],
    ["case-store","summary-store","references"], ["case-store","approval","references"],
    ["case-store","refund-id","references"],
    ["web-client","storefront","sends order request to"], ["storefront","checkout","calls"],
    ["checkout","orders","submits order to"], ["orders","inventory","writes reservation to"],
    ["orders","warehouse-api","sends fulfillment request to"], ["warehouse-api","carrier-api","sends shipment request to"],
    ["carrier-api","tracking-service","sends scan event to"], ["tracking-service","orders","updates delivery on"],
    ["returns-api","orders","matches original lines in"], ["warehouse-api","inspection-log","writes receipt scan to"],
    ["inspection-log","approval","supplies inspection result for"], ["returns-app","returns-api","opens case with"],
    ["returns-api","case-store","writes"], ["orders","order-feed","publishes sale to"],
    ["tracking-service","crm","updates support case in"], ["returns-app","crm","reads customer case from"],
    ["event-reader","order-feed","polls"], ["event-reader","dedupe","checks event ID in"],
    ["event-reader","bus","queues event on"], ["journey-flow","bus","consumes"],
    ["journey-flow","marketing","starts campaign in"], ["marketing","consent","reads consent from"],
    ["orders","data-lake","exports sale to"], ["tracking-service","data-lake","exports delivery to"],
    ["merchant-ledger","data-lake","exports settled refund to"], ["analytics","data-lake","reads"],
    ["returns-api","data-lake","exports returned lines to"]
  ];
  return {groups,nodes,edges};
})();
