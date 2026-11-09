"""Authored storyboard illustrating the contract design."""
S=[]
def node(id,label,detail='',color='teal',pos=0):return dict(id=id,label=label,detail=detail,color=color,pos=pos)
def edge(a,b,label=''):return dict(a=a,b=b,label=label)
def frame(title,copy,nodes,edges=(),formula=''):return dict(title=title,copy=copy,nodes=nodes,edges=list(edges),formula=formula)
def add(title,short,group,topic,intro,rule,limit,refs,frames):
 S.append(dict(number=len(S)+1,title=title,short=short,group=group,topic=topic,intro=intro,rule=rule,limit=limit,refs=refs,frames=frames))
add('The master graph feeds derived views.','The master graph','Foundation','views',
 'The master graph holds shared facts. Projections select, reshape and combine those facts into derived graphs.',
 'Every graph space declares one master graph. Each derived graph is connected to it, directly or through other projections.',
 'Each graph has its own revision. An update can be visible in one projection before another catches up.',
 ['GraphSpaceDefinition','MasterGraphDefinition','ProjectionGraphInput'],[
 frame('Start with shared facts','The master contains the infrastructure facts. Two projections have consumed master revision 12.',[node('m','Master graph','master · r12','teal',0),node('s','Service graph','services · r8','blue',2),node('b','Business graph','business · r4','purple',3)],[edge('m','s'),edge('m','b')]),
 frame('A fact changes','A network observation changes the master to r13. Derived graphs still show their previous revisions.',[node('m','Master graph','master · r13 + change','amber',0),node('s','Service graph','services · r8','blue',2),node('b','Business graph','business · r4','purple',3)],[edge('m','s'),edge('m','b')]),
 frame('Service view advances','The service projection consumes the master change and commits its own r9.',[node('m','Master graph','master · r13','teal',0),node('s','Service graph','services · r9','amber',2),node('b','Business graph','business · r4','purple',3)],[edge('m','s'),edge('m','b')]),
 frame('Business view advances','The business projection now commits r5. The graph identities stayed the same throughout.',[node('m','Master graph','master · r13','teal',0),node('s','Service graph','services · r9','blue',2),node('b','Business graph','business · r5','amber',3)],[edge('m','s'),edge('m','b')])])
add('Events, batches, files and gRPC use async streams.','Async ingestion','Foundation','ingest',
 'An event feed, a finite file, a batch job and a gRPC client can all supply the same ordered asynchronous sequence.',
 'Preserve the input order, event boundaries and logical-time controls when changing batch sizes.',
 'Each record in a transport batch can be accepted or rejected. Each projection commits its changes independently.',
 ['IProjectionEngine','IngestBatch','EngineInput'],[
 frame('Events arrive','Three ordered records arrive separately.',[node('a','Event A','sequence 41','teal',0),node('b','Event B','sequence 42','blue',1),node('c','Event C','sequence 43','purple',2)],[edge('a','b'),edge('b','c')],'A → B → C'),
 frame('Batch the delivery','The same records travel together. Their order and boundaries remain visible.',[node('a','Batch [A, B, C]','one transport carrier','amber',0),node('g','Record stream','41 → 42 → 43','blue',2)],[edge('a','g')],'Batch [A, B, C] preserves three record boundaries'),
 frame('Read a finite file','A file reader yields the same ordered stream, then completes.',[node('a','File','3 native records','purple',0),node('g','Record stream','41 → 42 → 43','blue',2)],[edge('a','g')],'await foreach (var item in inputs)'),
 frame('Use gRPC','The delivery can stay open. The engine still observes individual ordered inputs.',[node('a','gRPC feed','A · B · C · …','teal',0),node('g','Record stream','41 → 42 → 43','blue',2)],[edge('a','g')],'Same initial state + rules + inputs + time → same result')])
add('Adapters map native records to graph observations.','Source adapters','Foundation','ingest',
 'Each adapter interprets a versioned native format and produces entity candidates, relationship candidates, properties and evidence.',
 'The first families are Hubble/Cilium, Azure Resource Graph, Kubernetes, Azure Network Watcher and OpenTelemetry.',
 'Kubernetes Events provide observations. Removing resources absent from an ARG result requires a declared coverage and reconciliation policy.',
 ['IGraphAdapter','GraphObservation','AdaptedWithdrawal'],[
 frame('A Hubble flow arrives','A native network observation identifies an API endpoint, a database endpoint and the flow verdict.',[node('raw','Hubble flow','src=api · dst=db','amber',0),node('ad','Hubble adapter','format + version','blue',2)],[edge('raw','ad')]),
 frame('Produce a graph observation','The adapter proposes endpoint identities and an observed Calls relationship, with the native record retained as evidence.',[node('api','API endpoint','identity candidate','teal',0),node('db','DB endpoint','identity candidate','purple',2),node('obs','Calls','observed connection','blue',4)],[edge('api','obs'),edge('obs','db')]),
 frame('Adapt a Kubernetes record','A Kubernetes watch record goes through its own adapter to produce a pod observation.',[node('raw','Kubernetes record','uid=pod-7','amber',0),node('ad','Kubernetes adapter','resource watch','blue',1),node('pod','Pod candidate','uid=pod-7','teal',2)],[edge('raw','ad'),edge('ad','pod')]),
 frame('Withdraw a contribution','An explicit deletion withdraws this source contribution. Other evidence may still support the entity.',[node('raw','DELETED pod-7','explicit source signal','coral',0),node('ad','Withdrawal','source + contribution','amber',2)],[edge('raw','ad')],'Other contributions can continue supporting the entity')])
add('Nodes and edges carry typed data.','Graph structure','Foundation','transforms',
 'Entities represent resources, services or other domain objects. Directed relationships connect them. Both carry an ID, a type and typed properties.',
 'A relationship has entity endpoints and its own projection-specific ID. Different relationship types can connect the same pair.',
 'Removing a node may leave an unresolved endpoint. Missing and not-yet-examined are distinct read outcomes.',
 ['EntityState','RelationshipState','GraphValue'],[
 frame('Two entities','Each entity has a stable ID and a type in this graph’s ontology.',[node('a','Payment API','entity e17 · Service','teal',0),node('b','Ledger DB','entity e31 · Service','purple',2)]),
 frame('Add a relationship','The directed relationship also has an ID. It describes an observed call.',[node('a','Payment API','entity e17 · Service','teal',0),node('b','Ledger DB','entity e31 · Service','purple',2),node('r','Calls','edge r6 · e17 → e31','blue',4)],[edge('a','r'),edge('r','b')]),
 frame('Attach edge data','A declared property records the call’s health on the relationship itself.',[node('a','Payment API','name = payments','teal',0),node('b','Ledger DB','name = ledger','purple',2),node('r','Calls','health = degraded','coral',4)],[edge('a','r'),edge('r','b')]),
 frame('Represent observed and expected calls','ExpectedCall can coexist with Calls. Intended structure and observed traffic answer different questions.',[node('a','Payment API','e17','teal',0),node('b','Ledger DB','e31','purple',2),node('r','Calls · r6','observed','blue',4),node('x','ExpectedCall · r9','intended','amber',3)],[edge('a','r'),edge('r','b'),edge('a','x'),edge('x','b')])])
add('Views share timeless entity identity.','Timeless identity','Foundation','ontology',
 'A shared identity table binds a tenant-scoped, canonical key to an entity ID. The ID persists across changes to graph type and properties.',
 'Projections reuse an identity when they refer to the same entity. An aggregate, such as a deployment containing several pods, receives its own identity.',
 'Renames, aliases, merges and splits require explicit policies and evidence for identity equivalence.',
 ['IdentityKey','IdentityEntry','IIdentityCatalog'],[
 frame('Resolve a stable key','The Kubernetes UID identifies the object independently of its current display name.',[node('k','Tenant + UID','team-a / pod-7','blue',0),node('id','Identity table','→ entity e17','teal',2)],[edge('k','id')]),
 frame('Show it in two graphs','Both projections refer to e17, even when they attach different meanings or properties.',[node('id','Identity table','entity e17','teal',0),node('a','Infrastructure','e17 · Pod','blue',2),node('b','Operations','e17 · Workload','purple',3)],[edge('id','a'),edge('id','b')]),
 frame('A property changes','A new display name changes graph state; the canonical key and entity ID remain fixed.',[node('id','Identity table','entity e17','teal',0),node('a','New display name','e17 · Pod','amber',2),node('b','Operations','e17 · Workload','purple',3)],[edge('id','a'),edge('id','b')]),
 frame('Assign an aggregate identity','A deployment aggregates multiple pods and receives its own identity.',[node('a','Pod e17','member','blue',0),node('b','Pod e18','member','purple',1),node('d','Deployment e50','new aggregate identity','amber',2)],[edge('a','d'),edge('b','d')])])
add('An ontology defines types and properties.','Versioned ontologies','Foundation','ontology',
 'An ontology declares entity types, relationship types and property value kinds. Every graph pins an ontology version and digest.',
 'Activation requires validation of the portable definition. Generated C# types are planned to check property and relationship types during compilation.',
 'A property name is local to its ontology. The same spelling in another ontology can have a different meaning and type.',
 ['OntologyDefinition','PropertyDefinition','OntologyRef'],[
 frame('Declare the contract','This ontology declares Service.health as text.',[node('o','infra / v1','Service.health : text','purple',0),node('v','health = "ok"','text value','teal',2)],[edge('o','v')]),
 frame('Validate a compatible value','The supplied type matches the declared property kind.',[node('o','infra / v1','digest pinned','purple',0),node('v','health = "ok"','accepted shape','teal',2)],[edge('o','v')],'text → text ✓'),
 frame('Reject an incompatible value','The boolean value fails validation against the declared text property.',[node('o','infra / v1','health : text','purple',0),node('v','health = true','type mismatch','coral',2)],[edge('o','v')],'boolean → text ✕'),
 frame('Pin a changed definition','An ontology change creates a new pinned definition and requires validation of dependent projections.',[node('o','infra / v2','new digest','amber',0),node('v','Revalidate dependents','check dependent definitions','blue',2)],[edge('o','v')])])
add('Patterns produce bindings for later operators.','Pattern matching','Projection','transforms',
 'A graph pattern names the entities and relationships to find. Matching produces bindings that later operators can filter, group or emit.',
 'Resolve pattern types and properties against the ontology of the selected input binding.',
 'Matching produces rows of bound entities and relationships. Emit operators define changes to the output graph.',
 ['MatchOperator','GraphPattern','FilterOperator'],[
 frame('Declare a pattern','Find a Pod that calls a Service.',[node('a','p : Pod','pattern variable','blue',0),node('b','s : Service','pattern variable','purple',2)],[edge('a','b','Calls')]),
 frame('Find a candidate','The source graph contains a matching relationship from pod-7 to ledger.',[node('a','pod-7','Pod','teal',0),node('b','ledger','Service','purple',2),node('c','dns','Service','gray',3)],[edge('a','b','Calls')]),
 frame('Bind the result','The operator produces a row containing the matched entity identities.',[node('a','p = e17','matched pod','teal',0),node('b','s = e31','matched service','purple',2),node('row','Binding row','p=e17 · s=e31','amber',4)],[edge('a','row'),edge('b','row')]),
 frame('Filter the bound rows','A filter can retain only unhealthy dependencies. A rejected row emits nothing.',[node('row','health = healthy','predicate is false','gray',0),node('out','No output row','master unchanged','blue',2)],[edge('row','out')])])
add('A projection is a configuration graph.','Transformation rules','Projection','transforms',
 'Portable operators and typed wires describe how input bindings become an output graph. The definition can include expected structure as a scaffold.',
 'Keep selection, identity and emission explicit. The compiler owns type checks and diagnostics; the runtime owns execution and commits.',
 'The typed authoring API currently covers expressions and a subset of row composition. Portable definitions require field, type and reference validation.',
 ['ProjectionDefinition','EmitEntityOperator','IProjectionCompiler'],[
 frame('Select source facts','Match workload facts in the source ontology.',[node('m','Match workloads','infra ontology','blue',0),node('e','Emit service','output ontology','gray',2)],[edge('m','e')]),
 frame('Choose identity','A declared domain and key decide which observations describe the same output entity.',[node('m','Match workloads','bindings','blue',0),node('k','Identity expression','service domain + key','amber',1),node('e','Emit service','output ontology','purple',2)],[edge('m','k'),edge('k','e')]),
 frame('Validate the definition','The compiler checks references, input fields and output property ownership.',[node('m','Definition','operators + wires','blue',0),node('e','Compiled projection','pinned digest','teal',2)],[edge('m','e')]),
 frame('Reject a broken wire','A missing operator or wrong field type returns a diagnostic before activation.',[node('m','Missing field','unknown binding','coral',0),node('e','Compilation rejected','diagnostic + location','coral',2)],[edge('m','e')])])
add('Stack projections through named graph inputs.','Stacked projections','Projection','views',
 'A service graph can feed a capability graph. The service projection groups infrastructure details into the entities and relationships used by the capability projection.',
 'Graph inputs name their source projection and expected ontology. Ordinary dependencies form an acyclic graph rooted at the master.',
 'Reverse lens edits use a separate command path. The forward graph dependencies remain acyclic.',
 ['ProjectionGraphSource','GraphProjectionDefinition','InputFrontier'],[
 frame('Three levels of detail','Each graph is separately versioned.',[node('m','Master','r30 · infrastructure','teal',0),node('s','Services','r12 · service model','blue',1),node('b','Capabilities','r6 · business model','purple',2)],[edge('m','s'),edge('s','b')]),
 frame('Master changes','A new dependency changes master r30 to r31.',[node('m','Master','r31 · new dependency','amber',0),node('s','Services','r12','blue',1),node('b','Capabilities','r6','purple',2)],[edge('m','s'),edge('s','b')]),
 frame('Services consume r31','The service graph commits r13 and records the input version it used.',[node('m','Master','r31','teal',0),node('s','Services','r13 ← master r31','amber',1),node('b','Capabilities','r6','purple',2)],[edge('m','s'),edge('s','b')]),
 frame('Capabilities consume r13','The capability graph commits r7 after consuming service revision r13. Each stage has committed separately.',[node('m','Master','r31','teal',0),node('s','Services','r13','blue',1),node('b','Capabilities','r7 ← services r13','amber',2)],[edge('m','s'),edge('s','b')])])
add('Combine graph inputs with union and join.','Union and join','Projection','views',
 'A projection can consume multiple graph inputs. Union collects compatible rows. Join relates rows using a declared predicate or correspondence.',
 'Each input keeps its own ontology pin and graph revision. Duplicate facts need an explicit conflict policy.',
 'The recorded frontier identifies the consumed versions. Branches may reflect different master revisions; aligning them requires a separate consistency policy.',
 ['UnionOperator','JoinOperator','ProjectionGraphInput'],[
 frame('Two independent inputs','One input contains observed calls; another contains expected calls.',[node('a','Observed','A→B · B→C','teal',0),node('b','Expected','A→B · A→D','purple',2)]),
 frame('Union the rows','First select each relationship’s endpoints into a common {source, target} row schema. Union collects three distinct pairs; observed and expected relationship facts remain in their upstream graphs.',[node('a','Observed','A→B · B→C','teal',0),node('b','Expected','A→B · A→D','purple',2),node('r','Union','A→B · B→C · A→D','amber',4)],[edge('a','r'),edge('b','r')],'Union requires a declared conflict policy'),
 frame('Join on endpoints','An equality join finds the pair present in both input relations.',[node('a','Observed','A→B · B→C','teal',0),node('b','Expected','A→B · A→D','purple',2),node('r','Join result','A→B','blue',4)],[edge('a','r'),edge('b','r')],'observed.source = expected.source ∧ targets equal'),
 frame('Record the consumed versions','The result records observed r8 from master revision 13 and expected r3 from master revision 12.',[node('a','Observed r8','master frontier 13','teal',0),node('b','Expected r3','master frontier 12','purple',2),node('r','Combined r5','inputs: [r8, r3]','amber',4)],[edge('a','r'),edge('b','r')])])
add('Map between input and output ontologies.','Cross-ontology views','Projection','ontology',
 'A source graph can describe infrastructure while its projection describes business capabilities. The mapping declares how source entities contribute to target entities.',
 'Match against each input ontology. Emit against the output ontology. Validate both sides, including conflicting local type and property names.',
 'Source and target types retain their definitions. Shared identity requires both representations to describe the same entity.',
 ['ProjectionGraphInput','GraphProjectionDefinition','OntologyRef'],[
 frame('Start in infrastructure','The source graph pins infra v1 and describes a deployment.',[node('s','Payment deployment','infra v1 · Deployment','teal',0),node('t','Business view','business v1','purple',2)]),
 frame('Apply a named mapping','The projection groups infrastructure evidence under a capability identity.',[node('s','Payment deployment','source e50','teal',0),node('rule','payments-to-capability','declared key mapping','amber',1),node('t','Accept payments','Capability e90','purple',2)],[edge('s','rule'),edge('rule','t')]),
 frame('Keep source lineage','The capability has its own meaning and identity. The correspondence records where it came from.',[node('s','Deployment e50','infra v1','teal',0),node('rule','Correspondence','e50 ↔ e90','blue',1),node('t','Capability e90','business v1','purple',2)],[edge('s','rule'),edge('rule','t')]),
 frame('Reject accidental type reuse','Emission requires a type declared in the output ontology. Business v1 has no Deployment type.',[node('s','Emit Deployment','target = business v1','coral',0),node('t','Unknown target type','definition rejected','coral',2)],[edge('s','t')])])
add('Aggregate dependency signals with a declared rule.','Health propagation','Projection','impact',
 'A health projection aggregates dependency signals into service or capability properties. Its rule defines how dependency states determine the result.',
 'Make the rule part of the projection: for example, any required dependency failing makes the service degraded.',
 'Each health projection defines its own interpretation. A causal diagnosis would require additional evidence and rules.',
 ['GroupOperator','MapOperator','Expr'],[
 frame('All required dependencies pass','The service uses an all-required-dependencies rule.',[node('db','Ledger DB','healthy','teal',0),node('dns','DNS','healthy','teal',2),node('api','Payment service','healthy','blue',4)],[edge('db','api'),edge('dns','api')]),
 frame('A signal changes','The database dependency becomes unavailable. The service still shows its previous revision.',[node('db','Ledger DB','unavailable','coral',0),node('dns','DNS','healthy','teal',2),node('api','Payment service','healthy · prior revision','blue',4)],[edge('db','api'),edge('dns','api')]),
 frame('Apply the rollup','The declared rule emits a degraded service property.',[node('db','Ledger DB','unavailable','coral',0),node('dns','DNS','healthy','teal',2),node('api','Payment service','degraded','amber',4)],[edge('db','api'),edge('dns','api')],'any(required dependency unavailable) → degraded'),
 frame('Recovery is another input','When the dependency recovers, the rule derives healthy again and emits a new change.',[node('db','Ledger DB','healthy','teal',0),node('dns','DNS','healthy','teal',2),node('api','Payment service','healthy · next revision','teal',4)],[edge('db','api'),edge('dns','api')])])
add('A graph patch records one state transition.','Ordered graph diffs','Change','stream',
 'A graph patch describes a transition between two versions. Its edits contain before and after values, and its cause points to the inputs that produced the change.',
 'An input that leaves graph state unchanged emits no graph patch. Acceptance, progress and telemetry remain separate.',
 'Batching keeps constituent patches and their revision boundaries. Squashing is a distinct operation that can discard intermediate history.',
 ['GraphPatch','EntityEdit','PatchBatch'],[
 frame('Before the event','The service is healthy at graph revision 8.',[node('g','Service graph','r8 · healthy','teal',0),node('e','Input 42','health = degraded','amber',2)]),
 frame('Commit one transition','The changed property produces a patch from r8 to r9.',[node('g','Service graph','r9 · degraded','amber',0),node('p','Patch r8 → r9','healthy → degraded','blue',2)],[edge('g','p')],'before: healthy   after: degraded'),
 frame('Repeat the same fact','A separate delivery repeats the current health value. The graph stays at r9.',[node('g','Service graph','r9 · degraded','amber',0),node('e','Input 43','health = degraded','purple',2)],[edge('e','g')]),
 frame('No change, no graph diff','Progress advances while the graph revision and retained evidence history stay unchanged.',[node('g','Service graph','still r9','teal',0),node('p','No graph patch','progress advances','blue',2)],[],'Δgraph = ∅')])
add('Compose patches and add signed deltas.','Diff arithmetic','Change','stream',
 'Ordered patches compose transitions with preconditions. Signed deltas add and subtract weighted graph facts. Separate C# types require an explicit conversion between them.',
 'Patch composition requires adjacent compatible versions. Delta-to-patch conversion needs a base graph and validity checks.',
 'Signed-delta arithmetic permits negative weights. Materialization validates the resulting graph. Conversion reports any loss of chronology.',
 ['PatchThen','PatchInverse','DeltaAdd','ConvertDeltaToPatch'],[
 frame('Compose ordered patches','Apply healthy→degraded, then degraded→failed.',[node('p','Patch P','healthy → degraded','blue',0),node('q','Patch Q','degraded → failed','purple',2)],[edge('p','q')],'P then Q : healthy → failed'),
 frame('Try the opposite order','Q requires a degraded starting state. Applying it to healthy fails its before-value precondition.',[node('q','Patch Q first','needs degraded','coral',0),node('p','Precondition fails','base is healthy','coral',2)],[edge('q','p')],'P then Q ≠ Q then P'),
 frame('Add signed facts','F denotes one exact immutable graph fact, including identity and state. Opposite coefficients for that same fact cancel.',[node('p','+1 × Calls(A,B)','add graph fact','teal',0),node('q','−1 × Calls(A,B)','remove graph fact','coral',2),node('z','Zero delta','no net fact','blue',4)],[edge('p','z'),edge('q','z')],'+F + (−F) = 0'),
 frame('Materialize with validation','Materialization rejects a negative remaining fact multiplicity at the chosen base graph.',[node('p','−1 × Calls(A,B)','base(F) = 0','purple',0),node('q','Materialization rejected','0 − 1 = −1','coral',2)],[edge('p','q')],'Materialization validates fact weights at the base graph')])
add('Read stored and virtual graphs through one interface.','The infinite graph','Change','runtime',
 'A graph can hold stored facts, derive facts through a virtual provider, or combine both. Callers read a specified scope at a pinned revision.',
 'Reads pin a revision, scope and work budget, and report whether the answer is complete.',
 'Infinite extents require finite query scopes. Symbolic patches for virtual-definition changes remain an open design decision.',
 ['IGraphSpace','IVirtualGraphProvider','GraphReadPage','ReadCompletion'],[
 frame('Choose a finite question','Ask for the neighborhood of e17, at depth 1, within a fixed budget.',[node('q','Bounded read','root=e17 · depth=1','amber',0),node('s','Stored facts','paged index','teal',2),node('v','Virtual facts','pinned provider','purple',3)],[edge('q','s'),edge('q','v')]),
 frame('Read stored facts','A stored index supplies the known neighbors for the pinned graph revision.',[node('q','Same bounded read','graph r20','amber',0),node('s','e17 → e31','stored result','teal',2)],[edge('q','s')]),
 frame('Derive virtual facts','A deterministic provider answers the same supported finite scope.',[node('q','Same bounded read','graph r20','amber',0),node('v','e17 → e31','derived result','purple',2)],[edge('q','v')]),
 frame('Report budget exhaustion','A larger scope exhausts its budget. The response identifies the examined portion and reports BudgetExhausted.',[node('q','Larger request','budget = 100 facts','amber',0),node('v','BudgetExhausted','partial + continuation','coral',2)],[edge('q','v')],'Completion status: BudgetExhausted')])
add('Derive indirect dependencies to a fixed point.','Explicit recursion','Change','transforms',
 'A fixed-point operator can derive indirect dependencies by repeatedly adding newly justified facts.',
 'The proposed initial fragment is positive recursion over a pinned finite domain. Ordinary operator cycles fail validation.',
 'Iteration and fact budgets bound the work. Convergence requires a separate argument for the admitted language fragment.',
 ['FixpointOperator','RuleProgram'],[
 frame('Seed the relation','Direct dependencies are A→B and B→C.',[node('a','A','service','teal',0),node('b','B','service','blue',1),node('c','C','service','purple',2)],[edge('a','b'),edge('b','c')],'R₀ = { A→B, B→C }'),
 frame('Derive a new fact','Transitivity adds A→C within the finite domain.',[node('a','A','service','teal',0),node('b','B','service','blue',1),node('c','C','service','purple',2)],[edge('a','b'),edge('b','c'),edge('a','c','derived')],'R₁ = R₀ ∪ { A→C }'),
 frame('Reach the fixed point','Another iteration produces no new facts. The relation has converged for this example.',[node('a','A','service','teal',0),node('b','B','service','blue',1),node('c','C','service','purple',2)],[edge('a','b'),edge('b','c'),edge('a','c','derived')],'R₂ = R₁  ✓'),
 frame('Reject unsupported recursion','A recursive rule that continually creates fresh identities is outside the initial finite fragment.',[node('r','Create fresh ID','on every iteration','coral',0),node('v','Definition rejected','outside finite fragment','coral',2)],[edge('r','v')])])
add('Relate source and target through correspondence.','Triple graph grammars','Consistency','transforms',
 'A triple graph grammar describes consistency between three graphs: the source, the target, and a correspondence relating their elements.',
 'Correspondence records which source and target elements a rule relates. Many source entities can map to one target entity.',
 'Identity equivalence requires an identity rule. Backward updates require a declared transformation with verified lens laws.',
 ['Correspondence','CorrespondenceTemplate','BidirectionalRule'],[
 frame('Start with source structure','Two pods belong to one service group.',[node('a','Pod e17','group=payments','teal',0),node('b','Pod e18','group=payments','teal',3)]),
 frame('Create the target','The target graph contains one service entity with its own identity.',[node('a','Pod e17','source','teal',0),node('b','Pod e18','source','teal',3),node('t','Service e50','target','purple',2)]),
 frame('Record correspondence','Transformation metadata relates both source pods to the target service. These links belong to the correspondence graph.',[node('a','Pod e17','source','teal',0),node('b','Pod e18','source','teal',3),node('c','Correspondence','rule: pod-to-service','amber',1),node('t','Service e50','target','purple',2)],[edge('a','c'),edge('b','c'),edge('c','t')]),
 frame('Inspect the relationship','The linked IDs remain distinct. The correspondence can also retain information omitted from the view.',[node('a','Source set','{ e17, e18 }','teal',0),node('c','Rule + complement','versioned context','amber',1),node('t','Target set','{ e50 }','purple',2)],[edge('a','c'),edge('c','t')])])
add('Declared lenses translate view edits upstream.','Bidirectional lenses','Consistency','transforms',
 'A declared bidirectional transformation translates a supported view edit into a proposed upstream change using its rule and versioned complement.',
 'GetPut preserves the source after an unchanged view is put back. PutGet returns the accepted view after a supported edit is applied.',
 'Read-only projections reject edits. Each bidirectional rule requires validation of its lens laws and conflict handling.',
 ['IBidirectionalProjection','LensContext','BackwardProposal','LensLaw'],[
 frame('A declared forward view','This example exposes the source label and retains the omitted source data in a complement.',[node('s','Source e17','label = API','teal',0),node('v','Editable view','label = API','purple',2)],[edge('s','v','get')]),
 frame('Edit the supported field','The caller proposes a new label against pinned source and view versions.',[node('s','Source e17','label = API','teal',0),node('v','View edit','label = Payments','amber',2)],[edge('v','s','put')]),
 frame('Propose the upstream change','The backward rule uses the complement to preserve information omitted from the view.',[node('s','Source proposal','label = Payments','amber',0),node('v','Forward result','label = Payments','purple',2)],[edge('s','v','get')],'get(put(source, editedView)) = editedView'),
 frame('Reject a read-only edit','The read-only projection rejects the edit because it has no backward rule.',[node('s','Read-only projection','no backward rule','blue',0),node('v','Edit rejected','capability unavailable','coral',2)],[edge('v','s')])])
add('Change the rule, then replay retained evidence.','Configuration replay','Consistency','replay',
 'A projection edit starts from the previous graph and replays the deduplicated union of retained inputs in their recorded order.',
 'Queue new inputs during replay. Publish the final graph difference, and preserve eligible histories for surviving elements.',
 'Each element retains its recent N graph-changing inputs. Earlier and unchanged inputs are omitted. Lens complements are stored separately.',
 ['EditProjection','RetainedInput','ElementHistory'],[
 frame('Retain relevant evidence','Each element keeps a bounded history of inputs that changed its graph state.',[node('a','Element e17','inputs [4, 7, 9]','teal',0),node('b','Element e31','inputs [7, 8]','purple',2)]),
 frame('Union and order','The replay sequence deduplicates shared input 7 and uses original ingestion order.',[node('a','Replay queue','4 → 7 → 8 → 9','amber',0),node('b','New arrivals','queued separately','blue',2)],[],'deduplicated union = [4, 7, 8, 9]'),
 frame('Evaluate the changed rule','The previous graph and retained inputs produce a candidate under the new definition.',[node('a','Prior graph','configuration v1','teal',0),node('r','Revised projection','configuration v2','amber',1),node('b','Candidate graph','replay complete','purple',2)],[edge('a','r'),edge('r','b')]),
 frame('Emit one final difference','Publish the change between the old graph and the completed candidate, then resume queued inputs.',[node('a','Old graph','r20','teal',0),node('b','Final patch','r20 → r21','blue',2)],[edge('a','b')],'Intermediate replay states stay unpublished')])
add('Project graph data into document blocks.','Document projections','Output','views',
 'A document projection selects finite graph data and produces typed blocks. A renderer encodes those blocks as Markdown, HTML or plain text.',
 'Block and row identities are stable and separate from display order. Document patches describe structural changes before rendering.',
 'The renderer produces output text. Editing that text requires a separately designed input mapping. Document overflow either rejects the request or marks truncation.',
 ['DocumentTemplate','DocumentPatch','IDocumentProjection','IDocumentRenderer'],[
 frame('Select finite graph rows','Choose the payment service and its dependencies from a pinned graph revision.',[node('g','Service graph','r9 · finite selection','teal',0),node('r','Selected rows','payments · ledger','blue',2)],[edge('g','r')]),
 frame('Project typed blocks','The heading and table have stable block IDs. Table rows also carry stable keys.',[node('h','Heading','block: title','purple',0),node('t','Dependency table','block: deps · rows e17,e31','blue',2)],[],'Document = Heading + Table'),
 frame('Render Markdown','The renderer escapes values and produces the requested encoding from the typed structure.',[node('t','Typed blocks','document r3','purple',0),node('md','Markdown output','full rendered document','amber',2)],[edge('t','md')],'# Payments\n\n| Service | Health |\n| --- | --- |\n| Payments | Healthy |\n| Ledger | Healthy |'),
 frame('Reorder, preserve identity','Sorting changes display order. The table block and row keys remain the same, so the change is a document edit.',[node('t','Table block: deps','rows e31, e17','blue',0),node('p','Document patch','r3 → r4','amber',2)],[edge('t','p')],'row order changed · row identity preserved')])
add('Verify contract laws with finite examples.','Verification','Output','replay',
 'The design includes compilable contracts, generated schemas and a test-only mathematical reference model.',
 'The suite checks finite patch and delta laws, schema examples, selected composition rules, and C# programs that must fail to compile.',
 'General TGG correctness, production performance, arbitrary virtual graphs and unrestricted recursion remain unverified.',
 ['GraphPatch','SignedDelta','IGraphChangeAlgebra'],[
 frame('Choose a law','Start with a graph and a valid patch P.',[node('g','Graph G','healthy','teal',0),node('p','Patch P','healthy → failed','blue',2)],[edge('g','p')],'apply(inverse(P), apply(P, G)) = G'),
 frame('Apply the patch and inverse','The test-only model returns to the original graph.',[node('g','Graph G′','failed','coral',0),node('p','Inverse P','failed → healthy','purple',1),node('r','Graph G','healthy','teal',2)],[edge('g','p'),edge('p','r')],'Expected healthy · Actual healthy ✓'),
 frame('Inject a wrong inverse','An intentionally broken inverse returns degraded. The assertion expects healthy and fails.',[node('g','Wrong inverse','failed → degraded','coral',0),node('r','Assertion fails','expected healthy','coral',2)],[edge('g','r')],'Expected healthy · Actual degraded ✕'),
 frame('Inspect the test reports','The downloadable reports list the executed checks and the properties that remain unverified.',[node('a','14 model groups','1,921 assertions','teal',0),node('b','47 schema checks','selected semantics','blue',2),node('c','8 compile cases','2 valid · 6 rejected','purple',3)],[],'Production engine: contracts only')])

# Stable visual roles. State changes use separate marks and activation outlines.
ROLE_NAMES={'input':'Input','master':'Master graph','projection':'Projection','rule':'Rule / ontology','identity':'Identity','document':'Document','diff':'Diff'}
ROLES=[
 {'m':'master','s':'projection','b':'projection'},
 {'a':'input','b':'input','c':'input','g':'input'},
 {'raw':'input','ad':'rule','api':'master','db':'master','obs':'master','pod':'master'},
 {'a':'master','b':'master','r':'master','x':'master'},
 {'k':'identity','id':'identity','a':'master','b':'projection','d':'projection'},
 {'o':'rule','v':'input'},
 {'a':'master','b':'master','c':'master','row':'projection','out':'projection'},
 {'m':'rule','k':'identity','e':'projection'},
 {'m':'master','s':'projection','b':'projection'},
 {'a':'projection','b':'projection','r':'projection'},
 {'s':'master','rule':'rule','t':'projection'},
 {'db':'master','dns':'master','api':'projection'},
 {'g':'projection','e':'input','p':'diff'},
 {'p':'diff','q':'diff','z':'diff'},
 {'q':'rule','s':'master','v':'master'},
 {'a':'projection','b':'projection','c':'projection','r':'rule','v':'rule'},
 {'a':'master','b':'master','c':'rule','t':'projection'},
 {'s':'master','v':'projection'},
 {'a':'input','b':'input','r':'rule'},
 {'g':'projection','r':'projection','h':'document','t':'document','md':'document','p':'diff'},
 {'g':'master','p':'diff','r':'master','a':'rule','b':'rule','c':'rule'}]
ACTIVATIONS=[
 [['m'],['m'],['s'],['b']], [['a'],['g'],['g'],['g']], [['ad'],['obs'],['pod'],['ad']],
 [['a','b'],['r'],['r'],['x']], [['id'],['a','b'],['a'],['d']], [['o'],['v'],['v'],['o']],
 [['a','b'],['a','b'],['row'],['out']], [['m'],['k'],['e'],['e']], [['m'],['m'],['s'],['b']],
 [['a','b'],['r'],['r'],['r']], [['s'],['rule','t'],['rule'],['t']], [['api'],['db'],['api'],['api']],
 [['e'],['g','p'],['e'],['p']], [['q'],['p'],['z'],['q']], [['q'],['s'],['v'],['v']],
 [['a','b'],['c'],['a','b','c'],['v']], [['a','b'],['t'],['c'],['c']], [['v'],['v'],['s'],['v']],
 [['a','b'],['a'],['r'],['b']], [['r'],['h','t'],['md'],['p']], [['p'],['r'],['r'],['a','b','c']]]
MARKS={
 (1,1):{'m':'Δ'},(1,2):{'s':'Δ'},(1,3):{'b':'Δ'},(3,1):{'obs':'+'},(3,3):{'raw':'−'},
 (4,1):{'r':'+'},(4,2):{'r':'Δ'},(4,3):{'x':'+'},(5,2):{'a':'Δ'},(5,3):{'d':'+'},
 (6,2):{'v':'×'},(6,3):{'o':'Δ'},(8,3):{'e':'×'},(9,1):{'m':'Δ'},(9,2):{'s':'Δ'},(9,3):{'b':'Δ'},
 (10,1):{'r':'+'},(11,1):{'t':'+'},(11,3):{'t':'×'},(12,1):{'db':'Δ'},(12,2):{'api':'Δ'},(12,3):{'api':'Δ'},
 (13,1):{'g':'Δ','p':'Δ'},(13,3):{'p':'0'},(14,1):{'p':'×'},(14,2):{'p':'+','q':'−','z':'0'},(14,3):{'q':'×'},
 (15,3):{'v':'…'},(16,1):{'c':'+'},(16,3):{'v':'×'},(17,1):{'t':'+'},(17,2):{'c':'+'},
 (18,1):{'v':'Δ'},(18,2):{'s':'Δ'},(18,3):{'v':'×'},(19,2):{'r':'Δ'},(19,3):{'b':'Δ'},
 (20,1):{'h':'+','t':'+'},(20,3):{'t':'Δ','p':'Δ'},(21,2):{'r':'×'}}
for step,roles,activations in zip(S,ROLES,ACTIVATIONS):
 for index,f in enumerate(step['frames']):
  f['active']=activations[index]
  for n in f['nodes']:
   n['role']=roles[n['id']]
   n['mark']=MARKS.get((step['number'],index),{}).get(n['id'],'')
  if step['number']==19 and index>=2:
   for n in f['nodes']:
    if n['id']=='a':n['role']='projection'
    if n['id']=='b':n['role']='diff' if index==3 else 'projection'
 step['roles']=[{'id':k,'label':v} for k,v in ROLE_NAMES.items() if any(n['role']==k for f in step['frames'] for n in f['nodes'])]
