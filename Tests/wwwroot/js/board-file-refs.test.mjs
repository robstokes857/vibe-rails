import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// The pure helpers behind the composer's `@` typeahead (VB-35). They decide what the popup
// inserts and when it is open, so they must agree with the renderer in board-text.js and the
// extractor in BoardPromptComposer/BoardFileReferences.cs: whatever formatFileReference emits
// has to render as a reference and reach the launch prompt.
const moduleUrl = pathToFileURL(path.resolve('VibeRails/wwwroot/js/modules/board-file-refs.js')).href;
const { formatFileReference, findFileReferenceToken, toProjectRelativePath, insertFileReference, MAX_QUERY_LENGTH } = await import(moduleUrl);
const { renderCommentHtml } = await import(pathToFileURL(path.resolve('VibeRails/wwwroot/js/modules/board-text.js')).href);

test('formatFileReference emits the bare form when the renderer would recognise it, else quotes', () => {
    assert.equal(formatFileReference('VibeRails/Services/Board/BoardService.cs'), '@VibeRails/Services/Board/BoardService.cs');
    assert.equal(formatFileReference('AGENTS.md'), '@AGENTS.md');
    assert.equal(formatFileReference('docs/with space.md'), '@"docs/with space.md"');
    // No slash or dot: bare would read as prose, so it is quoted.
    assert.equal(formatFileReference('LICENSE'), '@"LICENSE"');
    // Characters the bare rule excludes, or a trailing sentence mark, force the quoted form too.
    assert.equal(formatFileReference('a&b.txt'), '@"a&b.txt"');
    assert.equal(formatFileReference('notes/todo.'), '@"notes/todo."');
    // Windows separators are normalised; a double quote cannot survive either form.
    assert.equal(formatFileReference('src\\Board\\x.cs'), '@src/Board/x.cs');
    assert.equal(formatFileReference('odd "name".md'), '@"odd name.md"');
    assert.equal(formatFileReference(''), '');
});

test('every inserted reference renders as exactly one file-reference chip', () => {
    for (const file of ['a/b.cs', 'docs/with space.md', 'LICENSE', 'a&b.txt', 'src\\x.cs', 'deep/dir/']) {
        const html = renderCommentHtml(`see ${formatFileReference(file)} now`);
        assert.equal((html.match(/board-file-ref/g) || []).length, 1, file);
        assert.match(html, /^see <code class="board-file-ref">@[^<]*<\/code> now$/, file);
    }
});

test('findFileReferenceToken tracks the token under the caret and nothing else', () => {
    assert.deepEqual(findFileReferenceToken('see @Boa', 8), { start: 4, query: 'Boa', quoted: false });
    assert.deepEqual(findFileReferenceToken('@', 1), { start: 0, query: '', quoted: false });
    assert.deepEqual(findFileReferenceToken('x\n@src/a', 8), { start: 2, query: 'src/a', quoted: false });
    assert.deepEqual(findFileReferenceToken('see @"docs/with sp', 18), { start: 4, query: 'docs/with sp', quoted: true });
    // Finished references, emails and a caret past a space are not tokens.
    assert.equal(findFileReferenceToken('see @a/b.cs now', 15), null);
    assert.equal(findFileReferenceToken('see @"a b.md" ', 14), null);
    assert.equal(findFileReferenceToken('rob@example.com', 15), null);
    assert.equal(findFileReferenceToken('', 0), null);
    assert.equal(findFileReferenceToken('no at here', 5), null);
    // The caret in the middle of a token only sees what precedes it.
    assert.deepEqual(findFileReferenceToken('see @Board now', 9), { start: 4, query: 'Boar', quoted: false });
    assert.equal(findFileReferenceToken(`@${'x'.repeat(MAX_QUERY_LENGTH + 1)}`, MAX_QUERY_LENGTH + 2), null);
});

test('toProjectRelativePath strips the project root case-insensitively on Windows and keeps outside picks absolute', () => {
    assert.equal(toProjectRelativePath('C:\\board-fixture\\docs\\x.md', 'C:/board-fixture'), 'docs/x.md');
    assert.equal(toProjectRelativePath('c:/BOARD-fixture/src/a.cs', 'C:/board-fixture/'), 'src/a.cs');
    assert.equal(toProjectRelativePath('C:/elsewhere/a.cs', 'C:/board-fixture'), 'C:/elsewhere/a.cs');
    assert.equal(toProjectRelativePath('/home/rob/proj/a.cs', '/home/rob/proj'), 'a.cs');
    // POSIX roots compare case-sensitively.
    assert.equal(toProjectRelativePath('/home/rob/Proj/a.cs', '/home/rob/proj'), '/home/rob/Proj/a.cs');
    assert.equal(toProjectRelativePath('/home/rob/proj/a.cs', ''), '/home/rob/proj/a.cs');
});

test('insertFileReference replaces the token, adds one separating space and parks the caret after it', () => {
    assert.deepEqual(insertFileReference('see @Boa', 4, 8, '@a/b.cs'), { value: 'see @a/b.cs ', caret: 12 });
    assert.deepEqual(insertFileReference('see @Boa then', 4, 8, '@a/b.cs'), { value: 'see @a/b.cs then', caret: 11 });
    assert.deepEqual(insertFileReference('@x\nmore', 0, 2, '@a.md'), { value: '@a.md\nmore', caret: 5 });
});

test('the controller binds the popup to every composer and disposes it only when the editor closes', () => {
    const source = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/board-controller.js'), 'utf8');
    assert.match(source, /import \{ bindFileReferencePopup \} from '\.\/board-file-refs\.js'/);
    const bind = source.slice(source.indexOf('bindComposer(composer,'), source.indexOf('async attachImages('));
    assert.match(bind, /this\.composerDisposers\.push\(bindFileReferencePopup\(input, \{ app: this\.app, host: composer \}\)\)/);
    // bindCardEditor re-runs disposeCardPickers() AFTER the composers are wired (to reset the
    // assignee/chat pickers). If the composer disposers lived in that bucket the popup would be
    // torn down before it ever opened, which is exactly how the first UI run failed.
    const pickers = source.slice(source.indexOf('disposeCardPickers() {'), source.indexOf('disposeComposers() {'));
    assert.doesNotMatch(pickers, /composerDisposers/);
    const composers = source.slice(source.indexOf('disposeComposers() {'), source.indexOf('setBusy(busy)'));
    assert.match(composers, /this\.composerDisposers\.splice\(0\)/);
    const onClose = source.slice(source.indexOf('{ onClose: () => {'), source.indexOf('} });', source.indexOf('{ onClose: () => {')));
    assert.match(onClose, /this\.disposeComposers\(\)/);
    const destroyStart = source.indexOf('this.closeSessionModal();');
    const destroy = source.slice(destroyStart, source.indexOf('this.root = null;', destroyStart));
    assert.match(destroy, /this\.disposeComposers\(\)/);
});

test('the popup anchors inside a relatively positioned composer', () => {
    const html = readFileSync(path.resolve('VibeRails/wwwroot/index.html'), 'utf8');
    const css = html.slice(html.indexOf('id="board-template"'), html.indexOf('</template>', html.indexOf('id="board-template"')));
    assert.match(css, /\.board-composer \{\s*position: relative;/);
    assert.match(css, /\.board-file-popup \{\s*position: absolute;/);
    assert.match(css, /code\.board-file-ref \{/);
});
