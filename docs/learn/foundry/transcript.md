# One order through the whole shop

Transcript of the narrated Sattle field guide.

## 00:00 · One order through the whole shop

A shopper orders eighty dollars of merchandise. The parcel arrives late, and she sends back two items. The shop follows Microsoft’s Dynamics 365 Commerce architecture. Order one oh four and the Foundry service that helps with her return are examples we’ve added to it.

At checkout, the storefront sends the order to the Commerce Scale Unit, the engine behind the store. Pull back to the whole shop. This version of its master graph has forty-six entities and sixty-eight links. Each link says something specific, such as who ships a parcel or which system looks up an order.

A packing list and a financial report can both refer to order one oh four. A person looking after the whole shop needs a different view again. Which parts of the business depend on each other, and what would explain a broken link between them?

## 00:55 · From the master graph to shop dependencies

The warehouse hands parcels to a carrier. The refund worker reads a case record. Both links belong to the same master graph, along with the services and records for the rest of the shop. Their verbs tell us what each connection means.

A projection can group the whole shop into fourteen business activities. The refund worker belongs to Payout. The case store and the approval record belong to Returns Intake. The worker reads the store and requires the approval. Both links carry up to the business view. The white paper calls these levels of abstraction.

In a tree view, Returns Intake appears under Payout and under Customer Support. Both entries point to one part of the shop. Under Payout the evidence still says which record the worker reads and which it requires. Only the approval link declares a requirement. A read alone cannot tell us that a failure will block payment.

## 01:54 · From checkout to the books

Checkout records order one oh four and reserves the items so another shopper can’t buy the same stock. Dynamics 365 Supply Chain Management covers the warehouse and transportation. The packer uses the order lines. The carrier assigns a tracking number that ties delivery updates back to the order.

A scan from the carrier changes the delivery date. That one event can serve several purposes. Support wants to answer a question about the parcel. Marketing may send a message, depending on the shopper’s consent. Neither process controls the truck carrying her order.

At the end of this order, analytics can join the sale, delivery and refund. The original sale was eighty dollars. The shopper sends back two items. Finance needs to know the value of those items and whether the money has gone back to the customer. The warehouse can answer the first part.

## 02:54 · Inside the returns warehouse

Return one oh four points back to two lines on the original order. The warehouse scans both items at the receiving dock. That proves both arrived. The boxes still need inspection before the shop knows what to do with the goods or how much credit to approve.

The eighteen-dollar item can go back on the shelf. The twelve-dollar item arrived damaged and goes to scrap. Dynamics calls the decision for each item its disposition. In this case both decisions include a credit, although only one item can be sold again.

The stock count gains one sellable item. The warehouse records one item as scrap. Finance now has two approved lines worth thirty dollars. The inspection accounts for the goods. A receipt from the payment processor will show when the money went back to the shopper.

## 03:49 · One event, several readers

The carrier’s delayed scan, D seven, carries a tracking number. That number points back to order one oh four. The shop’s customer records system attaches the update to one support case. An agent can see what happened to the parcel as soon as that case updates.

An order event called E eighty-one arrives twice after a retry. In Microsoft’s reference design, an Azure Function checks its ID in Cosmos DB and puts one message on Service Bus. Logic Apps starts one Customer Insights journey. The shopper opted out of promotions, so the journey sends her no marketing email.

At the first reporting cutoff, finance has an eighty-dollar sale and no settled refund. The support case is already visible. Later the payment receipt arrives for thirty dollars. Finance now reports fifty dollars in merchandise sales, before shipping or tax. Each answer reflects the evidence available at that time.

## 04:51 · The live conversation

The parcel has arrived. The customer writes, “My order came late. I sent two items back. Where’s my refund?” The shop has opened return one oh four for her order. Its support agent runs in Microsoft Foundry Agent Service with instructions, a deployed model and an order lookup tool.

The model asks the order lookup tool about order one oh four. The request has a function name and an argument, the order identifier. The shop’s application runs the function against the merchant’s order system. That system can tell whether the warehouse has received the return.

The order system replies, “Return received.” The agent can say, “We’ve received your items. Your refund is still pending.” The warehouse can inspect a return before the shop approves a credit. The record supports an answer about receipt. A payment confirmation would support an answer about money.

## 05:50 · The receipt calculation

The customer attaches an itemized receipt to the same conversation. It lists the two items she sent back, one at eighteen dollars and one at twelve. The shop supplies the receipt to the agent’s analysis tool. The return number ties that file to her case.

Foundry’s Code Interpreter runs Python in a sandbox, an isolated place to run code. It can read the attached file and add those two amounts. Thirty dollars is the receipt subtotal. A later step decides whether the case qualifies for a refund.

The application saves the calculation in Azure Blob Storage and adds a link to the case. A reviewer can open the result after the sandbox session ends. The graph can distinguish where the work happened from where its evidence remains.

## 06:42 · After the chat

The conversation ends. A queue holds a message with the case number so work can continue later. The live chat can finish even while another service is waiting to pick up the return.

A review agent picks up the case. It reads the saved calculation and drafts a summary of the customer’s explanation. A model helps with this step because the wording varies from case to case. Its summary can help a reviewer decide what to check.

Once the case has approval, the payout worker sends the approved amount and refund ID to the payment processor. Later, a scheduled job compares the processor’s receipts with the case records. Both jobs follow fixed procedures. Neither needs the model that wrote the summary.

## 07:31 · Health across both paths

Suppose the summary is late. A reviewer can still open the original receipt, so the summary is optional here. An amber warning marks the delay in case review. An approved payout can still go ahead.

Now the payout worker’s calls to the payment processor time out. Sending money requires a successful call, so the payout rule turns red. The live order lookup still succeeds and stays green. One customer can get an accurate status update while her refund is stuck.

Does the timeout prove that the processor itself is down? The worker recorded a failed call. It has no direct readings from inside the external processor. The health model can mark the payout path Unhealthy and leave the processor’s own health Unknown.

## 08:21 · When Azure changes

The shop starts saving return attachments at a new storage address. Its configuration names the destination. That setting supplies evidence for a new dependency, even before the Azure inventory contains a matching account. Warehouse and carrier records have their own paths.

Azure then reports a storage account at that address. A query can match the setting to the inventory record and connect the app to the account. A separate reading can show whether a file write succeeds. Finding a resource and measuring it answer different questions.

The model needs words for what changed. Its ontology defines kinds of things and links, such as a storage account and a service that writes to it. People can maintain that vocabulary and refine the health rules. A receipt write might be required while a summary remains optional.

## 09:18 · Checking an explanation

The old view links the returns app to its original storage account. The new view points to the replacement. Each version is a revision. A patch lists the changes between them, so a past investigation can use the map that applied at the time.

The payout warning has a trail of its own. The worker recorded the timeout. The dependency says that a payout needs a successful payment call. The rule brings those facts together. Tracing a conclusion back through the rule to its evidence is called lineage.

Order one oh four is an illustration. In the companion shop there’s a recording the Sattle engine can replay. At 10:11, the database meter drops to zero. Revision sixteen shows which callers turn red. Two good minutes clear the alarm. The recorded inputs and rules let Sattle reproduce the result.
