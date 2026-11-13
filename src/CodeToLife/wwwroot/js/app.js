// Wires the page together: loads the graph, listens to the server's event stream, and owns the
// top bar, dock tabs, keyboard shortcuts and modal.
import { state, on, emit, api, indexGraph, escapeHtml, memberLabel, toast } from './state.js';
import * as graph from './graph.js';
import { initDetails, initSearch, initSidebar, initOverlays, goTo, clearSourceCache } from './panels.js';
import { initDebug, refreshStatus } from './debug.js';
import { initLive } from './live.js';
import { initChanges } from './changes.js';
import { initPlan } from './plan.js';
import { initInsights } from './insights.js';

const $ = id => document.getElementById(id);

async function main() {
  applyTheme(localGet('theme') ?? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'));
  const cy = graph.init($('cy'));
  window.codeToLife = { cy, state }; // handy from the browser console
  $('cy').addEventListener('contextmenu', e => e.preventDefault());
  initDetails();
  initSearch();
  initSidebar();
  initOverlays();
  initDebug();
  initLive();
  initChanges();
  initInsights();
  initTopbar();
  initDock();
  initModal();
  on('go', goTo);
  on('view', renderCrumbs);
  on('rendered', ({ nodes }) => {
    $('empty').hidden = nodes > 0 || !state.graph;
    $('empty').textContent = 'Nothing to show with these filters.';
  });
  on('focus', depth => { $('focusDepth').textContent = depth === 0 ? 'all' : depth; });
  on('output', appendOutput);
  connectEvents();
  await refreshStatusInfo();
  await loadGraph({ fit: true });
  await initPlan();
}

async function loadGraph({ fit = false } = {}) {
  try {
    const g = await api('/graph');
    const first = !state.graph;
    indexGraph(g);
    clearSourceCache();
    $('projectName').textContent = g.name;
    $('projectName').title = g.root;
    document.title = `${g.name} · Code to Life`;
    $('loading').hidden = true;
    // Big solutions: start with test projects hidden, they double the picture.
    if (first && g.types.length > 250) { state.options.hideTests = true; $('hideTests').checked = true; }
    if (state.view.kind === 'members' && !state.types.has(state.view.typeId)) state.view = { kind: 'types' };
    if (state.view.kind === 'calls' && !state.members.has(state.view.memberId)) state.view = { kind: 'types' };
    emit('graph', g);
    graph.render({ relayout: first, fit });
    renderCrumbs();
  } catch (e) {
    if (String(e.message).includes('analyzing') || String(e.message).includes('Still')) {
      setTimeout(() => loadGraph({ fit }), 600);
      return;
    }
    $('loading').innerHTML = `<span style="color:var(--bad)">${escapeHtml(e.message)}</span>`;
  }
}

async function refreshStatusInfo() {
  try {
    state.status = await api('/status');
    state.debug.breakpoints = state.status.debug.breakpoints ?? {};
    emit('status', state.status);
    refreshStatus();
    if (state.status.live) emit('sse:live.started', { processId: state.status.live.processId });
    // Reloaded while the debugger is paused: pick the stack back up once the graph is in.
    const debug = state.status.debug;
    if (debug.state === 'stopped' && debug.frames?.length) on('graph', () => emit('sse:debug.stopped', { reason: 'paused', frames: debug.frames, locals: [] }));
    else if (debug.state !== 'idle') emit('sse:debug.state', { state: debug.state });
  } catch { }
}

function connectEvents() {
  const source = new EventSource('/api/events');
  const forward = type => source.addEventListener(type, e => emit('sse:' + type, JSON.parse(e.data)));
  for (const type of ['debug.state', 'debug.stopped', 'debug.exited', 'debug.output', 'debug.breakpoints', 'debug.breakpoint', 'debug.animating', 'debug.error',
    'live.started', 'live.stopped', 'live.sample', 'live.events', 'plan.changed', 'rules.changed']) forward(type);
  source.addEventListener('graph.analyzing', () => { $('reload').classList.add('spinning'); $('reload').textContent = '…'; });
  source.addEventListener('graph.changed', () => {
    $('reload').textContent = '⟳';
    loadGraph();
  });
  source.addEventListener('graph.error', e => { $('reload').textContent = '⟳'; toast('Analysis failed: ' + JSON.parse(e.data).message, 'error'); });
  source.onerror = () => { $('statusPill').innerHTML = '<span style="color:var(--bad)">●</span> Lost the connection to Code to Life. Retrying…'; };
  source.onopen = () => { if ($('statusPill').textContent.includes('Lost')) $('statusPill').innerHTML = ''; };
}

function initTopbar() {
  $('layout').addEventListener('change', e => { state.layout = e.target.value; graph.runLayout('full', { animated: true }); });
  $('overlay').addEventListener('change', e => {
    state.overlay = e.target.value;
    if (state.overlay === 'changes' && !state.changes) emit('show-tab', 'changes');
    if (state.overlay === 'live' && !state.live.processId) emit('show-tab', 'live');
    if (state.overlay === 'incidents' && !state.incidents) emit('show-tab', 'trace');
    graph.applyOverlay();
  });
  $('reload').addEventListener('click', () => api('/reload', { method: 'POST' }).catch(e => toast(e.message, 'error')));
  $('themeToggle').addEventListener('click', () => applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light'));
  $('fit').addEventListener('click', () => graph.getCy().fit(undefined, 40));
  $('focusMore').addEventListener('click', () => {
    if (!state.selected) { toast('Select something to focus on first.'); return; }
    graph.setFocusDepth(state.focusDepth + 1);
  });
  $('focusLess').addEventListener('click', () => graph.setFocusDepth(Math.max(0, state.focusDepth - 1)));
  $('exportPng').addEventListener('click', graph.exportPng);

  document.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); $('search').focus(); $('search').select(); return; }
    if (e.target.matches('input, textarea, select') || $('modal').open) return;
    if (e.key === 'Escape') {
      if (state.focusDepth > 0) graph.setFocusDepth(0);
      else if (state.selected && state.view.kind === 'types') graph.select(null);
      else graph.back();
    } else if (e.key === 'f' || e.key === 'F') graph.getCy().fit(undefined, 40);
    else if (e.key === 'Backspace') graph.back();
  });
}

function renderCrumbs() {
  const crumbs = $('crumbs');
  if (state.view.kind === 'types') { crumbs.innerHTML = ''; return; }
  const label = state.view.kind === 'members'
    ? `${state.types.get(state.view.typeId)?.name ?? '?'}: methods & calls`
    : (() => { const m = state.members.get(state.view.memberId); return `calls from ${state.types.get(state.memberType.get(state.view.memberId))?.name}.${memberLabel(m)}`; })();
  crumbs.innerHTML = `<button id="crumbHome">All types</button>${state.viewStack.length > 1 ? '<span class="sep">›</span><button id="crumbBack">back</button>' : ''}<span class="sep">›</span><span class="here">${escapeHtml(label)}</span>`;
  $('crumbHome').addEventListener('click', graph.goHome);
  $('crumbBack')?.addEventListener('click', graph.back);
}

function initDock() {
  const tabs = document.querySelectorAll('.dock-tabs button[data-tab]');
  const show = name => {
    tabs.forEach(t => t.classList.toggle('active', t.dataset.tab === name));
    document.querySelectorAll('.dock-pane').forEach(p => p.classList.toggle('active', p.dataset.pane === name));
    $('dock').classList.remove('collapsed');
    $('dockToggle').textContent = '▾';
    localSet('tab', name);
  };
  tabs.forEach(t => t.addEventListener('click', () => { show(t.dataset.tab); emit('show-tab', t.dataset.tab); }));
  on('show-tab', show);
  $('dockToggle').addEventListener('click', () => {
    const collapsed = $('dock').classList.toggle('collapsed');
    $('dockToggle').textContent = collapsed ? '▴' : '▾';
    setTimeout(() => graph.getCy().resize(), 50);
  });
  const saved = localGet('tab');
  if (saved) show(saved);
}

function appendOutput({ category, output }) {
  const out = $('output');
  const span = document.createElement('span');
  span.className = category;
  span.textContent = output;
  out.appendChild(span);
  while (out.childNodes.length > 2000) out.firstChild.remove();
  out.parentElement.scrollTop = out.parentElement.scrollHeight;
}

function initModal() {
  const dialog = $('modal');
  on('modal', ({ title, html, wire }) => {
    $('modalTitle').textContent = title;
    $('modalBody').innerHTML = html;
    if (!dialog.open) dialog.showModal();
    wire?.($('modalBody'), () => dialog.close());
  });
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  localSet('theme', theme);
  emit('theme', theme);
}

function localGet(key) { try { return localStorage.getItem('ctl.' + key); } catch { return null; } }
function localSet(key, value) { try { localStorage.setItem('ctl.' + key, value); } catch { } }

main();
