// One definition stays beside the case while the corresponding passage plays.
const ShopTerms = {
  estate: [
    ["Customer journey", "Order O-104 connects checkout, the warehouse, delivery and return R-104. Each stage can leave a record."],
    ["Master graph", "Entities are the things that sources describe. Typed links say how they relate, such as ships, reads or pays."],
    ["Projection", "A query can transform the same source graph into a shop-level view of orders, logistics, customer service, marketing and finance."]
  ],
  dependencies: [
    ["Master graph", "All 46 entities and 68 typed links belong to this example’s first graph. It covers the whole shop, including its services and records."],
    ["Levels of abstraction", "A projection groups the shop into 14 activities. The refund worker reads the case store and requires the approval record. Both links remain as evidence."],
    ["Tree view", "Payout and Customer Support both reach Returns Intake. The tree can show both paths to the same capability."]
  ],
  commerce: [
    ["Order fulfillment", "A shared order ID links stock reservations and packing records. The tracking number connects the parcel to delivery updates."],
    ["Independent subscribers", "Support can use a delivery update while marketing checks consent. A promotional email is optional for fulfillment."],
    ["Net merchandise", "A settled $30 refund on an $80 order leaves $50 in merchandise sales. This example leaves out shipping and tax."]
  ],
  logistics: [
    ["Return lines", "R-104 links both returned items to O-104. Arrival records say which items reached the warehouse."],
    ["Disposition", "The inspection decides what happens to each item and whether to credit it. Here the $18 item goes back into stock and the $12 item goes to scrap."],
    ["Stock and credit", "One item can sell again. Both lines qualify for credit. A separate payment record shows when the $30 refund settles."]
  ],
  engagement: [
    ["Customer record", "A tracking number connects delay D-7 to order O-104 and one support case."],
    ["Event identity", "A retry keeps the same event ID. Checking E-81 in Cosmos DB lets the integration queue one message."],
    ["Reporting cutoff", "The ledger shows $80 before payment receipt P-30 and $50 after it. The support case can update earlier."]
  ],
  live: [
    ["Conversation", "A managed agent can use a model and tools to answer across several turns."],
    ["Tool request", "The model can ask for a named function with specific arguments. The application runs the function."],
    ["Tool result", "The order system supplies the status. The agent can use that result in its reply."]
  ],
  sandbox: [
    ["Attachment", "A case identifier connects the receipt to the return it belongs to."],
    ["Sandbox", "An isolated session can run code on an attached file. Its result here is a subtotal."],
    ["Stored result", "Saving the calculation and linking it to the case lets the evidence outlive the session."]
  ],
  jobs: [
    ["Queue", "A message carries a case number across the gap between a live request and work that happens later."],
    ["Background agent", "A model can draft a case summary from a receipt and the customer’s explanation."],
    ["Fixed procedure", "Payment and reconciliation follow written steps. Neither step needs a language model."]
  ],
  failure: [
    ["Degraded", "An amber warning marks a delay. Here a reviewer still has the original receipt."],
    ["Unhealthy", "A required call has failed. Payout can be blocked while order lookup succeeds."],
    ["Unknown", "A failed call shows what the worker experienced. The processor’s own health needs separate evidence."]
  ],
  evolution: [
    ["Changed setting", "A new storage address gives the model something to match against Azure’s inventory."],
    ["Inventory match", "A setting and an inventory entry can identify the same account. A successful write needs its own evidence."],
    ["Ontology and rules", "People can maintain the vocabulary of things and links, and choose or refine the rules that assign health."]
  ],
  proof: [
    ["Revision", "A numbered version records what the graph looked like. A patch lists the edits between versions."],
    ["Lineage", "The trail from a conclusion to the facts and rules that support it."],
    ["Recorded replay", "The pg-shop recording lets Sattle reproduce the database outage from the same inputs and rules."]
  ]
};
