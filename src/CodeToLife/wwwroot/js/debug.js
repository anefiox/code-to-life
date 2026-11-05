// The debugger tab: launch or attach through netcoredbg, step, and watch the call stack move
// across the graph. "Animate" keeps stepping on its own so you can watch the code run.
import { state, on, emit, api, escapeHtml, fileName, memberLabel, toast } from './state.js';
import * as graph from './graph.js';

const $ = id => document.getElementById(id);
let pane;
let targets = [];
let follow = true;
let pendingRunTo = null;

export function breakpointsIn(file) {
  if (!file) return [];
  const key = Object.keys(state.debug.breakpoints).find(k => k.toLowerCase() === file.toLowerCase());
  return key ? state.debug.breakpoints[key] : [];
}

export async function toggleBreakpoint(file, line) {
  if (!file) return;
  const lines = new Set(breakpointsIn(file));
  if (lines.has(line)) lines.delete(line); else lines.add(line);
  try { await api('/debug/breakpoints', { method: 'POST', body: { file, lines: [...lines] } }); }
  catch (e) { toast(e.message, 'error'); }
}

export function initDebug() {
  pane = $('debugPane');
  renderPane();
  on('graph', loadTargets);
  on('run-to', runTo);
  on('sse:debug.state', d => { setState(d.state); });
  on('sse:debug.breakpoints', bps => {
    state.debug.breakpoints = bps;
    graph.applyOverlay();
    emit('breakpoints');
  });
  on('sse:debug.stopped', onStopped);
  on('sse:debug.exited', d => {
    state.debug.frames = [];
    state.debug.trail = [];
    graph.applyRuntime();
    renderStack();
    renderLocals([]);
    setState('idle');
    if (d.reason && d.reason !== 'stopped') emit('output', { category: 'console', output: `Debugger: ${d.reason}\n` });
    if (pendingRunTo) { removeTempBreakpoint(); }
  });
  on('sse:debug.animating', d => {
    state.debug.animating = d.on;
    $('animate').textContent = d.on ? '■ Stop animating' : '▶▶ Animate';
    $('animate').classList.toggle('on', d.on);
  });
  on('sse:debug.output', d => emit('output', d));
  on('sse:debug.error', d => toast(d.message, 'error'));

  document.addEventListener('keydown', e => {
    if (e.target.matches('input, textarea, select')) return;
    if (e.key === 'F5') { e.preventDefault(); state.debug.state === 'stopped' ? command('continue') : state.debug.state === 'idle' || state.debug.state === 'exited' ? launch() : null; }
    else if (e.key === 'F10') { e.preventDefault(); command('next'); }
    else if (e.key === 'F11' && e.shiftKey) { e.preventDefault(); command('stepOut'); }
    else if (e.key === 'F11') { e.preventDefault(); command('stepIn'); }
  });
}

function renderPane() {
  pane.innerHTML = `
    <div class="row">
      <select id="dbgTarget" class="grow" title="What to debug"></select>
      <input id="dbgArgs" type="text" placeholder="arguments" style="width:160px" title="Command-line arguments">
      <button id="dbgBuild" title="dotnet build, so the debugger runs what you see">Build</button>
      <button id="dbgLaunch" class="primary" title="Start debugging (F5)">▶ Start</button>
      <button id="dbgAttach" title="Attach to a running .NET process">Attach…</button>
      <span style="width:12px"></span>
      <button id="dbgContinue" title="Continue (F5)">▶ Continue</button>
      <button id="dbgPause" title="Pause">❚❚</button>
      <button id="dbgNext" title="Step over (F10)">↷ Over</button>
      <button id="dbgStepIn" title="Step into (F11)">↓ Into</button>
      <button id="dbgStepOut" title="Step out (Shift+F11)">↑ Out</button>
      <button id="dbgStop" class="danger" title="Stop">■</button>
      <span style="width:12px"></span>
      <button id="animate" class="toggle" title="Keep stepping into each line, pausing between steps, to watch the flow">▶▶ Animate</button>
      <label class="slider" title="Animation speed">slow<input id="speed" class="speed" type="range" min="50" max="1500" step="50" value="500" style="direction:rtl">fast</label>
      <label class="check" title="Move the graph to wherever the program is"><input type="checkbox" id="follow" checked> Follow</label>
    </div>
    <div id="dbgNotice"></div>
    <div class="cols">
      <div class="box"><h4>Call stack</h4><ul id="frames" class="frames"><li class="external">Not running.</li></ul></div>
      <div class="box"><h4>Locals</h4><ul id="locals" class="vars"></ul></div>
      <div class="box"><h4>Evaluate</h4>
        <div class="row"><input id="evalInput" class="grow" type="text" placeholder="order.Lines.Count" spellcheck="false"><button id="evalGo">=</button></div>
        <ul id="evalOut" class="plain"></ul>
      </div>
    </div>`;

  $('dbgBuild').addEventListener('click', build);
  $('dbgLaunch').addEventListener('click', () => launch());
  $('dbgAttach').addEventListener('click', attach);
  for (const [id, cmd] of [['dbgContinue', 'continue'], ['dbgPause', 'pause'], ['dbgNext', 'next'], ['dbgStepIn', 'stepIn'], ['dbgStepOut', 'stepOut'], ['dbgStop', 'stop']])
    $(id).addEventListener('click', () => command(cmd));
  $('animate').addEventListener('click', toggleAnimate);
  $('follow').addEventListener('change', e => { follow = e.target.checked; });
  $('evalGo').addEventListener('click', evaluate);
  $('evalInput').addEventListener('keydown', e => { if (e.key === 'Enter') evaluate(); });
  setState('idle');
}

async function loadTargets() {
  try { targets = await api('/debug/targets'); } catch { targets = []; }
  const select = $('dbgTarget');
  const previous = select.value;
  select.innerHTML = targets.map(t => `<option value="${escapeHtml(t.project)}">${escapeHtml(t.project)}${t.program ? '' : ' (not built yet)'}</option>`).join('')
    || '<option value="">No runnable projects found</option>';
  if (targets.some(t => t.project === previous)) select.value = previous;
  renderNotice();
}

function renderNotice() {
  const notice = $('dbgNotice');
  if (!state.status?.debugger) {
    notice.innerHTML = `<div class="notice" style="margin-bottom:8px">Stepping needs <b>netcoredbg</b> (the open-source .NET debugger). Get it with
      <code>pwsh tools/Get-Netcoredbg.ps1</code>, put it on PATH, or set <code>CODETOLIFE_NETCOREDBG</code>, then restart.
      Meanwhile the <b>Live</b> tab works without it.</div>`;
  } else notice.innerHTML = '';
}

function setState(s) {
  state.debug.state = s;
  const badge = $('debugState');
  if (!state.live.processId || s !== 'idle') {
    badge.textContent = s;
    badge.className = 'state-badge ' + s;
  }
  const running = s === 'running', stopped = s === 'stopped', active = running || stopped || s === 'starting';
  $('dbgLaunch').disabled = active;
  $('dbgAttach').disabled = active;
  $('dbgBuild').disabled = active;
  $('dbgContinue').disabled = !stopped;
  $('dbgNext').disabled = !stopped;
  $('dbgStepIn').disabled = !stopped;
  $('dbgStepOut').disabled = !stopped;
  $('animate').disabled = !stopped && !state.debug.animating;
  $('dbgPause').disabled = !running;
  $('dbgStop').disabled = !active;
  const pill = $('statusPill');
  if (active) pill.innerHTML = `<span class="pulse debug"></span> ${s === 'stopped' ? 'Paused' : s === 'starting' ? 'Starting…' : 'Running'} under the debugger`;
  else if (!state.live.processId) pill.innerHTML = '';
  emit('debug-state', s);
}

async function build() {
  const project = $('dbgTarget').value;
  if (!project) return;
  emit('output', { category: 'build', output: `Building ${project}…\n` });
  emit('show-tab', 'output');
  $('dbgBuild').disabled = true;
  try {
    const r = await api('/build', { method: 'POST', body: { project } });
    toast(r.success ? `${project} built.` : `Build failed: see Output.`, r.success ? 'info' : 'error');
    await loadTargets();
  } catch (e) { toast(e.message, 'error'); }
  finally { $('dbgBuild').disabled = false; }
}

export async function launch({ stopAtEntry = false } = {}) {
  const target = targets.find(t => t.project === $('dbgTarget').value);
  if (!target) { toast('Pick a project to run.', 'error'); return; }
  if (!target.program) { toast(`${target.project} hasn't been built yet. Click Build first.`, 'error'); return; }
  const args = ($('dbgArgs').value.match(/"[^"]*"|\S+/g) ?? []).map(a => a.replace(/^"|"$/g, ''));
  state.debug.trail = [];
  setState('starting');
  try { await api('/debug/launch', { method: 'POST', body: { program: target.program, args, stopAtEntry } }); }
  catch (e) { toast(e.message, 'error'); setState('idle'); }
}

async function attach() {
  let processes;
  try { processes = await api('/live/processes'); } catch (e) { toast(e.message, 'error'); return; }
  emit('modal', {
    title: 'Attach the debugger to…',
    html: processes.length ? `<table class="table">${processes.map(p => `<tr class="clickable" data-pid="${p.id}"><td class="num">${p.id}</td><td>${escapeHtml(p.name)}</td><td class="muted">${escapeHtml(p.mainModule ?? '')}</td></tr>`).join('')}</table>`
      : '<p class="muted">No .NET processes found.</p>',
    wire: (body, close) => body.querySelectorAll('[data-pid]').forEach(tr => tr.addEventListener('click', async () => {
      close();
      setState('starting');
      try { await api('/debug/attach', { method: 'POST', body: { processId: Number(tr.dataset.pid) } }); }
      catch (e) { toast(e.message, 'error'); setState('idle'); }
    })),
  });
}

async function command(cmd) {
  try { await api('/debug/' + cmd, { method: 'POST' }); }
  catch (e) { toast(e.message, 'error'); }
}

async function toggleAnimate() {
  if (state.debug.animating) { await api('/debug/animate/stop', { method: 'POST' }); return; }
  const delayMs = Number($('speed').value);
  try { await api('/debug/animate', { method: 'POST', body: { steps: 1000, delayMs, stepOver: false } }); }
  catch (e) { toast(e.message, 'error'); }
}

/** Run to a method: a temporary breakpoint on its first line, removed once it's hit. */
async function runTo(member) {
  if (!member?.file) return;
  const line = member.bodyLine || member.line;
  const had = breakpointsIn(member.file).includes(line);
  pendingRunTo = had ? null : { file: member.file, line };
  if (!had) await toggleBreakpoint(member.file, line);
  if (state.debug.state === 'stopped') await command('continue');
  else if (state.debug.state === 'idle' || state.debug.state === 'exited') await launch();
  toast(`Running to ${memberLabel(member)}…`);
}

async function removeTempBreakpoint() {
  const pending = pendingRunTo;
  pendingRunTo = null;
  if (pending && breakpointsIn(pending.file).includes(pending.line)) await toggleBreakpoint(pending.file, pending.line);
}

function onStopped(d) {
  state.debug.frames = d.frames ?? [];
  state.debug.activeFrame = Math.max(0, state.debug.frames.findIndex(f => f.typeId));
  const top = state.debug.frames[state.debug.activeFrame];
  if (top?.typeId) {
    const last = state.debug.trail[state.debug.trail.length - 1];
    if (!last || last.memberId !== top.memberId) state.debug.trail.push({ typeId: top.typeId, memberId: top.memberId });
    if (state.debug.trail.length > 12) state.debug.trail.shift();
  }
  if (pendingRunTo && top?.file?.toLowerCase() === pendingRunTo.file.toLowerCase()) removeTempBreakpoint();

  setState('stopped');
  showFrame();
  renderStack();
  renderLocals(d.locals ?? []);
  if (d.reason === 'exception') {
    toast(`Exception: ${d.text ?? 'see the call stack'}`, 'error');
    emit('output', { category: 'stderr', output: `Stopped on exception: ${d.text ?? ''}\n` });
  }
  emit('debug-stopped', d);
}

function showFrame() {
  const frame = state.debug.frames[state.debug.activeFrame];
  graph.ensureVisible(frame);
  graph.applyRuntime();
  if (!frame?.typeId) return;
  const target = frame.memberId && graph.getCy().getElementById(frame.memberId).length ? frame.memberId : frame.typeId;
  if (follow && !state.debug.animating) graph.select(frame.memberId ?? frame.typeId, { center: true });
  else if (follow) {
    graph.centerOn(target);
    if (state.selected !== (frame.memberId ?? frame.typeId)) { state.selected = null; emit('select', frame.memberId ?? frame.typeId); }
  }
}

function renderStack() {
  const list = $('frames');
  const frames = state.debug.frames;
  if (!frames.length) { list.innerHTML = `<li class="external">${state.debug.state === 'running' ? 'Running…' : 'Not running.'}</li>`; return; }
  list.innerHTML = frames.map((f, i) => {
    const member = f.memberId ? state.members.get(f.memberId) : null;
    const type = f.typeId ? state.types.get(f.typeId) : null;
    const label = member ? `${type?.name}.${memberLabel(member)}` : f.name;
    return `<li data-i="${i}" class="${i === state.debug.activeFrame ? 'active' : ''} ${f.typeId ? '' : 'external'}" title="${escapeHtml(f.name)}">
      <span>${escapeHtml(label)}</span><span class="where">${f.file ? escapeHtml(fileName(f.file)) + ':' + f.line : ''}</span></li>`;
  }).join('');
  list.querySelectorAll('li[data-i]').forEach(li => li.addEventListener('click', async () => {
    state.debug.activeFrame = Number(li.dataset.i);
    renderStack();
    showFrame();
    const frame = state.debug.frames[state.debug.activeFrame];
    try { renderLocals(await api('/debug/frame?frameId=' + frame.id)); } catch { }
  }));
}

function renderLocals(vars) {
  $('locals').innerHTML = vars.length ? vars.map(varHtml).join('') : '<li class="muted">—</li>';
  wireVars($('locals'));
}

function varHtml(v) {
  return `<li><span class="toggle" data-ref="${v.variablesReference}">${v.variablesReference ? '▸' : ''}</span><span class="name">${escapeHtml(v.name)}</span> = ${escapeHtml(v.value)} ${v.type ? `<span class="type">${escapeHtml(v.type)}</span>` : ''}</li>`;
}

function wireVars(root) {
  root.querySelectorAll('.toggle[data-ref]').forEach(t => {
    if (t.dataset.wired) return;
    t.dataset.wired = '1';
    if (t.dataset.ref === '0') return;
    t.addEventListener('click', async () => {
      const li = t.closest('li');
      const open = li.querySelector(':scope > ul');
      if (open) { open.remove(); t.textContent = '▸'; return; }
      t.textContent = '▾';
      try {
        const children = await api('/debug/variables?reference=' + t.dataset.ref);
        const ul = document.createElement('ul');
        ul.innerHTML = children.map(varHtml).join('') || '<li class="muted">empty</li>';
        li.appendChild(ul);
        wireVars(ul);
      } catch (e) { toast(e.message, 'error'); }
    });
  });
}

async function evaluate() {
  const expression = $('evalInput').value.trim();
  if (!expression) return;
  const frame = state.debug.frames[state.debug.activeFrame];
  const r = await api('/debug/evaluate', { method: 'POST', body: { expression, frameId: frame?.id } }).catch(e => ({ error: e.message }));
  const li = document.createElement('li');
  li.innerHTML = `<code>${escapeHtml(expression)}</code> = ${r.error ? `<span style="color:var(--bad)">${escapeHtml(r.error)}</span>` : escapeHtml(r.result)}`;
  $('evalOut').prepend(li);
}

export function refreshStatus() { renderNotice(); }
