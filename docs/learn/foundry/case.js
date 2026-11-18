// This teaching model adapts the retail architecture mapped in sources.md.
const FoundryCase = (() => {
  const groups = [
    {id:"customer", label:"Customer interaction", parts:[
      ["shopper","Customer"], ["chat","Chat"], ["returns-app","Returns app"], ["conversation","Conversation"]
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
      ["orders","Order system"], ["processor","Payment processor"], ["merchant-ledger","Merchant ledger"]
    ]},
    {id:"jobs", label:"Background work", parts:[
      ["case-queue","Case queue"], ["review-worker","Review worker"], ["refund-worker","Refund worker"],
      ["scheduler","Schedule"], ["reconciler","Reconciliation job"]
    ]},
    {id:"data", label:"Stored records", parts:[
      ["case-store","Case store"], ["receipt-blob","Receipt storage"], ["summary-store","Case summaries"],
      ["approval","Approval record"], ["refund-id","Refund ID"], ["processor-receipt","Payment receipt"]
    ]},
    {id:"commerce", label:"Store and orders", parts:[
      ["storefront","Online storefront"], ["checkout","Commerce Scale Unit"],
      ["inventory","Available stock"], ["order-feed","Commerce events"]
    ]},
    {id:"fulfillment", label:"Logistics and returns", parts:[
      ["warehouse","Warehouse"], ["carrier","Carrier"], ["tracking","Shipment tracking"],
      ["inspection","Returns inspection"], ["returns-desk","Returns desk"]
    ]},
    {id:"engagement", label:"CRM, marketing and analytics", parts:[
      ["crm","Customer Service CRM"], ["consent","Marketing consent"], ["marketing","Customer Insights – Journeys"],
      ["analytics","Sales analytics"], ["data-lake","Retail data lake"], ["event-reader","Azure Function"],
      ["dedupe","Cosmos DB event IDs"], ["bus","Service Bus"], ["journey-flow","Logic Apps"]
    ]}
  ];
  const nodes = groups.flatMap(group => group.parts.map(([id,label])=>({id,label,group:group.id})));
  // Directed typed links. "Hosts" and "reads" have different meanings from "requires".
  const edges = [
    ["shopper","chat","sends a message to"], ["chat","returns-app","calls"],
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
    ["review-worker","case-queue","reads"], ["review-worker","review-agent","calls"],
    ["review-agent","review-model","uses"], ["review-worker","case-store","reads"],
    ["review-worker","receipt-blob","reads"], ["review-worker","summary-store","writes agent output to"],
    ["refund-worker","case-store","reads"], ["refund-worker","approval","requires"],
    ["refund-worker","refund-id","uses"], ["refund-worker","processor","calls"],
    ["refund-worker","processor-receipt","records"], ["refund-worker","merchant-ledger","posts to"],
    ["scheduler","reconciler","starts"], ["reconciler","merchant-ledger","reads"],
    ["reconciler","processor-receipt","compares"], ["case-store","receipt-blob","references"],
    ["case-store","summary-store","references"], ["case-store","approval","references"],
    ["case-store","refund-id","references"],
    ["shopper","storefront","places order at"], ["storefront","checkout","calls"],
    ["checkout","orders","submits order to"], ["orders","inventory","reserves stock in"],
    ["orders","warehouse","requests fulfillment from"], ["warehouse","carrier","hands parcel to"],
    ["carrier","tracking","sends scan to"], ["tracking","orders","updates delivery on"],
    ["returns-desk","orders","matches original lines in"], ["warehouse","inspection","receives returned items for"],
    ["inspection","approval","records disposition for"], ["returns-app","returns-desk","opens case with"],
    ["returns-desk","case-store","writes"], ["orders","order-feed","publishes sale to"],
    ["tracking","crm","updates support case in"], ["returns-app","crm","reads customer case from"],
    ["event-reader","order-feed","polls"], ["event-reader","dedupe","checks event ID in"],
    ["event-reader","bus","queues event on"], ["journey-flow","bus","consumes"],
    ["journey-flow","marketing","triggers journey in"], ["marketing","consent","checks before sending"],
    ["orders","data-lake","exports sale to"], ["tracking","data-lake","exports delivery to"],
    ["merchant-ledger","data-lake","exports settled refund to"], ["analytics","data-lake","reads"],
    ["returns-desk","data-lake","exports returned lines to"]
  ];
  return {groups,nodes,edges};
})();
