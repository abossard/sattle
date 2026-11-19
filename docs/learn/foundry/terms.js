// One definition stays beside the case while the corresponding passage plays.
const ShopTerms = {
  estate: [
    ["Order record", "O-104 is the order ID. The stock reservation, tracking update and return record each refer back to it."],
    ["Master graph", "Each dot represents software or data in the shop. Each link names a relationship, such as calls, reads or writes."],
    ["Projection", "A query can transform the same source graph into a shop-level view of orders, logistics, customer service, marketing and finance."]
  ],
  dependencies: [
    ["Master graph", "All 46 entities and 68 typed links belong to this example’s first graph. It covers the whole shop, including its services and records."],
    ["Levels of abstraction", "A projection groups the software by what it does. Two links support Payout → Returns intake. The refund job reads the case store and requires an approval record."],
    ["Tree view", "Payout and Support services both link to Returns intake. Either path leads to the same group of services and records."]
  ],
  commerce: [
    ["Order records", "A reservation and a shipment request share order ID O-104. A tracking number links later carrier events to that order."],
    ["Independent subscribers", "Support can use a delivery update while marketing checks consent. A promotional email is optional for fulfillment."],
    ["Net merchandise", "A settled $30 refund on an $80 order leaves $50 in merchandise sales. This example leaves out shipping and tax."]
  ],
  logistics: [
    ["Return lines", "R-104 references two order lines on O-104. A receipt scan gives each line a recorded arrival status."],
    ["Disposition", "The inspection log records a decision for each line. The $18 line says restock. The $12 line says scrap. Both carry a credit decision."],
    ["Stock and credit", "The stock ledger gains one sellable unit. The approval covers $30. A separate payment receipt confirms settlement."]
  ],
  engagement: [
    ["Support case", "A tracking number connects delay event D-7 to order O-104 and one support case."],
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
    ["Background agent", "A model can draft a case summary from a receipt file and the text of a support conversation."],
    ["Fixed procedure", "Payment and reconciliation follow written steps. Neither step needs a language model."]
  ],
  failure: [
    ["Degraded", "An amber warning marks a late summary. The original receipt file is still available for review."],
    ["Unhealthy", "A required call has failed. Payout can be blocked while order lookup succeeds."],
    ["Unknown", "The refund job recorded a timeout. The payment service’s own health needs a separate reading."]
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
