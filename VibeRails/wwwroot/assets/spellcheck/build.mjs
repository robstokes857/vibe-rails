// Rebuild only when upgrading the pinned dependencies; ordinary app builds need no npm step.
import { createRequire } from 'node:module';
import { readFile, copyFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const modules = resolve(process.argv[2] || 'node_modules');
const require = createRequire(join(modules, 'spellcheck-build.cjs'));
for (const [name, version] of Object.entries({ nspell: '2.1.5', 'dictionary-en': '4.0.0', 'is-buffer': '2.0.5', esbuild: '0.25.12' })) {
    const pkg = JSON.parse(await readFile(join(modules, name, 'package.json'), 'utf8'));
    if (pkg.version !== version) throw new Error(`Expected ${name}@${version}, found ${pkg.version}`);
}
const output = fileURLToPath(new URL('.', import.meta.url));
await require('esbuild').build({
    stdin: { contents: `import nspell from ${JSON.stringify(join(modules, 'nspell/lib/index.js'))};
        import aff from ${JSON.stringify(join(modules, 'dictionary-en/index.aff'))};
        import dic from ${JSON.stringify(join(modules, 'dictionary-en/index.dic'))};
        export function createEnglishSpeller() { return nspell(aff, dic); }`, resolveDir: modules },
    bundle: true, format: 'esm', platform: 'browser', target: 'es2022', minify: true,
    loader: { '.aff': 'text', '.dic': 'text' },
    banner: { js: '/*! nspell 2.1.5 (MIT), is-buffer 2.0.5 (MIT), dictionary-en 4.0.0. See adjacent LICENSE files. */' },
    outfile: join(output, 'english.js')
});
for (const [source, destination] of [
    ['nspell/license', 'LICENSE-nspell.txt'],
    ['is-buffer/LICENSE', 'LICENSE-is-buffer.txt'],
    ['dictionary-en/license', 'LICENSE-dictionary-en.txt']
]) await copyFile(join(modules, source), join(output, destination));
