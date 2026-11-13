// The canvas: builds Cytoscape elements for the current view, styles them, and keeps the
// runtime highlights (debugger stack, live activity, changes) drawn on top.
import { state, emit, on, typeOf, isTestProject, memberLabel, debounce } from './state.js';

export const EDGE_KINDS = [
  { kind: 'inherits', label: 'Inherits', color: '#b48cff', style: 'solid', help: 'Base class, or an interface extending another' },
  { kind: 'implements', label: 'Implements', color: '#56c99a', style: 'dashed', help: 'Implements an interface' },
  { kind: 'has', label: 'Has (field / property)', color: '#f2b544', style: 'solid', help: 'Holds the other type in a field or property' },
  { kind: 'calls', label: 'Calls', color: '#7aa2ff', style: 'solid', help: 'Calls a method or property on it' },
  { kind: 'creates', label: 'Creates (new)', color: '#ff9e64', style: 'dotted', help: 'Creates instances with new' },
  { kind: 'uses', label: 'Mentions', color: '#8a96a8', style: 'dotted', help: 'Uses the type in a signature, local or generic argument' },
  { kind: 'nested', label: 'Nested in', color: '#6b7585', style: 'dashed', help: 'Declared inside the other type' },
];
const EDGE_PRIORITY = ['inherits', 'implements', 'has', 'creates', 'calls', 'uses', 'nested'];

export const TYPE_KINDS = [
  { kind: 'class', color: '#7aa2ff', shape: 'round-rectangle' },
  { kind: 'static class', color: '#5cc8ff', shape: 'round-rectangle' },
  { kind: 'interface', color: '#56c99a', shape: 'round-rectangle' },
  { kind: 'record', color: '#b48cff', shape: 'round-rectangle' },
  { kind: 'struct', color: '#f78c6c', shape: 'rectangle' },
  { kind: 'record struct', color: '#f78c6c', shape: 'rectangle' },
  { kind: 'enum', color: '#f2b544', shape: 'cut-rectangle' },
  { kind: 'delegate', color: '#ff79c6', shape: 'tag' },
];
const PALETTE = ['#7aa2ff', '#56c99a', '#f2b544', '#b48cff', '#ff9e64', '#3fd0e0', '#ff79c6', '#a3d977', '#e0a7ff', '#ffcf70', '#6ad1b8', '#ff8f8f'];

let cy;
let tapInterceptor = null;
const positions = new Map(); // view key -> Map(node id -> position)
let dashOffset = 0;

const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const kindInfo = kind => TYPE_KINDS.find(k => k.kind === kind) ?? TYPE_KINDS[0];
const edgeInfo = kind => EDGE_KINDS.find(k => k.kind === kind) ?? EDGE_KINDS[3];

export function init(container) {
  cy = cytoscape({
    container,
    wheelSensitivity: 0.25,
    minZoom: 0.05,
    maxZoom: 4,
    boxSelectionEnabled: false,
    selectionType: 'single',
    style: buildStyle(),
  });

  cy.on('tap', 'node', e => {
    const node = e.target;
    if (node.data('group')) return; // namespace / owner boxes
    if (tapInterceptor && tapInterceptor(node.id())) return;
    select(node.id());
  });
  cy.on('tap', e => { if (e.target === cy) { if (tapInterceptor?.(null)) return; select(null); } });
  cy.on('dbltap', 'node', e => {
    const node = e.target;
    const id = node.data('group') ? node.data('typeId') : node.id();
    if (!id) return;
    if (state.types.has(id)) openView({ kind: 'members', typeId: id });
    else if (state.members.has(id)) openView({ kind: 'calls', memberId: id });
  });
  cy.on('cxttap', 'node', e => {
    const node = e.target;
    const id = node.data('group') ? node.data('typeId') : node.id();
    if (id) emit('context', { id, x: e.renderedPosition.x, y: e.renderedPosition.y });
  });
  cy.on('mouseover', 'node', e => emit('hover', { id: e.target.data('group') ? e.target.data('typeId') : e.target.id(), x: e.renderedPosition.x, y: e.renderedPosition.y }));
  cy.on('mouseout', 'node', () => emit('hover', null));
  cy.on('mouseover', 'edge', e => emit('hover', { edge: e.target.data(), x: e.renderedPosition.x, y: e.renderedPosition.y }));
  cy.on('mouseout', 'edge', () => emit('hover', null));
  cy.on('viewport', debounce(() => emit('hover', null), 30));
  cy.on('position', 'node', debounce(savePositions, 200));

  // Marching ants on the edges that show the live call stack.
  setInterval(() => {
    const moving = cy.edges('.stack, .observed, .trail');
    if (moving.length === 0) return;
    dashOffset = (dashOffset - 2) % 1000;
    moving.style('line-dash-offset', dashOffset);
  }, 60);

  on('theme', () => cy.style(buildStyle()));
  return cy;
}

export const getCy = () => cy;
export const setTapInterceptor = fn => { tapInterceptor = fn; };

// ---------------------------------------------------------------- views

const viewKey = view => view.kind === 'types' ? 'types' : `${view.kind}:${view.typeId ?? view.memberId}`;

export function openView(view, { push = true } = {}) {
  if (push && viewKey(view) !== viewKey(state.view)) state.viewStack.push({ view: state.view, selected: state.selected });
  state.view = view;
  state.focusDepth = 0;
  render({ relayout: false });
  if (view.kind !== 'types') select(view.typeId ?? view.memberId, { center: false });
  emit('view', view);
}

export function back() {
  const previous = state.viewStack.pop();
  if (!previous) { select(null); return false; }
  state.view = previous.view;
  render({ relayout: false });
  select(previous.selected, { center: false });
  emit('view', state.view);
  return true;
}

export function goHome() {
  state.viewStack = [];
  state.view = { kind: 'types' };
  render({ relayout: false });
  select(null);
  emit('view', state.view);
}

/** Rebuild the elements for the current view, keeping node positions where possible. */
export function render({ relayout = false, fit = false } = {}) {
  if (!cy || !state.graph) return;
  savePositions();
  const elements = state.view.kind === 'types' ? typeElements() : memberElements(state.view);
  const key = viewKey(state.view);
  const cached = positions.get(key);

  cy.batch(() => {
    cy.elements().remove();
    cy.add(elements);
  });

  const missing = cached ? cy.nodes().filter(n => !n.isParent() && !cached.has(n.id())) : cy.nodes();
  if (!relayout && cached && missing.length === 0) {
    cy.nodes().forEach(n => { if (!n.isParent() && cached.has(n.id())) n.position(cached.get(n.id())); });
    if (fit) cy.fit(undefined, 40);
  } else {
    runLayout(cached && missing.length < cy.nodes().length * 0.3 && !relayout ? 'incremental' : 'full');
  }
  applyOverlay();
  applyRuntime();
  applySelectionClasses();
  emit('rendered', { nodes: cy.nodes().length, edges: cy.edges().length });
}

function savePositions() {
  if (!cy || cy.nodes().length === 0) return;
  const map = new Map();
  cy.nodes().forEach(n => { if (!n.isParent()) map.set(n.id(), { ...n.position() }); });
  positions.set(viewKey(state.view), map);
}

export function runLayout(mode = 'full', { animated = false } = {}) {
  // A class opened up reads best clustered by owner; a call chain reads best left to right.
  const name = state.view.kind === 'calls' && state.layout === 'fcose' ? 'dagre-lr' : state.layout;
  // Animation runs on requestAnimationFrame, which a hidden tab never fires: lay out instantly then.
  // Only animate when the user asked for a new arrangement; a first layout should just appear.
  // (Animation runs on requestAnimationFrame, which a hidden or background tab doesn't fire.)
  const animate = animated && cy.nodes().length < 400 && document.visibilityState === 'visible';
  const options = {
    'fcose': {
      name: 'fcose', quality: cy.nodes().length > 600 ? 'draft' : 'default', randomize: mode === 'full', animate, animationDuration: 450,
      nodeRepulsion: () => (state.view.kind === 'types' ? 7000 : 4500), idealEdgeLength: () => (state.view.kind === 'types' ? 110 : 70), edgeElasticity: () => 0.3, nestingFactor: 0.5, gravity: 0.3,
      gravityRangeCompound: 1.4, packComponents: true, tile: true, nodeDimensionsIncludeLabels: true, padding: 30,
    },
    'dagre-tb': { name: 'dagre', rankDir: 'TB', nodeSep: 24, rankSep: 70, edgeSep: 8, animate, animationDuration: 400, padding: 30 },
    'dagre-lr': { name: 'dagre', rankDir: 'LR', nodeSep: 16, rankSep: 90, edgeSep: 6, animate, animationDuration: 400, padding: 30 },
    'concentric': { name: 'concentric', concentric: n => n.degree(), levelWidth: () => 2, minNodeSpacing: 20, animate, animationDuration: 400, padding: 30 },
    'grid': { name: 'grid', avoidOverlap: true, condense: true, animate, animationDuration: 300, padding: 30, sort: (a, b) => (a.data('ns') ?? '').localeCompare(b.data('ns') ?? '') },
  }[name] ?? { name: 'fcose' };
  const layout = cy.layout({ ...options, fit: mode === 'full' });
  if (mode === 'full') layout.one('layoutstop', () => cy.fit(undefined, 40));
  layout.run();
}

// ---------------------------------------------------------------- types view

function visibleTypes() {
  const o = state.options;
  return state.graph.types.filter(t =>
    !state.hiddenProjects.has(t.project)
    && !state.hiddenNamespaces.has(nsKey(t))
    && !state.hiddenKinds.has(t.kind)
    && !(o.hideTests && isTestProject(t.project))
    && (o.showNested || !t.containingType));
}

export const nsKey = t => `${t.project}::${t.namespace}`;

function typeElements() {
  const types = visibleTypes();
  const visible = new Set(types.map(t => t.id));
  const planned = state.plan.types;
  for (const p of planned) visible.add(p.id);

  // Collapse several kinds of edge between the same two types into the strongest one.
  const pairs = new Map();
  for (const e of state.graph.edges) {
    if (!state.shownEdges.has(e.kind)) continue;
    if (e.kind === 'nested' && !state.options.showNested) continue;
    if (!visible.has(e.source) || !visible.has(e.target)) continue;
    const key = state.options.simplifyEdges ? `${e.source}\u0000${e.target}` : `${e.source}\u0000${e.target}\u0000${e.kind}`;
    const existing = pairs.get(key);
    if (!existing) pairs.set(key, { ...e, kinds: [e.kind], total: e.count });
    else {
      existing.kinds.push(e.kind);
      existing.total += e.count;
      if (EDGE_PRIORITY.indexOf(e.kind) < EDGE_PRIORITY.indexOf(existing.kind)) Object.assign(existing, { kind: e.kind, file: e.file, line: e.line });
    }
  }
  let edges = [...pairs.values()];
  for (const p of state.plan.edges)
    if (visible.has(p.source) && visible.has(p.target)) edges.push({ source: p.source, target: p.target, kind: p.kind, kinds: [p.kind], total: 1, planned: true, note: p.note });

  // Runtime-only calls the static graph can't see (through interfaces), from live sampling.
  if (state.overlay === 'live' || state.live.processId) {
    const seen = new Set(edges.map(e => `${e.source}\u0000${e.target}`));
    for (const [key, count] of state.live.observed) {
      const [from, to] = key.split('\u0000').map(typeOf);
      if (!from || !to || from === to || !visible.has(from) || !visible.has(to)) continue;
      const pair = `${from}\u0000${to}`;
      if (seen.has(pair)) continue;
      seen.add(pair);
      edges.push({ source: from, target: to, kind: 'calls', kinds: ['calls'], total: count, observed: true });
    }
  }

  let shown = types.map(t => t.id).concat(planned.map(p => p.id));
  if (state.options.hideIsolated) {
    const connected = new Set(edges.flatMap(e => [e.source, e.target]));
    shown = shown.filter(id => connected.has(id) || id === state.selected || id.startsWith('plan:'));
  }
  if (state.focusDepth > 0 && state.selected && typeOf(state.selected)) {
    const keep = neighborhood(typeOf(state.selected), edges, state.focusDepth);
    shown = shown.filter(id => keep.has(id));
  }
  const shownSet = new Set(shown);
  edges = edges.filter(e => shownSet.has(e.source) && shownSet.has(e.target));

  // Broken architecture rules: mark the edge that breaks them, adding it if its kind is hidden.
  const violations = state.rules?.violations ?? [];
  if (violations.length) {
    const byPair = new Map(violations.map(v => [`${v.source}\u0000${v.target}`, v]));
    for (const e of edges) if (byPair.has(`${e.source}\u0000${e.target}`)) { e.violation = byPair.get(`${e.source}\u0000${e.target}`); byPair.delete(`${e.source}\u0000${e.target}`); }
    for (const v of byPair.values())
      if (shownSet.has(v.source) && shownSet.has(v.target))
        edges.push({ source: v.source, target: v.target, kind: v.kind.split(',')[0].trim(), kinds: [v.kind], total: v.count, file: v.file, line: v.line, violation: v });
  }

  const elements = [];
  const groups = new Set();
  const group = (project, ns) => {
    if (!state.options.groupNamespaces) return undefined;
    const id = `ns:${project}::${ns}`;
    if (!groups.has(id)) {
      groups.add(id);
      elements.push({ data: { id, label: ns || `${project} (global)`, group: 'namespace', project } });
    }
    return id;
  };

  for (const t of types) {
    if (!shownSet.has(t.id)) continue;
    elements.push({ data: typeNodeData(t, group(t.project, t.namespace)) });
  }
  for (const p of planned) {
    if (!shownSet.has(p.id)) continue;
    const project = state.graph.types.find(t => t.namespace === p.namespace)?.project ?? state.graph.projects[0]?.name ?? '';
    elements.push({
      data: { id: p.id, label: `${p.name}\n${p.kind} · planned`, kind: p.kind, shape: kindInfo(p.kind).shape, color: '#ff79c6', parent: group(project, p.namespace), planned: true, ns: p.namespace },
      classes: 'planned',
    });
  }
  for (const e of edges) {
    elements.push({
      data: {
        id: `e:${e.source}->${e.target}:${state.options.simplifyEdges ? '' : e.kind}${e.observed ? ':obs' : ''}${e.planned ? ':plan' : ''}`,
        source: e.source, target: e.target, kind: e.kind, kinds: e.kinds, count: e.total,
        width: Math.min(1.2 + Math.log2(1 + e.total) * 0.7, 5), file: e.file, line: e.line, note: e.note ?? e.violation?.message,
      },
      classes: [e.kind, e.observed ? 'observed' : '', e.planned ? 'planned' : '', e.violation ? 'violation' : ''].join(' '),
    });
  }
  return elements;
}

function typeNodeData(t, parent) {
  const info = kindInfo(t.kind);
  return {
    id: t.id, label: typeLabel(t), kind: t.kind, shape: info.shape, color: info.color, parent,
    ns: t.namespace, project: t.project, members: t.members.length,
  };
}

function typeLabel(t) {
  let name = t.name;
  if (t.containingType) name = `${state.types.get(t.containingType)?.name ?? ''}.${name}`;
  const badges = [];
  const heap = state.live.heap.get(t.id);
  if (heap) badges.push(`×${heap.count}`);
  const findings = state.overlay === 'changes' ? (state.changes?.findings ?? []).filter(f => f.typeId === t.id).length : 0;
  if (findings) badges.push(`⚠${findings}`);
  if (hasBreakpoint(t)) badges.push('●');
  const sub = [t.kind === 'class' ? (t.modifiers.includes('abstract') ? 'abstract' : '') : t.kind,
    `${t.members.length} member${t.members.length === 1 ? '' : 's'}`].filter(Boolean).join(' · ');
  return `${name}${badges.length ? '  ' + badges.join(' ') : ''}\n${sub}`;
}

function neighborhood(start, edges, depth) {
  const adjacent = new Map();
  for (const e of edges) {
    (adjacent.get(e.source) ?? adjacent.set(e.source, []).get(e.source)).push(e.target);
    (adjacent.get(e.target) ?? adjacent.set(e.target, []).get(e.target)).push(e.source);
  }
  const seen = new Set([start]);
  let frontier = [start];
  for (let d = 0; d < depth; d++) {
    const next = [];
    for (const id of frontier) for (const n of adjacent.get(id) ?? []) if (!seen.has(n)) { seen.add(n); next.push(n); }
    frontier = next;
  }
  return seen;
}

// ---------------------------------------------------------------- member views

const interesting = m => !['field', 'const', 'enum member'].includes(m.kind)
  || (state.callsTo.get(m.id)?.length ?? 0) > 0 || (state.callsFrom.get(m.id)?.length ?? 0) > 0;

function memberElements(view) {
  const include = new Set();
  const edges = new Map();
  const addEdge = (source, target, kind, count = 1) => {
    if (source === target) return;
    const id = `m:${source}->${target}:${kind}`;
    if (!edges.has(id)) edges.set(id, { id, source, target, kind, count });
  };

  if (view.kind === 'members') {
    const type = state.types.get(view.typeId);
    if (!type) return [];
    for (const m of type.members.filter(interesting)) include.add(m.id);
    for (const id of [...include]) {
      for (const c of state.callsFrom.get(id) ?? []) { include.add(c.target); addEdge(c.source, c.target, c.kind === 'creates' ? 'creates' : 'calls', c.count); }
      for (const c of state.callsTo.get(id) ?? []) { include.add(c.source); addEdge(c.source, c.target, c.kind === 'creates' ? 'creates' : 'calls', c.count); }
      const member = state.members.get(id);
      for (const target of member?.implements ?? []) { include.add(target); addEdge(id, target, 'implements'); }
      for (const impl of state.implementers.get(id) ?? []) { include.add(impl); addEdge(impl, id, 'implements'); }
    }
  } else {
    // Everything reachable from one member, following calls (and interface dispatch) a few levels deep.
    const start = view.memberId;
    include.add(start);
    let frontier = [start];
    for (let depth = 0; depth < 4 && frontier.length; depth++) {
      const next = [];
      for (const id of frontier) {
        const targets = (state.callsFrom.get(id) ?? []).map(c => [c.target, c.kind === 'creates' ? 'creates' : 'calls', c.count]);
        for (const impl of state.implementers.get(id) ?? []) targets.push([impl, 'dispatch', 1]);
        for (const [target, kind, count] of targets) {
          addEdge(id, target, kind, count);
          if (!include.has(target) && include.size < 160) { include.add(target); next.push(target); }
        }
      }
      frontier = next;
    }
    for (const c of state.callsTo.get(start) ?? []) { include.add(c.source); addEdge(c.source, start, 'calls', c.count); }
  }

  // Calls between anything already on screen, so the picture is complete.
  for (const id of include)
    for (const c of state.callsFrom.get(id) ?? [])
      if (include.has(c.target)) addEdge(c.source, c.target, c.kind === 'creates' ? 'creates' : 'calls', c.count);

  const elements = [];
  const groups = new Set();
  for (const id of include) {
    const member = state.members.get(id);
    const typeId = state.memberType.get(id);
    const type = state.types.get(typeId);
    if (!member || !type) continue;
    // The call-chain view is a flat flow; grouping by owner would fight the left-to-right layout.
    const flat = view.kind === 'calls';
    const groupId = flat ? undefined : `grp:${typeId}`;
    if (!flat && !groups.has(groupId)) {
      groups.add(groupId);
      elements.push({
        data: { id: groupId, label: `${type.name}  ·  ${type.kind}`, group: 'owner', typeId, color: kindInfo(type.kind).color },
        classes: typeId === view.typeId ? 'home' : '',
      });
    }
    elements.push({ data: { id, label: memberNodeLabel(member, flat ? type : null), kind: member.kind, parent: groupId, color: memberColor(member), shape: memberShape(member), member: true } });
  }
  for (const e of edges.values())
    if (include.has(e.source) && include.has(e.target))
      elements.push({ data: { ...e, width: Math.min(1.2 + Math.log2(1 + e.count) * 0.7, 5) }, classes: `${e.kind} member-edge` });
  return elements;
}

function memberNodeLabel(m, owner = state.view.kind === 'calls' ? state.types.get(state.memberType.get(m.id)) : null) {
  const badges = [];
  if (memberHasBreakpoint(m)) badges.push('●');
  const heat = state.live.heat.get(m.id);
  if (heat && state.overlay === 'live') badges.push(`${Math.round(heat)}`);
  const cx = m.complexity > 1 ? ` · cx ${m.complexity}` : '';
  return `${badges.length ? badges.join(' ') + ' ' : ''}${owner ? owner.name + '.' : ''}${memberLabel(m)}\n${m.kind}${m.isAsync ? ' · async' : ''}${cx}`;
}

const memberColor = m => ({ method: '#7aa2ff', property: '#56c99a', constructor: '#ff9e64', 'primary constructor': '#ff9e64', event: '#ff79c6', field: '#f2b544', 'top-level statements': '#ffd84a' }[m.kind] ?? '#8a96a8');
const memberShape = m => m.kind === 'property' ? 'round-tag' : m.kind.includes('constructor') ? 'round-diamond' : 'round-rectangle';

// ---------------------------------------------------------------- selection

export function select(id, { center = true } = {}) {
  state.selected = id && cy.getElementById(id).length ? id : (id && (state.types.has(id) || state.members.has(id)) ? id : null);
  if (state.focusDepth > 0 && state.view.kind === 'types') render();
  applySelectionClasses();
  const node = state.selected ? cy.getElementById(state.selected) : null;
  if (center && node?.length) cy.animate({ center: { eles: node }, zoom: Math.max(cy.zoom(), 0.9) }, { duration: 350 });
  emit('select', state.selected);
}

function applySelectionClasses() {
  cy.batch(() => {
    cy.elements().removeClass('faded selected neighbor');
    const node = state.selected ? cy.getElementById(state.selected) : null;
    if (!node?.length) return;
    node.addClass('selected');
    const hood = node.closedNeighborhood();
    const keep = hood.union(hood.parents()).union(node.parents());
    cy.elements().not(keep).addClass('faded');
    hood.nodes().not(node).addClass('neighbor');
  });
}

/** Fade everything except these types and members (and the types that own the members), or clear with null. */
export function highlightSet(ids) {
  cy.batch(() => {
    cy.elements().removeClass('faded selected neighbor impact');
    if (!ids) return;
    const keep = new Set();
    for (const id of ids) { keep.add(id); const t = typeOf(id); if (t) keep.add(t); }
    const nodes = cy.nodes().filter(n => keep.has(n.id()) || keep.has(n.data('typeId')));
    nodes.addClass('impact');
    const kept = nodes.union(nodes.parents()).union(nodes.edgesWith(nodes));
    cy.elements().not(kept).addClass('faded');
  });
}

export function focus(id, depth = 1) {
  state.selected = id;
  state.focusDepth = depth;
  render({ relayout: true });
  select(id);
  emit('focus', depth);
}

export function setFocusDepth(depth) {
  state.focusDepth = Math.max(0, depth);
  render({ relayout: state.focusDepth > 0 });
  if (state.focusDepth === 0) cy.fit(undefined, 40);
  emit('focus', state.focusDepth);
}

export function centerOn(id, zoom) {
  const node = cy.getElementById(id);
  if (node.length) cy.animate({ center: { eles: node }, zoom: zoom ?? Math.max(cy.zoom(), 0.8) }, { duration: 300 });
}

// ---------------------------------------------------------------- overlays

export function applyOverlay() {
  if (!cy) return;
  const overlay = state.overlay;
  const projects = state.graph.projects.map(p => p.name);
  const maxHeat = Math.max(1, ...state.live.typeHeat.values());
  const maxMemberHeat = Math.max(1, ...state.live.heat.values());
  const changesByType = new Map();
  const changesByMember = new Map();
  for (const c of state.changes?.items ?? []) {
    (changesByType.get(c.typeId) ?? changesByType.set(c.typeId, []).get(c.typeId)).push(c);
    if (c.memberId) changesByMember.set(c.memberId, c);
  }

  cy.batch(() => {
    cy.nodes().forEach(node => {
      if (node.data('group') || node.data('planned')) return;
      const id = node.id();
      const isMember = node.data('member');
      const type = isMember ? state.types.get(state.memberType.get(id)) : state.types.get(id);
      const member = isMember ? state.members.get(id) : null;
      if (!type) return;
      node.removeClass('dim-overlay');
      let color = isMember ? memberColor(member) : kindInfo(type.kind).color;
      switch (overlay) {
        case 'project': color = PALETTE[projects.indexOf(type.project) % PALETTE.length]; break;
        case 'complexity': {
          const cx = isMember ? member.complexity : type.metrics.maxComplexity;
          color = heatColor(Math.min(1, Math.max(0, (cx - 1) / 24)));
          break;
        }
        case 'coupling': {
          const c = type.metrics.incomingCount + type.metrics.outgoingCount;
          color = heatColor(Math.min(1, c / 20));
          break;
        }
        case 'changes': {
          const change = isMember ? changesByMember.get(id) : worstChange(changesByType.get(id));
          if (change) color = changeColor(change.change);
          else { color = '#8a96a8'; node.addClass('dim-overlay'); }
          break;
        }
        case 'tests': {
          const tests = state.tests;
          if (!tests) { color = '#8a96a8'; break; }
          if (isMember) {
            const depth = tests.members.get(id);
            const runsCode = ['method', 'constructor', 'operator', 'property', 'indexer', 'top-level statements'].includes(member.kind) && member.complexity > 0 && !member.isAbstract;
            if (tests.projects.has(type.project)) { color = '#8a96a8'; node.addClass('dim-overlay'); }
            else if (depth !== undefined) color = coverageColor(depth <= 2 ? 1 : depth <= 4 ? 0.6 : 0.35);
            else if (runsCode) color = '#ff6b6b';
            else { color = '#8a96a8'; node.addClass('dim-overlay'); }
          } else {
            const c = tests.types.get(id);
            if (!c || c.members === 0) { color = '#8a96a8'; node.addClass('dim-overlay'); }
            else color = c.covered === 0 ? '#ff6b6b' : coverageColor(c.covered / c.members);
          }
          break;
        }
        case 'incidents': {
          const hits = state.incidents?.get(type.id) ?? 0;
          const max = Math.max(1, ...(state.incidents?.values() ?? []));
          color = hits ? heatColor(0.35 + 0.65 * hits / max) : '#8a96a8';
          if (!hits) node.addClass('dim-overlay');
          break;
        }
        case 'live': {
          const heat = isMember ? (state.live.heat.get(id) ?? 0) / maxMemberHeat : (state.live.typeHeat.get(id) ?? 0) / maxHeat;
          color = heat > 0 ? liveColor(heat) : '#8a96a8';
          if (heat === 0) node.addClass('dim-overlay');
          break;
        }
      }
      node.data('color', color);
      node.data('label', isMember ? memberNodeLabel(member) : typeLabel(type));
    });
  });
}

const CHANGE_ORDER = ['removed', 'signature', 'behavior', 'added', 'renamed', 'moved'];
const worstChange = list => list?.slice().sort((a, b) => CHANGE_ORDER.indexOf(a.change) - CHANGE_ORDER.indexOf(b.change))[0];
export const changeColor = change => ({ added: '#56c99a', behavior: '#f2b544', signature: '#b48cff', removed: '#ff6b6b', renamed: '#3fd0e0', moved: '#3fd0e0' }[change] ?? '#8a96a8');

function heatColor(t) {
  // green -> amber -> red
  const stops = [[86, 201, 154], [242, 181, 68], [255, 107, 107]];
  const [a, b, u] = t < 0.5 ? [stops[0], stops[1], t * 2] : [stops[1], stops[2], (t - 0.5) * 2];
  return `rgb(${a.map((v, i) => Math.round(v + (b[i] - v) * u)).join(',')})`;
}

/** Red (nothing reaches it) through amber to green (most of it is reached by tests). */
function coverageColor(t) {
  const stops = [[255, 107, 107], [242, 181, 68], [86, 201, 154]];
  const [a, b, u] = t < 0.5 ? [stops[0], stops[1], t * 2] : [stops[1], stops[2], (t - 0.5) * 2];
  return `rgb(${a.map((v, i) => Math.round(v + (b[i] - v) * u)).join(',')})`;
}

function liveColor(t) {
  const from = [70, 110, 140], to = [63, 208, 224];
  const u = Math.sqrt(t);
  return `rgb(${from.map((v, i) => Math.round(v + (to[i] - v) * u)).join(',')})`;
}

// ---------------------------------------------------------------- runtime highlights

function hasBreakpoint(type) {
  const bps = state.debug.breakpoints;
  return type.members.some(m => m.file && (bps[normalize(m.file)] ?? []).some(l => l >= m.line && l <= m.endLine));
}

function memberHasBreakpoint(m) {
  return !!m.file && (state.debug.breakpoints[normalize(m.file)] ?? []).some(l => l >= m.line && l <= m.endLine);
}

export const normalize = file => Object.keys(state.debug.breakpoints).find(k => k.toLowerCase() === file.toLowerCase()) ?? file;

/** Draw the debugger's stack and the live sampler's activity onto whatever is on screen. */
export function applyRuntime() {
  if (!cy) return;
  cy.batch(() => {
    cy.elements().removeClass('current on-stack live-active trail-1 trail-2 trail-3');
    cy.edges('.stack').remove();

    const ids = frame => [frame.memberId, frame.typeId].filter(Boolean);
    const nodeFor = frame => {
      for (const id of ids(frame)) {
        const n = cy.getElementById(id);
        if (n.length) return n;
      }
      const grp = frame.typeId ? cy.getElementById(`grp:${frame.typeId}`) : null;
      return grp?.length ? grp : null;
    };

    // Recently visited places fade out behind the current one, like a comet tail.
    state.debug.trail.slice(-4, -1).reverse().forEach((frame, i) => nodeFor(frame)?.addClass(`trail-${i + 1}`));

    // The debugger's stack when paused; otherwise a stack trace pasted into the Trace tab.
    const debugging = state.debug.frames.length > 0;
    const frames = (debugging ? state.debug.frames : state.trace.frames).filter(f => f.typeId);
    const active = debugging ? state.debug.activeFrame : state.trace.active;
    let previous = null;
    frames.forEach((frame, i) => {
      const node = nodeFor(frame);
      if (!node) return;
      node.addClass(i === active ? 'current' : 'on-stack');
      // Draw caller -> callee along the stack (frames are innermost first).
      if (previous && previous.id() !== node.id() && !node.isParent() && !previous.isParent()) {
        cy.add({ group: 'edges', data: { id: `stack:${i}`, source: node.id(), target: previous.id(), kind: 'stack' }, classes: 'stack' });
      }
      previous = node;
    });

    for (const id of state.live.active) {
      const n = cy.getElementById(id);
      if (n.length) n.addClass('live-active');
      else {
        const typeNode = cy.getElementById(typeOf(id) ?? '');
        if (typeNode.length) typeNode.addClass('live-active');
      }
    }
  });
}

/** Re-render if a node we need isn't on screen, so the debugger's location is never off-canvas. */
export function ensureVisible(frame) {
  if (!frame?.typeId) return;
  const present = cy.getElementById(frame.memberId ?? '').length || cy.getElementById(frame.typeId).length || cy.getElementById(`grp:${frame.typeId}`).length;
  if (!present && state.view.kind === 'types') {
    state.focusDepth = 0;
    const type = state.types.get(frame.typeId);
    if (type) { state.hiddenProjects.delete(type.project); state.hiddenNamespaces.delete(nsKey(type)); state.hiddenKinds.delete(type.kind); }
    render();
  }
}

export function exportPng() {
  const png = cy.png({ full: true, scale: 2, bg: css('--bg') });
  const a = document.createElement('a');
  a.href = png;
  a.download = `${state.graph?.name ?? 'graph'}-${state.view.kind}.png`;
  a.click();
}

// ---------------------------------------------------------------- style

function buildStyle() {
  const text = css('--text') || '#dfe6f0';
  const muted = css('--muted') || '#8a96a8';
  const line = css('--line') || '#2a3240';
  const bg = css('--bg') || '#0f1218';
  const accent = css('--accent') || '#7aa2ff';
  const current = css('--current') || '#ffd84a';
  const live = css('--live') || '#3fd0e0';
  const light = document.documentElement.dataset.theme === 'light';

  const style = [
    {
      selector: 'node',
      style: {
        'label': 'data(label)', 'color': text, 'font-size': 11, 'font-family': 'Segoe UI, system-ui, sans-serif',
        'text-wrap': 'wrap', 'text-valign': 'center', 'text-halign': 'center', 'text-max-width': 220,
        'shape': 'data(shape)', 'background-color': 'data(color)', 'background-opacity': light ? 0.16 : 0.2,
        'border-width': 1.5, 'border-color': 'data(color)',
        'width': 'label', 'height': 'label', 'padding': '9px',
        'min-zoomed-font-size': 5, 'transition-property': 'opacity, border-width, underlay-opacity', 'transition-duration': '180ms',
        'underlay-color': current, 'underlay-opacity': 0, 'underlay-padding': 8, 'underlay-shape': 'round-rectangle',
      },
    },
    {
      selector: 'node[group = "namespace"]',
      style: {
        'shape': 'round-rectangle', 'background-color': light ? '#e9edf5' : '#1a2029', 'background-opacity': light ? 0.6 : 0.55,
        'border-width': 1, 'border-style': 'dashed', 'border-color': line,
        'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': -4, 'font-size': 12, 'font-weight': 600, 'color': muted,
        'padding': '18px', 'min-zoomed-font-size': 4,
      },
    },
    {
      selector: 'node[group = "owner"]',
      style: {
        'shape': 'round-rectangle', 'background-color': 'data(color)', 'background-opacity': 0.06,
        'border-width': 1.5, 'border-color': 'data(color)', 'border-style': 'solid',
        'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': -4, 'font-size': 12, 'font-weight': 600, 'color': text, 'padding': '14px',
      },
    },
    { selector: 'node.home', style: { 'border-width': 2.5, 'background-opacity': 0.1 } },
    { selector: 'node[?member]', style: { 'font-size': 10.5, 'padding': '6px', 'background-opacity': light ? 0.14 : 0.18 } },
    { selector: 'node[kind = "interface"]', style: { 'border-style': 'dashed' } },
    { selector: 'node.planned', style: { 'border-style': 'dashed', 'border-width': 2, 'background-opacity': 0.12 } },
    { selector: 'node.dim-overlay', style: { 'opacity': 0.45 } },
    { selector: 'node.neighbor', style: { 'border-width': 2 } },
    { selector: 'node.selected', style: { 'border-width': 3, 'border-color': accent, 'underlay-color': accent, 'underlay-opacity': 0.18 } },
    { selector: '.faded', style: { 'opacity': 0.13 } },
    { selector: 'node.trail-3', style: { 'underlay-color': current, 'underlay-opacity': 0.1 } },
    { selector: 'node.trail-2', style: { 'underlay-color': current, 'underlay-opacity': 0.18 } },
    { selector: 'node.trail-1', style: { 'underlay-color': current, 'underlay-opacity': 0.28 } },
    { selector: 'node.live-active', style: { 'underlay-color': live, 'underlay-opacity': 0.55, 'underlay-padding': 10, 'border-color': live, 'border-width': 2.5, 'opacity': 1 } },
    { selector: 'node.on-stack', style: { 'underlay-color': current, 'underlay-opacity': 0.3, 'border-color': current, 'border-width': 2.5, 'opacity': 1 } },
    { selector: 'node.current', style: { 'underlay-color': current, 'underlay-opacity': 0.7, 'underlay-padding': 12, 'border-color': current, 'border-width': 3.5, 'opacity': 1, 'z-index': 99 } },

    {
      selector: 'edge',
      style: {
        'curve-style': 'bezier', 'width': 'data(width)', 'line-color': muted, 'target-arrow-color': muted, 'source-arrow-color': muted,
        'target-arrow-shape': 'triangle', 'arrow-scale': 0.9, 'opacity': 0.75,
        'transition-property': 'opacity', 'transition-duration': '180ms',
      },
    },
  ];
  for (const k of EDGE_KINDS) {
    const s = { 'line-color': k.color, 'target-arrow-color': k.color, 'source-arrow-color': k.color };
    if (k.style !== 'solid') s['line-style'] = k.style;
    if (k.kind === 'inherits' || k.kind === 'implements') Object.assign(s, { 'target-arrow-shape': 'triangle', 'target-arrow-fill': 'hollow', 'arrow-scale': 1.3 });
    if (k.kind === 'has') Object.assign(s, { 'source-arrow-shape': 'diamond', 'target-arrow-shape': 'vee', 'arrow-scale': 1 });
    if (k.kind === 'uses') Object.assign(s, { 'opacity': 0.45, 'target-arrow-shape': 'vee' });
    if (k.kind === 'nested') Object.assign(s, { 'source-arrow-shape': 'circle', 'target-arrow-shape': 'none', 'opacity': 0.5 });
    style.push({ selector: `edge[kind = "${k.kind}"]`, style: s });
  }
  style.push(
    { selector: 'edge[kind = "dispatch"]', style: { 'line-color': '#56c99a', 'target-arrow-color': '#56c99a', 'line-style': 'dashed', 'target-arrow-shape': 'triangle-backcurve' } },
    { selector: 'edge.planned', style: { 'line-color': '#ff79c6', 'target-arrow-color': '#ff79c6', 'source-arrow-color': '#ff79c6', 'line-style': 'dashed', 'line-dash-pattern': [8, 4], 'width': 2.5, 'opacity': 0.95 } },
    { selector: 'edge.observed', style: { 'line-color': live, 'target-arrow-color': live, 'line-style': 'dashed', 'line-dash-pattern': [6, 6], 'width': 2.5, 'opacity': 0.95 } },
    { selector: 'edge.violation', style: { 'line-color': '#ff6b6b', 'target-arrow-color': '#ff6b6b', 'source-arrow-color': '#ff6b6b', 'line-style': 'dashed', 'line-dash-pattern': [4, 4], 'width': 3, 'opacity': 1, 'z-index': 90 } },
    { selector: 'node.impact', style: { 'border-color': '#f2b544', 'border-width': 3, 'underlay-color': '#f2b544', 'underlay-opacity': 0.25, 'opacity': 1 } },
    {
      selector: 'edge.stack',
      style: {
        'line-color': current, 'target-arrow-color': current, 'width': 4, 'line-style': 'dashed', 'line-dash-pattern': [10, 6],
        'curve-style': 'unbundled-bezier', 'control-point-distances': [30], 'control-point-weights': [0.5], 'opacity': 1, 'z-index': 100,
        'target-arrow-shape': 'triangle', 'arrow-scale': 1.3,
      },
    },
    { selector: 'edge.faded', style: { 'opacity': 0.06 } },
    { selector: 'node:active', style: { 'overlay-opacity': 0.06 } },
  );
  void bg;
  return style;
}
