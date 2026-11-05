// The Live tab: sample a running process a few times a second (no debugger, no pausing) and
// light up whatever is executing. Calls seen at runtime that the code alone can't show, such as
// the actual class behind an interface, appear as new cyan edges.
import { state, on, emit, api, escapeHtml, memberLabel, toast } from './state.js';
import * as graph from './graph.js';

const $ = id => document.getElementById(id);
const DECAY = 0.92;
let lastRender = 0;

export function initLive() {
  const pane = $('livePane');
  pane.innerHTML = `
    <div class="row">
      <select id="liveProcess" class="grow" title=".NET processes on this machine"><option value="">Pick a running .NET process…</option></select>
      <button id="liveRefresh" title="Refresh the process list">⟳</button>
      <label class="slider">every <input id="liveInterval" type="number" min="50" max="5000" step="50" value="250" style="width:70px"> ms</label>
      <button id="liveStart" class="primary">◉ Watch</button>
      <button id="liveStop" disabled>■ Stop</button>
      <button id="liveHeap" disabled title="Count live instances of each class (walks the heap)">Count instances</button>
      <button id="liveReset" title="Forget what's been seen so far">Reset</button>
    </div>
    <div class="cols two">
      <div class="box"><h4>Hottest code <span id="liveSamples" class="muted"></span></h4><table class="table" id="liveHot"></table></div>
      <div class="box"><h4>Instances on the heap</h4><table class="table" id="liveHeapTable"><tr><td class="muted">Click "Count instances" while watching.</td></tr></table></div>
    </div>
    <p class="muted" style="margin:8px 0 0">Live view reads the process's stacks from a snapshot several times a second. It never stops the program, so it's safe on anything you're allowed to inspect. It sees code that's <i>running</i>: an async method waiting on I/O doesn't show until it resumes.</p>`;

  $('liveRefresh').addEventListener('click', refreshProcesses);
  $('liveStart').addEventListener('click', start);
  $('liveStop').addEventListener('click', stop);
  $('liveHeap').addEventListener('click', countInstances);
  $('liveReset').addEventListener('click', reset);
  on('show-tab', tab => { if (tab === 'live') refreshProcesses(); });
  on('sse:live.started', d => setWatching(d.processId));
  on('sse:live.stopped', d => {
    setWatching(null);
    if (d.reason && d.reason !== 'stopped') toast(`Live view stopped: ${d.reason}`);
  });
  on('sse:live.sample', onSample);
  refreshProcesses();
}

async function refreshProcesses() {
  try {
    const processes = await api('/live/processes');
    const select = $('liveProcess');
    const previous = select.value;
    const roots = (state.graph?.projects ?? []).map(p => p.name.toLowerCase());
    select.innerHTML = '<option value="">Pick a running .NET process…</option>' + processes.map(p => {
      const ours = roots.some(r => (p.name ?? '').toLowerCase() === r || (p.mainModule ?? '').toLowerCase().includes(r));
      return `<option value="${p.id}">${ours ? '★ ' : ''}${escapeHtml(p.name)} (${p.id})${p.mainModule ? ' · ' + escapeHtml(p.mainModule) : ''}</option>`;
    }).join('');
    if (previous && [...select.options].some(o => o.value === previous)) select.value = previous;
    else {
      // Pick the first process that looks like it belongs to this codebase.
      const mine = [...select.options].find(o => o.textContent.startsWith('★'));
      if (mine) select.value = mine.value;
    }
  } catch (e) { toast(e.message, 'error'); }
}

async function start() {
  const processId = Number($('liveProcess').value);
  if (!processId) { toast('Pick a process first. Start your app, then click ⟳.', 'error'); return; }
  reset();
  try { await api('/live/start', { method: 'POST', body: { processId, intervalMs: Number($('liveInterval').value) || 250 } }); }
  catch (e) { toast(e.message, 'error'); }
}

async function stop() {
  await api('/live/stop', { method: 'POST' }).catch(() => { });
}

function setWatching(processId) {
  state.live.processId = processId;
  $('liveStart').disabled = !!processId;
  $('liveStop').disabled = !processId;
  $('liveHeap').disabled = !processId;
  $('liveProcess').disabled = !!processId;
  const badge = $('debugState');
  if (processId) {
    badge.textContent = 'live';
    badge.className = 'state-badge live';
    $('statusPill').innerHTML = `<span class="pulse"></span> Watching process ${processId}`;
    if (state.overlay !== 'live') { state.overlay = 'live'; $('overlay').value = 'live'; }
    graph.render();
  } else {
    state.live.active.clear();
    graph.applyRuntime();
    if (state.debug.state === 'idle') { badge.textContent = 'idle'; badge.className = 'state-badge'; $('statusPill').innerHTML = ''; }
  }
}

function reset() {
  state.live.heat.clear();
  state.live.typeHeat.clear();
  state.live.observed.clear();
  state.live.active.clear();
  state.live.heap.clear();
  state.live.samples = 0;
  graph.render();
  $('liveHot').innerHTML = '';
}

function onSample(sample) {
  const live = state.live;
  live.samples = sample.number;
  for (const [k, v] of live.heat) live.heat.set(k, v * DECAY);
  for (const [k, v] of live.typeHeat) live.typeHeat.set(k, v * DECAY);
  for (const [k, v] of Object.entries(sample.memberHits)) live.heat.set(k, (live.heat.get(k) ?? 0) + v);
  for (const [k, v] of Object.entries(sample.typeHits)) live.typeHeat.set(k, (live.typeHeat.get(k) ?? 0) + v);

  // What's on a stack right now, innermost frame of each thread first.
  live.active.clear();
  for (const thread of sample.threads)
    for (const frame of thread.stack.slice(0, 3)) live.active.add(frame.memberId ?? frame.typeId);

  let newEdges = false;
  for (const call of sample.observedCalls) {
    const key = `${call.sourceMember}\u0000${call.targetMember}`;
    if (!live.observed.has(key)) newEdges = true;
    live.observed.set(key, (live.observed.get(key) ?? 0) + call.count);
  }

  const now = performance.now();
  if (newEdges && state.view.kind === 'types' && hasNewTypePair()) graph.render();
  else if (now - lastRender > 200) { graph.applyOverlay(); graph.applyRuntime(); }
  else graph.applyRuntime();
  lastRender = now;
  renderHot();
}

let knownPairs = new Set();
function hasNewTypePair() {
  let added = false;
  for (const key of state.live.observed.keys()) {
    const [a, b] = key.split('\u0000');
    const pair = `${state.memberType.get(a)}\u0000${state.memberType.get(b)}`;
    if (!knownPairs.has(pair)) { knownPairs.add(pair); added = true; }
  }
  return added;
}

function renderHot() {
  const rows = [...state.live.heat.entries()].sort((a, b) => b[1] - a[1]).slice(0, 12);
  const max = rows[0]?.[1] ?? 1;
  $('liveSamples').textContent = `· ${state.live.samples} samples`;
  $('liveHot').innerHTML = rows.map(([id, heat]) => {
    const m = state.members.get(id);
    const t = state.types.get(state.memberType.get(id));
    return `<tr class="clickable" data-go="${escapeHtml(id)}"><td>${escapeHtml(t?.name ?? '?')}.${escapeHtml(memberLabel(m))}</td>
      <td style="width:40%"><div class="bar" style="width:${Math.max(3, heat / max * 100)}%"></div></td></tr>`;
  }).join('');
  $('liveHot').querySelectorAll('[data-go]').forEach(tr => tr.addEventListener('click', () => emit('go', tr.dataset.go)));
}

async function countInstances() {
  if (!state.live.processId) return;
  $('liveHeap').disabled = true;
  $('liveHeapTable').innerHTML = '<tr><td class="muted">Walking the heap…</td></tr>';
  try {
    const r = await api('/live/heap?processId=' + state.live.processId);
    state.live.heap = new Map(r.counts.map(c => [c.typeId, c]));
    $('liveHeapTable').innerHTML = `<tr><th>Type</th><th class="num">Alive</th><th class="num">Bytes</th></tr>` + r.counts.slice(0, 50).map(c =>
      `<tr class="clickable" data-go="${escapeHtml(c.typeId)}"><td>${escapeHtml(state.types.get(c.typeId)?.name ?? c.typeId)}</td><td class="num">${c.count.toLocaleString()}</td><td class="num">${c.bytes.toLocaleString()}</td></tr>`).join('')
      + (r.complete ? '' : `<tr><td colspan="3" class="muted">Stopped after ${r.objectsSeen.toLocaleString()} objects (time limit).</td></tr>`);
    $('liveHeapTable').querySelectorAll('[data-go]').forEach(tr => tr.addEventListener('click', () => emit('go', tr.dataset.go)));
    graph.applyOverlay();
  } catch (e) {
    $('liveHeapTable').innerHTML = `<tr><td style="color:var(--bad)">${escapeHtml(e.message)}</td></tr>`;
  } finally { $('liveHeap').disabled = !state.live.processId; }
}
