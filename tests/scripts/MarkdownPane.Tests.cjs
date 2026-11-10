const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../src/OnlyWorkspace.Outlook/UI/TaskPane.html'), 'utf8');
const from = html.indexOf('  // ---------- Markdown');
const to = html.indexOf('  // ---------- Scrolling', from);
assert.ok(from >= 0 && to > from);

const ctx = vm.createContext({
  escapeHtml: s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
});
new vm.Script(html.slice(from, to)).runInContext(ctx);

// Web search text (18/10/2026) carried tables that the pane showed with their bars.
const table = ctx.renderMarkdown('Sintesi\n\n| Dominio | Legittimo? |\n|---|:---:|\n| **linkedin.com** | Sì \\| ufficiale |\n| licdn.com | Sì |\n\nFine');
assert.match(table, /<div class="data-table-wrap"><table class="data-table"><thead><tr><th>Dominio<\/th><th>Legittimo\?<\/th><\/tr><\/thead>/);
assert.match(table, /<tr><td><b>linkedin\.com<\/b><\/td><td>Sì \| ufficiale<\/td><\/tr><tr><td>licdn\.com<\/td><td>Sì<\/td><\/tr><\/tbody><\/table><\/div>/);
assert.match(table, /^<p>Sintesi<\/p>.*<p>Fine<\/p>$/);

// A bar in plain text, or a header still waiting for its separator while streaming, stays text.
assert.equal(ctx.renderMarkdown('Scelta A | scelta B'), '<p>Scelta A | scelta B</p>');
assert.equal(ctx.renderMarkdown('| Campo | Valore |'), '<p>| Campo | Valore |</p>');
// Model text cannot add markup through a cell.
assert.doesNotMatch(ctx.renderMarkdown('| a |\n|---|\n| <img src=x> |'), /<img/);

console.log('PASS Markdown tables in the pane');
