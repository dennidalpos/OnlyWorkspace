const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
for (const block of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)) new vm.Script(block[1]);

// Execute the production contacts section with DOM doubles; this does not qualify browser layout.
const start = html.indexOf('  // ---------- Contacts (archive people');
const end = html.indexOf('  // ---------- Keyboard', start);
assert.ok(start >= 0 && end > start, 'Contacts section was not found.');

function node(tag) {
  const n = {
    tag, className: '', textContent: '', title: '', hidden: false, disabled: false, checked: false, value: '', type: '',
    style: {}, dataset: {}, children: [], listeners: {}, options: [], selectedOptions: [],
    appendChild(child) { this.children.push(child); if (tag === 'select' && child.tag === 'option') this.options.push(child); return child; },
    replaceChild(a, b) { const i = this.children.indexOf(b); if (i >= 0) this.children[i] = a; },
    setAttribute() {},
    addEventListener(type, fn) { this.listeners[type] = fn; },
    // Only the selectors the contacts section uses.
    querySelectorAll(sel) {
      const all = [];
      const walk = (n, inOpen) => n.children.forEach(c => { all.push({ c, inOpen }); walk(c, inOpen || (c.tag === 'details' && c.open)); });
      walk(this, false);
      const m = /^(details\[open\] )?input\[data-(store|folder)\]$/.exec(sel);
      return m ? all.filter(x => x.c.tag === 'input' && x.c.dataset[m[2]] != null && (!m[1] || x.inOpen)).map(x => x.c) : [];
    },
    querySelector() { return null; },
    contains() { return false; },
    get innerHTML() { return ''; },
    set innerHTML(value) { this.children = []; this.options = []; },
  };
  return n;
}
const text = n => n.textContent + n.children.map(text).join('');
const find = (n, pred) => pred(n) ? n : n.children.map(c => find(c, pred)).find(Boolean);
const nodes = new Map();
const posted = [];
const timers = [];
const context = vm.createContext({
  $: id => {
    if (!nodes.has(id)) nodes.set(id, node(id === 'ctDestStore' || id === 'ctDestFolder' ? 'select' : 'div'));
    return nodes.get(id);
  },
  el: (tag, cls, value) => Object.assign(node(tag), { className: cls || '', textContent: value == null ? '' : value }),
  post: m => posted.push(JSON.parse(JSON.stringify(m))),
  formatCount: n => String(n || 0),
  plural: (n, one, many) => String(n || 0) + ' ' + (n === 1 ? one : many),
  armConfirm: (b, label, fn) => fn(),
  showToast: () => {},
  state: { busy: false }, hasTurns: () => false,
  updateJobBar: () => {},
  document: { querySelector: () => null },
  CSS: { escape: s => s },
  setInterval: (fn, ms) => timers.push({ fn, ms }),
});
new vm.Script(html.slice(start, end)).runInContext(context);

// Scope: archived mailboxes selectable, the others disabled; one mailbox searched → its own address book.
context.renderContactsScope({
  available: true,
  stores: [
    { id: 's1', name: 'Posta', open: true, archived: true, note: '', issue: '', folders: [{ id: 1, path: 'Posta\\Inbox', emails: 10 }, { id: 2, path: 'Posta\\Fornitori', emails: 4 }] },
    { id: 's2', name: 'Vecchio PST', open: true, archived: false, note: 'Non archiviata', issue: '', folders: [] }
  ],
  saved: { stores: ['s1'], excluded: [2], destStore: 's1', destFolder: '', excludeOwnDomains: true }
});
const stores = nodes.get('ctStores').children;
assert.equal(stores.length, 2);
const s1box = find(stores[0], n => n.dataset.store === 's1');
const s2box = find(stores[1], n => n.dataset.store === 's2');
assert.equal(s1box.checked, true);
assert.equal(s2box.disabled, true);
assert.equal(find(stores[0], n => n.dataset.folder === '2').checked, false, 'excluded folder remembered');
assert.equal(nodes.get('ctDestStore').value, 's1');
assert.equal(nodes.get('ctOwnDomains').checked, true);
assert.deepEqual(posted.pop(), { type: 'contacts_folders', storeId: 's1' });

// While the page is open the scope is asked again; an unchanged one leaves the page as it is (C1).
assert.equal(timers.length, 1);
context.$('ctPhase').hidden = true;
timers[0].fn();
assert.deepEqual(posted.pop(), { type: 'contacts_open', refresh: true });
const firstStores = nodes.get('ctStores').children[0];
const scope2 = {
  available: true,
  stores: [
    { id: 's1', name: 'Posta', open: true, archived: true, note: '', issue: '', folders: [{ id: 1, path: 'Posta\\Inbox', emails: 11 }, { id: 2, path: 'Posta\\Fornitori', emails: 4 }] },
    { id: 's2', name: 'Vecchio PST', open: true, archived: false, note: 'Non archiviata', issue: '', folders: [] }
  ],
  saved: { stores: ['s1'], excluded: [2], destStore: 's1', destFolder: '', excludeOwnDomains: true }
};
context.renderContactsScope(scope2);
assert.equal(nodes.get('ctStores').children[0], firstStores, 'same scope: not redrawn');
// A mailbox archived meanwhile: selectable, the choices on screen kept, the address books not asked again.
find(firstStores, n => n.dataset.folder === '1').checked = false;
find(firstStores, n => n.dataset.folder === '2').checked = true;
nodes.get('ctOwnDomains').checked = false;
firstStores.open = true;
const sent = posted.length;
context.renderContactsScope(Object.assign({}, scope2, { stores: [scope2.stores[0],
  { id: 's2', name: 'Vecchio PST', open: true, archived: true, note: '', issue: '', folders: [{ id: 7, path: 'Vecchio\\Inbox', emails: 3 }] }] }));
const redrawn = nodes.get('ctStores').children;
assert.notEqual(redrawn[0], firstStores);
assert.equal(find(redrawn[1], n => n.dataset.store === 's2').disabled, false);
assert.equal(find(redrawn[0], n => n.dataset.store === 's1').checked, true);
assert.equal(find(redrawn[0], n => n.dataset.folder === '1').checked, false, 'folder unticked on screen kept');
assert.equal(find(redrawn[0], n => n.dataset.folder === '2').checked, true, 'folder ticked on screen kept');
assert.equal(redrawn[0].open, true, 'expanded mailbox kept open');
assert.equal(nodes.get('ctOwnDomains').checked, false);
assert.equal(posted.length, sent);
// Busy with an analysis: no refresh.
context.contacts.busy = true;
timers[0].fn();
assert.equal(posted.length, sent);
context.contacts.busy = false;

// Groups by company; up-to-date contacts only counted unless asked.
const list = {
  destStore: 's1', destFolder: 'f1',
  counts: { missing: 2, check: 1, conflict: 0, uptodate: 1, filtered: 3 },
  groups: [
    { key: 'acme.it', name: 'Acme', items: [
      { address: 'mario@acme.it', name: 'Mario Rossi', status: 'missing', emails: 5, sources: 3, last: '01/10/2026', presentIn: 'Gmail' },
      { address: 'info@acme.it', name: '', role: true, status: 'missing', emails: 2, sources: 1 },
      { address: 'anna@acme.it', name: 'Anna', status: 'uptodate', emails: 1, sources: 1, contacts: [{ entryId: 'c9', name: 'Anna' }] }
    ] },
    { key: 'beta.it', name: 'Beta', items: [
      { address: 'luca@beta.it', name: 'Luca', status: 'check', emails: 3, sources: 2, contacts: [{ entryId: 'c1', name: 'Luca' }] }
    ] }
  ]
};
context.renderContactsList(list);
let groups = nodes.get('ctResults').children;
assert.equal(groups.length, 2);
assert.equal(groups[0].children.length, 1 + 2, 'Acme: header + two items, the up-to-date one hidden');
assert.match(text(nodes.get('ctCounts')), /2 mancanti · 1 da verificare · 0 in conflitto · 1 aggiornato · 3 esclusi/);
assert.match(text(groups[0]), /presente in Gmail/);
assert.match(text(groups[0]), /casella condivisa/);

nodes.get('ctShowAll').checked = true;
nodes.get('ctShowAll').listeners.change.call(nodes.get('ctShowAll'));
assert.equal(nodes.get('ctResults').children[0].children.length, 1 + 3);
nodes.get('ctShowAll').checked = false;
nodes.get('ctShowAll').listeners.change.call(nodes.get('ctShowAll'));

// A missing contact can be refused before any analysis.
const quick = find(nodes.get('ctResults').children[0], n => n.textContent === 'Rifiuta' && n.className === 'link-btn');
quick.listeners.click();
assert.deepEqual(posted.pop(), { type: 'contacts_reject', addresses: ['mario@acme.it'] });

// Selecting a group, then analyzing it.
const head = nodes.get('ctResults').children[0].children[0];
const all = find(head, n => n.tag === 'input' && n.type === 'checkbox');
all.checked = true;
all.listeners.change();
nodes.get('ctAnalyze').listeners.click();
const analyze = posted.pop();
assert.equal(analyze.type, 'contacts_analyze');
assert.deepEqual([...analyze.addresses].sort(), ['info@acme.it', 'mario@acme.it']);

// Leaving the page and coming back during the analysis keeps results, progress and Interrompi; nothing is asked again.
const shownGroups = nodes.get('ctResults').children.length;
const sentBefore = posted.length;
context.contactsOpen();
assert.equal(posted.length, sentBefore, 'no scope request while the analysis runs');
assert.equal(nodes.get('ctResults').children.length, shownGroups);
nodes.get('ctSummary').hidden = true;
context.contactsProgress({ done: 1, total: 2 });
assert.equal(nodes.get('ctSummary').hidden, false);
assert.equal(nodes.get('ctStop').hidden, false);
assert.equal(nodes.get('ctProgress').textContent, 'Analizzati 1 di 2');

// Esc stops the running analysis like Interrompi on the ribbon (A20): the first Esc closes the open page,
// the next one sends the pane's cancel, which the host also applies to Contatti.
let keydown = null;
let drawer = 'contactsDrawer';
context.document.addEventListener = (type, fn) => { if (type === 'keydown') keydown = fn; };
Object.assign(context, {
  state: { busy: false, composerMode: 'chat' },
  openMenuId: () => null, closeMenus() {}, menuButtons: {}, setComposerMode() {},
  openDrawerId: () => drawer, closeDrawers() { drawer = null; },
});
const keyboard = html.indexOf('  // ---------- Keyboard');
new vm.Script(html.slice(keyboard, html.indexOf('  // ---------- Host messages', keyboard))).runInContext(context);
const before = posted.length;
keydown({ key: 'Escape' });
assert.equal(drawer, null);
assert.equal(posted.length, before);
keydown({ key: 'Escape' });
assert.deepEqual(posted.pop(), { type: 'cancel' });
context.contactsAnalyzeEnd({ done: 1, cancelled: true });
assert.equal(nodes.get('ctProgress').textContent, 'Analisi interrotta dopo 1 indirizzo.');
assert.equal(nodes.get('ctStop').hidden, true);
keydown({ key: 'Escape' });
assert.equal(posted.length, before, 'nothing left to stop');

// Proposals arrive: the group can be applied as shown, refusals go to the host.
context.contactsProposal({ address: 'mario@acme.it', fields: [{ field: 'fullName', label: 'Nome', value: 'Mario Rossi' }, { field: 'company', label: 'Azienda', value: 'Acme' }], existing: [], source: { subject: 'Offerta', date: '01/10/2026' }, problem: '' });
context.contactsProposal({ address: 'info@acme.it', fields: [{ field: 'company', label: 'Azienda', value: 'Acme' }], existing: [], source: null, problem: '' });
context.contactsProposal({ address: 'luca@beta.it', fields: [], existing: [{ entryId: 'c1', name: 'Luca', changes: [{ field: 'mobilePhone', label: 'Cellulare', old: '', new: '333 1' }] }], source: null, problem: '' });
context.contactsAnalyzeEnd({ done: 3 });
groups = nodes.get('ctResults').children;
const apply = find(groups[0].children[0], n => /^Applica gruppo/.test(n.textContent));
assert.ok(apply, 'group apply offered for analyzed items');
apply.listeners.click();
const applied = posted.pop();
assert.equal(applied.type, 'contacts_apply');
assert.deepEqual(applied.items.map(i => i.action), ['create', 'create']);
assert.deepEqual(applied.items[0].fields, { fullName: 'Mario Rossi', company: 'Acme' });

const update = find(groups[1], n => n.textContent === 'Aggiorna');
update.listeners.click();
assert.deepEqual(posted.pop().items[0], { address: 'luca@beta.it', action: 'update', entryId: 'c1', fields: { mobilePhone: '333 1' } });

const refuse = find(groups[0].children[0], n => n.textContent === 'Rifiuta mancanti');
refuse.listeners.click();
assert.deepEqual(posted.pop(), { type: 'contacts_reject', addresses: ['mario@acme.it', 'info@acme.it'] });

// Written contacts and refused addresses leave the list.
context.contactsApplied({ results: [{ address: 'luca@beta.it', ok: true, message: 'Contatto aggiornato.' }] });
context.contactsRemoved({ addresses: ['mario@acme.it', 'info@acme.it'] });
assert.equal(text(nodes.get('ctResults')), 'Nessun contatto da proporre o aggiornare.');
assert.match(text(nodes.get('ctCounts')), /^0 mancanti · 0 da verificare · 0 in conflitto · 2 aggiornati · 3 esclusi/);

// Rejected list of the chosen address book, with «Riproponi».
context.renderContactsRejected({ storeId: 's1', items: [{ address: 'x@y.it', date: '01/10/2026' }] });
assert.match(text(nodes.get('ctRejectedTitle')), /\(1\)/);
find(nodes.get('ctRejected'), n => n.textContent === 'Riproponi').listeners.click();
assert.deepEqual(posted.pop(), { type: 'contacts_unreject', storeId: 's1', address: 'x@y.it' });

console.log('ContactsPane tests passed');
