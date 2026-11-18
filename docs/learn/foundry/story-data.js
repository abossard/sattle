const story = [
  {
    "id": "estate",
    "chapter": "One order through the whole shop",
    "title": "Which parts of a shop does one order touch?",
    "beats": [
      {
        "focus": "Customer journey",
        "text": "A shopper orders eighty dollars of merchandise. The parcel arrives late, and she sends back two items. The shop follows Microsoft’s Dynamics 365 Commerce architecture. Order one oh four and the Foundry service that helps with her return are examples we’ve added to it.",
        "start": 0.0,
        "end": 19.833333333333332
      },
      {
        "focus": "Master graph",
        "text": "At checkout, the storefront sends the order to the Commerce Scale Unit, the engine behind the store. Pull back to the whole shop. This version of its master graph has forty-six entities and sixty-eight links. Each link says something specific, such as who ships a parcel or which system looks up an order.",
        "start": 19.833333333333332,
        "end": 39.458333333333336
      },
      {
        "focus": "Projection",
        "text": "A packing list and a financial report can both refer to order one oh four. A person looking after the whole shop needs a different view again. Which parts of the business depend on each other, and what would explain a broken link between them?",
        "start": 39.458333333333336,
        "end": 55.833333333333336
      }
    ]
  },
  {
    "id": "dependencies",
    "chapter": "From the master graph to shop dependencies",
    "title": "How can a graph explain the shop in business terms?",
    "beats": [
      {
        "focus": "Master graph",
        "text": "The warehouse hands parcels to a carrier. The refund worker reads a case record. Both links belong to the same master graph, along with the services and records for the rest of the shop. Their verbs tell us what each connection means.",
        "start": 55.833333333333336,
        "end": 71.58333333333333
      },
      {
        "focus": "Levels of abstraction",
        "text": "A projection can group the whole shop into fourteen business activities. The refund worker belongs to Payout. The case store and the approval record belong to Returns Intake. The worker reads the store and requires the approval. Both links carry up to the business view. The white paper calls these levels of abstraction.",
        "start": 71.58333333333333,
        "end": 92.83333333333333
      },
      {
        "focus": "Tree view",
        "text": "In a tree view, Returns Intake appears under Payout and under Customer Support. Both entries point to one part of the shop. Under Payout the evidence still says which record the worker reads and which it requires. Only the approval link declares a requirement. A read alone cannot tell us that a failure will block payment.",
        "start": 92.83333333333333,
        "end": 114.91666666666667
      }
    ]
  },
  {
    "id": "commerce",
    "chapter": "From checkout to the books",
    "title": "Who needs to know what happened to the order?",
    "beats": [
      {
        "focus": "Order fulfillment",
        "text": "Checkout records order one oh four and reserves the items so another shopper can’t buy the same stock. Dynamics 365 Supply Chain Management covers the warehouse and transportation. The packer uses the order lines. The carrier assigns a tracking number that ties delivery updates back to the order.",
        "start": 114.91666666666667,
        "end": 136.375
      },
      {
        "focus": "Independent subscribers",
        "text": "A scan from the carrier changes the delivery date. That one event can serve several purposes. Support wants to answer a question about the parcel. Marketing may send a message, depending on the shopper’s consent. Neither process controls the truck carrying her order.",
        "start": 136.375,
        "end": 154.75
      },
      {
        "focus": "Net merchandise",
        "text": "At the end of this order, analytics can join the sale, delivery and refund. The original sale was eighty dollars. The shopper sends back two items. Finance needs to know the value of those items and whether the money has gone back to the customer. The warehouse can answer the first part.",
        "start": 154.75,
        "end": 174.5
      }
    ]
  },
  {
    "id": "logistics",
    "chapter": "Inside the returns warehouse",
    "title": "What does the warehouse actually credit?",
    "beats": [
      {
        "focus": "Return lines",
        "text": "Return one oh four points back to two lines on the original order. The warehouse scans both items at the receiving dock. That proves both arrived. The boxes still need inspection before the shop knows what to do with the goods or how much credit to approve.",
        "start": 174.5,
        "end": 192.58333333333334
      },
      {
        "focus": "Disposition",
        "text": "The eighteen-dollar item can go back on the shelf. The twelve-dollar item arrived damaged and goes to scrap. Dynamics calls the decision for each item its disposition. In this case both decisions include a credit, although only one item can be sold again.",
        "start": 192.58333333333334,
        "end": 210.79166666666666
      },
      {
        "focus": "Stock and credit",
        "text": "The stock count gains one sellable item. The warehouse records one item as scrap. Finance now has two approved lines worth thirty dollars. The inspection accounts for the goods. A receipt from the payment processor will show when the money went back to the shopper.",
        "start": 210.79166666666666,
        "end": 229.20833333333334
      }
    ]
  },
  {
    "id": "engagement",
    "chapter": "One event, several readers",
    "title": "Why can support know before finance?",
    "beats": [
      {
        "focus": "Customer record",
        "text": "The carrier’s delayed scan, D seven, carries a tracking number. That number points back to order one oh four. The shop’s customer records system attaches the update to one support case. An agent can see what happened to the parcel as soon as that case updates.",
        "start": 229.20833333333334,
        "end": 246.66666666666666
      },
      {
        "focus": "Event identity",
        "text": "An order event called E eighty-one arrives twice after a retry. In Microsoft’s reference design, an Azure Function checks its ID in Cosmos DB and puts one message on Service Bus. Logic Apps starts one Customer Insights journey. The shopper opted out of promotions, so the journey sends her no marketing email.",
        "start": 246.66666666666666,
        "end": 269.25
      },
      {
        "focus": "Reporting cutoff",
        "text": "At the first reporting cutoff, finance has an eighty-dollar sale and no settled refund. The support case is already visible. Later the payment receipt arrives for thirty dollars. Finance now reports fifty dollars in merchandise sales, before shipping or tax. Each answer reflects the evidence available at that time.",
        "start": 269.25,
        "end": 291.6666666666667
      }
    ]
  },
  {
    "id": "live",
    "chapter": "The live conversation",
    "title": "How does support find the status of a return?",
    "beats": [
      {
        "focus": "Conversation",
        "text": "The parcel has arrived. The customer writes, “My order came late. I sent two items back. Where’s my refund?” The shop has opened return one oh four for her order. Its support agent runs in Microsoft Foundry Agent Service with instructions, a deployed model and an order lookup tool.",
        "start": 291.6666666666667,
        "end": 311.5833333333333
      },
      {
        "focus": "Tool request",
        "text": "The model asks the order lookup tool about order one oh four. The request has a function name and an argument, the order identifier. The shop’s application runs the function against the merchant’s order system. That system can tell whether the warehouse has received the return.",
        "start": 311.5833333333333,
        "end": 330.4166666666667
      },
      {
        "focus": "Tool result",
        "text": "The order system replies, “Return received.” The agent can say, “We’ve received your items. Your refund is still pending.” The warehouse can inspect a return before the shop approves a credit. The record supports an answer about receipt. A payment confirmation would support an answer about money.",
        "start": 330.4166666666667,
        "end": 350.875
      }
    ]
  },
  {
    "id": "sandbox",
    "chapter": "The receipt calculation",
    "title": "What survives a temporary calculation?",
    "beats": [
      {
        "focus": "Attachment",
        "text": "The customer attaches an itemized receipt to the same conversation. It lists the two items she sent back, one at eighteen dollars and one at twelve. The shop supplies the receipt to the agent’s analysis tool. The return number ties that file to her case.",
        "start": 350.875,
        "end": 368.8333333333333
      },
      {
        "focus": "Sandbox",
        "text": "Foundry’s Code Interpreter runs Python in a sandbox, an isolated place to run code. It can read the attached file and add those two amounts. Thirty dollars is the receipt subtotal. A later step decides whether the case qualifies for a refund.",
        "start": 368.8333333333333,
        "end": 386.6666666666667
      },
      {
        "focus": "Stored result",
        "text": "The application saves the calculation in Azure Blob Storage and adds a link to the case. A reviewer can open the result after the sandbox session ends. The graph can distinguish where the work happened from where its evidence remains.",
        "start": 386.6666666666667,
        "end": 402.875
      }
    ]
  },
  {
    "id": "jobs",
    "chapter": "After the chat",
    "title": "Which background jobs need a model?",
    "beats": [
      {
        "focus": "Queue",
        "text": "The conversation ends. A queue holds a message with the case number so work can continue later. The live chat can finish even while another service is waiting to pick up the return.",
        "start": 402.875,
        "end": 415.875
      },
      {
        "focus": "Background agent",
        "text": "A review agent picks up the case. It reads the saved calculation and drafts a summary of the customer’s explanation. A model helps with this step because the wording varies from case to case. Its summary can help a reviewer decide what to check.",
        "start": 415.875,
        "end": 432.7916666666667
      },
      {
        "focus": "Fixed procedure",
        "text": "Once the case has approval, the payout worker sends the approved amount and refund ID to the payment processor. Later, a scheduled job compares the processor’s receipts with the case records. Both jobs follow fixed procedures. Neither needs the model that wrote the summary.",
        "start": 432.7916666666667,
        "end": 451.9583333333333
      }
    ]
  },
  {
    "id": "failure",
    "chapter": "Health across both paths",
    "title": "Can a refund get stuck while chat still works?",
    "beats": [
      {
        "focus": "Degraded",
        "text": "Suppose the summary is late. A reviewer can still open the original receipt, so the summary is optional here. An amber warning marks the delay in case review. An approved payout can still go ahead.",
        "start": 451.9583333333333,
        "end": 466.125
      },
      {
        "focus": "Unhealthy",
        "text": "Now the payout worker’s calls to the payment processor time out. Sending money requires a successful call, so the payout rule turns red. The live order lookup still succeeds and stays green. One customer can get an accurate status update while her refund is stuck.",
        "start": 466.125,
        "end": 484.4583333333333
      },
      {
        "focus": "Unknown",
        "text": "Does the timeout prove that the processor itself is down? The worker recorded a failed call. It has no direct readings from inside the external processor. The health model can mark the payout path Unhealthy and leave the processor’s own health Unknown.",
        "start": 484.4583333333333,
        "end": 501.9583333333333
      }
    ]
  },
  {
    "id": "evolution",
    "chapter": "When Azure changes",
    "title": "How can a new storage dependency enter the model?",
    "beats": [
      {
        "focus": "Changed setting",
        "text": "The shop starts saving return attachments at a new storage address. Its configuration names the destination. That setting supplies evidence for a new dependency, even before the Azure inventory contains a matching account. Warehouse and carrier records have their own paths.",
        "start": 501.9583333333333,
        "end": 521.125
      },
      {
        "focus": "Inventory match",
        "text": "Azure then reports a storage account at that address. A query can match the setting to the inventory record and connect the app to the account. A separate reading can show whether a file write succeeds. Finding a resource and measuring it answer different questions.",
        "start": 521.125,
        "end": 538.6666666666666
      },
      {
        "focus": "Ontology and rules",
        "text": "The model needs words for what changed. Its ontology defines kinds of things and links, such as a storage account and a service that writes to it. People can maintain that vocabulary and refine the health rules. A receipt write might be required while a summary remains optional.",
        "start": 538.6666666666666,
        "end": 558.3333333333334
      }
    ]
  },
  {
    "id": "proof",
    "chapter": "Checking an explanation",
    "title": "What evidence would support the answer?",
    "beats": [
      {
        "focus": "Revision",
        "text": "The old view links the returns app to its original storage account. The new view points to the replacement. Each version is a revision. A patch lists the changes between them, so a past investigation can use the map that applied at the time.",
        "start": 558.3333333333334,
        "end": 575.2916666666666
      },
      {
        "focus": "Lineage",
        "text": "The payout warning has a trail of its own. The worker recorded the timeout. The dependency says that a payout needs a successful payment call. The rule brings those facts together. Tracing a conclusion back through the rule to its evidence is called lineage.",
        "start": 575.2916666666666,
        "end": 592.7083333333334
      },
      {
        "focus": "Recorded replay",
        "text": "Order one oh four is an illustration. In the companion shop there’s a recording the Sattle engine can replay. At 10:11, the database meter drops to zero. Revision sixteen shows which callers turn red. Two good minutes clear the alarm. The recorded inputs and rules let Sattle reproduce the result.",
        "start": 592.7083333333334,
        "end": 614.25
      }
    ]
  }
];
