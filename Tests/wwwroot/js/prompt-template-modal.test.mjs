import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const sourceModule = path.resolve('VibeRails/wwwroot/js/modules/prompt-template-modal.js');
const {
    extractPromptTemplateVariables,
    hasPromptTemplateVariables,
    renderPromptTemplate,
    labelizeVariableName,
    findEnvironmentForPrompt
} = await import(pathToFileURL(sourceModule).href);

test('extractPromptTemplateVariables finds names and defaults', () => {
    const variables = extractPromptTemplateVariables(
        'Create {{branch_name}} and read {{path default="docs/runbook.md"}}.'
    );

    assert.deepEqual(variables.map(v => ({
        name: v.name,
        label: v.label,
        defaultValue: v.defaultValue,
        hasDefault: v.hasDefault
    })), [
        {
            name: 'branch_name',
            label: 'Branch Name',
            defaultValue: '',
            hasDefault: false
        },
        {
            name: 'path',
            label: 'Path',
            defaultValue: 'docs/runbook.md',
            hasDefault: true
        }
    ]);
});

test('extractPromptTemplateVariables deduplicates repeated variables', () => {
    const variables = extractPromptTemplateVariables(
        '{{ticket}} then {{ticket default="TICKET-123"}} then {{ticket}}'
    );

    assert.equal(variables.length, 1);
    assert.equal(variables[0].name, 'ticket');
    assert.equal(variables[0].defaultValue, 'TICKET-123');
    assert.equal(variables[0].count, 3);
});

test('renderPromptTemplate replaces all occurrences', () => {
    const rendered = renderPromptTemplate(
        'Create {{branch_name}} and read {{path default="docs/runbook.md"}} for {{branch_name}}.',
        {
            branch_name: 'TICKET-123',
            path: 'runbooks/new-feature.md'
        }
    );

    assert.equal(
        rendered,
        'Create TICKET-123 and read runbooks/new-feature.md for TICKET-123.'
    );
});

test('helpers handle non-template prompts and environment lookup', () => {
    assert.equal(hasPromptTemplateVariables('No placeholders here.'), false);
    assert.equal(labelizeVariableName('runbook.path'), 'Runbook Path');

    const env = findEnvironmentForPrompt([
        { name: 'Feature', cli: 'Codex', customPrompt: 'x' }
    ], 'codex', 'feature');

    assert.equal(env.customPrompt, 'x');
});

// ---------------------------------------------------------------------------------------
//  Auto-filled (reserved) tokens
// ---------------------------------------------------------------------------------------

test('built-in and step tokens never become fill-in variables', () => {
    const variables = extractPromptTemplateVariables(
        'On {{datetime}} ({{date}} {{time}}) in {{env_name}} on {{git_branch}}: '
        + '{{step:b7e3f2a1-0000-4000-8000-000000000000}} and {{DateTime}} but ask for {{ticket}}.'
    );

    assert.deepEqual(variables.map(v => v.name), ['ticket']);
    assert.equal(
        hasPromptTemplateVariables('Only {{datetime}} and {{step:b7e3f2a1-0000-4000-8000-000000000000}}'),
        false);
});

test('renderPromptTemplate resolves the client-side built-ins', () => {
    const rendered = renderPromptTemplate('{{datetime}} | {{date}} | {{time}} | {{env_name}}', {}, {
        environmentName: 'Nightly'
    });

    // The clock is live, so assert shape rather than value.
    const [datetime, date, time, envName] = rendered.split(' | ');
    assert.match(datetime, /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
    assert.match(date, /^\d{4}-\d{2}-\d{2}$/);
    assert.match(time, /^\d{2}:\d{2}$/);
    assert.equal(envName, 'Nightly');
});

test('renderPromptTemplate leaves server-resolved tokens literal for the launch pass', () => {
    // git_branch needs a git call and step tokens run a shell command — only the backend
    // (PromptPlaceholderService) may resolve those, exactly once per launch.
    const template = 'On {{git_branch}}: {{step:b7e3f2a1-0000-4000-8000-000000000000}} for {{ticket}}';
    const rendered = renderPromptTemplate(template, { ticket: 'T-123' });

    assert.equal(
        rendered,
        'On {{git_branch}}: {{step:b7e3f2a1-0000-4000-8000-000000000000}} for T-123');
});

test('a user variable value containing a reserved-looking token is not blanked', () => {
    // {{step:...}} parses under TOKEN_PATTERN as name "step"; before the reserved list existed
    // it would have been swallowed as an unknown variable and replaced with ''.
    const rendered = renderPromptTemplate('{{step:not-a-guid}}', {});
    assert.equal(rendered, '{{step:not-a-guid}}');
});
