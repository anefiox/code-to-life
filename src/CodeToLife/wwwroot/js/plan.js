// Plan mode: sketch types that don't exist yet, connect them to the real code, leave notes, and
// turn the sketch into C# stubs. Saved to .codetolife/plan.json beside the code.
import { state, on, emit, api, escapeHtml, toast } from './state.js';
import * as graph from './graph.js';

const $ = id => document.getElementById(id);
let connectFrom = null;
let connectKind = 'calls';

export async function initPlan() {
  renderPane();
  $('planToggle').addEventListener('click', () => setPlanMode(!state.planMode));
  on('plan-edit', id => editType(state.plan.types.find(t => t.id === id)));
  on('plan-delete', id => removeType(id));
  on('plan-note', id => addNote(id));
  on('plan-connect', id => startConnect(id));
  on('sse:plan.changed', load);
  await load();
}

async function load() {
  try {
    const plan = await api('/plan');
    state.plan = { types: plan.types ?? [], edges: plan.edges ?? [], notes: plan.notes ?? [] };
  } catch { state.plan = { types: [], edges: [], notes: [] }; }
  renderList();
  if (state.graph) graph.render();
}

async function save() {
  try { await api('/plan', { method: 'PUT', body: state.plan }); }
  catch (e) { toast(`Couldn't save the plan: ${e.message}`, 'error'); }
  renderList();
  graph.render();
}

function setPlanMode(on) {
  state.planMode = on;
  $('planToggle').classList.toggle('on', on);
  if (on) emit('show-tab', 'plan');
}

function renderPane() {
  $('planPane').innerHTML = `
    <div class="row">
      <button id="planAdd" class="primary">+ New type</button>
      <label class="select">Connect as
        <select id="planKind">
          <option value="calls">calls</option><option value="has">has (field)</option><option value="creates">creates</option>
          <option value="implements">implements</option><option value="inherits">inherits</option><option value="uses">mentions</option>
        </select></label>
      <button id="planConnect" title="Click the source type, then the target">⇢ Connect…</button>
      <button id="planStubs">{ } C# stubs</button>
      <span id="planHint" class="muted"></span>
    </div>
    <div class="cols">
      <div class="box"><h4>Planned types</h4><ul id="planTypes" class="plain"></ul></div>
      <div class="box"><h4>Planned connections</h4><ul id="planEdges" class="plain"></ul></div>
      <div class="box"><h4>Notes</h4><ul id="planNotes" class="plain"></ul></div>
    </div>`;
  $('planAdd').addEventListener('click', () => editType(null));
  $('planConnect').addEventListener('click', () => startConnect(state.selected && graph.getCy().getElementById(state.selected).length && !state.members.has(state.selected) ? state.selected : null));
  $('planKind').addEventListener('change', e => { connectKind = e.target.value; });
  $('planStubs').addEventListener('click', showStubs);
}

function renderList() {
  if (!$('planTypes')) return;
  const name = id => state.types.get(id)?.name ?? state.plan.types.find(t => t.id === id)?.name ?? id;
  $('planTypes').innerHTML = state.plan.types.map(t => `<li style="display:flex;gap:6px;padding:2px 0"><a data-go="${escapeHtml(t.id)}" style="color:var(--accent);cursor:pointer;flex:1">${escapeHtml(t.name)}</a><span class="muted">${escapeHtml(t.kind)}</span><button data-edit="${escapeHtml(t.id)}" class="icon">✎</button><button data-del="${escapeHtml(t.id)}" class="icon danger">✕</button></li>`).join('')
    || '<li class="muted">Nothing yet. Add a type, then connect it to the real code.</li>';
  $('planEdges').innerHTML = state.plan.edges.map((e, i) => `<li style="display:flex;gap:6px;padding:2px 0"><span style="flex:1">${escapeHtml(name(e.source))} <span class="muted">${e.kind}</span> ${escapeHtml(name(e.target))}${e.note ? ` <span class="muted">· ${escapeHtml(e.note)}</span>` : ''}</span><button data-edel="${i}" class="icon danger">✕</button></li>`).join('')
    || '<li class="muted">None.</li>';
  $('planNotes').innerHTML = state.plan.notes.map((n, i) => `<li style="display:flex;gap:6px;padding:2px 0"><a data-go="${escapeHtml(n.targetId)}" style="color:var(--accent);cursor:pointer">${escapeHtml(name(n.targetId))}</a><span style="flex:1">${escapeHtml(n.text)}</span><button data-ndel="${i}" class="icon danger">✕</button></li>`).join('')
    || '<li class="muted">Right-click a type → Add a note.</li>';
  $('planPane').querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => emit('go', a.dataset.go)));
  $('planPane').querySelectorAll('[data-edit]').forEach(b => b.addEventListener('click', () => editType(state.plan.types.find(t => t.id === b.dataset.edit))));
  $('planPane').querySelectorAll('[data-del]').forEach(b => b.addEventListener('click', () => removeType(b.dataset.del)));
  $('planPane').querySelectorAll('[data-edel]').forEach(b => b.addEventListener('click', () => { state.plan.edges.splice(Number(b.dataset.edel), 1); save(); }));
  $('planPane').querySelectorAll('[data-ndel]').forEach(b => b.addEventListener('click', () => { state.plan.notes.splice(Number(b.dataset.ndel), 1); save(); }));
}

function editType(existing) {
  const namespaces = [...new Set(state.graph?.types.map(t => t.namespace).filter(Boolean) ?? [])].sort();
  const selectedType = state.types.get(state.selected);
  emit('modal', {
    title: existing ? `Edit ${existing.name}` : 'Plan a new type',
    html: `<div class="form">
      <label>Name</label><input id="pName" type="text" value="${escapeHtml(existing?.name ?? '')}" placeholder="DiscountService" autofocus>
      <label>Kind</label><select id="pKind">${['class', 'interface', 'record', 'abstract class', 'static class', 'struct', 'enum'].map(k => `<option ${existing?.kind === k ? 'selected' : ''}>${k}</option>`).join('')}</select>
      <label>Namespace</label><input id="pNs" type="text" list="pNsList" value="${escapeHtml(existing?.namespace ?? selectedType?.namespace ?? namespaces[0] ?? '')}"><datalist id="pNsList">${namespaces.map(n => `<option value="${escapeHtml(n)}">`).join('')}</datalist>
      <label>Purpose</label><input id="pNote" type="text" value="${escapeHtml(existing?.note ?? '')}" placeholder="What it's for, in a sentence">
      <label class="full">Members, one per line (C# signatures)</label>
      <textarea id="pMembers" class="full" placeholder="decimal Apply(Order order)\nstring Name { get; }">${escapeHtml((existing?.members ?? []).map(m => m.signature).join('\n'))}</textarea>
      ${!existing && selectedType ? `<label class="check full"><input type="checkbox" id="pLink" checked> Connect from ${escapeHtml(selectedType.name)} (calls)</label>` : ''}
      <div class="full" style="display:flex;gap:8px;justify-content:flex-end"><button value="close">Cancel</button><button id="pSave" class="primary" type="button">Save</button></div>
    </div>`,
    wire: (body, close) => {
      body.querySelector('#pName').focus();
      body.querySelector('#pSave').addEventListener('click', () => {
        const name = body.querySelector('#pName').value.trim().replace(/\s+/g, '');
        if (!/^[A-Za-z_][A-Za-z0-9_<>,]*$/.test(name)) { toast('Give it a valid C# type name.', 'error'); return; }
        const ns = body.querySelector('#pNs').value.trim();
        const kind = body.querySelector('#pKind').value;
        const members = body.querySelector('#pMembers').value.split('\n').map(s => s.trim()).filter(Boolean).map(signature => ({
          name: (signature.match(/([A-Za-z_][A-Za-z0-9_]*)\s*(\(|\{|$|=)/) ?? [, signature])[1],
          kind: signature.includes('(') ? 'method' : signature.includes('{') ? 'property' : kind === 'enum' ? 'enum member' : 'field',
          signature,
        }));
        const id = existing?.id ?? `plan:${ns ? ns + '.' : ''}${name}`;
        const type = { id, name, namespace: ns, kind, members, note: body.querySelector('#pNote').value.trim() || null };
        if (existing) Object.assign(existing, type);
        else {
          state.plan.types.push(type);
          if (body.querySelector('#pLink')?.checked && selectedType) state.plan.edges.push({ source: selectedType.id, target: id, kind: 'calls', note: null });
        }
        close();
        save().then(() => graph.select(id));
      });
    },
  });
}

function removeType(id) {
  state.plan.types = state.plan.types.filter(t => t.id !== id);
  state.plan.edges = state.plan.edges.filter(e => e.source !== id && e.target !== id);
  state.plan.notes = state.plan.notes.filter(n => n.targetId !== id);
  graph.select(null);
  save();
}

function addNote(targetId) {
  const name = state.types.get(targetId)?.name ?? targetId;
  emit('modal', {
    title: `Note on ${name}`,
    html: `<div class="form"><textarea id="nText" class="full" placeholder="Split this up: pricing shouldn't know about shipping." autofocus></textarea>
      <div class="full" style="display:flex;gap:8px;justify-content:flex-end"><button value="close">Cancel</button><button id="nSave" class="primary" type="button">Save note</button></div></div>`,
    wire: (body, close) => {
      body.querySelector('#nText').focus();
      body.querySelector('#nSave').addEventListener('click', () => {
        const text = body.querySelector('#nText').value.trim();
        if (!text) return;
        state.plan.notes.push({ targetId, text, color: 'warn' });
        close();
        save().then(() => emit('select', targetId));
      });
    },
  });
}

function startConnect(from) {
  connectKind = $('planKind').value;
  connectFrom = from;
  emit('show-tab', 'plan');
  hint(from ? `Now click the type ${state.types.get(from)?.name ?? state.plan.types.find(t => t.id === from)?.name} should ${connectKind}… (Esc to cancel)` : 'Click the source type… (Esc to cancel)');
  if (state.view.kind !== 'types') graph.goHome();
  graph.setTapInterceptor(id => {
    if (id === null) { stopConnect(); return true; }
    const isType = state.types.has(id) || state.plan.types.some(t => t.id === id);
    if (!isType) return true;
    if (!connectFrom) {
      connectFrom = id;
      hint(`Now click the type it should ${connectKind}… (Esc to cancel)`);
      return true;
    }
    if (id !== connectFrom) {
      state.plan.edges.push({ source: connectFrom, target: id, kind: connectKind, note: null });
      save();
      toast('Connection planned.');
    }
    stopConnect();
    return true;
  });
  const esc = e => { if (e.key === 'Escape') { stopConnect(); document.removeEventListener('keydown', esc); } };
  document.addEventListener('keydown', esc);
}

function stopConnect() {
  connectFrom = null;
  graph.setTapInterceptor(null);
  hint('');
}

const hint = text => { $('planHint').textContent = text; };

async function showStubs() {
  let code;
  try { code = await api('/plan/stubs', { text: true }); } catch (e) { toast(e.message, 'error'); return; }
  emit('modal', {
    title: 'C# stubs for the plan',
    html: `<pre id="stubCode">${escapeHtml(code)}</pre><div style="display:flex;justify-content:flex-end;margin-top:10px;gap:8px"><button id="stubCopy" type="button" class="primary">Copy</button></div>`,
    wire: body => body.querySelector('#stubCopy').addEventListener('click', async () => {
      await navigator.clipboard.writeText(code);
      toast('Copied.');
    }),
  });
}
