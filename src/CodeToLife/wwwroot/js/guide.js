// Helpers for understanding a codebase quickly: "How do I…" recipes, the settings map, the Razor
// template map, framework term tooltips, examples from tests, Git history, step-by-step page
// playback, reading progress, the onboarding guide and "Ask the codebase".
import { state, on, emit, api, escapeHtml, shortPath, toast } from './state.js';
import * as graph from './graph.js';
import { highlight } from './panels.js';
import { learn, showFlow, showTemplate } from './learn.js';

const $ = id => document.getElementById(id);

export const guide = {
  recipes: null, settings: null, templates: null, history: null, concepts: [], matchers: [],
  read: new Set(), playback: null, asking: false, answer: null,
};

const plural = (count, word) => `${count} ${count === 1 ? word : word === 'person' ? 'people' : /(s|x|ch|sh)$/.test(word) ? word + 'es' : word + 's'}`;

export function initGuide() {
  on('graph', () => {
    guide.recipes = guide.settings = guide.history = null;
    loadProgress();
    loadTemplates();
    paintActivePane();
  });
  on('rendered', paintRead);
  on('select', id => {
    if (id?.startsWith('template:')) inspectTemplate(id.slice('template:'.length));
    else if (id?.startsWith('thandler:')) emit('go', id.slice('thandler:'.length));
    if (guide.playback && id && !id.startsWith('template:')) stopPlayback(false);
  });
  on('show-tab', tab => paintPane(tab));
  initTips();
}

function paintActivePane() {
  const active = document.querySelector('.dock-tabs button.active')?.dataset.tab;
  if (active) paintPane(active);
}

function paintPane(tab) {
  if (tab === 'recipes') paintRecipes();
  else if (tab === 'settings') paintSettings();
  else if (tab === 'ask') paintAsk();
}

/** Opens a file at a line: the class or method there, a Razor view, or else the file itself. */
export function openFile(file, line = 1) {
  if (guide.templates?.templates.some(t => t.file === file)) { inspectTemplate(file, line); return; }
  // C# files: the smallest class or method around the line (or the file's first class for line 1).
  const types = state.graph.types.filter(t => t.file && shortPath(t.file).toLowerCase() === file.toLowerCase());
  let best = line <= 1 ? types[0]?.id ?? null : null;
  let span = Infinity;
  if (line > 1)
    for (const type of types) {
      if (type.line <= line && type.endLine >= line && type.endLine - type.line < span) { best = type.id; span = type.endLine - type.line; }
      for (const m of type.members) {
        const top = m.kind === 'top-level statements';
        if ((top || (m.line <= line && m.endLine >= line)) && (top ? 1e9 : m.endLine - m.line) < span) { best = m.id; span = top ? 1e9 : m.endLine - m.line; }
      }
    }
  if (best) emit('go', best);
  else showProjectFile(file, line);
}

const fileLink = (file, line = 1) => `<a data-file="${escapeHtml(file)}" data-line="${line}">${escapeHtml(file)}${line > 1 ? ':' + line : ''}</a>`;

function wire(root) {
  root.querySelectorAll('[data-file]').forEach(a => a.addEventListener('click', () => openFile(a.dataset.file, Number(a.dataset.line) || 1)));
  root.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => emit('go', a.dataset.go)));
  root.querySelectorAll('[data-template]').forEach(a => a.addEventListener('click', () => inspectTemplate(a.dataset.template)));
}

function showBody(html) {
  $('detailsEmpty').hidden = true;
  const body = $('detailsBody');
  body.hidden = false;
  body.innerHTML = html;
  wire(body);
  return body;
}

const sourceRows = (code, start = 1, mark = 0, csharp = false) => code.split('\n').map((text, i) =>
  `<div class="ln${start + i === mark ? ' hit' : ''}"><span class="gutter"></span><span class="num">${start + i}</span><span class="txt">${csharp ? highlight(text) : escapeHtml(text)}</span></div>`).join('');

function scrollToHit(root) {
  const hit = root.querySelector('.ln.hit');
  if (hit) requestAnimationFrame(() => hit.scrollIntoView({ block: 'center' }));
}

async function showProjectFile(file, line = 1) {
  const body = showBody(`<div class="d-head"><div class="kind"><span>file</span></div><h2>${escapeHtml(file)}</h2></div><div class="d-section muted">Loading…</div>`);
  try {
    const source = await api('/file?file=' + encodeURIComponent(file));
    body.querySelector('.d-section').outerHTML = `<pre class="source">${sourceRows(source.code, 1, line, file.endsWith('.cs'))}</pre>`;
    scrollToHit(body);
  } catch (e) {
    body.querySelector('.d-section').textContent = e.message;
  }
}

// ---------------------------------------------------------------- framework terms

export function setConcepts(concepts) {
  guide.concepts = concepts ?? [];
  guide.matchers = guide.concepts.map(concept => ({
    concept,
    pattern: new RegExp('(?<![\\w/.#-])(?:' + concept.pattern + ')(?![\\w-])', /[A-Z]/.test(concept.pattern) ? 'g' : 'gi'),
  }));
}

/** Escaped text with the first mention of each framework term underlined; hover or focus shows what it means. */
export function annotate(text) {
  const value = String(text ?? '');
  const hits = [];
  for (const { concept, pattern } of guide.matchers) {
    pattern.lastIndex = 0;
    const m = pattern.exec(value);
    if (m && m[0]) hits.push({ start: m.index, end: m.index + m[0].length, concept });
  }
  hits.sort((a, b) => a.start - b.start || b.end - a.end);
  let at = 0;
  let html = '';
  for (const hit of hits) {
    if (hit.start < at) continue;
    const word = value.slice(hit.start, hit.end);
    html += escapeHtml(value.slice(at, hit.start)) + `<span class="term" tabindex="0" data-term="${escapeHtml(hit.concept.term)}" data-meaning="${escapeHtml(hit.concept.meaning)}" aria-label="${escapeHtml(word + ': ' + hit.concept.meaning)}">${escapeHtml(word)}</span>`;
    at = hit.end;
  }
  return html + escapeHtml(value.slice(at));
}

function initTips() {
  const tip = document.createElement('div');
  tip.id = 'termTip';
  tip.className = 'term-tip';
  tip.hidden = true;
  tip.setAttribute('role', 'tooltip');
  document.body.append(tip);
  const show = target => {
    tip.innerHTML = `<b>${escapeHtml(target.dataset.term)}</b><span>${escapeHtml(target.dataset.meaning)}</span>`;
    tip.hidden = false;
    const box = target.getBoundingClientRect();
    const width = Math.min(300, innerWidth - 16);
    tip.style.width = width + 'px';
    tip.style.left = Math.max(8, Math.min(box.left, innerWidth - width - 8)) + 'px';
    const below = box.bottom + 6;
    tip.style.top = (below + tip.offsetHeight > innerHeight ? box.top - tip.offsetHeight - 6 : below) + 'px';
  };
  document.addEventListener('mouseover', e => { const t = e.target.closest?.('.term'); if (t) show(t); });
  document.addEventListener('mouseout', e => { if (e.target.closest?.('.term')) tip.hidden = true; });
  document.addEventListener('focusin', e => { if (e.target.classList?.contains('term')) show(e.target); else tip.hidden = true; });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') tip.hidden = true; });
  $('details').addEventListener('scroll', () => { tip.hidden = true; });
}

// ---------------------------------------------------------------- reading progress

const progressKey = () => 'codetolife:read:' + (state.graph?.root ?? '');
const tourKey = () => 'codetolife:tour:' + (state.graph?.root ?? '');

function loadProgress() {
  try { guide.read = new Set(JSON.parse(localStorage.getItem(progressKey()) ?? '[]')); }
  catch { guide.read = new Set(); }
}

function saveProgress() {
  try { localStorage.setItem(progressKey(), JSON.stringify([...guide.read])); }
  catch { /* Storage may be blocked; progress then lasts for this visit only. */ }
}

export function savedTourStep() {
  try { return Number(localStorage.getItem(tourKey()) ?? -1); } catch { return -1; }
}

export function saveTourStep(index) {
  try { localStorage.setItem(tourKey(), String(Math.max(index, savedTourStep()))); } catch { /* see saveProgress */ }
}

export const isRead = id => guide.read.has(id);

export function setRead(id, read) {
  if (read) guide.read.add(id); else guide.read.delete(id);
  saveProgress();
  graph.getCy()?.getElementById(id).toggleClass('read', read);
}

function paintRead() {
  const cy = graph.getCy();
  if (!cy || !guide.read.size) return;
  cy.batch(() => cy.nodes().forEach(n => { if (guide.read.has(n.id())) n.addClass('read'); }));
}

// ---------------------------------------------------------------- overview

/** Adds progress, the onboarding guide, hotspots and terms to the "New to this code?" box. */
export function paintGuideOverview(box) {
  const tests = new Set(state.graph.projects.filter(p => p.outputKind === 'test').map(p => p.name));
  const types = state.graph.types.filter(t => !tests.has(t.project) && !t.containingType);
  const read = types.filter(t => guide.read.has(t.id)).length;
  const steps = learn.tour.length;
  const reached = savedTourStep();
  const div = document.createElement('div');
  div.className = 'guide-overview';
  div.innerHTML = `
    <h3>Your progress</h3>
    <p>You've marked ${read} of ${plural(types.length, 'class')} as read.</p>
    <progress max="${types.length || 1}" value="${read}" aria-label="Classes read"></progress>
    <div class="row">
      ${steps ? `<button data-resume>${reached >= 0 && reached < steps - 1 ? `Continue Start here at step ${reached + 2}` : reached >= steps - 1 ? 'Start here again' : 'Take the Start here tour'}</button>` : ''}
      ${read || reached >= 0 ? '<button data-reset class="link-button">Clear my progress</button>' : ''}
    </div>
    <h3>Share it</h3>
    <p>One page with the projects, tools, how to run it, the folders, the tour, pages, settings, recipes and explanations. Commit it as ONBOARDING.md for the team.</p>
    <div class="row"><button data-download>⇩ Onboarding guide (Markdown)</button></div>
    <div data-hotspots></div>
    ${guide.concepts.length ? `<details class="terms"><summary>Terms you'll meet (${guide.concepts.length})</summary><dl class="glossary">${guide.concepts.map(c =>
      `<dt>${escapeHtml(c.term)}</dt><dd>${escapeHtml(c.meaning)}</dd>`).join('')}</dl></details>` : ''}`;
  const glossary = box.querySelector('dl.glossary');
  if (glossary) glossary.previousElementSibling.before(div); else box.append(div);
  div.querySelector('[data-resume]')?.addEventListener('click', () => emit('tour-resume', reached >= 0 && reached < steps - 1 ? reached + 1 : 0));
  div.querySelector('[data-reset]')?.addEventListener('click', () => {
    guide.read.clear();
    saveProgress();
    try { localStorage.removeItem(tourKey()); } catch { /* see saveProgress */ }
    graph.getCy()?.nodes().removeClass('read');
    emit('overview');
  });
  div.querySelector('[data-download]').addEventListener('click', downloadGuide);
  paintHotspots(div.querySelector('[data-hotspots]'));
}

async function downloadGuide(e) {
  const button = e.currentTarget;
  button.disabled = true;
  try {
    const text = await api('/onboarding', { text: true });
    const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = 'ONBOARDING.md';
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (error) {
    toast("Couldn't build the guide: " + error.message, 'error');
  } finally {
    button.disabled = false;
  }
}

async function paintHotspots(slot) {
  try { guide.history ??= await api('/history'); } catch { return; }
  const h = guide.history;
  if (!h.available || !h.files.length || !slot.isConnected) return;
  slot.innerHTML = `<h3>Most changed files</h3>
    <p>Where most of the work happens: ${plural(h.commits, 'commit')}${h.limited ? ' (the latest)' : ''} by ${plural(h.contributors, 'person')}${h.first ? `, ${escapeHtml(h.first)} to ${escapeHtml(h.last)}` : ''}.</p>
    <ul class="rel">${h.files.slice(0, 8).map(f => `<li><span class="k">${f.commits}×</span>${fileLink(f.file)}${f.authors[0] ? ` <span class="muted">mostly ${escapeHtml(f.authors[0].name)}</span>` : ''}</li>`).join('')}</ul>`;
  wire(slot);
}

// ---------------------------------------------------------------- details additions

/** Read toggle, examples from tests and history, added under "What it does". */
export function guideDetails(slot, id) {
  const extra = document.createElement('div');
  extra.innerHTML = `<label class="read-toggle"><input type="checkbox" ${isRead(id) ? 'checked' : ''}> I've read this</label>
    <div data-examples></div><div data-history></div>`;
  slot.querySelector('.explanation h3')?.after(extra.firstElementChild);
  slot.append(...extra.children);
  slot.querySelector('.read-toggle input').addEventListener('change', e => setRead(id, e.target.checked));
  paintExamples(slot.querySelector('[data-examples]'), id);
  paintHistory(slot.querySelector('[data-history]'), id);
}

async function paintExamples(slot, id) {
  let data;
  try { data = await api('/examples?id=' + encodeURIComponent(id)); } catch { return; }
  if (!slot.isConnected) return;
  const tests = new Set(state.graph.projects.filter(p => p.outputKind === 'test').map(p => p.name));
  const type = state.types.get(state.types.has(id) ? id : state.memberType.get(id));
  if (type && tests.has(type.project)) return;
  if (!data.examples.length) {
    slot.innerHTML = `<div class="d-section"><h3>Examples from tests</h3><p class="muted">No tests call this yet.</p></div>`;
    return;
  }
  slot.innerHTML = `<div class="d-section"><h3>Examples from tests</h3><p class="muted">How the tests use it: real inputs and the results they expect.</p>
    ${data.examples.map(e => `<p class="example-title"><a data-go="${escapeHtml(e.id)}">${escapeHtml(e.title)}</a> <span class="muted">${escapeHtml(e.file)}:${e.start}</span></p>
      <pre class="source">${sourceRows(e.code, e.start, 0, true)}</pre>`).join('')}</div>`;
  wire(slot);
}

async function paintHistory(slot, id) {
  let data;
  try { data = await api('/history?id=' + encodeURIComponent(id)); } catch { return; }
  if (!slot.isConnected || !data.available) return;
  slot.innerHTML = `<div class="d-section"><h3>History</h3>${!data.commits.length ? `<p class="muted">${escapeHtml(data.note ?? 'No commits found.')}</p>` : `
    ${data.authors.length ? `<p>Who to ask: ${data.authors.map(a => `${escapeHtml(a.name)} (${plural(a.commits, 'commit')})`).join(', ')}, from the latest changes.</p>` : ''}
    <ul class="commits">${data.commits.map(c => `<li>${escapeHtml(c.date)} · ${escapeHtml(c.subject)} · ${escapeHtml(c.author)} <code>${escapeHtml(c.hash)}</code></li>`).join('')}</ul>`}</div>`;
}

// ---------------------------------------------------------------- how do I…

async function paintRecipes() {
  const pane = $('recipesPane');
  if (!state.graph) return;
  if (!guide.recipes) {
    pane.innerHTML = '<p class="muted">Working out the patterns…</p>';
    try { guide.recipes = (await api('/recipes')).recipes; }
    catch (e) { pane.innerHTML = `<div class="notice">${escapeHtml(e.message)}</div>`; return; }
  }
  if (!guide.recipes.length) { pane.innerHTML = '<div class="notice">No common patterns found yet in this code.</div>'; return; }
  pane.innerHTML = `<p class="muted">Checklists for common changes, worked out from what this code already does. Each step links to an example to copy.</p>
    <div class="recipes">${guide.recipes.map((r, i) => `<details class="recipe" ${i === 1 ? 'open' : ''}><summary>${escapeHtml(r.title)}</summary><p>${annotate(r.summary)}</p>
      <ol>${r.steps.map(s => `<li>${annotate(s.text)}${s.file ? ' ' + fileLink(s.file, s.line) : ''}</li>`).join('')}</ol></details>`).join('')}</div>`;
  wire(pane);
}

// ---------------------------------------------------------------- settings

async function paintSettings() {
  const pane = $('settingsPane');
  if (!state.graph) return;
  if (!guide.settings) {
    pane.innerHTML = '<p class="muted">Reading the settings…</p>';
    try { guide.settings = await api('/settings'); }
    catch (e) { pane.innerHTML = `<div class="notice">${escapeHtml(e.message)}</div>`; return; }
  }
  const { settings, schema } = guide.settings;
  if (!settings.length) { pane.innerHTML = '<div class="notice">No settings found. Code to Life reads appsettings.json and the environment variables the code asks for.</div>'; return; }
  const filter = pane.querySelector('input')?.value ?? '';
  pane.innerHTML = `<div class="row"><input type="search" placeholder="Filter settings" aria-label="Filter settings" value="${escapeHtml(filter)}">
    <span class="muted">${plural(settings.length, 'setting')}${schema ? `, from ${fileLink(schema)}` : ''}. Environment variables override them without changing code; : becomes __.</span></div>
    <div class="settings-list"></div>`;
  const list = pane.querySelector('.settings-list');
  const input = pane.querySelector('input');
  const draw = () => {
    const term = input.value.trim().toLowerCase();
    list.innerHTML = settings.filter(s => !term || `${s.key} ${s.env ?? ''} ${s.doc}`.toLowerCase().includes(term)).map(s => `
      <details class="setting"><summary><code>${escapeHtml(s.key)}</code>${s.env && s.env !== s.key ? ` <span class="env">${escapeHtml(s.env)}</span>` : ''}${s.varies ? ' <span class="tag">differs by environment</span>' : ''}</summary>
        ${s.doc ? `<p>${annotate(s.doc)}</p>` : ''}
        <dl class="facts">
          <dt>Default</dt><dd>${s.sensitive ? '<span class="muted">(secret, not shown)</span>' : `<code>${escapeHtml(s.default || '(none)')}</code>`}</dd>
          ${Object.entries(s.environments).map(([env, v]) => `<dt>In ${escapeHtml(env)}</dt><dd>${v === null ? '<span class="muted">(secret)</span>' : `<code>${escapeHtml(v)}</code>`}</dd>`).join('')}
          ${s.file ? `<dt>Defined in</dt><dd>${fileLink(s.file, s.line)}</dd>` : ''}
          ${s.setIn.length ? `<dt>Set in</dt><dd>${s.setIn.map(x => fileLink(x.file, x.line) + (x.value !== null ? ` = <code>${escapeHtml(x.value)}</code>` : '')).join(', ')}</dd>` : ''}
        </dl>
        ${s.uses.length ? `<p class="muted">Read by:</p><ul class="rel">${s.uses.map(u => `<li>${u.id ? `<a data-go="${escapeHtml(u.id)}">${escapeHtml(label(u.id))}</a> ` : ''}${fileLink(u.file, u.line)}${u.key !== s.key ? ` <span class="muted">(${escapeHtml(u.key)})</span>` : ''}</li>`).join('')}</ul>`
          : '<p class="muted">Nothing in the code reads this directly. ASP.NET Core may read it itself.</p>'}
      </details>`).join('');
    wire(list);
  };
  input.addEventListener('input', draw);
  wire(pane.querySelector('.row'));
  draw();
}

function label(id) {
  const m = state.members.get(id);
  const t = state.types.get(m ? state.memberType.get(id) : id);
  if (!m) return t?.name ?? id;
  if (m.kind === 'top-level statements') return `${t?.name ?? 'Program'} (top-level code)`;
  return `${t?.name ?? '?'}.${m.name}`;
}

// ---------------------------------------------------------------- templates

async function loadTemplates() {
  try { guide.templates = await api('/templates'); } catch { guide.templates = { templates: [] }; }
  const option = $('mapLevel').querySelector('option[value="templates"]');
  option.hidden = !guide.templates.templates.length;
  if (state.view.kind === 'templates') graph.render({ relayout: true, fit: true });
}

const KIND_COLORS = { page: '#7aa2ff', layout: '#b48cff', partial: '#8a96a8', component: '#56c99a', viewstart: '#f2b544', imports: '#f2b544' };
const KIND_TEXT = {
  page: 'A view: an action, handler or Razor Page fills it in and sends it to the browser.',
  layout: 'A layout: views are rendered inside it at @RenderBody(), so it holds what every page shares, such as the header and menu.',
  partial: 'A partial view: a piece of page that other views render with <partial name="…" />.',
  component: 'A component: a reusable piece of page with its own logic.',
  viewstart: '_ViewStart.cshtml runs before every view in its folder and below, usually to set the layout.',
  imports: 'The usings and tag helpers every view in its folder and below gets.',
};

export function templateElements() {
  const items = guide.templates?.templates ?? [];
  const elements = [];
  const nodes = new Set();
  const edges = new Set();
  const node = (id, label, color, classes, shape = 'round-rectangle') => {
    if (nodes.has(id)) return;
    nodes.add(id);
    elements.push({ data: { id, label, color, shape, kind: 'template' }, classes });
  };
  const edge = (source, target, how) => {
    const key = source + '>' + target;
    if (edges.has(key)) return;
    edges.add(key);
    elements.push({ data: { id: 'tedge:' + key, source, target, kind: 'template', width: how === 'layout' ? 2.4 : 1.4 }, classes: 'folder-edge template-edge ' + how });
  };
  for (const t of items) node('template:' + t.file, t.file.split('/').slice(-2).join('/'), KIND_COLORS[t.kind] ?? '#8a96a8', 'template template-' + t.kind,
    t.kind === 'component' ? 'hexagon' : t.kind === 'layout' ? 'round-rectangle' : 'round-rectangle');
  const link = (from, ref, how) => {
    if (!ref) return;
    const target = ref.file ? 'template:' + ref.file : 'template-ext:' + ref.external;
    if (!ref.file) node(target, ref.external, '#8a96a8', 'template template-external');
    edge('template:' + from, target, how);
  };
  for (const t of items) {
    link(t.file, t.extends, 'layout');
    for (const ref of t.includes) link(t.file, ref, 'partial');
    for (const ref of t.imports) link(t.file, ref, 'component');
    for (const by of t.renderedBy) {
      const id = 'thandler:' + by.id;
      node(id, by.routes[0] ? `${by.routes[0]}${by.routes.length > 1 ? ' +' + (by.routes.length - 1) : ''}` : by.name, '#56c99a', 'template-handler', 'tag');
      edge(id, 'template:' + t.file, 'renders');
    }
  }
  return elements;
}

export function inspectTemplate(file, line = 1) {
  const t = guide.templates?.templates.find(x => x.file === file);
  if (!t) return;
  learn.flow = null;
  const ref = r => r.file ? `<a data-template="${escapeHtml(r.file)}">${escapeHtml(r.file)}</a>` : `${escapeHtml(r.name)} <span class="muted">(${escapeHtml(r.external)})</span>`;
  const body = showBody(`
    <div class="d-head"><div class="kind"><span>${escapeHtml(t.kind === 'viewstart' ? 'view start' : t.kind === 'imports' ? 'view imports' : t.kind)}</span></div>
      <h2>${escapeHtml(file.split('/').pop())}</h2><div class="ns">${escapeHtml(file)}</div>
      <p class="summary-text">${annotate(KIND_TEXT[t.kind] ?? '')}</p></div>
    ${t.model ? `<div class="d-section"><h3>Model</h3><p>${t.modelId ? `<a data-go="${escapeHtml(t.modelId)}">${escapeHtml(t.model)}</a>` : escapeHtml(t.model)}: the data the view is given to show.</p></div>` : ''}
    ${t.extends ? `<div class="d-section"><h3>Layout</h3><p>Rendered inside ${ref(t.extends)}${t.extends.line > 1 ? '' : ' (set by _ViewStart.cshtml)'}${t.blocks.length && t.kind !== 'layout' ? `, filling its ${t.blocks.map(escapeHtml).join(', ')} section${t.blocks.length === 1 ? '' : 's'}` : ''}.</p></div>`
      : t.blocks.length ? `<div class="d-section"><h3>Sections</h3><p>Defines ${t.blocks.map(escapeHtml).join(', ')}, which views can fill with @section.</p></div>` : ''}
    ${t.includes.length || t.imports.length ? `<div class="d-section"><h3>Uses</h3><ul class="rel">${t.includes.map(r => `<li><span class="k">partial</span>${ref(r)}</li>`).join('')}${t.imports.map(r => `<li><span class="k">component</span>${ref(r)}</li>`).join('')}</ul></div>` : ''}
    ${t.usedBy.length ? `<div class="d-section"><h3>Used by</h3><ul class="rel">${t.usedBy.map(u => `<li><a data-template="${escapeHtml(u.file)}">${escapeHtml(u.file)}</a> <span class="muted">${escapeHtml(u.how)}</span></li>`).join('')}</ul></div>` : ''}
    ${t.renderedBy.length ? `<div class="d-section"><h3>Rendered by</h3><ul class="rel">${t.renderedBy.map(r => `<li><a data-go="${escapeHtml(r.id)}">${escapeHtml(r.name)}</a>${r.routes.length ? ` <span class="muted">for ${r.routes.map(escapeHtml).join(', ')}</span>` : ''}</li>`).join('')}</ul></div>` : ''}
    <div data-template-preview></div>`);
  showTemplate(file, body.querySelector('[data-template-preview]')).then(() => {
    if (line > 1) {
      const row = body.querySelectorAll('[data-template-preview] .ln')[line - 1];
      row?.classList.add('hit');
      scrollToHit(body);
    }
  });
}

// ---------------------------------------------------------------- play a page step by step

export function playFlow(route) {
  const frames = [{ kind: 'request' }];
  if (learn.pages?.before) frames.push({ kind: 'before' });
  frames.push({ kind: 'route' });
  if (state.members.has(route.handler)) frames.push({ kind: 'code', id: route.handler, title: route.inline ? 'The code written inline runs' : 'The handler runs' });
  const children = new Map();
  for (const step of route.steps) (children.get(step.from) ?? children.set(step.from, []).get(step.from)).push(step);
  const walk = from => {
    for (const step of children.get(from) ?? []) {
      if (!state.members.has(step.id)) continue;
      frames.push({ kind: 'code', id: step.id, title: `It calls ${label(step.id)}${state.members.has(step.from) ? ` (from ${label(step.from)})` : ''}` });
      walk(step.id);
    }
  };
  walk(route.handler);
  for (const t of route.templates) if (t.file) frames.push({ kind: 'template', file: t.file });
  frames.push({ kind: 'done' });
  guide.playback = { route, frames, index: 0, timer: null };
  showFrame();
}

export function stopPlayback(back = true) {
  const play = guide.playback;
  if (!play) return;
  clearInterval(play.timer);
  guide.playback = null;
  graph.getCy()?.nodes().removeClass('flow-now');
  if (back) showFlow(play.route);
}

async function showFrame() {
  const play = guide.playback;
  if (!play) return;
  const { route, frames, index } = play;
  const frame = frames[index];
  const verb = route.method.split(',')[0].trim();
  let title = '';
  let html = '';
  if (frame.kind === 'request') {
    title = `The browser asks for ${route.path}`;
    html = `<p>${annotate(`Someone ${verb === 'GET' || verb === '*' ? 'follows a link or types the address' : 'submits a form or the page sends a request'}, so the browser sends a ${verb === '*' ? '' : verb + ' '}request for ${route.path} to the server.`)}</p>`;
  } else if (frame.kind === 'before') {
    title = 'The server gets it ready';
    html = `<p>${annotate(learn.pages.before)}</p>`;
  } else if (frame.kind === 'route') {
    title = 'The route matches the address';
    html = `<p>${annotate(`Routing finds the ${route.kind === 'page' ? 'Razor Page' : route.kind === 'controller' ? 'controller action' : 'endpoint'} for ${route.method === '*' ? 'ANY' : route.method} ${route.path} and hands the request to it.`)}</p>
      ${route.file ? `<p>Mapped in ${fileLink(route.file, route.line)}</p>` : ''}${route.note ? `<p class="muted">${escapeHtml(route.note)}</p>` : ''}`;
  } else if (frame.kind === 'code') {
    title = frame.title;
    const explained = learn.explanations[frame.id];
    html = `<p class="${explained ? 'explained' : 'muted'}">${annotate(explained?.summary ?? 'No explanation yet. Read the code below to see what it does.')}</p>
      <p><a data-stop-go="${escapeHtml(frame.id)}">Open ${escapeHtml(label(frame.id))}</a></p><div data-code></div>`;
  } else if (frame.kind === 'template') {
    title = 'It fills in the Razor view';
    html = `<p>${annotate('The handler passes its model to the view, which turns it into the HTML the browser shows.')}</p><div data-template-preview></div>`;
  } else {
    title = 'The response goes back to the browser';
    html = `<p>${annotate(`The server sends the page (or JSON, a file or a redirect) back, and the browser shows it. That's the whole journey of ${route.method === '*' ? 'ANY' : route.method} ${route.path}.`)}</p>`;
  }
  const body = showBody(`<div class="d-head"><div class="kind"><span>step ${index + 1} of ${frames.length}</span></div><h2>${escapeHtml(route.method === '*' ? 'ANY' : route.method)} ${escapeHtml(route.path)}</h2></div>
    <div class="d-section player"><h3>${escapeHtml(title)}</h3>${html}
      <div class="row"><button data-back ${index === 0 ? 'disabled' : ''}>← Back</button><button data-next class="primary">${index === frames.length - 1 ? 'Finish' : 'Next →'}</button>
      <button data-auto>${play.timer ? '❚❚ Pause' : '▶ Play'}</button><button data-stop>Stop</button></div></div>`);
  body.querySelector('[data-back]').addEventListener('click', () => move(-1));
  body.querySelector('[data-next]').addEventListener('click', () => index === frames.length - 1 ? stopPlayback() : move(1));
  body.querySelector('[data-stop]').addEventListener('click', () => stopPlayback());
  body.querySelector('[data-auto]').addEventListener('click', () => {
    if (play.timer) { clearInterval(play.timer); play.timer = null; }
    else play.timer = setInterval(() => play.index < play.frames.length - 1 ? move(1, true) : stopPlayback(false), 6000);
    showFrame();
  });
  body.querySelector('[data-stop-go]')?.addEventListener('click', e => { stopPlayback(false); emit('go', e.currentTarget.dataset.stopGo); });

  const cy = graph.getCy();
  cy.nodes().removeClass('flow-now');
  if (frame.kind === 'code') {
    const typeId = state.memberType.get(frame.id);
    const n = cy.getElementById(frame.id).length ? cy.getElementById(frame.id) : cy.getElementById(typeId);
    n.addClass('flow-now');
    if (n.length) cy.animate({ center: { eles: n } }, { duration: 300 });
    const member = state.members.get(frame.id);
    const slot = body.querySelector('[data-code]');
    if (member?.file) {
      try {
        const lines = (await api('/source?file=' + encodeURIComponent(member.file), { text: true })).split(/\r?\n/);
        const start = member.kind === 'top-level statements' ? 1 : member.line;
        const end = member.kind === 'top-level statements' ? lines.length : member.endLine;
        if (guide.playback === play && play.index === index && slot.isConnected)
          slot.innerHTML = `<pre class="source">${sourceRows(lines.slice(start - 1, end).join('\n'), start, 0, true)}</pre>`;
      } catch { /* the explanation is enough */ }
    }
  } else if (frame.kind === 'template') {
    showTemplate(frame.file, body.querySelector('[data-template-preview]'));
  }
}

function move(delta, automatic = false) {
  const play = guide.playback;
  if (!play) return;
  if (!automatic && play.timer) { clearInterval(play.timer); play.timer = null; }
  play.index = Math.max(0, Math.min(play.frames.length - 1, play.index + delta));
  showFrame();
}

// ---------------------------------------------------------------- ask the codebase

function paintAsk() {
  const pane = $('askPane');
  if (pane.querySelector('form')) return;
  pane.innerHTML = `<form class="ask-form">
      <label for="askQuestion">Ask a question about this code</label>
      <div class="row"><textarea id="askQuestion" rows="2" maxlength="500" placeholder="Where are orders saved? What happens on POST /api/orders?"></textarea><button type="submit" class="primary">Ask</button></div>
      <p class="muted">${learn.ai ? `Answers come from ${escapeHtml(learn.ai.label)}, using the code and explanations that best match.`
        : 'Start Code to Life with --ai to get written answers. For now, Ask shows the code that best matches your question.'}</p>
    </form><div class="answer" aria-live="polite"></div>`;
  const form = pane.querySelector('form');
  const input = form.querySelector('textarea');
  const answer = pane.querySelector('.answer');
  const submit = form.querySelector('button');
  form.addEventListener('submit', async e => {
    e.preventDefault();
    if (guide.asking) return;
    guide.asking = true;
    submit.disabled = true;
    answer.innerHTML = `<p class="muted">${learn.ai ? 'Thinking… a local model can take a minute.' : 'Searching…'}</p>`;
    try { showAnswer(answer, await api('/ask', { method: 'POST', body: { question: input.value } })); }
    catch (error) { answer.innerHTML = `<p class="muted">${escapeHtml(error.message)}</p>`; }
    finally { guide.asking = false; submit.disabled = false; }
  });
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); } });
}

/** Turns `Name`, `Type.Method` and `path/file.cs` references in an answer into links. */
function linkify(text) {
  const byName = new Map();
  const tests = new Set(state.graph.projects.filter(p => p.outputKind === 'test').map(p => p.name));
  for (const t of state.graph.types) {
    if (tests.has(t.project)) continue;
    if (!byName.has(t.name)) byName.set(t.name, t.id);
    for (const m of t.members) {
      const key = `${t.name}.${m.name}`;
      if (!byName.has(key)) byName.set(key, m.id);
    }
  }
  const files = state.graph.types.filter(t => t.file).map(t => [shortPath(t.file), t.id]);
  return String(text).split(/(`[^`\n]+`)/).map(part => {
    if (!part.startsWith('`') || part.length < 3) return annotate(part);
    const value = part.slice(1, -1).trim();
    const bare = value.replace(/\(\)$/, '').replace(/:\d+(?:-\d+)?$/, '');
    const id = byName.get(bare) ?? (() => {
      const found = files.filter(([f]) => f === bare || f.endsWith('/' + bare));
      return new Set(found.map(([f]) => f)).size === 1 ? found[0][1] : null;
    })();
    if (id) return `<a data-go="${escapeHtml(id)}">${escapeHtml(value)}</a>`;
    const view = guide.templates?.templates.filter(t => t.file === bare || t.file.endsWith('/' + bare));
    if (view?.length === 1) return `<a data-template="${escapeHtml(view[0].file)}">${escapeHtml(value)}</a>`;
    return `<code>${escapeHtml(value)}</code>`;
  }).join('');
}

function showAnswer(slot, data) {
  guide.answer = data;
  slot.innerHTML = `${data.answer ? data.answer.split(/\n{2,}/).map(p => `<p class="answer-text">${linkify(p)}</p>`).join('') : ''}
    ${data.sources.length ? `<h3>${data.answer ? 'Based on' : 'The code that best matches'}</h3>
      <ul class="rel">${data.sources.map(s => `<li><span class="k">${escapeHtml(s.kind)}</span><a data-go="${escapeHtml(s.id)}">${escapeHtml(s.name)}</a> <span class="muted">${escapeHtml(s.file ?? '')}</span></li>`).join('')}</ul>
      <div class="row"><button data-highlight>Highlight these on the map</button></div>`
      : data.answer ? '' : '<p class="muted">Nothing in the code matched. Try the words you would expect in a class or method name.</p>'}`;
  wire(slot);
  slot.querySelector('[data-highlight]')?.addEventListener('click', () => {
    if (state.view.kind !== 'types') graph.goHome();
    graph.highlightSet(data.sources.map(s => state.types.has(s.id) ? s.id : state.memberType.get(s.id)));
    const cy = graph.getCy();
    const lit = cy.nodes('.impact');
    if (lit.length) cy.animate({ fit: { eles: lit, padding: 60 } }, { duration: 300 });
  });
}
