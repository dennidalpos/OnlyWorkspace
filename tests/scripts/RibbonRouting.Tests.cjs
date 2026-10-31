const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

// Messages the ribbon buttons send to the pane (OnlyWorkspaceAddin → TaskPaneCoordinator → onHostMessage).
const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function scriptBetween(start, end) {
  const first = html.indexOf(start);
  const last = html.indexOf(end, first);
  assert.ok(first >= 0 && last > first, start);
  return html.slice(first, last);
}

const nodes = new Map();
const calls = [];
const state = { analysis: { preset: 'summary', stores: {} }, busy: false, current: null, job: {} };
const context = vm.createContext({
  state,
  $: id => {
    if (!nodes.has(id)) nodes.set(id, { hidden: true, value: '', scrollIntoView() {}, classList: { remove() {}, add() {}, toggle() {} } });
    return nodes.get(id);
  },
  // Drawer doubles: one screen open at a time, as in the pane.
  openDrawer: id => { for (const n of ['historyDrawer', 'infoDrawer', 'analysisDrawer', 'partyDrawer']) context.$(n).hidden = n !== id; calls.push('open:' + id); },
  closeDrawers: () => { for (const n of ['historyDrawer', 'infoDrawer', 'analysisDrawer', 'partyDrawer']) context.$(n).hidden = true; calls.push('close'); },
  focusComposer: (mode, prompt) => calls.push('composer:' + mode + ':' + (prompt || '')),
  clearConversation: () => calls.push('clear'),
  finishTurn: () => {},
  updateActions: () => {},
  endPartyMerge: () => {},
  showPartyList: () => calls.push('parties'),
  hideEmailContext: () => calls.push('hide-context'),
});
new vm.Script('function toggleDrawer(id) { if ($(id).hidden) openDrawer(id); else closeDrawers(); }').runInContext(context);
new vm.Script(scriptBetween('  function openAnalysis(preset) {', '  function analysisScope() {')).runInContext(context);
context.scopeRadio = () => ({ checked: false });
new vm.Script(scriptBetween('  function onHostMessage(data) {', '  if (webview) {')).runInContext(context);
const send = data => context.onHostMessage(data);
const open = () => ['historyDrawer', 'infoDrawer', 'analysisDrawer', 'partyDrawer'].filter(n => !context.$(n).hidden);

// Storico and Stato: the first click opens; a second click on a pane already on screen closes.
send({ type: 'toggle_history', toggle: false });
assert.deepEqual(open(), ['historyDrawer']);
send({ type: 'toggle_history', toggle: true });
assert.deepEqual(open(), []);
send({ type: 'toggle_info', toggle: true });
assert.deepEqual(open(), ['infoDrawer']);
send({ type: 'toggle_info', toggle: true });
assert.deepEqual(open(), []);
// Pane just shown by the click (toggle false): always opens, even if the screen was left open.
send({ type: 'toggle_info', toggle: false });
send({ type: 'toggle_info', toggle: false });
assert.deepEqual(open(), ['infoDrawer']);

send({ type: 'toggle_info', section: 'archive', toggle: false });
assert.deepEqual(open(), ['infoDrawer']);
// Cerca e analizza and Email senza risposta: each opens its own kind of search.
send({ type: 'toggle_analysis', preset: 'followups', toggle: true });
assert.deepEqual(open(), ['analysisDrawer']);
assert.equal(state.analysis.preset, 'followups');
send({ type: 'toggle_analysis', preset: 'summary', toggle: true });
assert.deepEqual(open(), ['analysisDrawer'], 'Another kind of search switches, it does not close.');
assert.equal(state.analysis.preset, 'summary');
send({ type: 'toggle_analysis', preset: 'summary', toggle: true });
assert.deepEqual(open(), []);
send({ type: 'toggle_analysis', toggle: false });
assert.equal(state.analysis.preset, 'summary', 'No preset means the plain search.');

// Controparti.
send({ type: 'toggle_party', toggle: true });
assert.deepEqual(open(), ['partyDrawer']);
send({ type: 'toggle_party', toggle: true });
assert.deepEqual(open(), []);
send({ type: 'toggle_party' });
send({ type: 'toggle_party' });
assert.deepEqual(open(), ['partyDrawer'], 'The context menu always opens.');

// Chat, Bozza risposta, Nuova chat, Riquadro.
calls.length = 0;
send({ type: 'show_input', mode: 'reply', prompt: '' });
send({ type: 'show_input', mode: 'chat' });
send({ type: 'new_chat_ready' });
send({ type: 'hide_email_context' });
assert.deepEqual(calls, ['composer:reply:', 'composer:chat:', 'clear', 'composer:chat:', 'hide-context']);

// Every message the ribbon path posts has a handler.
const coordinator = ['TaskPaneCoordinator.cs', 'TaskPaneCoordinator.Archive.cs']
  .map(f => fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI', f), 'utf8')).join('\n');
for (const type of ['toggle_history', 'toggle_info', 'toggle_analysis', 'toggle_party', 'show_input', 'new_chat_ready', 'hide_email_context']) {
  assert.match(coordinator, new RegExp('type = "' + type + '"'), type + ' is sent by the host');
  assert.match(html, new RegExp("case '" + type + "':"), type + ' is handled by the pane');
}
console.log('PASS ribbon routing: each button opens its screen, a second click closes it, presets switch');
