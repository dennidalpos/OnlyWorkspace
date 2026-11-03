// PostToolUse: syntax-check TaskPane.html's inline script and edited JavaScript, like scripts/check-code.ps1.
// A broken string in TaskPane.html disables the whole pane without any build error. Exit 2 reports to Claude.
'use strict';
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

let input = '';
process.stdin.on('data', chunk => { input += chunk; });
process.stdin.on('end', () => {
    const payload = JSON.parse(input || '{}');
    const file = (payload.tool_input && payload.tool_input.file_path)
        || (payload.tool_response && payload.tool_response.filePath) || '';
    const name = path.basename(file);
    let result;
    if (name === 'TaskPane.html') {
        const html = fs.readFileSync(file, 'utf8');
        const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)];
        if (scripts.length !== 1 || scripts[0][1].trim()) {
            fail(`${file}: expected one inline classic <script>; update this hook and check-code.ps1 for the new layout.`);
        }
        result = spawnSync(process.execPath, ['--check', '--input-type=commonjs'], { input: scripts[0][2], encoding: 'utf8' });
    } else if (/\.(c|m)?js$/i.test(name)) {
        result = spawnSync(process.execPath, ['--check', file], { encoding: 'utf8' });
    } else {
        return;
    }
    if (result.status !== 0) fail(`JavaScript syntax error in ${file}:\n${result.stderr}`);
});

function fail(message) {
    process.stderr.write(message + '\n');
    process.exit(2);
}
