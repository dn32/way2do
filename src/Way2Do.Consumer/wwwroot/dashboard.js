// Painel do espaço: busca GET /v/<H>/state a cada 2 s e desenha. O conteúdo das tarefas vem de fora (qualquer um
// escreve no próprio espaço), então todo texto entra só via textContent, nunca como HTML.
'use strict';

const POLL_MS = 2000;
const match = /^\/v\/([0-9a-f]{64})\/?$/.exec(location.pathname);
const spaceId = match ? match[1] : null;
const $ = (id) => document.getElementById(id);

const STATUS = { pending: 'Pendente', done: 'Concluída' };
const PRIORITY = { high: 'Alta', medium: 'Média', low: 'Baixa' };
const PRIORITY_BADGE = { high: 'badge--prio-high', medium: 'badge--prio-med', low: 'badge--prio-low' };
const MODE = { off: 'Desligado', down: 'Fora do ar (tudo 503)', flaky: 'Instável (~50% sem confirmação)' };
const RESULT = {
  applied: ['aplicado', 'c-result--ok'],
  duplicate: ['duplicado', 'c-result--warn'],
  stale: ['desatualizado', 'c-result--warn'],
  ping: ['ping', 'c-result--muted'],
  ignored: ['ignorado', 'c-result--muted'],
  limit: ['descartado (limite)', 'c-result--bad'],
};

let lastState = null;

// Cria um elemento com classe e texto (sempre textContent).
function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
}

function setText(id, value) {
  $(id).textContent = String(value);
}

function setStatus(text, isError) {
  const status = $('status');
  status.textContent = text;
  status.classList.toggle('c-status--error', Boolean(isError));
}

function formatInstant(value) {
  return value ? new Date(value).toLocaleString('pt-BR') : '—';
}

// dueDate é uma data de calendário (AAAA-MM-DD), sem fuso: formata sem passar por Date.
function formatDate(value) {
  const parts = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || '');
  return parts ? `${parts[3]}/${parts[2]}/${parts[1]}` : (value || '—');
}

function todayLocal() {
  const now = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

// Vencida pela data local do aparelho, como no app.
function isOverdue(todo, today) {
  return !todo.deleted && todo.status === 'pending' && Boolean(todo.dueDate) && todo.dueDate < today;
}

function formatLatency(ms) {
  if (ms === null || ms === undefined) return '—';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1).replace('.', ',')} s`;
  return `${(ms / 60000).toFixed(1).replace('.', ',')} min`;
}

function fillDl(id, entries, label) {
  const dl = $(id);
  const nodes = [];
  for (const [key, count] of entries) {
    nodes.push(el('dt', null, label ? label(key) : key), el('dd', null, count));
  }
  if (nodes.length === 0) nodes.push(el('dt', null, 'Nenhum'), el('dd', null, '—'));
  dl.replaceChildren(...nodes);
}

function sortedEntries(obj) {
  return Object.entries(obj || {}).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0], 'pt-BR'));
}

function renderCounters(state) {
  const c = state.counters;
  const today = todayLocal();
  setText('c-total', c.total);
  setText('c-pending', c.byStatus.pending || 0);
  setText('c-done', c.byStatus.done || 0);
  setText('c-urgent', c.urgentPending);
  setText('c-overdue', state.todos.filter((t) => isOverdue(t, today)).length);
  setText('c-deleted', c.deleted);
  fillDl('c-priority', sortedEntries(c.byPriority), (k) => PRIORITY[k] || k);
  fillDl('c-user', sortedEntries(c.byUser), (k) => k || '(sem nome)');
}

function renderHealth(state) {
  const h = state.health;
  setText('h-received', h.received);
  setText('h-duplicates', h.duplicates);
  setText('h-stale', h.stale);
  setText('h-snapshots', h.snapshotsRequested);
  setText('h-latency-avg', formatLatency(h.latencyAvgMs));
  setText('h-latency-max', formatLatency(h.latencyMaxMs));
  setText('h-last', formatInstant(h.lastEventAt));
  setText('h-limit', h.droppedByLimit);
  fillDl('h-types', sortedEntries(h.eventsByType));
}

function renderFailureMode(state) {
  const f = state.failureMode;
  setText('f-mode', MODE[f.mode] || f.mode);
  setText('f-refused', f.refused);
  for (const button of document.querySelectorAll('[data-mode]')) {
    const on = button.dataset.mode === f.mode;
    button.classList.toggle('seg__opt--on', on);
    button.setAttribute('aria-pressed', String(on));
  }
}

function syncUserFilter(todos) {
  const select = $('filter-user');
  const current = select.value;
  const users = [...new Set(todos.map((t) => t.userName))].sort((a, b) => a.localeCompare(b, 'pt-BR'));
  const options = [el('option', null, 'Todos')];
  options[0].value = '';
  for (const user of users) {
    const option = el('option', null, user || '(sem nome)');
    option.value = user;
    options.push(option);
  }
  select.replaceChildren(...options);
  select.value = users.includes(current) ? current : '';
}

function matchesStatus(todo, filter) {
  switch (filter) {
    case 'pending': return !todo.deleted && todo.status === 'pending';
    case 'done': return !todo.deleted && todo.status === 'done';
    case 'deleted': return todo.deleted;
    case 'all': return true;
    default: return !todo.deleted;
  }
}

function todoRow(todo, today) {
  const row = el('tr', todo.deleted ? 'c-row--deleted' : null);

  const title = el('td', 'c-title');
  title.append(el('span', null, todo.title || '(sem título)'));
  if (todo.deleted) title.append(' ', el('span', 'badge badge--due', 'excluída'));
  row.append(title);

  row.append(el('td', null, todo.userName || '—'));
  row.append(el('td', null, STATUS[todo.status] || todo.status));

  const priority = el('td');
  priority.append(el('span', `badge ${PRIORITY_BADGE[todo.priority] || 'badge--due'}`, PRIORITY[todo.priority] || todo.priority));
  row.append(priority);

  const urgent = el('td');
  urgent.append(todo.urgent ? el('span', 'badge badge--urgent', 'urgente') : el('span', 'c-muted', 'não'));
  row.append(urgent);

  const due = el('td');
  if (todo.dueDate) {
    due.append(el('span', `badge ${isOverdue(todo, today) ? 'badge--overdue' : 'badge--due'}`, formatDate(todo.dueDate)));
  } else {
    due.append(el('span', 'c-muted', '—'));
  }
  row.append(due);

  const tags = el('td');
  const tagList = el('span', 'c-tags');
  for (const tag of todo.tags) tagList.append(el('span', 'tag', tag));
  tags.append(todo.tags.length ? tagList : el('span', 'c-muted', '—'));
  row.append(tags);

  row.append(el('td', 'c-num', todo.version));
  row.append(el('td', 'c-nowrap', formatInstant(todo.updatedAt)));
  return row;
}

function renderTodos() {
  if (!lastState) return;
  const today = todayLocal();
  const user = $('filter-user').value;
  const status = $('filter-status').value;
  const visible = lastState.todos.filter((t) => (!user || t.userName === user) && matchesStatus(t, status));
  $('todos').replaceChildren(...visible.map((t) => todoRow(t, today)));
  $('todos-empty').hidden = visible.length > 0;
}

function renderEvents(state) {
  const rows = state.recentEvents.map((e) => {
    const row = el('tr');
    row.append(el('td', 'c-nowrap', formatInstant(e.receivedAt)));
    row.append(el('td', 'c-mono', e.type));
    row.append(el('td', null, e.title || (e.todoId ? e.todoId : '—')));
    row.append(el('td', 'c-num', e.version ?? '—'));
    const [label, cls] = RESULT[e.result] || [e.result, 'c-result--muted'];
    const result = el('td');
    result.append(el('span', `badge ${cls}`, label));
    row.append(result);
    return row;
  });
  if (rows.length === 0) {
    const row = el('tr');
    const cell = el('td', 'c-muted', 'Nenhum evento.');
    cell.colSpan = 5;
    row.append(cell);
    rows.push(row);
  }
  $('events').replaceChildren(...rows);
}

function render(state) {
  lastState = state;
  $('empty').hidden = true;
  $('content').hidden = false;
  renderCounters(state);
  renderHealth(state);
  renderFailureMode(state);
  syncUserFilter(state.todos);
  renderTodos();
  renderEvents(state);
}

function showEmpty() {
  lastState = null;
  $('content').hidden = true;
  $('empty').hidden = false;
}

async function refresh() {
  try {
    const response = await fetch(`/v/${spaceId}/state`, { cache: 'no-store' });
    if (response.status === 404) {
      showEmpty();
      setStatus(`Verificado às ${new Date().toLocaleTimeString('pt-BR')}`);
      return;
    }
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    render(await response.json());
    setStatus(`Atualizado às ${new Date().toLocaleTimeString('pt-BR')}`);
  } catch {
    setStatus('Sem resposta do consumidor; tentando de novo…', true);
  }
}

async function setFailureMode(mode) {
  const error = $('f-error');
  error.hidden = true;
  try {
    const response = await fetch(`/v/${spaceId}/failure-mode`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode }),
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
  } catch {
    error.textContent = 'Não foi possível trocar o modo de falha.';
    error.hidden = false;
  }
  await refresh();
}

async function loop() {
  await refresh();
  setTimeout(loop, POLL_MS);
}

if (!spaceId) {
  setStatus('Endereço de painel inválido.', true);
} else {
  for (const button of document.querySelectorAll('[data-mode]')) {
    button.addEventListener('click', () => setFailureMode(button.dataset.mode));
  }
  $('filter-user').addEventListener('change', renderTodos);
  $('filter-status').addEventListener('change', renderTodos);
  loop();
}
