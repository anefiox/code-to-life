// Everything around the canvas: details, source, search, sidebar filters, tooltips and menus.
import { state, on, emit, api, typeOf, escapeHtml, shortPath, fileName, memberLabel, isTestProject, toast } from './state.js';
import * as graph from './graph.js';
import { toggleBreakpoint, breakpointsIn } from './debug.js';

const $ = id => document.getElementById(id);
const sourceCache = new Map();

// ---------------------------------------------------------------- details

export function initDetails() {
  on('select', id => showDetails(id));
  on('graph', () => { renderSummary(); if (state.selected) showDetails(state.selected); });
  on('debug-stopped', () => { if (state.selected) showDetails(state.selected, { keepScroll: true }); });
  on('breakpoints', () => { if (state.selected) showDetails(state.selected, { keepScroll: true }); });
}

function renderSummary() {
  const g = state.graph;
  if (!g) return;
  const members = g.types.reduce((n, t) => n + t.members.length, 0);
  const complex = g.types.flatMap(t => t.members.map(m => ({ t, m }))).sort((a, b) => b.m.complexity - a.m.complexity).slice(0, 5);
  const hubs = g.types.slice().sort((a, b) => (b.metrics.incomingCount) - (a.metrics.incomingCount)).slice(0, 5);
  $('summary').innerHTML = `
    <div class="stat"><b>${g.types.length}</b>types</div>
    <div class="stat"><b>${members}</b>members</div>
    <div class="stat"><b>${g.projects.length}</b>project${g.projects.length === 1 ? '' : 's'}</div>
    <div class="stat"><b>${g.calls.length}</b>call paths</div>
    <div style="grid-column:1/-1">
      <h3 style="margin-top:12px">Most depended on</h3>
      <ul class="rel">${hubs.map(t => `<li><span class="k">${t.metrics.incomingCount} users</span><a data-go="${escapeHtml(t.id)}">${escapeHtml(t.name)}</a></li>`).join('')}</ul>
      <h3 style="margin-top:12px">Most complex methods</h3>
      <ul class="rel">${complex.map(({ t, m }) => `<li><span class="k">cx ${m.complexity}</span><a data-go="${escapeHtml(m.id)}">${escapeHtml(t.name)}.${escapeHtml(memberLabel(m))}</a></li>`).join('')}</ul>
    </div>`;
  wireLinks($('summary'));
}

function wireLinks(root) {
  root.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => goTo(a.dataset.go)));
}

/** Show something wherever it lives: select it in this view, or switch to a view that has it. */
export function goTo(id) {
  const cy = graph.getCy();
  if (cy.getElementById(id).length) { graph.select(id); return; }
  if (state.members.has(id)) {
    const typeId = state.memberType.get(id);
    if (state.view.kind === 'folders') graph.goHome();
    if (state.view.kind === 'types') { graph.ensureVisible({ typeId }); graph.select(typeId); showDetails(id); return; }
    graph.openView({ kind: 'members', typeId });
    graph.select(id);
    return;
  }
  if (state.types.has(id)) {
    if (state.view.kind !== 'types') graph.goHome();
    graph.ensureVisible({ typeId: id });
    graph.select(id);
  }
}

async function showDetails(id, { keepScroll = false } = {}) {
  const body = $('detailsBody');
  const scroll = $('details').scrollTop;
  if (!id) { body.hidden = true; $('detailsEmpty').hidden = false; return; }
  const planned = state.plan.types.find(p => p.id === id);
  if (planned) { renderPlanned(planned); return; }
  const member = state.members.get(id);
  const type = state.types.get(member ? state.memberType.get(id) : id);
  if (!type) return;
  $('detailsEmpty').hidden = true;
  body.hidden = false;
  body.innerHTML = member ? memberHtml(type, member) : typeHtml(type);
  wireLinks(body);
  wireActions(body, type, member);
  emit('details', { body, type, member });
  const focusMember = member ?? null;
  await renderSource(body.querySelector('[data-source]'), type, focusMember);
  if (keepScroll) $('details').scrollTop = scroll;
}

function typeHtml(t) {
  const out = (state.edgesFrom.get(t.id) ?? []).filter(e => e.kind !== 'uses' && e.kind !== 'nested');
  const inc = (state.edgesTo.get(t.id) ?? []).filter(e => e.kind !== 'uses' && e.kind !== 'nested');
  const rel = (edges, dir) => dedupe(edges, dir).map(([other, kinds]) =>
    `<li><span class="k">${kinds.join(', ')}</span><a data-go="${escapeHtml(other)}">${escapeHtml(state.types.get(other)?.name ?? other)}</a></li>`).join('') || '<li class="muted">nothing</li>';
  const notes = state.plan.notes.filter(n => n.targetId === t.id);
  const changes = (state.changes?.items ?? []).filter(c => c.typeId === t.id);
  const findings = (state.changes?.findings ?? []).filter(f => f.typeId === t.id);
  const heap = state.live.heap.get(t.id);
  const live = state.live.typeHeat.get(t.id);
  return `
    <div class="d-head">
      <div class="kind"><span>${escapeHtml(t.accessibility)} ${escapeHtml(t.modifiers.join(' '))} ${escapeHtml(t.kind)}</span><span class="chip">${escapeHtml(t.project)}</span>
        ${heap ? `<span class="chip live">${heap.count} alive · ${formatBytes(heap.bytes)}</span>` : ''}
        ${live ? `<span class="chip live">seen in ${Math.round(live)} samples</span>` : ''}</div>
      <h2>${escapeHtml(t.name)}</h2>
      <div class="ns">${escapeHtml(t.namespace || '(global namespace)')} · <a data-editor>${escapeHtml(shortPath(t.file))}:${t.line}</a></div>
      ${t.summary ? `<p class="summary-text">${escapeHtml(t.summary)}</p>` : ''}
      <div class="actions">
        <button data-act="members" title="See its methods and what they call">Open up ⤵</button>
        <button data-act="focus" title="Show only this and what it touches">Focus</button>
        <button data-act="note">+ Note</button>
        <button data-act="impact" title="What would need to change, or be checked, if this changes">⚠ Impact</button>
        ${t.members.some(m => m.kind === 'method') ? '<button data-act="bp-all" title="Break on entry to every method">● Break on all</button>' : ''}
      </div>
    </div>
    <div data-explain></div>
    ${notes.map(n => `<div class="d-section"><div class="note-card">${escapeHtml(n.text)}</div></div>`).join('')}
    <div class="d-section">
      <div class="metrics">
        <div><b>${t.metrics.lines}</b>lines</div>
        <div><b>${t.members.length}</b>members</div>
        <div><b>${t.metrics.maxComplexity}</b>max cx</div>
        <div><b>${t.metrics.incomingCount}/${t.metrics.outgoingCount}</b>in/out</div>
      </div>
    </div>
    ${changes.length || findings.length ? `<div class="d-section"><h3>What changed</h3>${changes.map(c => `<div><span class="chip" style="color:${graph.changeColor(c.change)};border-color:${graph.changeColor(c.change)}">${c.change}</span> ${escapeHtml(c.displayName)}${c.publicApi ? ' <span class="chip warn">public API</span>' : ''}${c.detail ? ` <span class="muted">${escapeHtml(c.detail)}</span>` : ''}</div>`).join('')}
      ${findings.map(findingHtml).join('')}</div>` : ''}
    <div class="d-section">
      <h3>Members</h3>
      <ul class="members">${t.members.map(m => memberRow(m)).join('')}</ul>
    </div>
    <div data-tests></div>
    <div class="d-section"><h3>Depends on</h3><ul class="rel">${rel(out, 'target')}</ul></div>
    <div class="d-section"><h3>Used by</h3><ul class="rel">${rel(inc, 'source')}</ul></div>
    <div data-source></div>`;
}

function memberHtml(t, m) {
  const calls = (state.callsFrom.get(m.id) ?? []);
  // Callers through an interface or base member count too: that's how most real code is reached.
  const callers = (state.callsTo.get(m.id) ?? []).concat(
    m.implements.flatMap(i => (state.callsTo.get(i) ?? []).map(c => ({ ...c, kind: `via ${state.types.get(state.memberType.get(i))?.name ?? 'base'}` }))));
  const impls = state.implementers.get(m.id) ?? [];
  const link = id => {
    const mm = state.members.get(id);
    const tt = state.types.get(state.memberType.get(id));
    return `<a data-go="${escapeHtml(id)}">${escapeHtml(tt?.name ?? '?')}.${escapeHtml(memberLabel(mm))}</a>`;
  };
  const heat = state.live.heat.get(m.id);
  const findings = (state.changes?.findings ?? []).filter(f => f.memberId === m.id);
  const change = (state.changes?.items ?? []).find(c => c.memberId === m.id);
  const hasBp = breakpointsIn(m.file).some(l => l >= m.line && l <= m.endLine);
  return `
    <div class="d-head">
      <div class="kind"><span>${escapeHtml(m.accessibility)}${m.isStatic ? ' static' : ''}${m.isAbstract ? ' abstract' : ''}${m.isAsync ? ' async' : ''} ${escapeHtml(m.kind)}</span>
        ${m.complexity > 1 ? `<span class="chip ${m.complexity >= 25 ? 'bad' : m.complexity >= 10 ? 'warn' : ''}">complexity ${m.complexity}</span>` : ''}
        ${heat ? `<span class="chip live">seen in ${Math.round(heat)} samples</span>` : ''}
        ${change ? `<span class="chip" style="color:${graph.changeColor(change.change)};border-color:${graph.changeColor(change.change)}">${change.change}</span>` : ''}</div>
      <h2>${escapeHtml(memberLabel(m))}</h2>
      <div class="ns"><a data-go="${escapeHtml(t.id)}">${escapeHtml(t.name)}</a> · <a data-editor>${escapeHtml(shortPath(m.file))}:${m.line}</a></div>
      <p class="summary-text"><code>${escapeHtml(m.signature)}</code></p>
      ${m.summary ? `<p class="summary-text">${escapeHtml(m.summary)}</p>` : ''}
      <div class="actions">
        <button data-act="calls" title="Follow its calls a few levels deep">Follow calls ⤵</button>
        <button data-act="bp">${hasBp ? '○ Remove breakpoint' : '● Breakpoint'}</button>
        <button data-act="run-to" title="Run until this method is reached">▶ Run to here</button>
        <button data-act="impact" title="What would need to change, or be checked, if this changes">⚠ Impact</button>
      </div>
    </div>
    <div data-explain></div>
    ${findings.length ? `<div class="d-section">${findings.map(findingHtml).join('')}</div>` : ''}
    <div class="d-section"><h3>Calls</h3><ul class="rel">${calls.map(c => `<li><span class="k">${c.kind}${c.count > 1 ? ' ×' + c.count : ''}</span>${link(c.target)}</li>`).join('') || '<li class="muted">nothing in this codebase</li>'}</ul></div>
    <div class="d-section"><h3>Called by</h3><ul class="rel">${callers.map(c => `<li><span class="k">${c.kind}</span>${link(c.source)}</li>`).join('') || '<li class="muted">nothing (an entry point, or only called from outside)</li>'}</ul></div>
    <div data-tests></div>
    ${m.implements.length ? `<div class="d-section"><h3>Implements / overrides</h3><ul class="rel">${m.implements.map(i => `<li><span class="k">from</span>${link(i)}</li>`).join('')}</ul></div>` : ''}
    ${impls.length ? `<div class="d-section"><h3>Implemented by</h3><ul class="rel">${impls.map(i => `<li><span class="k">in</span>${link(i)}</li>`).join('')}</ul></div>` : ''}
    <div data-source></div>`;
}

function memberRow(m) {
  const icon = { method: 'ƒ', property: 'P', field: '▪', const: 'c', event: '⚡', constructor: '✦', 'primary constructor': '✦', 'enum member': '#', 'top-level statements': '▶' }[m.kind] ?? '·';
  const hasBp = breakpointsIn(m.file).some(l => l >= m.line && l <= m.endLine);
  const current = state.debug.frames[state.debug.activeFrame]?.memberId === m.id;
  const cx = m.complexity;
  return `<li data-go="${escapeHtml(m.id)}" class="${current ? 'current' : ''}" title="${escapeHtml(m.signature)}">
    <span class="icon ${hasBp ? 'bp' : ''}">${hasBp ? '●' : icon}</span>
    <span class="sig">${escapeHtml(m.signature)}</span>
    <span class="cx ${cx >= 25 ? 'very' : cx >= 10 ? 'hot' : ''}">${cx > 1 ? cx : ''}</span></li>`;
}

function findingHtml(f) {
  return `<div class="finding-item ${f.severity}"><span class="rule">${escapeHtml(f.ruleId)} · ${escapeHtml(f.severity)}${f.line ? ` · line ${f.line}` : ''}</span><div>${escapeHtml(f.message)}</div></div>`;
}

function renderPlanned(p) {
  $('detailsEmpty').hidden = true;
  const body = $('detailsBody');
  body.hidden = false;
  const edges = state.plan.edges.filter(e => e.source === p.id || e.target === p.id);
  body.innerHTML = `
    <div class="d-head">
      <div class="kind"><span>planned ${escapeHtml(p.kind)}</span></div>
      <h2>${escapeHtml(p.name)}</h2>
      <div class="ns">${escapeHtml(p.namespace)}</div>
      ${p.note ? `<p class="summary-text">${escapeHtml(p.note)}</p>` : ''}
      <div class="actions"><button data-act="edit-plan">Edit</button><button data-act="delete-plan" class="danger">Delete</button></div>
    </div>
    <div class="d-section"><h3>Members</h3><ul class="members">${p.members.map(m => `<li><span class="icon">·</span><span class="sig">${escapeHtml(m.signature || m.name)}</span><span></span></li>`).join('') || '<li class="muted">none yet</li>'}</ul></div>
    <div class="d-section"><h3>Connections</h3><ul class="rel">${edges.map(e => {
      const other = e.source === p.id ? e.target : e.source;
      const name = state.types.get(other)?.name ?? state.plan.types.find(t => t.id === other)?.name ?? other;
      return `<li><span class="k">${e.source === p.id ? e.kind : 'from (' + e.kind + ')'}</span><a data-go="${escapeHtml(other)}">${escapeHtml(name)}</a></li>`;
    }).join('') || '<li class="muted">none yet: use Connect in the Plan tab</li>'}</ul></div>`;
  wireLinks(body);
  body.querySelector('[data-act="edit-plan"]').addEventListener('click', () => emit('plan-edit', p.id));
  body.querySelector('[data-act="delete-plan"]').addEventListener('click', () => emit('plan-delete', p.id));
}

function wireActions(body, type, member) {
  const act = (name, fn) => body.querySelector(`[data-act="${name}"]`)?.addEventListener('click', fn);
  act('members', () => graph.openView({ kind: 'members', typeId: type.id }));
  act('focus', () => graph.focus(type.id, 1));
  act('note', () => emit('plan-note', type.id));
  act('calls', () => graph.openView({ kind: 'calls', memberId: member.id }));
  act('bp', () => toggleBreakpoint(member.file, member.bodyLine || member.line));
  act('run-to', () => emit('run-to', member));
  act('impact', () => emit('impact', member?.id ?? type.id));
  act('bp-all', async () => {
    for (const m of type.members.filter(m => m.kind === 'method' || m.kind === 'constructor'))
      if (!breakpointsIn(m.file).some(l => l >= m.line && l <= m.endLine)) await toggleBreakpoint(m.file, m.bodyLine || m.line);
    toast(`Breakpoints set on ${type.name}'s methods.`);
  });
  body.querySelectorAll('[data-editor]').forEach(a => a.addEventListener('click', () => {
    const target = member ?? type;
    api('/editor', { method: 'POST', body: { file: target.file, line: target.line } }).catch(e => toast(e.message, 'error'));
  }));
}

function dedupe(edges, dir) {
  const map = new Map();
  for (const e of edges) {
    const other = e[dir];
    if (!map.has(other)) map.set(other, []);
    if (!map.get(other).includes(e.kind)) map.get(other).push(e.kind);
  }
  return [...map.entries()].sort((a, b) => (state.types.get(a[0])?.name ?? '').localeCompare(state.types.get(b[0])?.name ?? ''));
}

const formatBytes = b => b > 1048576 ? `${(b / 1048576).toFixed(1)} MB` : b > 1024 ? `${(b / 1024).toFixed(1)} KB` : `${b} B`;

// ---------------------------------------------------------------- source

export async function loadSource(file) {
  if (!sourceCache.has(file)) sourceCache.set(file, api('/source?file=' + encodeURIComponent(file), { text: true }).then(t => t.split(/\r?\n/)));
  return sourceCache.get(file);
}

export function clearSourceCache() { sourceCache.clear(); }

async function renderSource(container, type, member) {
  if (!container) return;
  const target = member ?? type;
  if (!target.file) return;
  let lines;
  try { lines = await loadSource(target.file); }
  catch { container.innerHTML = ''; return; }

  const frame = state.debug.frames[state.debug.activeFrame];
  const currentLine = frame?.file && frame.file.toLowerCase() === target.file.toLowerCase() ? frame.line : null;
  const from = Math.max(1, (member ? member.line : type.line) - 2);
  const to = Math.min(lines.length, (member ? member.endLine : type.endLine) + 1);
  const bps = new Set(breakpointsIn(target.file));
  const findingLines = new Set((state.changes?.findings ?? []).filter(f => f.path && target.file.replaceAll('\\', '/').toLowerCase().endsWith(f.path.toLowerCase())).map(f => f.line));

  const rows = [];
  for (let n = from; n <= to; n++) {
    const cls = ['ln'];
    if (member && n >= member.line && n <= member.endLine) cls.push('in-member');
    if (n === currentLine) cls.push('current');
    if (bps.has(n)) cls.push('bp');
    if (findingLines.has(n)) cls.push('finding');
    rows.push(`<div class="${cls.join(' ')}" data-line="${n}"><span class="gutter" title="Toggle breakpoint"></span><span class="num">${n}</span><span class="txt">${highlight(lines[n - 1] ?? '')}</span></div>`);
  }
  container.innerHTML = `<div class="d-section" style="display:flex;align-items:center;gap:8px;border-bottom:none;padding-bottom:6px"><h3 style="margin:0">Source</h3><span class="muted" style="font-size:11px">${escapeHtml(fileName(target.file))} · click the margin for a breakpoint</span></div>
    <pre class="source">${rows.join('')}</pre>`;
  container.querySelectorAll('.gutter').forEach(g => g.addEventListener('click', e => {
    const line = Number(e.target.closest('.ln').dataset.line);
    toggleBreakpoint(target.file, line);
  }));
  container.querySelector('.ln.current')?.scrollIntoView({ block: 'center' });
}

const KEYWORDS = new Set(('abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in init int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref required return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while with yield get set value nameof file scoped partial global dynamic').split(' '));

/** A small C# highlighter: good enough to read, not a parser. */
export function highlight(line) {
  const out = [];
  const re = /(\/\/.*$)|(@?\$?"(?:[^"\\]|\\.)*"?|'(?:[^'\\]|\\.)'?)|(\b\d[\d_]*(?:\.\d+)?[mMfFdDlLuU]?\b)|([A-Za-z_][A-Za-z0-9_]*)|(\s+)|(.)/g;
  let m;
  while ((m = re.exec(line)) !== null) {
    const [text, comment, str, num, word] = m;
    if (comment) out.push(`<span class="tok-com">${escapeHtml(comment)}</span>`);
    else if (str) out.push(`<span class="tok-str">${escapeHtml(str)}</span>`);
    else if (num) out.push(`<span class="tok-num">${escapeHtml(num)}</span>`);
    else if (word && KEYWORDS.has(word)) out.push(`<span class="tok-kw">${word}</span>`);
    else if (word && /^[A-Z]/.test(word) && /^I?[A-Z][a-z]/.test(word)) out.push(`<span class="tok-typ">${word}</span>`);
    else out.push(escapeHtml(text));
  }
  return out.join('');
}

// ---------------------------------------------------------------- search

export function initSearch() {
  const input = $('search');
  const list = $('searchResults');
  let results = [];
  let active = 0;

  const run = () => {
    const q = input.value.trim().toLowerCase();
    if (!q || !state.graph) { list.hidden = true; return; }
    const scored = [];
    for (const t of state.graph.types) {
      const s = score(t.name, t.id, q);
      if (s > 0) scored.push({ id: t.id, name: t.name, ns: t.namespace, kind: t.kind, s: s + 5 });
      for (const m of t.members) {
        const ms = score(m.name, `${t.name}.${m.name}`, q);
        if (ms > 0) scored.push({ id: m.id, name: `${t.name}.${memberLabel(m)}`, ns: t.namespace, kind: m.kind, s: ms });
      }
    }
    for (const p of state.plan.types) if (p.name.toLowerCase().includes(q)) scored.push({ id: p.id, name: p.name, ns: p.namespace, kind: 'planned', s: 50 });
    results = scored.sort((a, b) => b.s - a.s || a.name.length - b.name.length).slice(0, 30);
    active = 0;
    paint();
  };
  const paint = () => {
    list.hidden = results.length === 0;
    list.innerHTML = results.map((r, i) => `<li data-i="${i}" class="${i === active ? 'active' : ''}"><span>${escapeHtml(r.name)}</span><span class="ns">${escapeHtml(r.ns)}</span><span class="k">${escapeHtml(r.kind)}</span></li>`).join('');
    list.querySelectorAll('li').forEach(li => li.addEventListener('mousedown', e => { e.preventDefault(); pick(Number(li.dataset.i)); }));
  };
  const pick = i => {
    const r = results[i];
    if (!r) return;
    list.hidden = true;
    input.blur();
    goTo(r.id);
  };
  input.addEventListener('input', run);
  input.addEventListener('focus', run);
  input.addEventListener('blur', () => setTimeout(() => { list.hidden = true; }, 120));
  input.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { active = Math.min(active + 1, results.length - 1); paint(); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { active = Math.max(active - 1, 0); paint(); e.preventDefault(); }
    else if (e.key === 'Enter') { pick(active); e.preventDefault(); }
    else if (e.key === 'Escape') { input.value = ''; list.hidden = true; input.blur(); }
  });
}

function score(name, full, q) {
  const n = name.toLowerCase();
  if (n === q) return 100;
  if (n.startsWith(q)) return 80;
  if (n.includes(q)) return 60;
  if (full.toLowerCase().includes(q)) return 40;
  // Initials: "os" finds OrderService.
  const caps = name.replace(/[^A-Z]/g, '').toLowerCase();
  if (caps.startsWith(q)) return 50;
  return 0;
}

// ---------------------------------------------------------------- sidebar

export function initSidebar() {
  const rerender = () => graph.render();
  for (const key of Object.keys(state.options)) {
    const box = $(key);
    if (!box) continue;
    box.checked = state.options[key];
    box.addEventListener('change', () => { state.options[key] = box.checked; graph.render({ relayout: key === 'groupNamespaces' }); });
  }
  on('graph', () => { renderLegends(); renderTree(); renderWarnings(); });
  void rerender;
}

function renderLegends() {
  const counts = new Map();
  for (const e of state.graph.edges) counts.set(e.kind, (counts.get(e.kind) ?? 0) + 1);
  $('edgeKinds').innerHTML = graph.EDGE_KINDS.map(k => `
    <label class="check" title="${escapeHtml(k.help)}"><input type="checkbox" data-edge="${k.kind}" ${state.shownEdges.has(k.kind) ? 'checked' : ''}>
      <span class="swatch ${k.style}" style="border-color:${k.color}"></span>${k.label}<span class="count">${counts.get(k.kind) ?? 0}</span></label>`).join('');
  $('edgeKinds').querySelectorAll('input').forEach(i => i.addEventListener('change', () => {
    if (i.checked) state.shownEdges.add(i.dataset.edge); else state.shownEdges.delete(i.dataset.edge);
    if (i.dataset.edge === 'nested') { state.options.showNested = i.checked; $('showNested').checked = i.checked; }
    graph.render();
  }));

  const kinds = new Map();
  for (const t of state.graph.types) kinds.set(t.kind, (kinds.get(t.kind) ?? 0) + 1);
  $('typeKinds').innerHTML = graph.TYPE_KINDS.filter(k => kinds.has(k.kind)).map(k => `
    <label class="check"><input type="checkbox" data-kind="${k.kind}" ${state.hiddenKinds.has(k.kind) ? '' : 'checked'}>
      <span class="dot" style="background:${k.color}"></span>${k.kind}<span class="count">${kinds.get(k.kind)}</span></label>`).join('');
  $('typeKinds').querySelectorAll('input').forEach(i => i.addEventListener('change', () => {
    if (i.checked) state.hiddenKinds.delete(i.dataset.kind); else state.hiddenKinds.add(i.dataset.kind);
    graph.render();
  }));
}

function renderTree() {
  const byProject = new Map();
  for (const t of state.graph.types) {
    if (!byProject.has(t.project)) byProject.set(t.project, new Map());
    const ns = byProject.get(t.project);
    ns.set(t.namespace, (ns.get(t.namespace) ?? 0) + 1);
  }
  const projects = state.graph.projects.filter(p => byProject.has(p.name));
  $('tree').innerHTML = projects.map(p => {
    const nss = [...byProject.get(p.name).entries()].sort((a, b) => a[0].localeCompare(b[0]));
    return `<details open>
      <summary><input type="checkbox" data-project="${escapeHtml(p.name)}" ${state.hiddenProjects.has(p.name) ? '' : 'checked'}>
        <b>${escapeHtml(p.name)}</b><span class="count">${p.outputKind}${p.errorCount ? ` · <span title="compile errors without a build" style="color:var(--warn)">${p.errorCount} err</span>` : ''}</span></summary>
      ${nss.map(([ns, count]) => `<label class="ns" title="${escapeHtml(ns)}"><input type="checkbox" data-ns="${escapeHtml(p.name + '::' + ns)}" ${state.hiddenNamespaces.has(p.name + '::' + ns) ? '' : 'checked'}>
        <span class="name">${escapeHtml(ns || '(global)')}</span><span class="count">${count}</span></label>`).join('')}
    </details>`;
  }).join('');
  $('tree').querySelectorAll('input[data-project]').forEach(i => i.addEventListener('change', () => {
    if (i.checked) state.hiddenProjects.delete(i.dataset.project); else state.hiddenProjects.add(i.dataset.project);
    graph.render();
  }));
  $('tree').querySelectorAll('input[data-ns]').forEach(i => i.addEventListener('change', () => {
    if (i.checked) state.hiddenNamespaces.delete(i.dataset.ns); else state.hiddenNamespaces.add(i.dataset.ns);
    graph.render();
  }));
  // Test projects start hidden on big solutions: they double the picture without explaining much.
  void isTestProject;
}

function renderWarnings() {
  const w = state.graph.warnings;
  const box = $('warnings');
  box.hidden = w.length === 0;
  box.innerHTML = '<h3>Heads up</h3>' + w.map(x => `<p>${escapeHtml(x)}</p>`).join('') +
    (w.length ? '<p class="muted" style="color:var(--muted)">Run <code>dotnet restore</code> (or build) on those projects for complete call information.</p>' : '');
}

// ---------------------------------------------------------------- tooltip and context menu

export function initOverlays() {
  const tip = $('tooltip');
  on('hover', h => {
    if (!h) { tip.hidden = true; return; }
    let html = '';
    if (h.edge) {
      const e = h.edge;
      if (e.kind === 'stack') html = '<b>Call stack</b><div class="sig">caller → callee</div>';
      else {
        const from = state.types.get(e.source)?.name ?? state.members.get(e.source)?.name ?? e.source;
        const to = state.types.get(e.target)?.name ?? state.members.get(e.target)?.name ?? e.target;
        html = `<b>${escapeHtml(from)}</b> ${escapeHtml((e.kinds ?? [e.kind]).join(', '))} <b>${escapeHtml(to)}</b>${e.count > 1 ? ` <span class="sig">×${e.count}</span>` : ''}
          ${e.note ? `<div>${escapeHtml(e.note)}</div>` : ''}${e.id?.endsWith(':obs') ? '<div class="sig">seen at runtime only (through an interface or delegate)</div>' : ''}`;
      }
    } else {
      const m = state.members.get(h.id);
      const t = state.types.get(m ? state.memberType.get(h.id) : h.id);
      if (m) html = `<b>${escapeHtml(t?.name)}.${escapeHtml(memberLabel(m))}</b><div class="sig">${escapeHtml(m.signature)}</div>${m.summary ? `<div>${escapeHtml(m.summary)}</div>` : ''}`;
      else if (t) html = `<b>${escapeHtml(t.name)}</b> <span class="sig">${escapeHtml(t.kind)}</span>${t.summary ? `<div>${escapeHtml(t.summary)}</div>` : ''}<div class="sig">${escapeHtml(t.namespace)}</div>`;
      else return;
    }
    tip.innerHTML = html;
    tip.hidden = false;
    const stage = document.querySelector('.stage').getBoundingClientRect();
    tip.style.left = Math.min(h.x + 14, stage.width - 380) + 'px';
    tip.style.top = Math.min(h.y + 14, stage.height - 120) + 'px';
  });

  const menu = $('contextMenu');
  const close = () => { menu.hidden = true; };
  document.addEventListener('click', close);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
  on('context', ({ id, x, y }) => {
    const member = state.members.get(id);
    const type = state.types.get(member ? state.memberType.get(id) : id);
    const planned = state.plan.types.find(p => p.id === id);
    const items = [];
    if (planned) {
      items.push(['Edit planned type', () => emit('plan-edit', id)], ['Connect from here…', () => emit('plan-connect', id)], ['Delete', () => emit('plan-delete', id)]);
    } else if (member) {
      items.push(['Follow calls from here', () => graph.openView({ kind: 'calls', memberId: id })],
        ['Toggle breakpoint', () => toggleBreakpoint(member.file, member.bodyLine || member.line)],
        ['Run to here', () => emit('run-to', member)],
        ['Open in editor', () => api('/editor', { method: 'POST', body: { file: member.file, line: member.line } }).catch(e => toast(e.message, 'error'))]);
    } else if (type) {
      items.push(['Open up (methods & calls)', () => graph.openView({ kind: 'members', typeId: id })],
        ['Focus: just this and its neighbors', () => graph.focus(id, 1)],
        ['Focus: two steps out', () => graph.focus(id, 2)],
        null,
        ['Add a note', () => emit('plan-note', id)],
        ['Plan a connection from here…', () => emit('plan-connect', id)],
        ['Hide this namespace', () => { state.hiddenNamespaces.add(graph.nsKey(type)); graph.render(); renderTree(); }],
        ['Open in editor', () => api('/editor', { method: 'POST', body: { file: type.file, line: type.line } }).catch(e => toast(e.message, 'error'))]);
    }
    if (!items.length) return;
    const title = member ? `${type?.name}.${memberLabel(member)}` : planned?.name ?? type?.name;
    menu.innerHTML = `<div class="title">${escapeHtml(title)}</div>` + items.map((item, i) => item ? `<button data-i="${i}">${escapeHtml(item[0])}</button>` : '<hr>').join('');
    menu.querySelectorAll('button').forEach(b => b.addEventListener('click', e => { e.stopPropagation(); close(); items[Number(b.dataset.i)][1](); }));
    const stage = document.querySelector('.stage').getBoundingClientRect();
    menu.style.left = Math.min(x, stage.width - 230) + 'px';
    menu.style.top = Math.min(y, stage.height - 40 - items.length * 30) + 'px';
    menu.hidden = false;
  });
  void typeOf;
}
