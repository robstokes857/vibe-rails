// Explicit cross-checkout sync; production builds need no sibling repository.
import { cp, mkdir, readFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, join } from 'node:path';
const root = fileURLToPath(new URL('../', import.meta.url));
const source = join(root, 'VibeRails/wwwroot/session-replay');
const args = process.argv.slice(2), check = args.includes('--check');
const targets = args.filter(arg => arg !== '--check');
if (!targets.length) throw new Error('Pass destination wwwroot directories (optional --check).');
for (const target of targets) {
    const destination = resolve(target);
    for (const name of await readdir(source)) {
        if (name === 'README.md') continue;
        if (check) {
            if (!(await readFile(join(source,name))).equals(await readFile(join(destination,name))))
                throw new Error(`Viewer copy differs: ${join(destination,name)}`);
        } else {
            await mkdir(destination,{recursive:true});
            await cp(join(source,name),join(destination,name));
        }
    }
    console.log(`${check ? 'Verified' : 'Synced'} ${destination}`);
}
