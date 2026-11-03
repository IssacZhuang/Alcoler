import test from 'node:test';
import assert from 'node:assert/strict';
import {
  Transcript, rowElement, toolSummary, toolCallHtml, toolBodyHtml, jsonPreview,
} from '../../../src/Alcoler.Remote/wwwroot/render.js';

const tool = (overrides = {}) => ({
  $type: 'tool_call', rowId: 'tool-1', toolName: 'Read', status: 'Succeeded',
  input: { file_path: 'C:\\Projects\\Alcoler\\src\\Example.cs' },
  output: [{ $type: 'text', text: 'first output' }], duration: '00:00:01.5000000',
  ...overrides,
});
const expanded = (...all) => ({ open: new Set(['tool']), all: new Set(all) });

function withElements(run) {
  const previous = globalThis.document;
  globalThis.document = {
    createElement: () => ({ dataset: {}, innerHTML: '', querySelectorAll: () => [] }),
    createDocumentFragment: () => ({ children: [], appendChild(el) { this.children.push(el); } }),
  };
  try { return run(); }
  finally {
    if (previous === undefined) delete globalThis.document;
    else globalThis.document = previous;
  }
}

function container() {
  return {
    childNodes: [], listeners: new Map(),
    addEventListener(name, fn) { this.listeners.set(name, fn); },
    removeEventListener(name) { this.listeners.delete(name); },
    appendChild(el) { this.childNodes.push(el); },
    replaceChildren(fragment) { this.childNodes = fragment?.children || []; },
    querySelectorAll: () => [],
  };
}

test('summary follows desktop precedence, string rules, first line, and 160-character cap', () => {
  const keys = ['description', 'file_path', 'path', 'command', 'pattern', 'query', 'url'];
  for (let i = 0; i < keys.length; i++) {
    const input = Object.fromEntries(keys.slice(i).map(key => [key, key + '\r\nignored']));
    assert.equal(toolSummary(input), keys[i]);
  }
  assert.equal(toolSummary({ description: '  ', file_path: 2, path: '/src/file.cs' }), '/src/file.cs');
  assert.equal(toolSummary({ description: 'x'.repeat(200) }), 'x'.repeat(157) + '...');
  assert.equal(toolSummary({ command: 'x'.repeat(160) + '\nsecond line' }), 'x'.repeat(160));
  assert.equal(toolSummary(['ignored']), '');
  assert.equal(toolSummary(null), '');
});

test('file operations show only the filename in a compact header, including aliases', () => {
  for (const toolName of ['Read', 'Write', 'Edit', 'ReadFile', 'WriteFile', 'EditFile']) {
    const row = tool({ toolName });
    const html = toolCallHtml(row);
    assert.match(html, /class="tool-head is-compact"/);
    assert.match(html, /class="tool-summary is-path">Example\.cs<\/span>/);
    assert.doesNotMatch(html, /tool-context|C:\\Projects|"file_path"|first output|<pre/);
    assert.match(html, /<div class="tool-body"><\/div>/);
    assert.ok(toolBodyHtml(row).includes('C:\\Projects\\Alcoler\\src\\Example.cs'));
  }
});

test('headers use friendly desktop labels while preserving the actual tool name', () => {
  for (const [toolName, label] of [
    ['Bash', 'Terminal'], ['Shell', 'Terminal'], ['Grep', 'Search'], ['Glob', 'Search'],
    ['TodoRead', 'Tasks'], ['TodoWrite', 'Tasks'], ['ExitPlanMode', 'Plan'], ['Browser', 'Browser'],
  ]) {
    assert.match(toolCallHtml(tool({ toolName })), new RegExp(`title="${toolName}">${label}</span>`));
  }
  assert.match(toolCallHtml(tool({ toolName: 'toString' })), />toString<\/span>/);
});

test('Terminal shows only its title and keeps command details available when expanded', () => {
  for (const toolName of ['Bash', 'Shell', 'Terminal', 'RunCommand']) {
    const row = tool({ toolName, input: {
      description: 'Run focused tests\nnot in the header', command: 'dotnet test --filter Focused',
    } });
    const html = toolCallHtml(row);
    assert.match(html, /class="tool-head is-compact"/);
    assert.match(html, /Run focused tests/);
    assert.doesNotMatch(html, /tool-context|Command:|dotnet test|not in the header/);
    assert.match(toolBodyHtml(row), />Command<\/div>/);
    assert.match(toolBodyHtml(row), /dotnet test --filter Focused/);
  }
  assert.match(toolCallHtml(tool({ toolName: 'Bash', input: { command: 'pwd\nls' } })), />pwd<\/span>/);
});

test('Browser and generic tools keep a single compact summary with other fields in the disclosure', () => {
  const url = 'http://127.0.0.1:56387/#/session/sess_spacing_probe';
  for (const toolName of ['Browser', 'SendMessage', 'TaskOutput', 'Search', 'CustomTool']) {
    const row = tool({ toolName, input: {
      description: 'One summary line\nadditional description', path: '/workspace/src/extra.cs',
      url, action: 'navigate',
    } });
    const html = toolCallHtml(row);
    assert.match(html, /class="tool-head is-compact"/);
    assert.match(html, />One summary line<\/span>/);
    assert.doesNotMatch(html, /tool-context|additional description|navigate|extra\.cs|56387/);
    const body = toolBodyHtml(row);
    assert.match(body, /additional description/);
    assert.match(body, /extra\.cs/);
    assert.match(body, /navigate/);
    assert.match(body, /56387/);
  }
  const row = tool({ toolName: 'Browser', input: { action: 'navigate', url } });
  assert.ok(toolCallHtml(row).includes(`>${url}</span>`));
  assert.doesNotMatch(toolCallHtml(row), /tool-context|Action:|>navigate<\/span>/);
  assert.match(toolBodyHtml(row), />Action<\/div>/);
  assert.match(toolBodyHtml(row), />navigate<\/span>/);
  assert.match(toolCallHtml(tool({ toolName: 'Browser', input: { action: 'evaluate' } })), />evaluate<\/span>/);
});

test('collapsed generic headers do not inspect additional context or tool output', () => {
  const input = {
    description: 'Run the selected action',
    get file_path() { throw new Error('must not read additional header context'); },
    get action() { throw new Error('must not read additional header context'); },
    toJSON() { throw new Error('must not serialize input'); },
  };
  const row = tool({ toolName: 'Browser', input });
  Object.defineProperty(row, 'output', { get() { throw new Error('must not inspect output'); } });
  assert.match(toolCallHtml(row), />Run the selected action<\/span>/);
});

test('Search shows a single desktop summary with the full scope instead of extra context lines', () => {
  for (const toolName of ['Grep', 'Glob', 'gReP', 'GLOB']) {
    const row = tool({ toolName, input: { path: '/workspace/src', pattern: '**/*.cs' } });
    const html = toolCallHtml(row);
    assert.match(html, /class="tool-head is-compact is-search"/);
    assert.match(html, /class="tool-summary" title="\/workspace\/src">\/workspace\/src<\/span>/);
    assert.match(html, /stroke-width="1\.5"/);
    assert.doesNotMatch(html, /tool-context|is-path|Pattern:|\*\*\/\*\.cs|tool-status|Succeeded/);
    assert.match(toolBodyHtml(row), />Pattern<\/div>/);
    assert.match(toolBodyHtml(row), /\*\*\/\*\.cs/);
    assert.match(toolBodyHtml(row), /\/workspace\/src/);
  }
});

test('Search preserves Windows, relative, and root scopes and uses desktop field precedence', () => {
  for (const path of ['C:\\Projects\\Alcoler\\styles.css', 'src/styles.css', '/', 'C:\\', '/workspace/src/']) {
    const html = toolCallHtml(tool({ toolName: 'Grep', input: { path, pattern: 'hidden-pattern' } }));
    assert.ok(html.includes(`>${path}</span>`));
    assert.doesNotMatch(html, /hidden-pattern|tool-context|tool-file-parent/);
  }
  for (const [input, expected] of [
    [{ description: 'Find references\nignored line', file_path: '/src/first.cs', path: '/src', pattern: 'unused' }, 'Find references'],
    [{ file_path: '/src/first.cs', path: '/src', pattern: 'unused' }, '/src/first.cs'],
    [{ path: ' ', pattern: 'Find.*References' }, 'Find.*References'],
    [{ pattern: 42, query: 'search query' }, 'search query'],
  ]) {
    const html = toolCallHtml(tool({ toolName: 'Glob', input }));
    assert.ok(html.includes(`>${expected}</span>`));
    assert.doesNotMatch(html, /tool-context|ignored line/);
  }
  assert.doesNotMatch(toolCallHtml(tool({ toolName: 'Grep', input: { action: 'not-a-summary' } })),
    /tool-summary|not-a-summary/);
});

test('Search keeps summaries bounded and retains status, duration, and expandable details', () => {
  const path = '/workspace/' + 'long-directory/'.repeat(100) + 'styles.css';
  const row = tool({ toolName: 'Grep', input: { path, pattern: 'search pattern' } });
  const html = toolCallHtml(row);
  assert.ok(html.includes(`>${path.slice(0, 157)}...</span>`));
  assert.ok(html.length < 4000);
  assert.match(html, /Elapsed time: 1\.5s/);
  assert.match(toolBodyHtml(row, expanded('context:path', 'input')), new RegExp('long-directory/'.repeat(100)));
  assert.match(toolCallHtml({ ...row, status: 'Running', duration: null }), />Running<\/span>/);
  assert.match(toolCallHtml({ ...row, status: 'Failed' }), /class="tool-state failed">Error<\/span>/);
  assert.match(toolCallHtml({ ...row, status: 'Cancelled' }), />Cancelled<\/span>/);
  assert.match(toolCallHtml({ ...row, status: 'AwaitingPermission' }), />Needs permission<\/span>/);
  assert.doesNotMatch(toolCallHtml({ ...row, status: 'Running' }), /class="tool-status/);
});

test('literal Search and WebSearch keep their unrelated generic presentation', () => {
  for (const toolName of ['Search', 'WebSearch']) {
    const html = toolCallHtml(tool({ toolName, input: { query: 'network search query' } }));
    assert.ok(html.includes(`title="${toolName}">${toolName}</span>`));
    assert.match(html, />network search query<\/span>/);
    assert.doesNotMatch(html, /is-search|tool-agent-type/);
  }
});

test('Agent, Task, and subagent use the desktop robot, type, separator, and task title', () => {
  for (const toolName of ['Agent', 'Task', 'subagent', 'AGENT', 'tAsK', 'SUBAGENT']) {
    const row = tool({ toolName, input: {
      subagent_type: 'general-purpose', description: 'Prepare picker browser fixture',
      prompt: 'Long private task instructions must stay in the expanded input', run_in_background: true,
    } });
    const html = toolCallHtml(row);
    assert.match(html, /class="tool-head is-compact is-agent"/);
    assert.ok(html.includes(`title="${toolName}">SubAgent</span>`));
    assert.match(html, /class="tool-agent-type" title="general-purpose">general-purpose<\/span>/);
    assert.match(html, /class="tool-agent-separator" aria-hidden="true">·<\/span>/);
    assert.match(html, />Prepare picker browser fixture<\/span>/);
    assert.match(html, /stroke-width="1\.5"/);
    assert.ok(html.includes('M12 8V4H8M5 8h14'));
    assert.doesNotMatch(html, /tool-context|tool-status|tool-dur|tool-state|tool-chev|private task instructions|first output|<pre/);
    assert.match(toolBodyHtml(row), /Long private task instructions/);
    assert.match(toolBodyHtml(row), /first output/);
  }
});

test('Agent defaults match desktop without using prompt, path, or other summary fields', () => {
  for (const input of [null, [], {}, { description: ' ', subagent_type: 42 },
    { prompt: 'Never use the prompt as a title', path: '/not-a-title', subagent_type: '\t' }]) {
    const html = toolCallHtml(tool({ toolName: 'Agent', input }));
    assert.match(html, />general-purpose<\/span>/);
    assert.match(html, />Agent task<\/span>/);
    assert.doesNotMatch(html, /Never use the prompt|not-a-title/);
  }
});

test('Agent preserves longer task titles for visual ellipsis and distinguishes Explore case-insensitively', () => {
  const title = 'Inspect the implementation '.repeat(12) + 'visible title tail';
  for (const subagent_type of ['Explore', 'eXpLoRe']) {
    const html = toolCallHtml(tool({ toolName: 'Agent', input: { subagent_type, description: title } }));
    assert.match(html, /class="tool-agent-type is-explore"/);
    assert.ok(html.includes(`>${subagent_type}</span>`));
    assert.ok(html.includes(`>${title}</span>`));
  }
  assert.doesNotMatch(toolCallHtml(tool({ toolName: 'Agent', input: { subagent_type: 'custom-agent' } })), /is-explore/);
});

test('Agent only exposes failure text and marks active dispatches for the desktop label pulse', () => {
  for (const status of ['Pending', 'Running', 'pending', 'running']) {
    const html = toolCallHtml(tool({ toolName: 'Agent', status }));
    assert.match(html, /class="tool-head is-compact is-agent is-running"/);
    assert.doesNotMatch(html, />Running<|>Pending<|tool-status|tool-dur|tool-chev/);
  }
  for (const row of [tool({ toolName: 'Agent', status: 'Failed' }),
    tool({ toolName: 'Task', status: 'Succeeded', isError: true })]) {
    const html = toolCallHtml(row);
    assert.match(html, /class="tool is-error"/);
    assert.match(html, /class="tool-state failed">Failed<\/span>/);
    assert.doesNotMatch(html, /is-running|tool-status|tool-dur|tool-chev/);
  }
  for (const status of ['Succeeded', 'Cancelled', 'AwaitingPermission']) {
    assert.doesNotMatch(toolCallHtml(tool({ toolName: 'Agent', status })), /is-running|tool-state|tool-status|tool-dur|tool-chev/);
  }
});

test('Search and Agent headers escape metadata without serializing input or inspecting outputs', () => {
  const attack = '<img src=x onerror="alert(1)">&\'';
  for (const toolName of ['Grep', 'Glob', 'Agent', 'Task', 'subagent']) {
    const input = { description: attack, path: attack, subagent_type: attack,
      get prompt() { throw new Error('must not read prompt while collapsed'); },
      toJSON() { throw new Error('must not serialize input while collapsed'); } };
    const row = tool({ toolName, input });
    Object.defineProperty(row, 'output', { get() { throw new Error('must not read output while collapsed'); } });
    const html = toolCallHtml(row);
    assert.doesNotMatch(html, /<img src=x|onerror="alert/);
    assert.match(html, /&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt;&amp;&#39;/);
  }
  assert.ok(toolCallHtml(tool({ toolName: 'Agent', input: {
    description: 'a'.repeat(100_000), subagent_type: 'b'.repeat(100_000),
  } })).length < 12_000);
});

test('long absolute paths show only the basename and retain full expanded detail', () => {
  const path = 'C:\\' + 'long-directory\\'.repeat(100) + 'ImportantFile.cs';
  const row = tool({ input: { file_path: path } });
  const header = toolCallHtml(row);
  assert.match(header, /class="tool-summary is-path">ImportantFile\.cs<\/span>/);
  assert.doesNotMatch(header, /tool-context|tool-path-parent|long-directory/);
  assert.doesNotMatch(header, /C:\\long-directory/);
  assert.ok(header.length < 4000);
  const preview = toolBodyHtml(row);
  assert.match(preview, />File path<\/div>/);
  assert.match(preview, /data-section="context:file_path"/);
  assert.match(preview, /Show all file path/);
  assert.ok(toolBodyHtml(row, expanded('context:file_path')).includes(path));
});

test('relative file paths prefer the filename over descriptions without a second line', () => {
  for (const input of [
    { file_path: 'src/Feature.cs' },
    { file_path: 'Feature.cs' },
    { path: 'src/Feature.cs' },
    { description: 'Inspect the implementation', file_path: '/very-long-temporary-root/'.repeat(20) + 'src/Feature.cs' },
  ]) {
    const html = toolCallHtml(tool({ input }));
    assert.match(html, /class="tool-summary is-path">Feature\.cs<\/span>/);
    assert.doesNotMatch(html, /tool-context|src\/|very-long-temporary-root|Inspect the implementation/);
  }
});

test('header strings, tool names, input, output, and media metadata stay escaped', () => {
  const attack = '<img src=x onerror="alert(1)">&\'';
  const row = tool({ toolName: attack, input: { description: attack, file_path: attack },
    output: [{ text: attack }, { artifactUri: attack, mimeType: 'application/pdf', fileName: attack }] });
  for (const html of [toolCallHtml(row), toolBodyHtml(row)]) {
    assert.doesNotMatch(html, /<img src=x|onerror="alert/);
    assert.match(html, /&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt;&amp;&#39;/);
    assert.doesNotMatch(html, /data-full=/);
  }
});

test('collapsed rendering never serializes input or visits tool output', () => {
  const input = {
    file_path: '/workspace/file.cs',
    get content() { throw new Error('must not inspect content while collapsed'); },
    toJSON() { throw new Error('must not stringify input while collapsed'); },
  };
  const row = tool({ input });
  Object.defineProperty(row, 'output', { get() { throw new Error('must not inspect output while collapsed'); } });
  for (let i = 0; i < 1000; i++) assert.ok(toolCallHtml(row).length < 4000);
});

test('status, elapsed duration, and nonzero file-change counts remain in collapsed headers', () => {
  const html = toolCallHtml(tool({ fileChange: { added: 2, removed: 5 } }));
  assert.match(html, /aria-label="Succeeded"/);
  assert.match(html, /aria-label="Elapsed time: 1\.5s">1\.5s/);
  assert.match(html, /class="tool-add"[^>]*>\+2/);
  assert.match(html, /class="tool-del"[^>]*>\u22125/);
  assert.doesNotMatch(html, /class="tool-state[^>]*>Succeeded/);
  assert.doesNotMatch(toolCallHtml(tool({ fileChange: { added: 0, removed: 3 } })), /class="tool-add"/);
  assert.doesNotMatch(toolCallHtml(tool({ fileChange: { added: 3, removed: 0 } })), /class="tool-del"/);
  assert.match(toolCallHtml(tool({ status: 'Failed', isError: false })), /class="tool is-error"/);
  assert.match(toolCallHtml(tool({ status: 'AwaitingPermission' })), />Needs permission<\/span>/);
  assert.match(toolCallHtml(tool({ status: 'Running', duration: null })), />Running<\/span>/);
});

test('expanded tools show every text block in order, interleaved with appropriate media', () => {
  const html = toolBodyHtml(tool({ output: [
    { text: 'first distinct block' },
    { artifactUri: 'artifact:image', mimeType: 'IMAGE/PNG; charset=binary', fileName: 'shot.png' },
    { text: 'second distinct block' },
    { artifactUri: 'artifact:audio', mimeType: 'audio/wav', fileName: 'sound.wav' },
    { artifactUri: 'artifact:video', mimeType: 'video/webm', fileName: 'clip.webm' },
    { artifactUri: 'artifact:pdf', mimeType: 'application/pdf', fileName: 'notes.pdf' },
    { text: 'last distinct block' },
  ] }));
  assert.ok(html.indexOf('first distinct block') < html.indexOf('artifact:image'));
  assert.ok(html.indexOf('artifact:image') < html.indexOf('second distinct block'));
  assert.ok(html.indexOf('second distinct block') < html.indexOf('last distinct block'));
  assert.equal((html.match(/class="thumb"/g) || []).length, 1);
  assert.match(html, /<audio controls preload="none"/);
  assert.match(html, /<video controls preload="none"/);
  assert.match(html, /class="artifact-link"[^>]*download="notes.pdf"/);
  assert.doesNotMatch(html, /<iframe|<object|data-full=/);
});

test('initial input/output markup is bounded even with huge content and thousands of blocks', () => {
  const huge = 'PAYLOAD '.repeat(200_000);
  const row = tool({ input: { file_path: '/src/large.cs', content: huge },
    output: Array.from({ length: 10_000 }, () => ({ text: huge })) });
  const html = toolBodyHtml(row);
  assert.ok(html.length < 25_000, `unexpected markup size: ${html.length}`);
  assert.equal((html.match(/class="tool-text-block" data-section="output-/g) || []).length, 24);
  assert.match(html, /Show all output blocks \(9,976 more\)/);
  assert.doesNotMatch(html, /data-full=/);
});

test('Show all state expands only requested sections and survives rendering the updated row', () => {
  const long = 'x'.repeat(2000) + ' original tail';
  const row = tool({ output: [{ text: long }, { text: 'untouched ' + long }] });
  const state = expanded('output-0');
  let html = toolCallHtml(row, state);
  assert.match(html, /original tail/);
  assert.doesNotMatch(html, /Show all output 1/);
  assert.match(html, /Show all output 2/);
  html = toolCallHtml({ ...row, output: [{ text: long + ' streamed tail' }, row.output[1]] }, state);
  assert.match(html, /streamed tail/);
  assert.match(html, /Show all output 2/);
});

test('Show all output blocks makes later blocks reachable without expanding their text', () => {
  const row = tool({ input: null, output: Array.from({ length: 30 }, (_, i) => ({ text: `block-${i}` })) });
  assert.doesNotMatch(toolBodyHtml(row), /block-29/);
  assert.match(toolBodyHtml(row, expanded('outputs')), /block-29/);
  assert.doesNotMatch(toolBodyHtml(row, expanded('outputs')), /Show all output blocks/);
});

test('bounded JSON preview matches ordinary JSON formatting and safely handles cycles', () => {
  for (const value of [null, 42, true, 'line\n"<&', {}, [], { a: [1, null, 'yes'], b: { c: false } }]) {
    assert.deepEqual(jsonPreview(value), { text: JSON.stringify(value, null, 2), clipped: false });
  }
  const cycle = {};
  cycle.self = cycle;
  assert.equal(jsonPreview(cycle).clipped, true);
  assert.match(jsonPreview(cycle).text, /Circular/);
});

test('bounded JSON stops before visiting later fields or invoking serialization hooks', () => {
  let visits = 0;
  const input = {
    content: 'large'.repeat(200_000),
    get tail() { visits++; throw new Error('preview visited tail'); },
    toJSON() { visits++; throw new Error('preview called toJSON'); },
  };
  const preview = jsonPreview(input);
  assert.equal(preview.text.length, 4000);
  assert.equal(preview.clipped, true);
  assert.equal(visits, 0);
  assert.equal(jsonPreview('"'.repeat(1_000_000), 100).text.length, 100);
});

test('conversation rows preserve escaping, markdown, and reasoning disclosure state', () => withElements(() => {
  assert.match(rowElement({ $type: 'user_message', rowId: 'u', text: '<script>bad</script>' }).innerHTML,
    /&lt;script&gt;bad&lt;\/script&gt;/);
  const row = { $type: 'assistant_message', rowId: 'a', parts: [
    { $type: 'reasoning', text: '<thinking>' }, { $type: 'text', text: '**hello**' },
    { $type: 'file', artifactUri: 'artifact:pdf', mimeType: 'application/pdf', fileName: 'file.pdf' },
  ] };
  const el = rowElement(row, { open: new Set(['thinking']), all: new Set() });
  assert.match(el.innerHTML, /data-disclosure="thinking" open/);
  assert.match(el.innerHTML, /&lt;thinking&gt;/);
  assert.match(el.innerHTML, /<strong>hello<\/strong>/);
  assert.match(el.innerHTML, /download="file.pdf"/);
  assert.doesNotMatch(el.innerHTML, /class="thumb"/);
}));

test('sealed assistant rows without visible content stay addressable but take no layout space', () => withElements(() => {
  const call = { $type: 'tool_call', toolCallId: 'call-1', toolName: 'Browser', input: { action: 'evaluate' } };
  const result = { $type: 'tool_result', toolCallId: 'call-1', content: [{ $type: 'text', text: 'Tool output' }] };
  for (const parts of [undefined, [], [null], [call], [result], [call, result],
    [{ $type: 'text', text: ' \t\r\n ' }], [{ $type: 'reasoning', text: '\n\t ' }, call],
    [{ $type: 'file' }], [{ $type: 'unknown', text: 'Not a displayable part' }]]) {
    const el = rowElement({ $type: 'assistant_message', rowId: 'invisible-assistant', parts, isStreaming: false });
    assert.equal(el.dataset.rowId, 'invisible-assistant');
    assert.equal(el.className, 'row row-assistant');
    assert.equal(el.hidden, true);
    assert.equal(el.innerHTML, '');
  }
}));

test('real assistant prose, reasoning, files, and streaming placeholders keep their content and visibility', () => withElements(() => {
  for (const [row, pattern] of [
    [{ parts: [{ $type: 'text', text: '  **Visible prose**\n' }] }, /<strong>Visible prose<\/strong>/],
    [{ parts: [{ $type: 'reasoning', text: 'Real reasoning' }, { $type: 'text', text: '\n ' }] }, /class="thinking"/],
    [{ parts: [{ $type: 'file', artifactUri: 'artifact:image', mimeType: 'image/png' }] }, /class="msg-images"/],
    [{ parts: [], isStreaming: true }, /class="stream-cursor"/],
    [{ parts: [{ $type: 'text', text: '\n\t ' }], isStreaming: true }, /class="stream-cursor"/],
  ]) {
    const el = rowElement({ $type: 'assistant_message', rowId: 'visible-assistant', ...row });
    assert.equal(el.hidden, false);
    assert.match(el.innerHTML, pattern);
  }
}));

test('assistant rows can become visible and invisible on updates without losing their transcript slot', () => withElements(() => {
  const root = container();
  const transcript = new Transcript(root);
  const row = { $type: 'assistant_message', rowId: 'assistant-slot', parts: [] };
  transcript.rebuild([tool(), row]);
  assert.equal(transcript.els.get(row.rowId).hidden, true);
  for (const parts of [[{ $type: 'text', text: 'Visible follow-up' }], [{ $type: 'tool_result' }]]) {
    const current = transcript.els.get(row.rowId);
    current.replaceWith = replacement => { root.childNodes[1] = replacement; };
    transcript.upsert({ ...row, parts });
    assert.equal(transcript.els.size, 2);
    assert.equal(root.childNodes[1], transcript.els.get(row.rowId));
    assert.equal(root.childNodes[1].hidden, parts[0].$type !== 'text');
  }
  transcript.rebuild([tool(), { ...row, parts: [{ $type: 'text', text: 'Restored on reconnect' }] }]);
  assert.equal(transcript.els.get(row.rowId).hidden, false);
  assert.match(transcript.els.get(row.rowId).innerHTML, /Restored on reconnect/);
  transcript.dispose();
}));

test('Transcript notifies after mutations, retains constructor compatibility, and disposes listeners', () => withElements(() => {
  const root = container();
  let changes = 0;
  const transcript = new Transcript(root, { onChange: () => changes++ });
  transcript.rebuild([]);
  transcript.append(null);
  transcript.remove('missing');
  assert.equal(changes, 0);
  transcript.append(tool());
  assert.equal(changes, 1);
  transcript.els.get('tool-1').remove = () => {};
  transcript.remove('tool-1');
  assert.equal(changes, 2);
  transcript.rebuild([tool()]);
  assert.equal(changes, 3);
  transcript.dispose();
  assert.equal(root.listeners.size, 0);
  transcript.append(tool());
  transcript.rebuild([]);
  assert.equal(changes, 3);
  new Transcript(container()).dispose();
}));

test('row replacements and reconnect rebuilds retain disclosure and Show all state by row ID', () => withElements(() => {
  const root = container();
  const transcript = new Transcript(root);
  const row = tool({ output: [{ text: 'x'.repeat(2000) + ' full tail' }] });
  transcript.append(row);
  transcript.state(row.rowId).all.add('output-0');
  const old = transcript.els.get(row.rowId);
  old.querySelectorAll = () => [{ dataset: { disclosure: 'tool' }, open: true }];
  old.replaceWith = replacement => { root.childNodes = [replacement]; };
  transcript.upsert({ ...row, duration: 2500 });
  assert.match(transcript.els.get(row.rowId).innerHTML, /data-disclosure="tool" open/);
  assert.match(transcript.els.get(row.rowId).innerHTML, /full tail/);
  transcript.rebuild([{ ...row, duration: 3000 }]);
  assert.match(transcript.els.get(row.rowId).innerHTML, /data-disclosure="tool" open/);
  assert.match(transcript.els.get(row.rowId).innerHTML, /full tail/);
  transcript.els.get(row.rowId).querySelectorAll = () => [{ dataset: { disclosure: 'tool' }, open: false }];
  transcript.rebuild([row]);
  assert.match(transcript.els.get(row.rowId).innerHTML, /<div class="tool-body"><\/div>/);
  assert.equal(transcript.state(row.rowId).all.has('output-0'), true);
  transcript.rebuild([]);
  assert.equal(transcript.states.size, 0);
  transcript.dispose();
}));

test('native disclosure builds tool content lazily, notifies once, and releases it on close', () => withElements(() => {
  let changes = 0;
  const transcript = new Transcript(container(), { onChange: () => changes++ });
  transcript.append(tool());
  const el = transcript.els.get('tool-1');
  const body = { dataset: {}, innerHTML: '', querySelectorAll: () => [], replaceChildren() { this.innerHTML = ''; } };
  const detail = { dataset: { disclosure: 'tool' }, open: true, matches: () => true,
    closest: () => el, querySelector: () => body };
  transcript.toggle({ target: detail });
  assert.match(body.innerHTML, /first output/);
  assert.equal(changes, 2);
  transcript.toggle({ target: detail });
  assert.equal(changes, 2);
  detail.open = false;
  transcript.toggle({ target: detail });
  assert.equal(body.innerHTML, '');
  assert.equal(changes, 3);
  assert.equal(transcript.state('tool-1').open.has('tool'), false);
  transcript.dispose();
}));

test('delegated Show all retrieves model text rather than a giant HTML attribute', () => withElements(() => {
  let changes = 0;
  const transcript = new Transcript(container(), { onChange: () => changes++ });
  const full = '<script>not markup</script>' + 'x'.repeat(10_000);
  transcript.append(tool({ output: [{ text: full }] }));
  const el = transcript.els.get('tool-1');
  const clip = { textContent: '' };
  let removed = false;
  const block = { querySelector: selector => selector === '.out-clip' ? clip : { classList: { remove() {} } } };
  const button = { dataset: { section: 'output-0' },
    closest: selector => selector === '.row' ? el : block, remove: () => { removed = true; } };
  transcript.showAll({ target: { closest: () => button }, preventDefault() {} });
  assert.equal(clip.textContent, full);
  assert.equal(removed, true);
  assert.equal(transcript.state('tool-1').all.has('output-0'), true);
  assert.equal(changes, 2);
  transcript.dispose();
}));

test('assistant and active tool updates coalesce and completed rows bypass pending timers', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let now = 1000;
  t.mock.method(performance, 'now', () => now);
  const transcript = new Transcript(container());
  const replacements = [];
  transcript.els.set('tool-1', {});
  transcript.replace = (_, row) => replacements.push(row);
  const running = tool({ status: 'Running' });
  transcript.upsert(running);
  transcript.upsert({ ...running, input: { description: 'intermediate' } });
  transcript.upsert({ ...running, input: { description: 'latest' } });
  assert.equal(replacements.length, 1);
  now += 149;
  t.mock.timers.tick(149);
  assert.equal(replacements.length, 1);
  now++;
  t.mock.timers.tick(1);
  assert.equal(replacements.length, 2);
  assert.equal(replacements[1].input.description, 'latest');
  transcript.upsert({ ...running, input: { description: 'pending stale row' } });
  transcript.upsert(tool({ status: 'Failed', duration: 500 }));
  assert.equal(replacements.at(-1).status, 'Failed');
  t.mock.timers.tick(500);
  assert.equal(replacements.length, 3);
  transcript.els.set('assistant', {});
  transcript.upsert({ $type: 'assistant_message', rowId: 'assistant', isStreaming: true });
  transcript.upsert({ $type: 'assistant_message', rowId: 'assistant', isStreaming: true, parts: [{ text: 'latest' }] });
  assert.equal(replacements.length, 4);
  now += 150;
  t.mock.timers.tick(150);
  assert.equal(replacements.length, 5);
  transcript.dispose();
});
