// PreToolUse: refuse writes to secrets, signing material, backups and build output of this checkout. Exit 2 reports to Claude.
'use strict';
const path = require('path');

const FOLDERS = ['.local-secrets', 'dist', 'publish'];
const EXTENSIONS = ['.pfx', '.owbak'];

let input = '';
process.stdin.on('data', chunk => { input += chunk; });
process.stdin.on('end', () => {
    const payload = JSON.parse(input || '{}');
    const file = (payload.tool_input && payload.tool_input.file_path) || '';
    if (!file) return;
    const root = path.resolve(process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd());
    const target = path.resolve(root, file);
    const relative = path.relative(root, target).split(path.sep).map(part => part.toLowerCase());
    const extension = path.extname(target).toLowerCase();
    // Outside the checkout, or the checkout itself: not covered by this hook.
    if (relative[0] === '..' || path.isAbsolute(path.relative(root, target))) return;
    const folder = relative.slice(0, -1).find(part => FOLDERS.includes(part));
    if (folder) block(`${file}: writes under ${folder}/ are blocked (secrets or build output).`);
    if (EXTENSIONS.includes(extension)) block(`${file}: ${extension} files are blocked (signing material or backup).`);
});

function block(message) {
    process.stderr.write(message + ' Ask the user to change it by hand if it is really needed.\n');
    process.exit(2);
}
