// The "What changed" tab: PR Buddy's semantic diff of this branch, painted on the graph.
import { state, on, emit, api, escapeHtml, toast } from './state.js';
import * as graph from './graph.js';

const $ = id => document.getElementById(id);

export function initChanges() {
  const pane = $('changesPane');
  pane.innerHTML = `
    <div class="row">
      <label class="select">Compare with <input id="chBase" type="text" placeholder="main (auto)" style="width:140px"></label>
      <label class="check"><input type="checkbox" id="chUncommitted" checked> Include uncommitted work</label>
      <button id="chRun" class="primary">± Show changes</button>
      <button id="chClear">Clear</button>
      <span id="chSummary" class="muted"></span>
    </div>
    <div id="chBody"></div>`;
  $('chRun').addEventListener('click', run);
  $('chClear').addEventListener('click', () => {
    state.changes = null;
    $('chBody').innerHTML = '';
    $('chSummary').textContent = '';
    if (state.overlay === 'changes') { state.overlay = 'kind'; $('overlay').value = 'kind'; }
    graph.applyOverlay();
  });
  on('status', s => {
    if (!s.changesAvailable) $('chBody').innerHTML = `<div class="notice">The changes view uses <b>PR Buddy</b>. Clone <code>https://github.com/anefiox/pr-buddy</code> next to this repository and rebuild to turn it on.</div>`;
  });
  on('graph', () => { if (state.changes) run({ quiet: true }); });
}

async function run({ quiet = false } = {}) {
  const base = $('chBase').value.trim();
  const uncommitted = $('chUncommitted').checked;
  if (!quiet) $('chSummary').textContent = 'Comparing…';
  let report;
  try { report = await api(`/changes?base=${encodeURIComponent(base)}&uncommitted=${uncommitted}`); }
  catch (e) { $('chSummary').textContent = ''; toast(e.message, 'error'); return; }

  if (!report.available) {
    $('chSummary').textContent = '';
    $('chBody').innerHTML = `<div class="notice">${escapeHtml(report.message)}</div>`;
    return;
  }
  state.changes = report;
  state.overlay = 'changes';
  $('overlay').value = 'changes';
  graph.applyOverlay();

  const counts = {};
  for (const item of report.items) counts[item.change] = (counts[item.change] ?? 0) + 1;
  $('chSummary').innerHTML = `${escapeHtml(report.base)} → ${escapeHtml(uncommitted ? 'working tree' : report.head)} · ` +
    (Object.entries(counts).map(([k, v]) => `<span style="color:${graph.changeColor(k)}">${v} ${k}</span>`).join(' · ') || 'no member-level changes') +
    (report.message ? ` · ${escapeHtml(report.message)}` : '');

  const findings = report.findings.slice().sort((a, b) => sev(b.severity) - sev(a.severity));
  $('chBody').innerHTML = `
    <div class="cols">
      <div class="box"><h4>Files, riskiest first</h4><table class="table">${report.files.map(f => `<tr><td>${escapeHtml(f.path)}</td><td class="num muted">${f.status}</td><td class="num" title="real changes (formatting ignored)">${f.realChanges}</td><td class="num" title="risk">${f.risk}</td></tr>`).join('') || '<tr><td class="muted">none</td></tr>'}</table></div>
      <div class="box"><h4>Changed members</h4><table class="table">${report.items.map(i => `<tr class="clickable" data-go="${escapeHtml(i.memberId ?? i.typeId)}"><td><span style="color:${graph.changeColor(i.change)}">●</span> ${escapeHtml(i.displayName)}</td><td class="muted">${i.change}${i.publicApi ? ' · public API' : ''}</td></tr>`).join('') || '<tr><td class="muted">none</td></tr>'}</table></div>
      <div class="box"><h4>Things to look at (${findings.length})</h4>${findings.map(f => `<div class="finding-item ${f.severity} ${f.typeId ? 'clickable' : ''}" ${f.typeId ? `data-go="${escapeHtml(f.memberId ?? f.typeId)}"` : ''} style="${f.typeId ? 'cursor:pointer' : ''}"><span class="rule">${escapeHtml(f.ruleId)} · ${f.severity}${f.path ? ` · ${escapeHtml(f.path)}:${f.line ?? ''}` : ''}</span><div>${escapeHtml(f.message)}</div></div>`).join('') || '<p class="muted">Nothing flagged.</p>'}</div>
    </div>`;
  $('chBody').querySelectorAll('[data-go]').forEach(el => el.addEventListener('click', () => emit('go', el.dataset.go)));
  emit('changes-rendered', report);
  if (!quiet) toast(`${report.items.length} changed members across ${report.files.length} files.`);
}

const sev = s => ({ critical: 3, warning: 2, info: 1 }[s] ?? 0);
