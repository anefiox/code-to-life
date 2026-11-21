// Shared state and plumbing for every part of the UI.

export const state = {
  graph: null,
  types: new Map(),        // id -> type
  members: new Map(),      // id -> member
  memberType: new Map(),   // member id -> type id
  callsFrom: new Map(),    // member id -> [call]
  callsTo: new Map(),      // member id -> [call]
  implementers: new Map(), // interface member id -> [member id]
  edgesFrom: new Map(),    // type id -> [edge]
  edgesTo: new Map(),      // type id -> [edge]

  view: { kind: 'folders' }, // or { kind: 'types' } / { kind: 'members', typeId } / { kind: 'calls', memberId }
  viewStack: [],
  selected: null,          // node id
  focusDepth: 0,           // 0 = everything

  overlay: 'kind',
  layout: 'fcose',
  options: { groupNamespaces: true, simplifyEdges: true, hideTests: false, hideIsolated: false, showNested: false },
  hiddenProjects: new Set(),
  hiddenNamespaces: new Set(),
  hiddenKinds: new Set(['delegate']),
  shownEdges: new Set(['inherits', 'implements', 'has', 'calls', 'creates']),

  changes: null,
  tests: null,             // { tests: Set, projects: Set, members: Map(id -> nearest depth), types: Map(id -> coverage) }
  rules: null,             // the last architecture check: { hasRules, violations, problems }
  trace: { frames: [], active: 0 }, // a stack trace pasted into the Trace tab, drawn like the debugger's stack
  incidents: null,         // type id -> failures from a scanned log that pass through it
  live: { processId: null, heat: new Map(), typeHeat: new Map(), active: new Set(), observed: new Map(), heap: new Map(), samples: 0,
    waits: new Map(), typeWaits: new Map(), events: null }, // waits: member/type id -> ms spent awaiting (runtime events)
  debug: { state: 'idle', frames: [], breakpoints: {}, activeFrame: 0, trail: [], animating: false },
  plan: { types: [], edges: [], notes: [] },
  planMode: false,
  status: null,
};

const bus = new EventTarget();
export const on = (name, fn) => bus.addEventListener(name, e => fn(e.detail));
export const emit = (name, detail) => bus.dispatchEvent(new CustomEvent(name, { detail }));

export async function api(path, { method = 'GET', body, text = false } = {}) {
  const response = await fetch('/api' + path, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (text) {
    if (!response.ok) throw new Error(await response.text() || response.statusText);
    return response.text();
  }
  const data = response.headers.get('content-type')?.includes('json') ? await response.json() : null;
  if (!response.ok) throw new Error(data?.error ?? data?.detail ?? data?.title ?? response.statusText);
  return data;
}

export function indexGraph(graph) {
  state.graph = graph;
  for (const map of [state.types, state.members, state.memberType, state.callsFrom, state.callsTo, state.implementers, state.edgesFrom, state.edgesTo]) map.clear();
  for (const type of graph.types) {
    state.types.set(type.id, type);
    for (const member of type.members) {
      state.members.set(member.id, member);
      state.memberType.set(member.id, type.id);
    }
  }
  for (const type of graph.types)
    for (const member of type.members)
      for (const target of member.implements) push(state.implementers, target, member.id);
  for (const call of graph.calls) {
    push(state.callsFrom, call.source, call);
    push(state.callsTo, call.target, call);
  }
  for (const edge of graph.edges) {
    push(state.edgesFrom, edge.source, edge);
    push(state.edgesTo, edge.target, edge);
  }
}

function push(map, key, value) {
  const list = map.get(key);
  if (list) list.push(value); else map.set(key, [value]);
}

/** The type a graph node belongs to: itself for a type, its owner for a member. */
export const typeOf = id => state.types.has(id) ? id : state.memberType.get(id) ?? null;

export const projectOf = typeId => state.types.get(typeId)?.project;

export const isTestProject = name => state.graph?.projects.find(p => p.name === name)?.outputKind === 'test';

export function shortPath(file) {
  if (!file) return '';
  const root = (state.graph?.root ?? '').replaceAll('\\', '/').toLowerCase();
  const f = file.replaceAll('\\', '/');
  return f.toLowerCase().startsWith(root) ? f.slice(root.length).replace(/^\//, '') : f;
}

export const fileName = file => file ? file.replaceAll('\\', '/').split('/').pop() : '';

export function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function memberLabel(member) {
  if (!member) return '';
  if (member.kind === 'top-level statements') return 'top-level code';
  if (member.kind === 'constructor' || member.kind === 'primary constructor') return 'new()';
  return member.kind === 'method' || member.kind === 'operator' ? member.name + '()' : member.name;
}

export function toast(message, kind = 'info') {
  emit('output', { category: kind === 'error' ? 'stderr' : 'info', output: message + '\n' });
  const pill = document.getElementById('statusPill');
  const el = document.createElement('div');
  el.className = 'toast';
  el.textContent = message;
  el.style.cssText = `position:fixed;left:50%;top:60px;transform:translateX(-50%);z-index:100;background:var(--panel);border:1px solid ${kind === 'error' ? 'var(--bad)' : 'var(--line)'};color:var(--text);padding:8px 14px;border-radius:8px;box-shadow:var(--shadow);max-width:70vw`;
  document.body.appendChild(el);
  setTimeout(() => el.remove(), kind === 'error' ? 6000 : 3000);
  void pill;
}

export function debounce(fn, ms) {
  let timer;
  return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}
