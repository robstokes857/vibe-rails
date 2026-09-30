// Parse emitted responses only; request history must not duplicate tool calls.
const text = value => value == null ? '' : typeof value === 'string' ? value : JSON.stringify(value);
export function parseResponseTools(body = '') {
    const calls = new Map(), blocks = new Map();
    let malformed = false, recognized = false;
    const add = item => {
        if (!['function_call','custom_tool_call','tool_use','server_tool_use','function'].includes(item?.type)) return;
        const fn = item.function || item, name = text(fn.name);
        if (!name) return;
        const args = text(fn.arguments ?? fn.input), id = text(item.call_id || item.id) || name+':'+args;
        calls.set(id,{id,name,arguments:args});
    };
    const read = (root, depth = 0) => {
        if (!root || typeof root !== 'object' || depth > 16) return;
        if (root.item) add(root.item);
        for (const list of [root.output, root.content]) if (Array.isArray(list)) list.forEach(add);
        if (root.response) read(root.response, depth+1);
        if (root.type === 'content_block_start' && ['tool_use','server_tool_use'].includes(root.content_block?.type)) {
            const block = root.content_block, input = text(block.input);
            blocks.set(root.index,{id:text(block.id),name:text(block.name),arguments:input==='{}'?'':input});
        }
        if (root.type === 'content_block_delta' && blocks.has(root.index))
            blocks.get(root.index).arguments += text(root.delta?.partial_json);
        for (const choice of Array.isArray(root.choices) ? root.choices : []) {
            if (Array.isArray(choice.message?.tool_calls)) choice.message.tool_calls.forEach(add);
            for (const fragment of Array.isArray(choice.delta?.tool_calls) ? choice.delta.tool_calls : []) {
                const key=fragment.index ?? 0, part=blocks.get(key) || {id:'',name:'',arguments:''};
                if (fragment.id) part.id=text(fragment.id);
                part.name+=text(fragment.function?.name); part.arguments+=text(fragment.function?.arguments);
                blocks.set(key,part);
            }
        }
    };
    const parse = value => {
        try { const root=JSON.parse(value); recognized=true; read(root); }
        catch { malformed=true; }
    };
    if (body.trimStart().startsWith('{')) parse(body);
    else {
        let data=[];
        const flush=()=>{const value=data.join('\n').trim();data=[];if(value && value!=='[DONE]') parse(value);};
        for (const line of body.split(/\r?\n/)) {
            if (!line) flush();
            else if (line.startsWith('data:')) data.push(line.slice(5).trimStart());
        }
        flush();
    }
    for (const [index,part] of blocks) if (part.name) {
        const id=part.id || 'block-'+index;
        calls.set(id,{...part,id,arguments:part.arguments || '{}'});
    }
    return {tools:[...calls.values()],note:malformed?'Some response data is incomplete or invalid JSON.':!recognized && body?'Unrecognized response format; inspect the raw exchange.':null};
}
