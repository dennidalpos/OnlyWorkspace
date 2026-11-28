const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function section(start, end) {
  const first = html.indexOf(start), last = html.indexOf(end, first);
  assert.ok(first >= 0 && last > first, start);
  return html.slice(first, last);
}
const nodes = new Map();
const suggestion = { dataset: { action: 'summary' } };
const mailTool = { dataset: {}, hasAttribute: name => name === 'data-needs-email' };
const state = { busy: false, configOk: true, contextKey: '', contextStoreId: '', composerMode: 'chat', analysis: { ctx: null } };
let closed = 0;
const ctx = vm.createContext({
  state, prompt: {},
  flowStale: () => {},
  $: id => {
    if (!nodes.has(id)) nodes.set(id, { hidden: true, innerHTML: '', dataset: {}, classList: { remove() {} } });
    return nodes.get(id);
  },
  document: { querySelectorAll: selector => selector === '.suggestion' ? [suggestion] : [mailTool] },
  hasTurns: () => false,
  updateSendButton: () => {},
  setComposerMode: mode => { state.composerMode = mode; },
  closeMenus: () => { closed++; },
  analysisScope: () => 'accounts',
});
new vm.Script(section('  function hideEmailContext() {', '  // ---------- Markdown')).runInContext(ctx);
new vm.Script(section('  function updateActions() {', '  function setBusy(')).runInContext(ctx);
new vm.Script(section('  function onHostMessage(data) {', '  if (webview) {')).runInContext(ctx);
const send = data => ctx.onHostMessage(Object.assign({ type: 'context_update' }, data));

send({ hasEmail: false });
assert.equal(nodes.get('emailGroup').hidden, true);
assert.equal(nodes.get('emailSelectionHint').hidden, false);
assert.equal(suggestion.hidden, true);
assert.equal(mailTool.disabled, true);
assert.equal(nodes.get('btnBriefing').disabled, false);
assert.equal(ctx.prompt.disabled, false);

const subject = '<img src=x onerror=alert(1)> Offerta';
send({ hasEmail: true, entryId: 'E1', storeId: 'S1', subject });
assert.equal(nodes.get('emailGroup').hidden, false);
assert.equal(nodes.get('selectedEmailSubject').textContent, subject);
assert.equal(nodes.get('selectedEmailSubject').innerHTML, '');
assert.equal(nodes.get('btnSummary').disabled, false);
assert.equal(suggestion.hidden, false);
assert.equal(nodes.get('contextCard'), undefined, 'selection is not a source already used by an operation');
ctx.showEmailContext({ entryId: 'E1', storeId: 'S1', subject: 'Fonte usata' });
send({ hasEmail: true, entryId: 'E1', storeId: 'S1', subject });
assert.equal(nodes.get('contextCard').hidden, false);

state.composerMode = 'reply';
send({ hasEmail: true, entryId: 'E1', storeId: 'S2', subject: 'Altro account' });
assert.equal(state.composerMode, 'chat', 'a reply instruction must not silently move to another account');
assert.equal(nodes.get('contextCard').hidden, true);
assert.ok(closed > 0);

// Explorer inline replies can still report the original selection: only the draft actions apply.
send({ hasEmail: true, entryId: 'E1', storeId: 'S2', subject, compose: { subject: 'Re: Offerta', kind: 'reply' } });
assert.equal(state.hasEmail, false);
assert.equal(nodes.get('emailGroup').hidden, true);
assert.equal(nodes.get('composeGroup').hidden, false);
assert.equal(nodes.get('composeEmailSubject').textContent, 'Re: Offerta');
assert.equal(nodes.get('selectedEmailSubject').textContent, '');
assert.equal(nodes.get('btnSummary').disabled, true);
assert.equal(nodes.get('btnFlow').disabled, true);
assert.equal(mailTool.disabled, true);
assert.equal(nodes.get('emailSelectionHint').hidden, true);
assert.equal(nodes.get('btnBriefing').disabled, false);
assert.equal(nodes.get('btnTranslateMenu').disabled, false);

send({ compose: { subject: '', kind: 'new' } });
assert.equal(nodes.get('composeEmailSubject').textContent, '(senza oggetto)');
state.composerMode = 'reply';
send({ hasEmail: false });
assert.equal(state.composerMode, 'chat');
assert.equal(nodes.get('composeGroup').hidden, true);
assert.equal(nodes.get('emailSelectionHint').hidden, false);
state.composerMode = 'refine';
send({ hasEmail: false });
assert.equal(state.composerMode, 'refine', 'a bound result can still be refined without a current selection');
// Reply instructions bind when armed. A pending native selection event cannot move their source.
const posted = [];
ctx.post = message => posted.push(JSON.parse(JSON.stringify(message)));
ctx.autoresize = () => {};
ctx.icon = () => '';
new vm.Script(section('  function setComposerMode(mode, refine) {', '  prompt.addEventListener(\'input\'')).runInContext(ctx);
state.selectedEmail = { entryId: 'reply-email', storeId: 'reply-store', subject: 'Fonte' };
ctx.setComposerMode('reply');
state.selectedEmail = { entryId: 'other-email', storeId: 'other-store' };
ctx.prompt.value = 'Conferma';
// The provisional prompt of #240 has its own test (ChatPendingSend.Tests.cjs).
ctx.beginPendingSend = () => {};
ctx.submitPrompt();
assert.deepEqual(posted.pop(), { type: 'custom_reply', instruction: 'Conferma', entryId: 'reply-email', storeId: 'reply-store' });
state.selectedEmail = null;
ctx.setComposerMode('reply');
assert.equal(state.composerMode, 'chat', 'reply mode needs an identifiable source');
console.log('PASS contextual email/draft actions, literal subject, cross-store changes, no-selection and inline compose');

// Without a working AI profile every dimmed action says why, and gets its own title back afterwards.
mailTool.title = 'Verifica';
state.configOk = false;
ctx.updateActions();
assert.match(mailTool.title, /^Verifica — IA non disponibile/);
state.configOk = true;
ctx.updateActions();
assert.equal(mailTool.title, 'Verifica');
