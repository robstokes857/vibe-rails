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
