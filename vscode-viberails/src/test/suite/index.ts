import * as fs from 'fs';
import * as path from 'path';
import Mocha from 'mocha';

function findTestFiles(directory: string): string[] {
    const results: string[] = [];

    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const fullPath = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            results.push(...findTestFiles(fullPath));
            continue;
        }

        if (entry.isFile() && entry.name.endsWith('.test.js')) {
            results.push(fullPath);
        }
    }

    return results;
}

export function run(): Promise<void> {
    const mocha = new Mocha({
        ui: 'tdd',
        color: true,
        timeout: 180000
    });

    const testsRoot = __dirname;
    for (const file of findTestFiles(testsRoot)) {
        mocha.addFile(file);
    }

    return new Promise((resolve, reject) => {
        try {
            mocha.run((failures) => {
                if (failures > 0) {
                    reject(new Error(`${failures} test(s) failed.`));
                    return;
                }

                resolve();
            });
        } catch (error) {
            reject(error);
        }
    });
}
