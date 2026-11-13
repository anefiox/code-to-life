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
      <label class="check" title="Also listen to the runtime's events: how long async code waits, exceptions thrown, allocations, HTTP calls"><input type="checkbox" id="liveEvents" checked> Runtime events</label>
      <button id="liveStart" class="primary">◉ Watch</button>
      <button id="liveStop" disabled>■ Stop</button>
      <button id="liveHeap" disabled title="Count live instances of each class (walks the heap)">Count instances</button>
      <button id="liveReset" title="Forget what's been seen so far">Reset</button>
    </div>
    <div class="cols two">
      <div class="box"><h4>Hottest code <span id="liveSamples" class="muted"></span></h4><table class="table" id="liveHot"></table></div>
      <div class="box"><h4>Instances on the heap</h4><table class="table" id="liveHeapTable"><tr><td class="muted">Click "Count instances" while watching.</td></tr></table></div>
    </div>
    <div class="cols four" id="liveEventBoxes" hidden>
      <div class="box"><h4>Waiting (awaits) <span class="muted" id="liveWaitNote"></span></h4><table class="table" id="liveWaits"></table></div>
      <div class="box"><h4>Exceptions thrown</h4><table class="table" id="liveThrows"></table></div>
      <div class="box"><h4>Allocating most</h4><table class="table" id="liveAllocs"></table></div>
      <div class="box"><h4>HTTP calls out</h4><table class="table" id="liveHttp"></table></div>
    </div>
    <p class="muted" style="margin:8px 0 0">Live view reads the process's stacks from a snapshot several times a second and never stops the program. Stack samples see code that's <i>running</i>; with <b>Runtime events</b> on, it also listens to the runtime's EventPipe stream, so time spent <i>awaiting</i> shows up in blue, along with exceptions, allocations and HTTP calls.</p>`;

  $('liveRefresh').addEventListener('click', refreshProcesses);
  $('liveStart').addEventListener('click', start);
  $('liveStop').addEventListener('click', stop);
  $('liveHeap').addEventListener('click', countInstances);
  $('liveReset').addEventListener('click', reset);
  on('show-tab', tab => { if (tab === 'live') refreshProcesses(); });
  on('sse:live.started', d => {
    setWatching(d.processId);
    $('liveEventBoxes').hidden = !d.events;
    if (d.eventsError) toast(`Runtime events aren't available: ${d.eventsError}`, 'error');
  });
  on('sse:live.events', onEvents);
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
  try { await api('/live/start', { method: 'POST', body: { processId, intervalMs: Number($('liveInterval').value) || 250, events: $('liveEvents').checked } }); }
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
  state.live.waits.clear();
  state.live.typeWaits.clear();
  state.live.events = null;
  graph.render();
  $('liveHot').innerHTML = '';
  for (const id of ['liveWaits', 'liveThrows', 'liveAllocs', 'liveHttp']) $(id).innerHTML = '';
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

const nameOf = (memberId, typeId) => memberId && state.members.has(memberId)
  ? `${state.types.get(state.memberType.get(memberId))?.name ?? '?'}.${memberLabel(state.members.get(memberId))}`
  : state.types.get(typeId)?.name ?? typeId ?? '(not this code)';
const ms = v => v >= 1000 ? `${(v / 1000).toFixed(1)} s` : `${Math.round(v)} ms`;
const bytes = b => b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(1)} GB` : b >= 1 << 20 ? `${(b / (1 << 20)).toFixed(1)} MB` : `${Math.round(b / 1024)} KB`;

/** Totals from the runtime's event stream, about once a second. */
function onEvents(e) {
  const live = state.live;
  live.events = e;
  live.waits = new Map(e.waits.filter(w => w.memberId).map(w => [w.memberId, w.totalMs]));
  live.typeWaits = new Map();
  for (const w of e.waits) live.typeWaits.set(w.typeId, (live.typeWaits.get(w.typeId) ?? 0) + w.totalMs);
  $('liveEventBoxes').hidden = false;
  $('liveWaitNote').textContent = e.note ? '· ' + e.note : `· ${Math.round(e.seconds)} s`;

  const row = (go, label, value, title = '') => `<tr class="${go ? 'clickable' : ''}" ${go ? `data-go="${escapeHtml(go)}"` : ''} title="${escapeHtml(title)}"><td>${label}</td><td class="num">${value}</td></tr>`;
  $('liveWaits').innerHTML = e.waits.slice(0, 10).map(w => row(w.memberId ?? w.typeId, escapeHtml(nameOf(w.memberId, w.typeId)),
    `${ms(w.totalMs)}<span class="muted"> · ${w.count}× · max ${ms(w.maxMs)}</span>`)).join('') || '<tr><td class="muted">No awaits seen in this code yet.</td></tr>';
  $('liveThrows').innerHTML = e.exceptions.slice(0, 10).map(x => row(x.memberId ?? x.typeId, `${escapeHtml(x.exceptionType.split('.').pop())} <span class="muted">in ${escapeHtml(nameOf(x.memberId, x.typeId))}</span>`, `${x.count}×`, x.message ?? '')).join('')
    || '<tr><td class="muted">Nothing thrown.</td></tr>';
  $('liveAllocs').innerHTML = (e.allocators.length
    ? e.allocators.slice(0, 8).map(a => row(a.memberId ?? a.typeId, escapeHtml(nameOf(a.memberId, a.typeId)), bytes(a.bytes)))
    : e.allocations.slice(0, 8).map(a => row(a.typeId, escapeHtml(a.typeName), bytes(a.bytes)))).join('')
    || '<tr><td class="muted">Nothing sampled yet.</td></tr>';
  $('liveHttp').innerHTML = e.http.slice(0, 8).map(h => row(null, escapeHtml(h.request), `${h.count}× · avg ${ms(h.averageMs)}${h.failures ? ` · <span style="color:var(--bad)">${h.failures} failed</span>` : ''}`)).join('')
    || `<tr><td class="muted">No outgoing HTTP${e.contentions ? `; ${e.contentions} lock contentions` : ''}.</td></tr>`;
  for (const id of ['liveWaits', 'liveThrows', 'liveAllocs'])
    $(id).querySelectorAll('[data-go]').forEach(tr => tr.addEventListener('click', () => emit('go', tr.dataset.go)));
  if (state.overlay === 'live') graph.applyOverlay();
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
