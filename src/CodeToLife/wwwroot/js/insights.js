// The tools built on the graph: which tests reach the code, what a change would break,
// architecture rules, and stack traces / logs drawn on the map.
import { state, on, emit, api, escapeHtml, memberLabel, shortPath, toast } from './state.js';
import * as graph from './graph.js';

const $ = id => document.getElementById(id);

export function initInsights() {
  on('graph', () => { loadTests(); checkRules({ quiet: true }); });
  on('sse:rules.changed', () => checkRules({ quiet: true }));
  on('details', enrichDetails);
  on('impact', showImpact);
  on('changes-rendered', addTestsToChanges);
  on('select', id => { if (id) graph.highlightSet(null); });
  initRules();
  initTrace();
}

const display = id => {
  const t = state.types.get(id);
  if (t) return t.name;
  const m = state.members.get(id);
  return m ? `${state.types.get(state.memberType.get(id))?.name ?? '?'}.${memberLabel(m)}` : id;
};

// ---------------------------------------------------------------- tests

async function loadTests() {
  try {
    const map = await api('/tests/map');
    state.tests = {
      tests: new Set(map.tests),
      projects: new Set(map.testProjects),
      members: new Map(Object.entries(map.members)),
      types: new Map(map.types.map(t => [t.typeId, t])),
    };
    if (state.overlay === 'tests') graph.applyOverlay();
    emit('tests-loaded', state.tests);
  } catch { /* still analyzing; the next graph event retries */ }
}

/** Adds "Reached by tests" and a coverage chip to the details panel. */
async function enrichDetails({ body, type, member }) {
  const slot = body.querySelector('[data-tests]');
  if (!slot || !state.tests) return;
  const id = member?.id ?? type.id;
  if (state.tests.projects.has(type.project)) {
    if (member && state.tests.tests.has(member.id))
      slot.innerHTML = `<div class="d-section"><h3>Test</h3><div class="row"><button data-run-test>▶ Run this test</button></div></div>`;
    slot.querySelector('[data-run-test]')?.addEventListener('click', () => runTests([member.id]));
    return;
  }
  let hits;
  try { hits = await api(`/tests/for?id=${encodeURIComponent(id)}`); } catch { return; }
  const coverage = !member ? state.tests.types.get(type.id) : null;
  const header = member
    ? (hits.length ? `${hits.length} test${hits.length === 1 ? '' : 's'} reach it` : 'No test reaches it')
    : coverage ? `${coverage.covered} of ${coverage.members} members reached by tests` : 'Tests';
  slot.innerHTML = `<div class="d-section">
      <h3>${escapeHtml(header)}</h3>
      <ul class="rel">${hits.slice(0, 12).map(h => `<li><span class="k">${h.depth === 1 ? 'direct' : h.depth + ' calls'}</span><a data-go="${escapeHtml(h.testId)}">${escapeHtml(h.name)}</a></li>`).join('')
        || `<li class="muted">${member ? 'Nothing in the test projects calls this, directly or indirectly.' : 'No tests reach this type.'}</li>`}
        ${hits.length > 12 ? `<li class="muted">and ${hits.length - 12} more</li>` : ''}</ul>
      ${hits.length ? `<div class="row" style="margin-top:6px"><button data-run-tests>▶ Run ${hits.length === 1 ? 'it' : 'them'}</button><span class="muted">found by following calls from the tests</span></div>` : ''}
    </div>`;
  slot.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => emit('go', a.dataset.go)));
  slot.querySelector('[data-run-tests]')?.addEventListener('click', () => runTests(hits.map(h => h.testId)));
}

async function runTests(ids) {
  emit('show-tab', 'output');
  toast(`Running ${ids.length} test${ids.length === 1 ? '' : 's'}…`);
  try {
    const result = await api('/tests/run', { method: 'POST', body: { ids } });
    toast(result.success ? 'Tests passed.' : 'Some tests failed: see Output.', result.success ? 'info' : 'error');
  } catch (e) { toast(e.message, 'error'); }
}

/** In the What changed tab: the tests to run for this branch, and changed code nothing tests. */
async function addTestsToChanges(report) {
  const ids = [...new Set(report.items.filter(i => i.change !== 'removed').map(i => i.memberId ?? i.typeId))];
  if (!ids.length) return;
  let affected;
  try { affected = await api('/tests/affected', { method: 'POST', body: { ids } }); } catch { return; }
  const box = document.createElement('div');
  box.className = 'cols two';
  box.style.marginTop = '12px';
  box.innerHTML = `
    <div class="box"><h4>Tests to run (${affected.tests.length})</h4>
      ${affected.tests.length ? `<div class="row"><button class="primary" data-run>▶ Run affected tests</button><button data-copy title="Copy the dotnet test command">Copy command</button></div>` : ''}
      <ul class="rel">${affected.tests.map(t => `<li><a data-go="${escapeHtml(t.testId)}">${escapeHtml(t.name)}</a></li>`).join('') || '<li class="muted">No tests reach the changed code.</li>'}</ul></div>
    <div class="box"><h4>Changed but untested (${affected.untested.length})</h4>
      <ul class="rel">${affected.untested.map(u => `<li><a data-go="${escapeHtml(u.id)}">${escapeHtml(u.name)}</a> <span class="muted">${escapeHtml(u.location)}</span></li>`).join('') || '<li class="muted">Every changed member is reached by some test.</li>'}</ul></div>`;
  $('chBody').appendChild(box);
  box.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => emit('go', a.dataset.go)));
  box.querySelector('[data-run]')?.addEventListener('click', () => runTests(affected.tests.map(t => t.testId)));
  box.querySelector('[data-copy]')?.addEventListener('click', () => {
    navigator.clipboard.writeText(affected.commands.map(c => c.commandLine).join('\n')).then(() => toast('Copied.'), () => toast(affected.commands.map(c => c.commandLine).join('\n')));
  });
}

// ---------------------------------------------------------------- impact

const GROUPS = {
  callers: 'Callers: what calls it, nearest first',
  implementations: 'Implementations and overrides',
  contracts: 'Contracts it fulfills',
  dependents: 'Types that hold, create or mention it',
  tests: 'Tests that reach it',
  risks: 'Things the compiler won\'t catch',
};

async function showImpact(id) {
  let report;
  try { report = await api(`/impact?id=${encodeURIComponent(id)}`); } catch (e) { toast(e.message, 'error'); return; }
  const byGroup = Object.keys(GROUPS).map(g => [g, report.items.filter(i => i.group === g)]).filter(([, items]) => items.length);
  const html = `
    <p class="muted">${report.summary.map(escapeHtml).join(' · ')}</p>
    <div class="impact">
      ${byGroup.map(([g, items]) => `<section><h3>${GROUPS[g]} <span class="muted">${items.length}</span></h3><ul class="checklist">${items.map((i, n) => `
        <li><label><input type="checkbox" data-item="${g}:${n}"> <a data-go="${escapeHtml(i.memberId ?? i.typeId)}">${escapeHtml(i.label)}</a></label>
          <span class="muted">${escapeHtml(i.detail)}${i.file ? ` · ${escapeHtml(shortPath(i.file))}:${i.line}` : ''}</span></li>`).join('')}</ul></section>`).join('')
        || '<p>Nothing else in this codebase depends on it.</p>'}
    </div>
    <div class="row" style="margin-top:12px">
      <button class="primary" data-highlight>Highlight on the map</button>
      <button data-plan title="Save a note on each affected type in the plan (.codetolife/plan.json)">Add to plan</button>
      <button data-copy title="Copy as a Markdown checklist">Copy checklist</button>
    </div>`;
  emit('modal', {
    title: `Impact of changing ${report.title}`,
    html,
    wire(root, close) {
      root.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', e => { e.preventDefault(); close(); emit('go', a.dataset.go); }));
      root.querySelector('[data-highlight]').addEventListener('click', () => {
        close();
        if (state.view.kind !== 'types') graph.goHome();
        graph.highlightSet([id, ...report.items.map(i => i.memberId ?? i.typeId)]);
        toast(`${new Set(report.items.map(i => i.typeId)).size} types affected. Click anything to clear.`);
      });
      root.querySelector('[data-plan]').addEventListener('click', async () => {
        const plan = await api('/plan');
        const byType = new Map();
        for (const item of report.items) if (item.typeId && state.types.has(item.typeId)) (byType.get(item.typeId) ?? byType.set(item.typeId, []).get(item.typeId)).push(item);
        for (const [typeId, items] of byType)
          plan.notes.push({ targetId: typeId, text: `Changing ${report.title}: ${items.map(i => `${i.label} (${i.detail})`).join('; ')}`, color: '#f2b544' });
        await api('/plan', { method: 'PUT', body: plan });
        close();
        toast(`Added notes to ${byType.size} types in the plan.`);
      });
      root.querySelector('[data-copy]').addEventListener('click', () => {
        const md = `## Changing ${report.title}\n\n${report.summary.join(' · ')}\n\n` + byGroup.map(([g, items]) =>
          `### ${GROUPS[g]}\n` + items.map(i => `- [ ] ${i.label}: ${i.detail}${i.file ? ` (${shortPath(i.file)}:${i.line})` : ''}`).join('\n')).join('\n\n');
        navigator.clipboard.writeText(md).then(() => toast('Copied as Markdown.'), () => toast('Copying failed.', 'error'));
      });
    },
  });
}

// ---------------------------------------------------------------- architecture rules

function initRules() {
  $('rulesPane').innerHTML = `
    <div class="row">
      <button id="rulesCheck" class="primary">✓ Check</button>
      <button id="rulesSave">Save rules</button>
      <button id="rulesSuggest" title="Fill in a starting point from the namespaces">Suggest</button>
      <span id="rulesSummary" class="muted"></span>
    </div>
    <div class="cols two">
      <div class="box tall"><h4>.codetolife/rules.json</h4><textarea id="rulesJson" spellcheck="false" class="code-input"></textarea></div>
      <div class="box tall"><h4>Violations</h4><div id="rulesViolations" class="muted">Not checked yet.</div></div>
    </div>
    <p class="muted" style="margin:8px 0 0">Rules: <code>{"from": "Domain", "notTo": "Infrastructure"}</code> or <code>{"to": "DbContext", "onlyFrom": "Data"}</code>, optionally with <code>"kinds"</code>, <code>"except"</code> and <code>"because"</code>. Patterns are layer names, <code>project:Name</code> or globs. Run <code>codetolife check</code> in CI to fail builds that break them.</p>`;
  $('rulesCheck').addEventListener('click', () => checkRules());
  $('rulesSave').addEventListener('click', saveRules);
  $('rulesSuggest').addEventListener('click', async () => {
    const r = await api('/rules');
    $('rulesJson').value = r.suggestion ?? '';
    toast('Suggested layers from the namespaces. Edit, then Save.');
  });
  on('show-tab', tab => { if (tab === 'rules') loadRulesText(); });
}

async function loadRulesText() {
  if ($('rulesJson').dataset.loaded) return;
  const r = await api('/rules');
  $('rulesJson').value = r.json ?? '';
  $('rulesJson').placeholder = 'No rules yet. Click Suggest for a starting point.';
  $('rulesJson').dataset.loaded = '1';
}

async function saveRules() {
  // The body is the file's text as typed (comments and all), not re-serialized JSON.
  const response = await fetch('/api/rules', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: $('rulesJson').value });
  if (!response.ok) { toast((await response.json().catch(() => ({}))).error ?? 'Saving failed.', 'error'); return; }
  toast('Rules saved.');
  checkRules();
}

async function checkRules({ quiet = false } = {}) {
  let result;
  try { result = await api('/rules/check'); } catch { return; }
  const hadViolations = (state.rules?.violations.length ?? 0) > 0;
  state.rules = result;
  const list = $('rulesViolations');
  $('rulesSummary').textContent = !result.hasRules ? 'No rules yet.' : result.violations.length ? `${result.violations.length} violation${result.violations.length === 1 ? '' : 's'}` : 'All rules hold.';
  $('rulesSummary').style.color = result.violations.length ? 'var(--bad)' : '';
  list.className = result.violations.length ? '' : 'muted';
  list.innerHTML = result.problems.map(p => `<div class="finding-item warning"><div>${escapeHtml(p)}</div></div>`).join('')
    + (result.violations.map(v => `<div class="finding-item critical clickable" data-go="${escapeHtml(v.source)}" data-target="${escapeHtml(v.target)}">
        <span class="rule">rule ${v.rule + 1} · ${escapeHtml(v.kind)}${v.file ? ` · ${escapeHtml(shortPath(v.file))}:${v.line}` : ''}</span><div>${escapeHtml(v.message)}</div></div>`).join('')
      || (result.hasRules ? 'Every rule holds.' : 'Write rules on the left, or click Suggest.'));
  list.querySelectorAll('[data-go]').forEach(el => el.addEventListener('click', () => {
    if (state.view.kind !== 'types') graph.goHome();
    graph.highlightSet([el.dataset.go, el.dataset.target]);
  }));
  // Draw the broken edges in red.
  if (state.view.kind === 'types' && (hadViolations || result.violations.length)) graph.render();
  if (!quiet && result.violations.length) toast(`${result.violations.length} architecture rule violation(s), drawn in red.`);
}

// ---------------------------------------------------------------- stack traces and logs

function initTrace() {
  $('tracePane').innerHTML = `
    <div class="row">
      <button id="traceShow" class="primary" title="Draw this stack trace on the map">Show trace</button>
      <button id="traceScan" title="Find and group every exception in a log">Scan as log</button>
      <input id="tracePath" type="text" class="grow" placeholder="…or a log file path (relative to the code, or absolute)" spellcheck="false">
      <button id="traceClear">Clear</button>
      <span id="traceSummary" class="muted"></span>
    </div>
    <div class="cols two">
      <div class="box tall"><h4>Paste an exception, stack trace or log</h4><textarea id="traceText" spellcheck="false" class="code-input" placeholder="System.InvalidOperationException: Out of stock&#10;   at Shop.Inventory.Reserve(Product p) in …:line 12&#10;   at Shop.OrderService.CheckoutAsync(Order order) …"></textarea></div>
      <div class="box tall"><h4 id="traceResultTitle">Result</h4><div id="traceResult" class="muted">The frames from this code light up on the map, innermost first.</div></div>
    </div>`;
  $('traceShow').addEventListener('click', showTrace);
  $('traceScan').addEventListener('click', scanLog);
  $('traceClear').addEventListener('click', () => {
    state.trace = { frames: [], active: 0 };
    state.incidents = null;
    if (state.overlay === 'incidents') { state.overlay = 'kind'; $('overlay').value = 'kind'; graph.applyOverlay(); }
    $('traceResult').innerHTML = '';
    $('traceSummary').textContent = '';
    graph.applyRuntime();
  });
}

async function showTrace() {
  const text = $('traceText').value;
  if (!text.trim()) { toast('Paste a stack trace first.'); return; }
  try { drawTrace(await api('/trace', { method: 'POST', body: { text } })); }
  catch (e) { toast(e.message, 'error'); }
}

function drawTrace(parsed, { title } = {}) {
  const mine = parsed.stack.filter(f => f.typeId);
  state.trace = { frames: parsed.stack.map(f => ({ ...f, name: f.method })), active: parsed.stack.findIndex(f => f.typeId) };
  if (state.view.kind !== 'types' && mine.length) graph.goHome();
  for (const f of mine) graph.ensureVisible(f);
  graph.applyRuntime();
  if (mine[0]) { graph.select(mine[0].memberId ?? mine[0].typeId, { center: true }); }
  $('traceSummary').textContent = `${mine.length} of ${parsed.stack.length} frames are in this code`;
  $('traceResultTitle').textContent = title ?? parsed.exceptions.map(e => e.type).join(' ← ');
  $('traceResult').className = '';
  $('traceResult').innerHTML = parsed.exceptions.map(e => `<div class="finding-item critical"><span class="rule">${escapeHtml(e.type)}</span><div>${escapeHtml(e.message ?? '')}</div></div>`).join('')
    + `<ol class="frames">${parsed.stack.map((f, i) => `<li class="${f.typeId ? 'clickable mine' : 'muted'}" data-i="${i}">${f.typeId ? escapeHtml(display(f.memberId ?? f.typeId)) : escapeHtml(f.method)}${f.line ? ` <span class="muted">:${f.line}</span>` : ''}</li>`).join('')}</ol>`;
  $('traceResult').querySelectorAll('li.mine').forEach(li => li.addEventListener('click', () => {
    const f = parsed.stack[Number(li.dataset.i)];
    state.trace.active = Number(li.dataset.i);
    graph.applyRuntime();
    emit('go', f.memberId ?? f.typeId);
  }));
}

async function scanLog() {
  const path = $('tracePath').value.trim();
  const text = $('traceText').value;
  if (!path && !text.trim()) { toast('Paste a log, or give a log file path.'); return; }
  $('traceSummary').textContent = 'Scanning…';
  let scan;
  try { scan = await api('/trace/log', { method: 'POST', body: path ? { path } : { text } }); }
  catch (e) { $('traceSummary').textContent = ''; toast(e.message, 'error'); return; }
  state.incidents = new Map(Object.entries(scan.typeHits));
  state.overlay = 'incidents';
  $('overlay').value = 'incidents';
  graph.applyOverlay();
  $('traceSummary').textContent = `${scan.failures} failure${scan.failures === 1 ? '' : 's'} in ${scan.lines} lines, ${scan.groups.length} distinct`;
  $('traceResultTitle').textContent = 'Failures, most frequent first';
  $('traceResult').className = '';
  $('traceResult').innerHTML = scan.groups.map((g, i) => `<div class="finding-item critical clickable" data-i="${i}">
      <span class="rule">${g.count}× · lines ${g.firstLine}${g.lastLine !== g.firstLine ? `–${g.lastLine}` : ''}</span>
      <div><b>${escapeHtml(g.title)}</b> ${escapeHtml(g.message ?? '')}</div>
      <div class="muted">${escapeHtml(g.sample.stack.filter(f => f.typeId).slice(0, 3).map(f => display(f.memberId ?? f.typeId)).join(' ← ') || 'no frames from this code')}</div></div>`).join('')
    || '<p class="muted">No exceptions found.</p>';
  $('traceResult').querySelectorAll('[data-i]').forEach(el => el.addEventListener('click', () => {
    const g = scan.groups[Number(el.dataset.i)];
    drawTrace(g.sample, { title: `${g.count}× ${g.title}` });
  }));
}
