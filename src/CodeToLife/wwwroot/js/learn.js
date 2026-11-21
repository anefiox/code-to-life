// Learning a new codebase: a folder-level overview, a "Start here" tour, what happens when a page
// is visited, and plain-English explanations (with the packages the project uses) beside the code.
import { state, on, emit, api, escapeHtml, shortPath, toast } from './state.js';
import * as graph from './graph.js';
import { loadSource } from './panels.js';

const $ = id => document.getElementById(id);

export const learn = {
  explanations: {}, glossary: [], ai: null, // from /api/explanations
  tour: [], pages: null,                     // from /api/learn
  tourIndex: -1, flow: null, folders: null,
};

export function initLearn() {
  on('graph', () => { learn.folders = null; load(); });
  on('details', explainDetails);
  on('select', id => {
    if (id?.startsWith('folder:')) showFolder(id);
    else if (id && learn.flow) { learn.flow = null; paintPages(); }
  });
  on('rendered', paintTour);
  on('open-folder', id => showFolderTypes(id));
  $('tourToggle').addEventListener('click', () => setTour(learn.tourIndex < 0));
  $('mapLevel').addEventListener('change', e => {
    if (e.target.value === 'folders') graph.openView({ kind: 'folders' });
    else graph.goHome();
  });
  on('view', view => { $('mapLevel').value = view.kind === 'folders' ? 'folders' : 'types'; });
  on('show-tab', tab => { if (tab === 'pages') paintPages(); });
}

async function load() {
  const [explained, guided] = await Promise.allSettled([api('/explanations'), api('/learn')]);
  if (explained.status === 'fulfilled') Object.assign(learn, { explanations: explained.value.explanations, glossary: explained.value.glossary, ai: explained.value.ai });
  else toast('Explanations unavailable: ' + explained.reason.message, 'error');
  if (guided.status === 'fulfilled') Object.assign(learn, { tour: guided.value.tour.steps, pages: guided.value.pages });
  else toast('Guided views unavailable: ' + guided.reason.message, 'error');
  paintOverview();
  paintPages();
  if (learn.tourIndex >= 0) showTourStep(Math.min(learn.tourIndex, learn.tour.length - 1));
  if (learn.flow) {
    const route = learn.pages?.routes.find(r => r.key === learn.flow);
    if (route) showFlow(route); else learn.flow = null;
  } else if (state.selected && !state.selected.startsWith('folder:')) emit('select', state.selected);
}

// ---------------------------------------------------------------- overview and explanations

function paintOverview() {
  const box = $('learnOverview');
  const explained = Object.keys(learn.explanations).length;
  box.innerHTML = `
    <h3>New to this code?</h3>
    <div class="row">
      <button data-learn="tour" class="primary">★ Start here</button>
      <button data-learn="pages">🌐 Pages${learn.pages?.routes.length ? ` (${learn.pages.routes.length})` : ''}</button>
      <button data-learn="folders">▦ Folders</button>
    </div>
    <h3>Explanations</h3>
    <p>${explained ? `${explained} classes and methods have a plain-English explanation. Click one on the map to read it.`
      : 'No explanations yet. Click any class or method to read its code.'}</p>
    ${learn.ai ? `<p>Click a class or method and choose <b>Explain with AI</b> to have ${escapeHtml(learn.ai.label)} write one (model ${escapeHtml(learn.ai.model)}).</p>`
      : explained ? '' : explainHint()}
    ${learn.glossary.length ? `<h3>Tools this project uses</h3>${glossaryList(learn.glossary)}` : ''}`;
  box.querySelector('[data-learn="tour"]').addEventListener('click', () => setTour(true));
  box.querySelector('[data-learn="pages"]').addEventListener('click', () => emit('show-tab', 'pages'));
  box.querySelector('[data-learn="folders"]').addEventListener('click', () => graph.openView({ kind: 'folders' }));
}

const explainHint = () => `<p class="hint">To add explanations, connect the codetolife MCP server to Claude Code and ask:
  <q>Use explain_next and save_explanation to explain this project for a new developer.</q>
  Or start Code to Life with <code>--ai openrouter</code> or <code>--ai &lt;your AI server's URL&gt;</code>.</p>`;

const glossaryList = items => `<dl class="glossary">${items.map(g =>
  `<dt>${escapeHtml(g.term)} <code>${escapeHtml(g.package)}</code></dt><dd>${escapeHtml(g.meaning)}</dd>`).join('')}</dl>`;

/** Adds "What it does" (and the packages its file uses) to the details panel. */
async function explainDetails({ body, type, member }) {
  const slot = body.querySelector('[data-explain]');
  if (!slot) return;
  const id = member?.id ?? type.id;
  const explanation = learn.explanations[id];
  slot.innerHTML = `<div class="d-section explanation">
      <h3>What it does</h3>
      ${explanation ? `<p class="explained">${escapeHtml(explanation.summary)}</p>
        ${explanation.stale ? '<p class="stale">⚠ The code has changed since this was written, so it may be out of date.</p>' : ''}`
        : `<p class="muted">${member ? 'No explanation yet. Its code is below.' : 'No explanation yet. Its members and code are below.'}</p>`}
      ${learn.ai ? `<div class="row"><button data-ai>${explanation ? 'Rewrite' : 'Explain'} with AI</button><span data-ai-status class="muted" role="status"></span></div>`
        : explanation ? '' : explainHint()}
    </div>
    <div data-packages></div>`;
  slot.querySelector('[data-ai]')?.addEventListener('click', async e => {
    const button = e.currentTarget;
    const status = slot.querySelector('[data-ai-status]');
    button.disabled = true;
    status.textContent = `Asking ${learn.ai.label}… this can take a minute on a local model.`;
    try {
      const saved = await api('/explain?id=' + encodeURIComponent(id), { method: 'POST' });
      learn.explanations[id] = { summary: saved.summary, stale: false, updated: saved.updated };
      if (state.selected === id || (member && state.selected === type.id)) explainDetails({ body, type, member });
      paintOverview();
    } catch (error) {
      button.disabled = false;
      status.textContent = error.message;
    }
  });
  // The packages this file pulls in with `using`, from the project's glossary.
  const file = (member ?? type).file;
  if (!file || !learn.glossary.length) return;
  let lines;
  try { lines = await loadSource(file); } catch { return; }
  const usings = lines.map(l => /^\s*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;/.exec(l)?.[1]).filter(Boolean);
  const used = learn.glossary.filter(g => g.namespaces.some(ns => usings.some(u => u === ns || u.startsWith(ns + '.'))));
  if (used.length && slot.isConnected) slot.querySelector('[data-packages]').innerHTML = `<div class="d-section"><h3>Packages this file uses</h3>${glossaryList(used)}</div>`;
}

// ---------------------------------------------------------------- folders

const FOLDER_NOTES = [
  [/^controllers?$/, 'Controllers: classes whose public methods answer web requests. Each action pairs an address with the code that handles it.'],
  [/^(pages|areas)$/, 'Razor Pages: each .cshtml page is a web address, with its code in the matching .cshtml.cs file.'],
  [/^(views|shared|layouts?|partials|components)$/, 'Razor views and components: the HTML the app fills in and sends to the browser.'],
  [/^(endpoints?|routes?|apis?)$/, 'The web addresses the app answers and the code that handles each one.'],
  [/^(middlewares?|filters)$/, 'Code that runs on every request before (or after) it reaches a page, for example sign-in, logging or error handling.'],
  [/^(models?|entities|domain|dtos?|viewmodels?|contracts|records)$/, 'The shapes of the data the app works with: classes with properties, and the rules that belong to them.'],
  [/^(data|persistence|repositories|repository|db|database|dbcontexts?)$/, 'Data access: how the app reads and saves data, usually in a database through Entity Framework Core or Dapper.'],
  [/^migrations$/, 'Database migrations: generated steps that change the database to match the code. Rarely edited by hand.'],
  [/^(services?|application|handlers|commands|queries|features|usecases)$/, 'Business logic: the work the app does, kept apart from the web and the database.'],
  [/^(infrastructure|clients?|integrations?|gateways?|adapters)$/, 'Code that talks to the outside world: other APIs, queues, files, email and the like.'],
  [/^(config|configuration|settings|options)$/, 'Settings classes, bound from appsettings.json and environment variables.'],
  [/^(extensions|helpers?|utils?|utilities|common|shared|core)$/, 'Shared helpers used in many places across the app.'],
  [/^(validators?|validation)$/, 'Rules that check incoming data is valid before it is used.'],
  [/^(mappings?|mappers?|profiles)$/, 'Code that copies data between similar classes, such as an entity and a DTO.'],
  [/^(jobs|workers?|background|hostedservices)$/, 'Background work that runs on a schedule or alongside the web app.'],
  [/^(hubs)$/, 'SignalR hubs: real-time messages between the server and the browser.'],
  [/^(properties)$/, 'Project settings, such as launchSettings.json for running locally.'],
  [/^(wwwroot|static|assets)$/, 'Static files (CSS, JavaScript, images) sent to the browser as they are.'],
  [/^(tests?|.*\.tests?|.*\.(unit|integration)tests?|specs?|fakes|mocks|fixtures)$/, 'Test code and helpers. They check the app works, but do not run in the live service.'],
  [/^(tools|scripts|build)$/, 'Scripts and tools for developers. They are not part of the running app.'],
  [/^src$/, "The app's source code."],
];

function folderNote(dir) {
  for (const part of (dir || '').split('/').filter(Boolean).reverse()) {
    const match = FOLDER_NOTES.find(([pattern]) => pattern.test(part.toLowerCase()));
    if (match) return match[1];
  }
  return dir ? null : 'Files at the top of the solution, usually the entry point and setup.';
}

/** Groups the code's folders into at most 14, merging the deepest into their parents until it fits on one screen. */
function folderGroups() {
  if (learn.folders) return learn.folders;
  const tests = new Set(state.graph.projects.filter(p => p.outputKind === 'test').map(p => p.name));
  const types = state.graph.types.filter(t => t.file && !t.containingType);
  const dirOf = file => { const f = shortPath(file); return f.includes('/') ? f.slice(0, f.lastIndexOf('/')) : ''; };
  const depth = dir => dir ? dir.split('/').length : 0;
  const files = new Map(types.map(t => [shortPath(t.file), dirOf(t.file)]));
  for (;;) {
    const dirs = new Set(files.values());
    const deepest = Math.max(0, ...[...dirs].map(depth));
    if (dirs.size <= 14 || deepest <= 1) break;
    for (const [file, dir] of files) if (depth(dir) === deepest) files.set(file, dir.split('/').slice(0, -1).join('/'));
  }
  const groups = new Map();
  const typeGroup = new Map();
  for (const t of types) {
    const dir = files.get(shortPath(t.file));
    if (!groups.has(dir)) groups.set(dir, { id: 'folder:' + dir, dir, files: new Set(), types: [], tests: 0 });
    const g = groups.get(dir);
    g.files.add(shortPath(t.file));
    g.types.push(t.id);
    if (tests.has(t.project)) g.tests++;
    typeGroup.set(t.id, dir);
  }
  const links = new Map();
  for (const e of state.graph.edges) {
    if (e.kind === 'nested' || e.kind === 'uses') continue;
    const from = typeGroup.get(e.source), to = typeGroup.get(e.target);
    if (from === undefined || to === undefined || from === to) continue;
    const key = from + '\n' + to;
    links.set(key, (links.get(key) ?? 0) + 1);
  }
  return (learn.folders = { groups, links });
}

export function folderElements() {
  const { groups, links } = folderGroups();
  const elements = [...groups.values()].map(g => {
    const count = g.files.size;
    return {
      data: {
        id: g.id, folder: true, kind: 'folder', shape: 'round-rectangle', color: g.tests === g.types.length ? '#8a96a8' : '#7aa2ff',
        label: `${g.dir || '(solution root)'}\n${count} file${count === 1 ? '' : 's'} · ${g.types.length} type${g.types.length === 1 ? '' : 's'}`,
        size: Math.round(60 + Math.sqrt(g.types.length) * 14),
      },
      classes: 'folder' + (g.tests === g.types.length ? ' test' : ''),
    };
  });
  for (const [key, count] of links) {
    const [from, to] = key.split('\n');
    elements.push({
      data: { id: 'folder-edge:' + key, source: 'folder:' + from, target: 'folder:' + to, kind: 'folder', count, width: Math.min(7, 1.2 + Math.log2(1 + count) * 1.3) },
      classes: 'folder-edge',
    });
  }
  return elements;
}

function showFolder(id) {
  const g = folderGroups().groups.get(id.slice('folder:'.length));
  if (!g) return;
  learn.flow = null;
  $('detailsEmpty').hidden = true;
  const body = $('detailsBody');
  body.hidden = false;
  const related = outgoing => [...folderGroups().links].map(([key, count]) => [key.split('\n'), count])
    .filter(([[from, to]]) => (outgoing ? from : to) === g.dir).sort((a, b) => b[1] - a[1])
    .map(([[from, to], count]) => { const other = outgoing ? to : from;
      return `<li><span class="k">${count} link${count === 1 ? '' : 's'}</span><a data-folder="folder:${escapeHtml(other)}">${escapeHtml(other || '(solution root)')}/</a></li>`; }).join('');
  const byFile = new Map();
  for (const typeId of g.types) {
    const t = state.types.get(typeId);
    const file = shortPath(t.file);
    if (!byFile.has(file)) byFile.set(file, []);
    byFile.get(file).push(t);
  }
  const uses = related(true), usedBy = related(false);
  body.innerHTML = `
    <div class="d-head">
      <div class="kind"><span>folder</span>${g.tests ? `<span class="chip">${g.tests === g.types.length ? 'tests' : g.tests + ' test types'}</span>` : ''}</div>
      <h2>${escapeHtml(g.dir || '(solution root)')}/</h2>
      <div class="ns">${g.files.size} file${g.files.size === 1 ? '' : 's'} · ${g.types.length} type${g.types.length === 1 ? '' : 's'}</div>
      <div class="actions"><button data-act="types" title="Switch to the types map with this folder's types highlighted">Show its types ⤵</button></div>
    </div>
    <div class="d-section"><h3>What this folder is for</h3><p>${escapeHtml(folderNote(g.dir) ?? 'There is no description for this kind of folder. Its files are listed below.')}</p></div>
    ${uses ? `<div class="d-section"><h3>Uses code from</h3><ul class="rel">${uses}</ul></div>` : ''}
    ${usedBy ? `<div class="d-section"><h3>Used by</h3><ul class="rel">${usedBy}</ul></div>` : ''}
    <div class="d-section"><h3>Files</h3><ul class="rel folder-files">${[...byFile].sort((a, b) => a[0].localeCompare(b[0])).map(([file, types]) =>
      `<li><span class="file" title="${escapeHtml(file)}">${escapeHtml(g.dir ? file.slice(g.dir.length + 1) : file)}</span>${types.map(t =>
        `<a data-go="${escapeHtml(t.id)}">${escapeHtml(t.name)}</a>`).join(', ')}</li>`).join('')}</ul></div>`;
  body.querySelector('[data-act="types"]').addEventListener('click', () => showFolderTypes(id));
  body.querySelectorAll('[data-folder]').forEach(a => a.addEventListener('click', () => graph.select(a.dataset.folder)));
  body.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => emit('go', a.dataset.go)));
}

function showFolderTypes(id) {
  const g = folderGroups().groups.get(id.slice('folder:'.length));
  if (!g) return;
  graph.goHome();
  graph.highlightSet(g.types);
  const cy = graph.getCy();
  const nodes = cy.nodes('.impact');
  if (nodes.length) cy.animate({ fit: { eles: nodes, padding: 60 } }, { duration: 300 });
  showFolder(id);
}

// ---------------------------------------------------------------- start here

function setTour(open) {
  $('tourToggle').classList.toggle('on', open);
  if (!open) {
    learn.tourIndex = -1;
    $('tour').hidden = true;
    paintTour();
    return;
  }
  showTourStep(0);
}

function showTourStep(index) {
  const steps = learn.tour;
  const card = $('tour');
  card.hidden = false;
  if (!steps.length) {
    learn.tourIndex = 0;
    card.innerHTML = `<div class="tour-head"><b>Start here</b><button class="icon" data-close title="Close">✕</button></div>
      <p>Couldn't find where this app starts. Try the Folders view to get your bearings instead.</p>`;
    card.querySelector('[data-close]').addEventListener('click', () => setTour(false));
    return;
  }
  index = Math.max(0, Math.min(index, steps.length - 1));
  learn.tourIndex = index;
  const step = steps[index];
  const roles = { entry: 'Where the app starts', backbone: 'Follow the main path', pages: 'Where addresses are handled', shared: 'Shared code' };
  card.innerHTML = `
    <div class="tour-head"><b>Start here</b><span class="muted">Step ${index + 1} of ${steps.length} · ${roles[step.role]}</span><button class="icon" data-close title="Close">✕</button></div>
    <h3>${escapeHtml(step.name)}</h3>
    ${step.file ? `<div class="muted file">${escapeHtml(step.file)}</div>` : ''}
    <p>${escapeHtml(step.reason)}</p>
    <div class="row"><button data-back ${index === 0 ? 'disabled' : ''}>← Back</button><button data-next class="primary">${index === steps.length - 1 ? 'Finish' : 'Next →'}</button></div>`;
  card.querySelector('[data-close]').addEventListener('click', () => setTour(false));
  card.querySelector('[data-back]').addEventListener('click', () => showTourStep(index - 1));
  card.querySelector('[data-next]').addEventListener('click', () => index === steps.length - 1 ? setTour(false) : showTourStep(index + 1));
  if (state.view.kind !== 'types') graph.goHome();
  emit('go', step.id);
  paintTour();
}

function paintTour() {
  const cy = graph.getCy();
  if (!cy) return;
  cy.nodes().removeClass('tour-seen tour-current');
  if (learn.tourIndex < 0 || state.view.kind !== 'types') return;
  learn.tour.slice(0, learn.tourIndex).forEach(s => cy.getElementById(s.typeId).addClass('tour-seen'));
  const current = learn.tour[learn.tourIndex];
  if (current) cy.getElementById(current.typeId).addClass('tour-current');
}

// ---------------------------------------------------------------- pages

function paintPages() {
  const pane = $('pagesPane');
  const pages = learn.pages;
  if (!pages?.routes.length) {
    pane.innerHTML = `<div class="notice">No web addresses found. Code to Life recognises ASP.NET Core minimal APIs
      (<code>app.MapGet("/path", …)</code>, with <code>MapGroup</code> prefixes), controller actions and Razor Pages.</div>`;
    return;
  }
  pane.innerHTML = `<p class="muted">Pick an address to see what happens when someone visits it.</p>
    <ul class="page-list">${pages.routes.map((r, i) => {
      const owner = state.types.get(state.memberType.get(r.handler));
      return `<li><button data-route="${i}" ${learn.flow === r.key ? 'aria-current="true"' : ''}>
        <span class="method method-${r.method.split(',')[0].trim().toLowerCase().replace('*', 'any')}">${escapeHtml(r.method === '*' ? 'ANY' : r.method)}</span>
        <span class="path ${r.fixedPath ? '' : 'dynamic'}">${escapeHtml(r.path)}</span>
        <span class="handler">${escapeHtml(r.inline ? 'inline in ' + (owner?.name ?? '?') : label(r.handler))}</span></button></li>`;
    }).join('')}</ul>`;
  pane.querySelectorAll('[data-route]').forEach(b => b.addEventListener('click', () => showFlow(pages.routes[Number(b.dataset.route)])));
}

const label = id => {
  const m = state.members.get(id);
  const t = state.types.get(state.memberType.get(id));
  if (!m) return id;
  if (m.kind === 'top-level statements') return `${t?.name ?? 'Program'} (top-level code)`;
  if (m.kind.includes('constructor')) return `new ${t?.name ?? '?'}()`;
  return `${t?.name ?? '?'}.${m.name}${m.kind === 'method' ? '()' : ''}`;
};

function showFlow(route) {
  learn.flow = route.key;
  paintPages();
  if (state.view.kind !== 'types') graph.goHome();
  const ids = [route.handler, ...route.steps.map(s => s.id)];
  graph.highlightSet(ids);
  const cy = graph.getCy();
  const lit = cy.nodes('.impact');
  if (lit.length) cy.animate({ fit: { eles: lit, padding: 70 }, }, { duration: 300 });

  const handler = state.members.get(route.handler);
  const verb = route.method.split(',')[0].trim();
  const explained = learn.explanations[route.handler];
  $('detailsEmpty').hidden = true;
  const body = $('detailsBody');
  body.hidden = false;
  const step = (title, html) => `<li><strong>${title}</strong>${html}</li>`;
  const go = (id, text) => `<a data-go="${escapeHtml(id)}">${escapeHtml(text)}</a>`;
  body.innerHTML = `
    <div class="d-head">
      <div class="kind"><span>${escapeHtml(route.kind === 'page' ? 'Razor Page' : route.kind === 'controller' ? 'controller action' : 'minimal API')}</span></div>
      <h2>${escapeHtml(route.method === '*' ? 'ANY' : route.method)} ${escapeHtml(route.path)}</h2>
      <p class="summary-text">What happens when someone visits this address, step by step.</p>
      ${route.fixedPath ? '' : '<p class="summary-text muted">Part of the address is worked out when the app runs, so it is shown as written in the code.</p>'}
      ${route.note ? `<p class="summary-text muted">${escapeHtml(route.note)}</p>` : ''}
    </div>
    <div class="d-section"><ol class="flow">
      ${step('The browser asks for the page', `<p>It sends a ${escapeHtml(verb === '*' ? 'request' : verb + ' request')} for ${escapeHtml(route.path)}${verb === 'GET' || verb === '*' ? ', for example by following a link.' : ', usually by submitting a form or from code in the page.'}</p>`)}
      ${learn.pages.before ? step('The server gets it ready', `<p>${escapeHtml(learn.pages.before)}</p>`) : ''}
      ${step('The route matches the address', `<p>${go(route.handler, `${route.file}:${route.line}`)}</p><p class="muted">This is where the address is connected to the code that handles it.</p>`)}
      ${handler ? step(route.inline ? 'The code written inline runs' : 'The handler runs', `<p>${go(route.handler, label(route.handler))}</p>
        <p class="${explained ? '' : 'muted'}">${escapeHtml(explained?.summary ?? (route.inline
          ? `The handler is a lambda written where the route is mapped. ${route.steps.length ? 'The steps below are what it calls.' : "It doesn't call any other code in this project."}`
          : 'The handler builds the response: usually a page, sometimes a redirect or data.'))}</p>`) : ''}
      ${route.steps.length ? step('It calls other code', `<p class="muted">Indented calls are made by the one above.</p><ul class="flow-calls">${route.steps.map(s =>
        `<li style="padding-left:${(s.depth - 1) * 14}px">${go(s.id, label(s.id))}</li>`).join('')}</ul>`) : ''}
      ${route.templates.length
        ? step('It fills in a page template', `<p class="muted">The Razor view is the HTML page. The handler passes in the values to show.</p><ul class="rel">${route.templates.map(t =>
          `<li>${t.file ? `<a data-template="${escapeHtml(t.file)}">${escapeHtml(t.file)}</a>` : `${escapeHtml(t.name)} <span class="muted">(view file not found)</span>`}</li>`).join('')}</ul>`)
        : step('It sends the response', '<p class="muted">No Razor view was found, so it probably returns data (such as JSON), a file or a redirect.</p>')}
    </ol></div>
    <div data-template-preview></div>`;
  body.querySelectorAll('[data-go]').forEach(a => a.addEventListener('click', () => emit('go', a.dataset.go)));
  const preview = body.querySelector('[data-template-preview]');
  body.querySelectorAll('[data-template]').forEach(a => a.addEventListener('click', () => showTemplate(a.dataset.template, preview)));
  const first = route.templates.find(t => t.file);
  if (first) showTemplate(first.file, preview);
}

async function showTemplate(file, slot) {
  slot.innerHTML = '<div class="d-section muted">Loading template…</div>';
  try {
    const source = await api('/template?file=' + encodeURIComponent(file));
    const rows = source.code.split('\n').map((text, i) =>
      `<div class="ln"><span class="gutter"></span><span class="num">${i + 1}</span><span class="txt">${escapeHtml(text)}</span></div>`).join('');
    slot.innerHTML = `<div class="d-section" style="border-bottom:none;padding-bottom:6px"><h3 style="margin:0">Template</h3><span class="muted" style="font-size:11px">${escapeHtml(file)} · ${source.total} lines</span></div>
      <pre class="source">${rows}</pre>`;
  } catch (e) {
    slot.innerHTML = `<div class="d-section muted">${escapeHtml(e.message)}</div>`;
  }
}
