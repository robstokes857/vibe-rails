import test from 'node:test';
import assert from 'node:assert/strict';
import {createEnvelopeSource} from '../../../VibeRails/wwwroot/session-replay/envelope.mjs';
import {prepareFrames} from '../../../VibeRails/wwwroot/session-replay/timeline.mjs';
import {parseResponseTools} from '../../../VibeRails/wwwroot/session-replay/tools.mjs';
const stamp = '2026-09-30T00:00:00.1234567';
const b64 = value => Buffer.from(value).toString('base64');
const base = () => ({session:{id:'one',startedUtc:stamp},userInputs:[],sessionLogs:[],terminalSessionLogs:[]});
test('byte geometry splits raw frames; final zero-byte resize and fallback preserve output once',async()=>{
    const envelope=base();
    envelope.sessionLogs=[{id:1,rawBytes:b64('abcdef'),timestampUtc:stamp}];
    envelope.terminalSessionLogs=[{id:1,sequence:0,cols:80,rows:24,rawBytes:b64('abc'),timestampUtc:stamp},{id:2,sequence:1,cols:100,rows:30,rawBytes:b64('def'),timestampUtc:stamp},{id:3,sequence:2,cols:90,rows:20,rawBytes:'',timestampUtc:stamp}];
    const source=createEnvelopeSource(envelope), manifest=await source('/api/sessions/one');
    const raw=await source('/api/sessions/one/frames?after=0');
    const frames=prepareFrames(raw.items,manifest.geometry,manifest.frameSource);
    assert.equal(Buffer.concat(frames.map(f=>Buffer.from(f.data))).toString(),'abcdef');
    assert.deepEqual(frames.map(f=>f.cols),[80,100,90]);
    assert.equal(manifest.session.started,Date.parse('2026-09-30T00:00:00.123Z'));
    envelope.sessionLogs=[];
    const fallback=createEnvelopeSource(envelope);
    assert.equal((await fallback('/api/sessions/one')).frameSource,'enriched');
    assert.equal((await fallback('/api/sessions/one/frames')).items.length,3);
});
test('prompt-window patches, absent capture data, exact identity and cancellation',async()=>{
    const envelope=base();
    envelope.userInputs=[{id:1,sequence:1,timestampUtc:stamp,inputText:'first',fileChanges:[{filePath:'a.js',diffContent:'+a'}]},
        {id:2,sequence:2,timestampUtc:'2026-09-30T00:01:00Z',fileChanges:[{filePath:'b.js',previousInputId:1,diffContent:null}]}];
    const source=createEnvelopeSource(envelope), manifest=await source('/api/sessions/one');
    assert.equal(manifest.changes[0].at,Date.parse('2026-09-30T00:01:00Z'));
    assert.equal(manifest.changes[1].hasDiff,false);
    assert.equal((await source('/api/sessions/one/changes/1')).diff,'+a');
    assert.equal(manifest.proxyMaxId,0);
    assert.deepEqual(manifest.cards,[]);
    await assert.rejects(source('/api/sessions/two/changes/1'),/Session not found/);
    const controller=new AbortController();controller.abort();
    await assert.rejects(source('/api/sessions/one',{signal:controller.signal}),{name:'AbortError'});
});
test('malformed frames fail and invalid grids stay bounded',async()=>{
    const envelope=base();
    envelope.terminalSessionLogs=[{cols:99999999,rows:-5,rawBytes:b64('safe'),timestampUtc:stamp}];
    const source=createEnvelopeSource(envelope);
    const frame=(await source('/api/sessions/one/frames')).items[0];
    assert.equal(frame.cols,120);assert.equal(frame.rows,30);
    envelope.terminalSessionLogs[0].rawBytes='!invalid!';
    assert.throws(()=>createEnvelopeSource(envelope));
});
test('optional proxy captures retain exact identity, model, tools, bodies and display limits',async()=>{
    const envelope=base();
    const call={type:'function_call',call_id:'call-1',name:'read_file',arguments:'{"path":"a.js"}'};
    const row={id:'exchange',sessionId:'one',createdUtc:stamp,provider:'openai',method:'POST',path:'/responses',statusCode:200,
        requestBefore:'{"model":"fixture-model","reasoning":{"effort":"high"}}',requestAfter:'after',
        responseBody:JSON.stringify({output:[call,call]}),responseTruncated:true};
    envelope.proxyExchanges=[{...row,id:'foreign',sessionId:'two'},row];
    const source=createEnvelopeSource(envelope);
    assert.equal((await source('/api/sessions/one')).proxyMaxId,1);
    const exchange=(await source('/api/sessions/one/exchanges?after=0')).items[0];
    assert.equal(exchange.model,'fixture-model');assert.equal(exchange.effort,'high');
    assert.equal(exchange.tools.length,1);assert.equal(exchange.tools[0].name,'read_file');
    assert.equal((await source('/api/sessions/one/exchanges/exchange')).after,'after');
    await assert.rejects(source('/api/sessions/one/exchanges/foreign'),/Capture not found/);
    row.responseBody='x'.repeat(2000001);
    const detail=await source('/api/sessions/one/exchanges/exchange');
    assert.equal(detail.response.length,2000000);assert.equal(detail.displayTruncated,true);assert.equal(detail.captureTruncated,true);
});
test('streamed Anthropic and Chat Completions tools assemble argument fragments',()=>{
    const sse=events=>events.map(item=>'data: '+JSON.stringify(item)+'\n\n').join('')+'data: [DONE]\n\n';
    const anthropic=parseResponseTools(sse([
        {type:'content_block_start',index:0,content_block:{type:'tool_use',id:'a',name:'edit',input:{}}},
        {type:'content_block_delta',index:0,delta:{partial_json:'{"ok":'}},
        {type:'content_block_delta',index:0,delta:{partial_json:'true}'}}
    ]));
    assert.deepEqual(anthropic.tools,[{id:'a',name:'edit',arguments:'{"ok":true}'}]);
    const chat=parseResponseTools(sse([{choices:[{delta:{tool_calls:[{index:0,id:'b',function:{name:'read',arguments:'{"path":'}}]}}]},
        {choices:[{delta:{tool_calls:[{index:0,function:{arguments:'"a"}'}}]}}]}]));
    assert.deepEqual(chat.tools,[{id:'b',name:'read',arguments:'{"path":"a"}'}]);
    assert.match(parseResponseTools('data: {bad}\n\n').note,/incomplete/);
});
