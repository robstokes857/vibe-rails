// Run only when upgrading the vendored parser; normal builds need no npm install.
import { readFile, writeFile, copyFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const modules = resolve(process.argv[2] || 'node_modules');
const output = fileURLToPath(new URL('.', import.meta.url));
const licenses = {
    'markdown-it': ['15.0.3', 'LICENSE'],
    entities: ['8.1.0', 'LICENSE'],
    'linkify-it': ['6.1.0', 'LICENSE'],
    mdurl: ['2.1.0', 'LICENSE'],
    'punycode.js': ['2.3.1', 'LICENSE-MIT.txt'],
    'uc.micro': ['3.0.0', 'LICENSE.txt']
};
for (const [name, [version, license]] of Object.entries(licenses)) {
    const folder = join(modules, name);
    const pkg = JSON.parse(await readFile(join(folder, 'package.json'), 'utf8'));
    if (pkg.version !== version) throw new Error(`Expected ${name}@${version}, found ${pkg.version}`);
    await copyFile(join(folder, license), join(output, `LICENSE-${name}.txt`));
}
// Upstream's browser ESM is self-contained. Omit its source-map pointer because we
// do not ship the development map; preserve the remaining bytes and notices.
const source = await readFile(join(modules, 'markdown-it/dist/browser/markdown-it.esm.min.mjs'), 'utf8');
await writeFile(join(output, 'markdown-it.min.js'), source.replace(/^\/\/# sourceMappingURL=.*\r?\n?/m, ''));
