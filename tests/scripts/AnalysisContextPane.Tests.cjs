const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
assert.match(html, /<div id="contextCard"[^>]* hidden>/);
assert.match(html, /id="btnSummary"/);

function scriptBetween(start, end) {
  const first = html.indexOf(start);
  const last = html.indexOf(end, first);
  assert.ok(first >= 0 && last > first);
  return html.slice(first, last);
}

const nodes = new Map();
const state = { analysis: { contextRequestId: 0, ctx: { hasEmail: true, entryId: 'old', folder: { path: 'old' } } }, activeEmailId: null };
let scope = 'accounts';
let posted;
const context = vm.createContext({
  state,
  flowStale: () => {},
  $: id => {
    if (!nodes.has(id)) nodes.set(id, { hidden: true, classList: { remove() {} } });
    return nodes.get(id);
  },
  analysisScope: () => scope,
  refreshAnalysisForm: () => {},
  closeMenus: () => {},
  post: message => { posted = message; },
  drawerLoads: {},
  finishDrawerLoad: () => {},
  setTimeout: () => 0,
  showToast: () => {},
});
new vm.Script(scriptBetween('  function hideEmailContext() {', '  // ---------- Markdown')).runInContext(context);
new vm.Script(scriptBetween('  function requestAnalysisContext() {', '  // What each kind')).runInContext(context);

context.showEmailContext({ entryId: 'mail-1', subject: 'Offerta', meta: 'Mario', threadPosition: 2 });
assert.equal(nodes.get('contextCard').hidden, false);
assert.equal(nodes.get('emailSubject').textContent, 'Offerta');
assert.equal(nodes.get('threadBadge').textContent, 'Conversazione · messaggio 2');
context.hideEmailContext();
assert.equal(nodes.get('contextCard').hidden, true);
assert.equal(state.activeEmailId, null);

context.requestAnalysisContext();
assert.equal(posted.scope, 'none');
assert.equal(state.analysis.ctx.entryId, null);
assert.equal(state.analysis.ctx.folder, null);

scope = 'thread';
context.requestAnalysisContext();
assert.equal(posted.scope, 'thread');
assert.equal(posted.requestId, 2);

scope = 'folder';
context.requestAnalysisContext();
assert.equal(posted.scope, 'folder');

scope = 'archive';
context.requestAnalysisContext();
assert.equal(posted.scope, 'none');

let rendered = 0;
context.renderAnalysisStores = () => { rendered += 1; };
new vm.Script(scriptBetween('  function fillAnalysisForm(ctx) {', '  function addAnalysisFolder(folder) {')).runInContext(context);
context.$('analysisDrawer');
nodes.get('analysisDrawer').hidden = false;
context.fillAnalysisForm({ requestId: 3, entryId: 'stale' });
assert.equal(rendered, 0);
assert.equal(state.analysis.ctx.entryId, null);
nodes.get('analysisDrawer').hidden = true;
context.fillAnalysisForm({ requestId: 4, entryId: 'closed' });
assert.equal(rendered, 0);

context.updateActions = () => {};
context.setComposerMode = () => {};
new vm.Script(scriptBetween('  function onHostMessage(data) {', '  if (webview) {')).runInContext(context);
scope = 'accounts';
state.activeEmailId = 'mail-1';
state.contextKey = 'mail-1';
context.onHostMessage({ type: 'context_update', hasEmail: true, entryId: 'mail-2', subject: 'Should stay hidden' });
assert.equal(nodes.get('contextCard').hidden, true);
assert.equal(nodes.get('emailSubject').textContent, 'Offerta');
scope = 'thread';
nodes.get('analysisDrawer').hidden = false;
context.onHostMessage({ type: 'context_update', hasEmail: true, entryId: 'mail-3' });
assert.equal(posted.scope, 'thread');
assert.equal(state.analysis.ctx.entryId, null);
nodes.get('analysisDrawer').hidden = true;
context.onHostMessage({ type: 'stream_context', entryId: 'mail-2', subject: 'Usata', meta: 'Autore' });
assert.equal(nodes.get('contextCard').hidden, false);
assert.equal(nodes.get('emailSubject').textContent, 'Usata');
console.log('PASS analysis pane: email context stays hidden until explicitly used');

// A208/A212: a displayed subject is not sufficient without both physical email identities.
state.analysis.preset = 'reconstruct';
state.analysis.ctx = { selectionScope: 'flow', hasEmail: true, entryId: 'E', subject: 'Selected', archive: { active: true } };
state.configOk = false;
context.analysisScope = () => 'flow';
context.$('anInstruction').value = '';
context.$('anStart').lastChild = {};
new vm.Script(scriptBetween('  function updateAnalysisStart() {', '  function setAnalysisScope')).runInContext(context);
context.updateAnalysisStart();
assert.equal(context.$('anStart').disabled, true, 'missing StoreId must not offer a broken reconstruction');
state.analysis.ctx.storeId = 'S';
context.updateAnalysisStart();
assert.equal(context.$('anStart').disabled, false, 'complete identity is sufficient without an AI profile');
console.log('PASS A208/A212 complete physical identity required before local reconstruction');
