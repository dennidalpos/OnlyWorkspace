const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const html = fs.readFileSync(require('node:path').join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
function el(tag, cls, text) {
  return { tag, textContent: text || '', children: [], appendChild(child) { this.children.push(child); return child; }, setAttribute() {} };
}
const ctx = vm.createContext({ el, document: { createElement: tag => el(tag) } });
vm.runInContext(html.slice(html.indexOf('  function addDocumentReport('), html.indexOf('  function addSources(')), ctx);
const root = el('div');
const files = Array.from({ length: 15 }, (_, i) => ({ Name: 'file-' + i + '.pdf', WasRead: true, IncludedInRequest: i < 5, EmailSubject: 'Origine' }));
files.push({ Name: '<script>blocked.pdf', WasRead: false, Reason: 'protetto da password' });
ctx.addDocumentReport(root, files);
const flat = [];
function walk(node) { flat.push(node); node.children.forEach(walk); }
walk(root);
assert.equal(flat.filter(n => n.tag === 'li').length, 16, 'no document disappears in a long list');
assert.equal(flat.filter(n => n.tag === 'details').length, 1, 'long lists are expandable');
assert.ok(flat.some(n => n.textContent.includes('non incluso nella richiesta')));
assert.ok(flat.some(n => n.textContent.includes('protetto da password')));
assert.ok(flat.some(n => n.textContent.includes('<script>blocked.pdf')), 'filenames remain text, never markup');
assert.ok(html.includes('documents: data.documents') && html.includes('addDocumentReport(a, m.documents)'), 'replay and history retain local reading evidence');
assert.ok(html.includes('modal.showModal()') && html.includes('trigger.focus()'), 'context help is modal and restores focus');
console.log('PASS document reports: complete lists, local/model distinction, exclusions, safe text, history and modal focus');
