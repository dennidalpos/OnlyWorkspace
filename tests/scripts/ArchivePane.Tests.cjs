const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
for (const block of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)) new vm.Script(block[1]);

// Execute the production renderer with DOM doubles; this does not qualify browser layout.
const start = html.indexOf('  var archive = ');
const end = html.indexOf('  function formatTokens(', start);
assert.ok(start >= 0 && end > start, 'Archive renderer was not found.');

function node(tag) {
  const n = {
    tag, className: '', textContent: '', title: '', hidden: false, style: {}, dataset: {}, children: [], attrs: {},
    appendChild(child) { this.children.push(child); return child; },
    setAttribute(k, v) { this.attrs[k] = v; },
    set innerHTML(value) { this.children = []; },
  };
  n.tBodies = [n];
  // The indicator's destination in words: <span class="ind-dest">PC|Cloud</span>.
  n.querySelector = () => n.dest || (n.dest = node('span'));
  return n;
}
const announced = [];
const text = n => n.textContent + n.children.map(text).join('');
const nodes = new Map();
const context = vm.createContext({ setupContextHelp() {}, addDocumentReport() {},
  $: id => {
    if (!nodes.has(id)) nodes.set(id, node('div'));
    return nodes.get(id);
  },
  el: (tag, cls, value) => Object.assign(node(tag), { className: cls || '', textContent: value == null ? '' : value }),
  formatTokens: n => String(n),
  announce: t => announced.push(t),
  showToast: () => {},
});
new vm.Script(html.slice(start, end)).runInContext(context);

const metrics = [
  { Key: 'bodies', Name: 'Testi email', Unit: 'count', Done: 1200, Total: 2000, Remaining: 700, Excluded: 100, Percent: 63 },
  { Key: 'db', Name: 'Database', Unit: 'bytes', Done: 3 * 1073741824, Total: null, Remaining: 0, Excluded: 0, Percent: null },
];
const stores = [{ Name: 'Posta', State: 'inventario', Emails: 2000, BodiesPercent: 63, BodiesRemaining: 700, FoldersDone: 3, Folders: 5, CardsPercent: null, Error: '' }];
context.renderArchive({ available: true, active: true, light: 'Working', phase: 'testi', error: '', metrics, stores, cards: { state: 'off' } });

// Figures in fixed columns: name, done, total, %, remaining, excluded.
const rows = nodes.get('arMetrics').children;
assert.equal(rows.length, 2);
assert.deepEqual(rows[0].children.map(c => c.textContent), ['Testi email', '1200', '2000', '63%', '700', '100']);
assert.equal(rows[0].children[3].children[0].children[0].style.width, '63%');
assert.equal(rows[1].children[1].textContent, '3 GB');
context.renderArchive({ available: true, active: true, light: 'Error', phase: 'attesa inattività',
  metrics: [{ Key: 'folders', Name: 'Cartelle', Unit: 'count', Done: 27, Total: 28, Remaining: 0, Excluded: 1, Percent: 100 }], stores: [] });
assert.deepEqual(nodes.get('arMetrics').children[0].children.map(c => c.textContent), ['Cartelle', '27', '28', '100%', '0', '1']);
context.renderArchive({ available: true, active: true, light: 'Working', phase: 'testi', error: '', metrics, stores, cards: { state: 'off' } });
assert.equal(nodes.get('arPhase').textContent, 'testi');
assert.equal(nodes.get('arErrorRow').hidden, true);
// The top bar keeps its indicator; the Stato section has none.
assert.equal(nodes.get('btnArchiveState').dataset.light, 'Working');
assert.ok(!nodes.has('arLight'));
// Each mailbox: its name on a row of its own, then the figures.
const storeRows = nodes.get('arStores').children;
assert.equal(storeRows.length, 2);
assert.equal(text(storeRows[0].children[0]), 'Posta');
assert.equal(storeRows[0].children[0].children[0].className, 'store-head', 'Name and reason wrap at the visible width.');
assert.equal(storeRows[0].children[0].colSpan, 5);
assert.equal(storeRows[1].children.length, 5, 'Cards column only when cards are on.');
assert.equal(text(storeRows[1].children[0]), 'inventario');
assert.equal(storeRows[1].children[4].textContent, '3/5');
context.renderArchive({ available: true, active: true, light: 'Ok', phase: 'aggiornato', metrics: [], stores, cards: { state: 'running' } });
assert.equal(nodes.get('arStores').children[0].children[0].colSpan, 5);
assert.equal(nodes.get('arStores').children[1].children.length, 5);
// A mailbox's own error shows under its name.
const limit = 'Elenco cartelle incompleto: superato il limite di 3000 cartelle per cassetta.';
context.renderArchive({ available: true, active: true, light: 'Ok', phase: 'aggiornato', metrics: [], stores: [{ ...stores[0], State: 'errore', Error: limit }], cards: { state: 'off' } });
assert.equal(nodes.get('arStores').children[0].children[0].children[0].children[0].textContent, limit);
// Red because a folder failed while the indexer waits: the phase stays grey and the Errore row says why.
context.renderArchive({ available: true, active: true, light: 'Error', label: 'errori', phase: 'attesa inattività', phaseLight: 'Waiting',
  error: null, reason: '1 cartella non leggibile', metrics: [], stores: [], cards: { state: 'off' } });
assert.equal(nodes.get('arPhase').dataset.light, 'Waiting');
assert.equal(nodes.get('arErrorRow').hidden, false);
assert.equal(nodes.get('arError').textContent, '1 cartella non leggibile');
assert.equal(nodes.get('btnArchiveState').attrs['aria-label'], 'Archivio: errori · fase attesa inattività. 1 cartella non leggibile');
context.renderArchive({ available: true, active: true, light: 'Ok', phase: 'aggiornato', metrics: [], stores: [], cards: { state: 'off' } });
assert.equal(nodes.get('arErrorRow').hidden, true, 'Back to green: no stale reason.');
announced.length = 0;

// Indicator: light, label, phase and destination from the host; an unavailable archive never keeps the previous light.
const indicator = nodes.get('btnArchiveState');
context.renderArchive({ available: true, active: true, light: 'Error', label: 'errori', phase: 'errore', error: 'Cassetta non raggiungibile', metrics: [], stores: [], cards: { state: 'running', external: true } });
assert.equal(indicator.dataset.light, 'Error');
assert.equal(indicator.attrs['aria-label'], 'Archivio: errori · fase errore. Cassetta non raggiungibile');
assert.equal(indicator.dest.textContent, 'PC');
assert.deepEqual(announced, ['Archivio: errori · fase errore. Cassetta non raggiungibile']);
context.renderArchive({ available: false, light: 'Off', label: 'non disponibile' });
assert.equal(indicator.dataset.light, 'Off');
assert.equal(indicator.attrs['aria-label'], 'Archivio: non disponibile');
assert.equal(indicator.dest.textContent, 'PC');
assert.equal(nodes.get('archiveSection').hidden, true);
assert.equal(announced.length, 2, 'Leaving an error is announced too.');
// Paused and waiting are grey with their own symbol, not the yellow of real work.
context.setArchiveIndicator({ light: 'Waiting', label: 'in attesa', phase: 'attesa inattività', external: false });
assert.equal(indicator.dataset.light, 'Waiting');
assert.equal(indicator.attrs['aria-label'], 'Archivio: in attesa · fase attesa inattività');
assert.equal(announced.length, 2, 'Only errors are announced.');

context.data = { message: 'Engine unavailable' };
const errorCase = html.match(/case 'archive_status_error':([\s\S]*?)case 'archive_status':/)[1];
new vm.Script("switch ('archive_status_error') { case 'archive_status_error':" + errorCase + "}").runInContext(context);
assert.equal(nodes.get('arError').textContent, 'Engine unavailable');
assert.equal(nodes.get('arErrorRow').hidden, false);
assert.equal(indicator.dataset.light, 'Error', 'A status read failure paints the indicator red too.');
const lightCase = html.match(/case 'archive_light':([\s\S]*?)case 'ai_status':/)[1];
context.data = { light: 'Ok', label: 'sincronizzato', phase: 'aggiornato', external: false };
new vm.Script("switch ('archive_light') { case 'archive_light':" + lightCase + "}").runInContext(context);
assert.equal(indicator.dataset.light, 'Ok');
assert.equal(indicator.attrs['aria-label'], 'Archivio: sincronizzato · fase aggiornato');
// The light summary is polled while the pane is visible, not only with Stato open.
assert.match(html, /visibilityState === 'hidden'\) return;\s*post\(\{ type: !\$\('infoDrawer'\)\.hidden && archive\.active \? 'archive_get' : 'archive_light_get' \}\);/);
assert.doesNotMatch(html, /archive_activate|archive_consent|welcomeCard/);

assert.doesNotMatch(html, /renderArchiveCards|arCardsRow|arStoreCards/);
console.log('PASS archive pane: syntax, local metrics, mailboxes and top-bar status without IA cards');

// A210: Stato shows the essentials; technical rows, metric tables and the export live in «Avanzate», closed by default.
const drawer = html.slice(html.indexOf('<aside id="infoDrawer"'), html.indexOf('<aside id="helpDrawer"'));
const advancedTag = drawer.match(/<details id="statusAdvanced"[^>]*>/);
assert.ok(advancedTag, 'Stato has an Avanzate section.');
assert.doesNotMatch(advancedTag[0], /\sopen[\s>=]/, 'Avanzate is closed by default.');
const advanced = drawer.slice(drawer.indexOf('<details id="statusAdvanced"'), drawer.indexOf('</details>'));
const essential = drawer.replace(advanced, '');
for (const id of ['arMetrics', 'arStores', 'btnDiag', 'infoEndpoint', 'infoProvider', 'infoOutlook', 'infoVersion']) {
  assert.ok(advanced.includes('id="' + id + '"'), id + ' is in Avanzate.');
  assert.ok(!essential.includes('id="' + id + '"'), id + ' is not among the essentials.');
}
for (const id of ['infoProfile', 'infoModel', 'infoTarget', 'btnCheckHealth', 'arError', 'arPhase', 'arChoose', 'arPause', 'btnOpenConfig']) {
  assert.ok(essential.includes('id="' + id + '"'), id + ' stays among the essentials.');
}
// The detail follows the archive: hidden while it is not active.
context.renderArchive({ available: true, active: false, light: 'Off', phase: 'spento', metrics: [], stores: [] });
assert.equal(nodes.get('arDetail').hidden, true);
context.renderArchive({ available: true, active: true, light: 'Working', phase: 'testi', metrics, stores });
assert.equal(nodes.get('arDetail').hidden, false);
console.log('PASS Stato: essentials in view, diagnostics in a closed Avanzate section');

// A192: execute the actual WebView event boundary, toast and live-region path.
const boundaryNodes = new Map(), diagnostics = [], spoken = [], consoleErrors = [];
const boundary = vm.createContext({ setupContextHelp() {}, addDocumentReport() {}, measurePaneChrome() {},
  $: id => { if (!boundaryNodes.has(id)) boundaryNodes.set(id, node('div')); return boundaryNodes.get(id); },
  announce: text => spoken.push(text), clearTimeout: () => {}, setTimeout: () => 1,
  console: { error: error => consoleErrors.push(error) },
  post: data => diagnostics.push(data),
  onHostMessage: () => { throw new TypeError('synthetic English detail'); },
  webview: { addEventListener: (event, listener) => { assert.equal(event, 'message'); boundary.listener = listener; } }
});
vm.runInContext(html.slice(html.indexOf('  var toastTimer = null;'), html.indexOf("  $('toastClose')")), boundary);
vm.runInContext(html.slice(html.lastIndexOf('  if (webview) {'), html.lastIndexOf("  setComposerMode('chat');")), boundary);
boundary.listener({ data: { type: 'archive_status' } });
assert.match(boundaryNodes.get('toastText').textContent, /Il riquadro non è riuscito.*chiudilo e riaprilo/);
assert.doesNotMatch(boundaryNodes.get('toastText').textContent, /synthetic English detail|TypeError|Dettaglio:/);
assert.match(spoken.at(-1), /^Errore: Il riquadro/);
assert.equal(diagnostics[0].type, 'page_error');
assert.match(diagnostics[0].detail, /TypeError: synthetic English detail/);
assert.equal(consoleErrors.length, 1);
boundary.onHostMessage = () => { throw new Error('x'.repeat(5000)); };
boundary.listener({ data: {} });
assert.equal(diagnostics[1].detail.length, 2048, 'Host diagnostic is bounded.');
boundary.post = () => { throw new Error('host unavailable'); };
boundary.listener({ data: {} });
assert.match(spoken.at(-1), /^Errore: Il riquadro/, 'A failed log post must not suppress user feedback.');

// The terminal event routes keep errors and deliberate interruptions distinct.
const terminalResults = [];
const terminal = vm.createContext({ setupContextHelp() {}, addDocumentReport() {}, state: { current: {} }, finishTurn: result => terminalResults.push(result) });
const terminalCases = html.slice(html.indexOf("      case 'stream_error':"), html.indexOf("      case 'toast':"));
vm.runInContext('function dispatch(data) { switch(data.type) {' + terminalCases + '} }', terminal);
terminal.dispatch({ type: 'stream_error', message: 'Operazione non riuscita.' });
terminal.dispatch({ type: 'stream_cancelled', message: 'Operazione annullata.' });
assert.equal(terminalResults[0].status, 'error');
assert.equal(terminalResults[1].status, 'cancelled');
console.log('PASS A192: JavaScript exception stays in diagnostic, Italian toast announced, cancellation distinct');

const terminalSpeech = [], terminalState = {};
const finish = vm.createContext({ setupContextHelp() {}, addDocumentReport() {},
  state: terminalState, clearTimeout: () => {}, isNearBottom: () => false,
  renderMarkdown: text => text, announce: text => terminalSpeech.push(text),
  el: (tag, cls, value) => Object.assign(node(tag), { className: cls || '', textContent: value || '' }),
  icon: () => '', actionButton: () => node('button'), openDrawerId: () => null, setBusy: () => {}
});
vm.runInContext(html.slice(html.indexOf('  function finishTurn(result)'), html.indexOf('  // ---------- Toast ----------')), finish);
function ending(status, message) {
  const assistant = node('div');
  terminalState.current = { phase: { remove() {} }, body: node('div'), assistant, text: '', startedAt: 0 };
  finish.finishTurn({ status, message });
  return assistant;
}
assert.equal(ending('error', 'Operazione non riuscita.').children[0].className, 'error-box');
assert.equal(ending('cancelled', 'Operazione annullata.').children[0].className, 'msg-note');
assert.deepEqual(terminalSpeech, ['Errore: Operazione non riuscita.', 'Operazione annullata.']);
console.log('PASS A192: real finishTurn renders and announces failures separately from cancellation');

announced.length = 0;
context.setArchiveIndicator({ light: 'Error', label: 'errori', reason: 'Primo errore.' });
context.setArchiveIndicator({ light: 'Error', label: 'errori', reason: 'Secondo errore.' });
context.setArchiveIndicator({ light: 'Error', label: 'errori', reason: 'Secondo errore.', phase: 'errore' });
assert.equal(announced.length, 2, 'A different error is announced even while already red; polling repeats are quiet.');
assert.match(announced[1], /Secondo errore/);
console.log('PASS A192: changed archive error announced once without polling repetitions');
