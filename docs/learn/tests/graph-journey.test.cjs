const test = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {join} = require('node:path');
const vm = require('node:vm');

// Run the same classic scripts, in the same order, as the guide and the film.
const scripts = ['case', 'projection', 'view-starts', 'graph-journey'].map(name =>
  readFileSync(join(__dirname, '../foundry', name + '.js'), 'utf8'));
const {journey, shop} = vm.runInNewContext(scripts.join('\n') + '\n({journey: ShopGraphJourney, shop: RetailProjection})');
const nodes = new Map(journey.nodes.map(node => [node.id, node]));
const links = [...journey.rawEdges, ...journey.joinedEdges];
const views = new Map(journey.views.map(view => [view.id, view]));
const hasLink = (from, kind, to) => links.some(link => link.from === from && link.kind === kind && link.to === to);

test('the Redis comparison identifies the added health edge and its recorded evidence', () => {
  const file = readFileSync(join(__dirname, '../foundry/replay-cases.js'), 'utf8');
  const {discovery:d, rollup} = vm.runInNewContext(file + '\nShopReplayCases;');
  assert.deepEqual(Array.from(d.masterPatch.edits, edit => [edit.operation,edit.type]), [['added','Sends']]);
  assert.ok(d.healthPatch.edits.some(e => e.id === d.lineage.workload.id && e.operation === 'added' && e.type === 'DependsOn'));
  assert.ok(d.lineage.workload.sources.some(s => s.graph === d.structurePatch.graph && s.id === d.dependency.id));
  assert.deepEqual(new Set(Array.from(d.proofs,p => p.how)), new Set(['privateEndpoint','dnsName']));
  assert.deepEqual(Object.keys(d.before).filter(key => d.before[key] !== d.after[key]).sort(), ['api','apim-shop']);
  assert.equal(d.before.shopcache, 'Degraded');
  assert.equal(d.after.api, d.after.shopcache);
  assert.deepEqual(Array.from(rollup.frames,f => f.states['pg-shop']), ['Healthy','Unhealthy','Unhealthy','Healthy']);
});

test('recorded questions, answers and view changes follow the editable scripts', () => {
  for (const [scriptDir, outputDir] of [['foundry','foundry'],['film','.']]) {
    const read = (dir,file) => readFileSync(join(__dirname,'..',dir,file),'utf8');
    const script = JSON.parse(read(scriptDir,'lesson.json'));
    const recorded = vm.runInNewContext(read(outputDir,'story-data.js')+'\nstory;');
    assert.equal(recorded.length, script.length);
    let end = 0;
    for (const [i,chapter] of script.entries()) {
      assert.equal(recorded[i].id, chapter.id);
      assert.equal(recorded[i].beats.length, chapter.cues.length);
      for (const [j,cue] of chapter.cues.entries()) {
        const beat = recorded[i].beats[j];
        assert.equal(beat.start, end);
        assert.equal(beat.focus, cue.focus);
        assert.equal(beat.question, cue.question);
        assert.equal(JSON.stringify(beat.answer), JSON.stringify(cue.answer));
        const answers = [].concat(cue.answer);
        assert.equal(beat.answerStarts.length, answers.length);
        let previous = beat.start;
        for (const start of beat.answerStarts) {
          assert.ok(start > previous && start < beat.end);
          previous = start;
        }
        if (chapter.id === 'estate' && Array.isArray(cue.answer)) {
          assert.equal(answers.length, journey.views.length);
          answers.forEach((_,k) => {
            assert.equal(journey.viewAt(journey.viewMoment(k)), k);
            if (k > 0) {
              const boundary = (beat.answerStarts[k] - beat.start) / (beat.end - beat.start);
              assert.equal(journey.viewAt(boundary - .00001), k - 1);
              assert.equal(journey.viewAt(boundary + .00001), k);
            }
          });
        }
        end = beat.end;
      }
    }
  }
});

test('source records keep their identities and the evidence for each typed link', () => {
  const {record, peer, dns, address, endpoint} = journey.example;
  const account = nodes.get(record);
  assert.equal(account.source, 'Azure Resource Graph');
  assert.equal(nodes.get(peer).source, 'Azure Monitor');
  assert.equal(account.identity, 'arm:' + journey.example.key.toLowerCase());
  assert.equal(account.entity, nodes.get(peer).entity);
  assert.ok(journey.identityEdges.some(edge => edge.from === peer && edge.to === record));

  assert.ok(hasLink(dns, 'resolves to', address));
  assert.ok(hasLink(endpoint, 'uses private IP', address));
  assert.ok(hasLink(endpoint, 'connects to', record));
  assert.notEqual(nodes.get(endpoint).entity, account.entity);
  assert.notEqual(nodes.get(dns).entity, nodes.get(address).entity);
  for (const edge of journey.identityEdges) {
    assert.equal(nodes.get(edge.from).identity, nodes.get(edge.to).identity);
    assert.equal(nodes.get(edge.from).entity, nodes.get(edge.to).entity);
  }
  for (const edge of links) {
    assert.ok(edge.field, edge.kind);
    assert.deepEqual([...edge.evidence], [edge.from, edge.to]);
    assert.ok(nodes.has(edge.from) && nodes.has(edge.to), edge.kind);
  }
  const initialEndpoints = new Set(journey.rawEdges.flatMap(edge => [edge.from, edge.to]));
  assert.equal(journey.stats.isolated, nodes.size - initialEndpoints.size);
  assert.ok(initialEndpoints.size > 0 && journey.stats.isolated > 0);
});

test('each grouping preserves every source record and the direction of every operational link', () => {
  const {technical, capabilities} = journey;
  assert.deepEqual(new Set(technical.groups.map(g => g.id)), new Set(['azure','kubernetes','network','dns','telemetry','applications']));
  assert.equal(shop.groups.length, 14);
  assert.deepEqual(new Set(capabilities.groups.map(g => g.id)), new Set([...shop.groups.map(g => g.id), 'infrastructure']));
  for (const level of [technical, capabilities]) {
    const memberships = level.groups.flatMap(group => group.members.map(id => [id, group.id]));
    const groupOf = new Map(memberships);
    assert.equal(memberships.length, nodes.size);
    assert.deepEqual(new Set(groupOf.keys()), new Set(nodes.keys()));
    const evidence = [];
    for (const group of level.groups) {
      for (const edge of group.internal) {
        assert.equal(groupOf.get(edge.from), group.id);
        assert.equal(groupOf.get(edge.to), group.id);
        evidence.push(edge);
      }
    }
    for (const bundle of level.edges) {
      assert.notEqual(bundle.from, bundle.to);
      assert.equal(bundle.count, bundle.evidence.length);
      for (const edge of bundle.evidence) {
        assert.equal(groupOf.get(edge.from), bundle.from);
        assert.equal(groupOf.get(edge.to), bundle.to);
        evidence.push(edge);
      }
    }
    assert.equal(evidence.length, links.length);
    assert.deepEqual(new Set(evidence), new Set(links));
  }
});

test('the five output views trace their answers back to the same shop evidence', () => {
  assert.deepEqual(new Set(views.keys()), new Set(['dashboard','health','kubernetes','azure','dns']));
  for (const {rows} of views.values()) {
    assert.ok(rows.length);
    for (const row of rows) assert.ok(row.evidence.length && row.evidence.every(id => nodes.has(id)), row.label);
  }
  const latest = views.get('dashboard').series.at(-1);
  assert.equal(latest.ok, 0);
  assert.equal(latest.total, latest.evidence.length);
  assert.ok(latest.evidence.every(id => nodes.get(id).detail.includes('timeout')));
  const health = new Map(views.get('health').rows.map(row => [row.label, row]));
  assert.equal(nodes.get('refund-job').healthRule.requiredReplyFrom, 'processor');
  assert.ok(latest.evidence.every(id => health.get('Payout').evidence.includes(id)));
  assert.equal(health.get('Payout').state, 'Unhealthy');
  assert.equal(health.get('Payment API internals').state, 'Unknown');
  assert.equal(health.get('Order lookup').state, 'Healthy');

  const workload = views.get('kubernetes').rows;
  const deployment = workload.find(row => row.label === 'Deployment').evidence[0];
  const replicaSet = workload.find(row => row.label === 'ReplicaSet').evidence[0];
  const pods = workload.filter(row => row.label === 'Pod');
  assert.ok(hasLink(deployment, 'runs component', 'refund-job'));
  assert.ok(hasLink(replicaSet, 'controlled by', deployment));
  assert.ok(pods.length && pods.every(row => hasLink(row.evidence[0], 'controlled by', replicaSet)));

  const resources = journey.nodes.filter(node => node.source === 'Azure Resource Graph' && node.identity?.startsWith('arm:'));
  assert.deepEqual(new Set(views.get('azure').rows.map(row => row.evidence[0])), new Set(resources.map(node => node.id)));
  const dns = views.get('dns').rows;
  assert.equal(dns[0].value, nodes.get(journey.example.address).label);
  const missing = dns.find(row => row.state === 'Unknown').evidence[0];
  assert.ok(journey.unresolved.some(ref => ref.from === missing));
  assert.ok(links.every(edge => edge.from !== missing && edge.to !== missing));
});
