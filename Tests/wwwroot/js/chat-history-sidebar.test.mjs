import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/chat-history-sidebar.js');
const { ChatHistorySidebar } = await import(pathToFileURL(modulePath).href);

test('ChatHistorySidebar removes document listeners when destroyed', (t) => {
    const originalDocument = globalThis.document;
    const originalLocalStorage = globalThis.localStorage;
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.localStorage = originalLocalStorage;
    });

    const added = [];
    const removed = [];
    globalThis.document = {
        activeElement: null,
        addEventListener(type, handler) { added.push({ type, handler }); },
        removeEventListener(type, handler) { removed.push({ type, handler }); }
    };
    globalThis.localStorage = {
        getItem() { return '1'; },
        setItem() {}
    };

    const sidebarElement = {
        classList: { contains: () => false },
        querySelector: () => null,
        addEventListener() {}
    };
    const root = {
        querySelector(selector) {
            return selector === '#ch-sidebar' ? sidebarElement : null;
        }
    };
    const sidebar = new ChatHistorySidebar({});

    sidebar.mount(root);
    sidebar.destroy();

    assert.deepEqual(added.map(entry => entry.type), ['click', 'keydown']);
    assert.equal(removed.length, 2);
    for (const addedEntry of added) {
        const removedEntry = removed.find(entry => entry.type === addedEntry.type);
        assert.equal(removedEntry?.handler, addedEntry.handler, `${addedEntry.type} removes the exact mounted handler`);
    }
});

test('Terminal manager destruction tears down its history sidebar', () => {
    const terminalSource = readFileSync(
        path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js'),
        'utf8');
    assert.match(terminalSource, /this\.historySidebar\?\.destroy\(\)/);
    assert.match(terminalSource, /this\.manager\.historySidebar = historySidebar/);
});

test('ChatHistorySidebar only emits allowlisted inline logo filters', () => {
    const sidebar = new ChatHistorySidebar({});
    const valid = sidebar._renderBrandLogo({
        logo: '/logo.svg',
        label: 'Safe',
        logoFilter: 'brightness(0) invert(1)'
    }, 'logo');
    const invalid = sidebar._renderBrandLogo({
        logo: '/logo.svg',
        label: 'Unsafe',
        logoFilter: 'none; background-image: url(javascript:alert(1))'
    }, 'logo');

    assert.match(valid, /style="filter: brightness\(0\) invert\(1\)"/);
    assert.doesNotMatch(invalid, /style=/);
    assert.doesNotMatch(invalid, /javascript:/);
});

test('Resume modal passes its title to the shared modal escaper exactly once', () => {
    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /showModal\(`Sending to \$\{llmDisplayLabel\}`/);
    assert.doesNotMatch(source, /showModal\(`Sending to \$\{escapeHtml\(llmDisplayLabel\)\}`/);
});
