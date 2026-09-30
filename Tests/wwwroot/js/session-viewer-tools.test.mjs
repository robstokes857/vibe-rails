import test from 'node:test';
import assert from 'node:assert/strict';
import {parseResponseTools} from '../../../VibeRails/wwwroot/session-replay/tools.mjs';
import {createEnvelopeSource} from '../../../VibeRails/wwwroot/session-replay/envelope.mjs';

const call = (id, argumentsText = 'x'.repeat(200)) => ({type:'function_call', call_id:id, name:'read', arguments:argumentsText});
const sse = events => events.map(event => 'data: ' + JSON.stringify(event) + '\n\n').join('');
function bounded(parsed) {
    assert.ok(parsed.tools.length <= 64);
    assert.ok(parsed.tools.every(tool => tool.id.length <= 256 && tool.name.length <= 256 && tool.arguments.length <= 8192));
    assert.ok(parsed.tools.reduce((sum, tool) => sum + tool.arguments.length, 0) <= 65536);
    assert.match(parsed.note, /truncated/);
}

test('5000 calls retain only 64 summaries; missing IDs never duplicate argument payloads', () => {
    const parsed = parseResponseTools(JSON.stringify({output:Array.from({length:5000}, (_, i) => call('call-' + i))}));
    bounded(parsed);
    assert.equal(parsed.tools.length, 64);
    assert.equal(parsed.tools[63].id, 'call-63');
    const anonymous = parseResponseTools(JSON.stringify({output:Array.from({length:80}, () => call('', 'x'.repeat(10000)))}));
    bounded(anonymous);
    assert.equal(new Set(anonymous.tools.map(tool => tool.id)).size, 64);
    assert.equal(anonymous.tools[0].id, 'replay-call-0');
    assert.equal(anonymous.tools[0].arguments.length, 8192);
});

test('JSON labels, per-call arguments and total arguments are bounded without splitting emoji', () => {
    const parsed = parseResponseTools(JSON.stringify({output:Array.from({length:12}, (_, i) => ({
        ...call(i + 'x'.repeat(253) + '😀tail', '😀'.repeat(9000)), name:'n'.repeat(255) + '😀tail'
    }))}));
    bounded(parsed);
    assert.equal(parsed.tools[0].name, 'n'.repeat(255));
    assert.equal(parsed.tools.reduce((sum, tool) => sum + tool.arguments.length, 0), 65536);
    assert.ok(parsed.tools.every(tool => !/[\uD800-\uDBFF]$/.test(tool.arguments + tool.name + tool.id)));
});

test('Chat and Anthropic SSE fragments share bounds with completed calls', () => {
    for (const provider of ['chat', 'anthropic']) {
        const events = [{output:[call('completed')]}];
        for (let index = 0; index < 90; index++) {
            if (provider === 'anthropic') events.push({type:'content_block_start', index,
                content_block:{type:'tool_use', id:'tool-' + index, name:'read', input:{}}});
            for (let part = 0; part < 4; part++) events.push(provider === 'chat'
                ? {choices:[{delta:{tool_calls:[{index, id:'tool-' + index,
                    function:{name:'read'.repeat(20), arguments:'x'.repeat(2500)}}]}}]}
                : {type:'content_block_delta', index, delta:{partial_json:'x'.repeat(2500)}});
        }
        const parsed = parseResponseTools(sse(events));
        bounded(parsed);
        assert.equal(parsed.tools.length, 64);
        assert.equal(parsed.tools[1].arguments.length, 8192);
    }
});

test('invalid structures and indexes do not discard later valid SSE events', () => {
    const parsed = parseResponseTools(sse([
        {output:[null, [], {type:'function', function:[]}]},
        {choices:[{delta:{tool_calls:[{index:-1}, {index:'0'}, {index:2**32}, {index:0, function:[]} ]}}]},
        {type:'content_block_start', index:[], content_block:{type:'tool_use'}},
        {type:'content_block_delta', index:0, delta:null},
        {output:[call('valid', '{"ok":true}')]}
    ]));
    assert.deepEqual(parsed.tools, [{id:'valid',name:'read',arguments:'{"ok":true}'}]);
    assert.match(parsed.note, /invalid structure/);
    assert.match(parseResponseTools('[]').note, /invalid structure/);
});

test('response prefixes report truncation and malformed data together', () => {
    const parsed = parseResponseTools(sse([{output:[call('first')]}]) + 'data: {"padding":"' + 'x'.repeat(2_000_000));
    bounded(parsed);
    assert.equal(parsed.tools[0].id, 'first');
    assert.match(parsed.note, /incomplete JSON/);
});

test('hostile uploaded envelopes page bounded summaries and still load subsequent exchanges and raw details', async () => {
    const body = JSON.stringify({output:Array.from({length:5000}, (_, i) => call('call-' + i))});
    const source = createEnvelopeSource({session:{id:'fixture',startedUtc:'2026-09-30T00:00:00Z'},
        proxyExchanges:[...Array.from({length:31}, (_, i) => ({id:'large-' + i,sessionId:'fixture',responseBody:body})),
            {id:'healthy',sessionId:'fixture',responseBody:JSON.stringify({output:[call('last','ok')]})}]});
    const first = await source('/api/sessions/fixture/exchanges');
    assert.equal(first.items.length, 30);
    for (const item of first.items) bounded({tools:item.tools,note:item.parseNote});
    const second = await source('/api/sessions/fixture/exchanges?after=' + first.next);
    assert.equal(second.done, true);
    assert.equal(second.items[1].tools[0].id, 'last');
    assert.equal(second.items[1].parseNote, null);
    const detail = await source('/api/sessions/fixture/exchanges/large-0');
    assert.equal(detail.response, body);
    assert.equal(detail.displayTruncated, false);
});
