import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  Transcript, rowElement, toolSummary, toolCallHtml, toolBodyHtml, jsonPreview, hydrateThumbs, releaseArtifacts,
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
    appendChild(el) { this.insertBefore(el, null); },
    insertBefore(el, anchor) {
      const previous = this.childNodes.indexOf(el);
      if (previous >= 0) this.childNodes.splice(previous, 1);
      const index = anchor ? this.childNodes.indexOf(anchor) : this.childNodes.length;
      assert.ok(index >= 0, 'insertBefore anchor must belong to its parent');
      this.childNodes.splice(index, 0, el);
      el.remove = () => { const at = this.childNodes.indexOf(el); if (at >= 0) this.childNodes.splice(at, 1); };
      el.replaceWith = replacement => {
        const at = this.childNodes.indexOf(el);
        assert.ok(at >= 0, 'replaceWith target must belong to its parent');
        this.childNodes.splice(at, 1);
        this.insertBefore(replacement, this.childNodes[at] || null);
      };
    },
    replaceChildren(fragment) { this.childNodes = []; for (const el of fragment?.children || []) this.appendChild(el); },
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
  old.querySelectorAll = selector => selector.startsWith('details') ? [{ dataset: { disclosure: 'tool' }, open: true }] : [];
  transcript.upsert({ ...row, duration: 2500 });
  assert.match(transcript.els.get(row.rowId).innerHTML, /data-disclosure="tool" open/);
  assert.match(transcript.els.get(row.rowId).innerHTML, /full tail/);
  transcript.rebuild([{ ...row, duration: 3000 }]);
  assert.match(transcript.els.get(row.rowId).innerHTML, /data-disclosure="tool" open/);
  assert.match(transcript.els.get(row.rowId).innerHTML, /full tail/);
  transcript.els.get(row.rowId).querySelectorAll = selector => selector.startsWith('details') ? [{ dataset: { disclosure: 'tool' }, open: false }] : [];
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
  transcript.showAll({ target: { closest: selector => selector === '.out-more[data-section]' ? button : null }, preventDefault() {} });
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

test('appended text buffers fine-grained chunks until render cadence and preserves immutable row metadata', t => withElements(() => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let now = 1000;
  t.mock.method(performance, 'now', () => now);
  const transcript = new Transcript(container());
  const row = { $type: 'assistant_message', rowId: 'append-row', id: 'message-id', isStreaming: true,
    at: 'initial', canFork: false, parts: [{ $type: 'text', id: 'stable-part', text: 'prefix' }] };
  transcript.append(row);
  const rendered = [];
  transcript.replace = (_, value) => rendered.push(value);
  const chunks = Array.from({ length: 10_000 }, (_, i) => i % 2 ? ' \n' : '中文');
  for (let i = 0; i < chunks.length; i++) {
    assert.equal(transcript.appendText({ rowId: row.rowId, delta: chunks[i], at: `at-${i}` }), true);
  }
  assert.equal(rendered.length, 1);
  assert.equal(transcript.textBuffers.get(row.rowId).chunks.length, 9999);
  assert.equal(row.parts[0].text, 'prefix');
  now += 150;
  t.mock.timers.tick(150);
  assert.equal(rendered.length, 2);
  const final = rendered.at(-1);
  assert.equal(final.parts[0].text, 'prefix' + chunks.join(''));
  assert.equal(final.parts[0].id, 'stable-part');
  assert.equal(final.id, row.id);
  assert.equal(final.canFork, false);
  assert.equal(final.at, 'at-9999');
  assert.equal(transcript.textBuffers.get(row.rowId).chunks.length, 0);
  transcript.dispose();
  assert.equal(transcript.textBuffers.size, 0);
}));

test('corrections, commits, removal, reconnect snapshots and disposal retire appended-text buffers', t => withElements(() => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let now = 1000;
  t.mock.method(performance, 'now', () => now);
  const transcript = new Transcript(container());
  const row = { $type: 'assistant_message', rowId: 'reset-row', id: 'message', isStreaming: true,
    parts: [{ $type: 'text', id: 'stable', text: 'first' }] };
  transcript.append(row);
  const rendered = [];
  transcript.replace = (_, value) => rendered.push(value);
  transcript.appendText({ rowId: row.rowId, delta: ' shown', at: 'shown' });
  transcript.appendText({ rowId: row.rowId, delta: ' stale', at: 'stale' });
  const correction = { ...row, parts: [{ ...row.parts[0], text: 'corrected' }] };
  transcript.upsert(correction);
  transcript.appendText({ rowId: row.rowId, delta: ' next', at: 'next' });
  now += 150;
  t.mock.timers.tick(150);
  assert.equal(rendered.at(-1).parts[0].text, 'corrected next');
  transcript.appendText({ rowId: row.rowId, delta: ' must not survive commit', at: 'stale' });
  transcript.upsert({ ...row, isStreaming: false, canFork: true, parts: [{ $type: 'reasoning', text: 'final reasoning' }] });
  t.mock.timers.tick(500);
  assert.equal(rendered.at(-1).isStreaming, false);
  assert.equal(rendered.at(-1).canFork, true);
  assert.equal(transcript.textBuffers.size, 0);
  transcript.rebuild([correction]);
  transcript.appendText({ rowId: row.rowId, delta: ' reconnect-stale', at: 'stale' });
  transcript.rebuild([row]);
  assert.equal(transcript.textBuffers.size, 0);
  assert.equal(transcript.timers.size, 0);
  transcript.els.get(row.rowId).remove = () => {};
  transcript.remove(row.rowId);
  assert.equal(transcript.appendText({ rowId: row.rowId, delta: 'missing', at: 'missing' }), false);
  transcript.dispose();
  assert.equal(transcript.appendText({ rowId: row.rowId, delta: 'disposed' }), false);
}));

function artifactTarget(kind, uri) {
  const listeners = new Map();
  const attrs = new Map();
  const target = {
    dataset: { artifact: uri, mime: kind === 'thumb' ? 'image/png' : `${kind}/fixture`, name: 'fixture' },
    isConnected: true, children: [], listeners, querySelectorAll: () => [], querySelector: () => null,
    classList: { add() {}, remove() {} },
    matches: selector => selector.split(',').some(value => value === (kind === 'thumb' ? '.thumb' : kind)),
    addEventListener(name, fn) { if (!listeners.has(name)) listeners.set(name, new Set()); listeners.get(name).add(fn); },
    removeEventListener(name, fn) { listeners.get(name)?.delete(fn); },
    fire(name, event = {}) { for (const fn of listeners.get(name) || []) fn(event); },
    setAttribute(name, value) { attrs.set(name, value); },
    getAttribute: name => attrs.get(name) ?? null,
    removeAttribute(name) { attrs.delete(name); },
    replaceChildren(...children) { this.children = children; },
    append() {}, pause() { this.pauses = (this.pauses || 0) + 1; }, load() { this.loads = (this.loads || 0) + 1; },
  };
  Object.defineProperty(target, 'src', { get: () => attrs.get('src'), set: value => attrs.set('src', value) });
  return target;
}

const decodeHtml = text => text.replace(/&(#x[\da-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (_, entity) => {
  if (entity[0] === '#') return String.fromCodePoint(parseInt(entity.slice(entity[1] === 'x' ? 2 : 1), entity[1] === 'x' ? 16 : 10));
  return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }[entity];
});
const encodeHtml = text => String(text).replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
const voidTags = new Set(['img', 'input', 'hr', 'br', 'wbr', 'source']);
const dataKey = key => key.slice(5).replace(/-([a-z])/g, (_, char) => char.toUpperCase());

class DomElement {
  constructor(tag = 'div', text = '') {
    this.tagName = tag.toUpperCase();
    this.nodeType = tag === '#text' ? 3 : 1;
    this.text = text;
    this.childNodes = [];
    this.parentNode = null;
    this.dataset = {};
    this.attrs = new Map();
    this.listeners = new Map();
    this.writes = 0;
    this.classList = {
      contains: name => this.className.split(/\s+/).includes(name),
      add: (...names) => { this.className = [...new Set([...this.className.split(/\s+/).filter(Boolean), ...names])].join(' '); },
      remove: (...names) => { this.className = this.className.split(/\s+/).filter(name => !names.includes(name)).join(' '); },
    };
  }
  get children() { return this.childNodes.filter(node => node.nodeType === 1); }
  get isConnected() { return !!this._connected || !!this.parentNode?.isConnected; }
  get className() { return this.attrs.get('class') || ''; }
  set className(value) { this.setAttribute('class', value); }
  get open() { return this.attrs.has('open'); }
  set open(value) { if (value) this.setAttribute('open', ''); else this.removeAttribute('open'); }
  get hidden() { return this.attrs.has('hidden'); }
  set hidden(value) { if (value) this.setAttribute('hidden', ''); else this.removeAttribute('hidden'); }
  get src() { return this.getAttribute('src'); }
  set src(value) { this.setAttribute('src', value); }
  get textContent() { return this.nodeType === 3 ? this.text : this.childNodes.map(node => node.textContent).join(''); }
  set textContent(value) { this.replaceChildren(new DomElement('#text', String(value))); }
  touch() {
    let root = this;
    while (root.parentNode) root = root.parentNode;
    if (root._connected) root.writes++;
  }
  setAttribute(name, value) {
    this.touch();
    if (name.startsWith('data-')) this.dataset[dataKey(name)] = String(value);
    else this.attrs.set(name, String(value));
  }
  getAttribute(name) { return name.startsWith('data-') ? this.dataset[dataKey(name)] ?? null : this.attrs.get(name) ?? null; }
  removeAttribute(name) {
    this.touch();
    if (name.startsWith('data-')) delete this.dataset[dataKey(name)];
    else this.attrs.delete(name);
  }
  get innerHTML() { return this.childNodes.map(node => node.outerHTML).join(''); }
  get outerHTML() {
    if (this.nodeType === 3) return encodeHtml(this.text);
    const attrs = [...this.attrs, ...Object.entries(this.dataset).map(([key, value]) => [
      'data-' + key.replace(/[A-Z]/g, char => '-' + char.toLowerCase()), value])];
    const tag = this.tagName.toLowerCase();
    const attributes = attrs.map(([key, value]) => value === '' ? ` ${key}` : ` ${key}="${encodeHtml(value)}"`).join('');
    return `<${tag}${attributes}>${voidTags.has(tag) ? '' : this.innerHTML + `</${tag}>`}`;
  }
  set innerHTML(html) {
    this.replaceChildren();
    const stack = [this];
    for (const token of String(html).match(/<[^>]+>|[^<]+/g) || []) {
      if (token.startsWith('</')) { assert.ok(stack.length > 1, 'balanced closing tag'); stack.pop(); continue; }
      if (!token.startsWith('<')) { stack.at(-1).appendChild(new DomElement('#text', decodeHtml(token))); continue; }
      if (token.startsWith('<!--')) continue;
      const match = /^<([\w-]+)([\s\S]*?)\/?>$/.exec(token);
      assert.ok(match, `supported HTML token: ${token}`);
      const node = new DomElement(match[1]);
      for (const attr of match[2].matchAll(/([^\s=]+)(?:="([^"]*)"|='([^']*)'|=([^\s]+))?/g)) {
        node.setAttribute(attr[1], decodeHtml(attr[2] ?? attr[3] ?? attr[4] ?? ''));
      }
      stack.at(-1).appendChild(node);
      if (!voidTags.has(match[1]) && !token.endsWith('/>')) stack.push(node);
    }
    assert.equal(stack.length, 1, 'balanced HTML tree');
  }
  appendChild(node) { return this.insertBefore(node, null); }
  insertBefore(node, anchor) {
    if (node === anchor) return node;
    assert.ok(anchor === null || anchor.parentNode === this, 'anchor belongs to parent');
    node.remove();
    this.touch();
    const index = anchor ? this.childNodes.indexOf(anchor) : this.childNodes.length;
    this.childNodes.splice(index, 0, node);
    node.parentNode = this;
    return node;
  }
  remove() {
    if (!this.parentNode) return;
    this.touch();
    const siblings = this.parentNode.childNodes;
    siblings.splice(siblings.indexOf(this), 1);
    this.parentNode = null;
  }
  replaceWith(node) {
    assert.ok(this.parentNode, 'replacement target belongs to parent');
    this.parentNode.insertBefore(node, this);
    this.remove();
  }
  replaceChildren(...nodes) {
    this.touch();
    for (const child of [...this.childNodes]) child.remove();
    for (const node of nodes) this.appendChild(node);
  }
  matches(selector) {
    return selector.split(',').some(part => {
      const chain = part.trim().split(/\s+(?![^\[]*\])/);
      const matchOne = (node, simple) => {
        if (node.nodeType !== 1) return false;
        const tag = /^[\w-]+/.exec(simple)?.[0];
        if (tag && tag.toUpperCase() !== node.tagName) return false;
        for (const match of simple.matchAll(/\.([\w-]+)/g)) if (!node.classList.contains(match[1])) return false;
        for (const match of simple.matchAll(/\[([\w-]+)(?:="?([^"\]]+)"?)?\]/g)) {
          const attr = node.getAttribute(match[1]);
          if (attr == null || (match[2] !== undefined && attr !== match[2])) return false;
        }
        return true;
      };
      if (!matchOne(this, chain.pop())) return false;
      let ancestor = this.parentNode;
      while (chain.length) {
        const simple = chain.pop();
        while (ancestor && !matchOne(ancestor, simple)) ancestor = ancestor.parentNode;
        if (!ancestor) return false;
        ancestor = ancestor.parentNode;
      }
      return true;
    });
  }
  closest(selector) { for (let node = this; node; node = node.parentNode) if (node.matches(selector)) return node; return null; }
  querySelectorAll(selector) {
    const matches = [];
    const visit = node => { for (const child of node.children) { if (child.matches(selector)) matches.push(child); visit(child); } };
    visit(this);
    return matches;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  addEventListener(name, handler, capture = false) {
    if (!this.listeners.has(name)) this.listeners.set(name, []);
    this.listeners.get(name).push({ handler, capture });
  }
  removeEventListener(name, handler) {
    this.listeners.set(name, (this.listeners.get(name) || []).filter(listener => listener.handler !== handler));
  }
  fire(name) {
    const path = [];
    for (let node = this; node; node = node.parentNode) path.push(node);
    const event = { target: this, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
    for (const node of [...path].reverse()) for (const listener of [...node.listeners.get(name) || []]) if (listener.capture) listener.handler(event);
    for (const node of path) {
      if (name === 'toggle' && node !== this) break;
      for (const listener of [...node.listeners.get(name) || []]) if (!listener.capture) listener.handler(event);
    }
    return event;
  }
  pause() { this.pauses = (this.pauses || 0) + 1; }
  load() { this.loads = (this.loads || 0) + 1; }
  play() { return Promise.resolve(); }
}

async function withDom(run) {
  const previous = { document: globalThis.document, Image: globalThis.Image };
  globalThis.document = { createElement: tag => new DomElement(tag) };
  globalThis.Image = class extends DomElement { constructor() { super('img'); } };
  const root = new DomElement();
  root._connected = true;
  try { return await run(root); }
  finally {
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
    }
  }
}

function setDisclosure(root, key, open) {
  const detail = root.querySelector(`details[data-disclosure="${key}"]`);
  assert.ok(detail, `${key} disclosure exists`);
  detail.open = open;
  detail.fire('toggle');
  return detail;
}

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const assistant = (rowId, text, overrides = {}) => ({ $type: 'assistant_message', rowId,
  parts: [{ $type: 'text', text }], isStreaming: false, ...overrides });
const compactTool = (overrides = {}) => {
  const { output, ...header } = tool();
  return { ...header, detailsDeferred: true, detailVersion: 'tool-v1', ...overrides };
};
const turn = (overrides = {}) => ({ $type: 'turn_summary', rowId: 'user-1',
  user: { $type: 'user_message', rowId: 'user-1', text: 'Visible question' },
  answer: assistant('answer-1', '**Visible answer**'), duration: '00:00:01.5000000', detailCount: 2,
  detailVersion: 'turn-v1', ...overrides });

const flushArtifacts = () => new Promise(resolve => setImmediate(resolve));

test('mixed cached API modules still support new rendering without missing named exports', () => {
  const source = readFileSync(new URL('../../../src/Alcoler.Remote/wwwroot/render.js', import.meta.url), 'utf8');
  const compatibility = source.slice(source.indexOf('// A previously active'), source.indexOf('export const PHASE_LABEL'));
  const promise = Promise.resolve('blob:legacy');
  const api = { artifactObjectUrl: () => promise, getToken: () => 'legacy-token' };
  const bridge = new Function('api', `${compatibility}; return { acquireArtifactObjectUrl, artifactUrl };`)(api);
  const lease = bridge.acquireArtifactObjectUrl('legacy', 'image/png');
  assert.equal(lease.promise, promise);
  lease.release();
  const url = new URL(bridge.artifactUrl('https://untrusted.example/value', 'video/webm'), 'https://paired.example');
  assert.equal(url.origin, 'https://paired.example');
  assert.equal(url.pathname, '/api/artifacts');
  assert.equal(url.searchParams.get('token'), 'legacy-token');
});

test('download links and media never hydrate eagerly and native actions remain in the user gesture', t => {
  let fetches = 0;
  t.mock.method(globalThis, 'fetch', async () => { fetches++; throw new Error('must not hydrate'); });
  const storage = globalThis.localStorage;
  globalThis.localStorage = { getItem: () => 'gesture-token' };
  t.after(() => { if (storage === undefined) delete globalThis.localStorage; else globalThis.localStorage = storage; });
  const link = artifactTarget('a', 'alcoler-artifact://lazy/download');
  const audio = artifactTarget('audio', 'alcoler-artifact://lazy/audio');
  const video = artifactTarget('video', 'alcoler-artifact://lazy/video');
  const buttons = [];
  let inGesture = false;
  for (const media of [audio, video]) {
    const button = artifactTarget('button', '');
    buttons.push(button);
    media.closest = () => ({ querySelector: () => button });
    media.play = () => { assert.equal(inGesture, true); media.plays = (media.plays || 0) + 1; return Promise.resolve(); };
  }
  const scope = { querySelectorAll: () => [link, audio, video] };
  hydrateThumbs(scope);
  assert.equal(fetches, 0);
  assert.equal(link.href, undefined);
  assert.equal(audio.src, undefined);
  assert.equal(video.src, undefined);
  inGesture = true;
  link.fire('click', { preventDefault() { assert.fail('native download was cancelled'); } });
  buttons[0].fire('click');
  buttons[1].fire('click');
  inGesture = false;
  for (const value of [link.href, audio.src, video.src]) {
    assert.ok(value.startsWith('/api/artifacts?'));
    assert.equal(new URL(value, 'https://paired.example').searchParams.get('token'), 'gesture-token');
  }
  assert.equal(audio.plays, 1);
  assert.equal(video.plays, 1);
  audio.fire('playing');
  assert.equal(buttons[0].hidden, true);
  audio.fire('error');
  assert.equal(buttons[0].hidden, false);
  assert.equal(buttons[0].textContent, 'Retry playback');
  releaseArtifacts(scope);
  assert.equal(audio.pauses, 1);
  assert.equal(video.pauses, 1);
  assert.equal(audio.src, undefined);
  assert.equal(video.src, undefined);
  assert.equal(link.listeners.get('click').size, 0);
  assert.equal(fetches, 0);
});

test('detaching one thumbnail keeps shared hydration alive; disposing the last consumer aborts it', async t => {
  const storage = globalThis.localStorage;
  globalThis.localStorage = { getItem: () => 'thumbnail-token' };
  t.after(() => { if (storage === undefined) delete globalThis.localStorage; else globalThis.localStorage = storage; });
  const requests = [];
  t.mock.method(globalThis, 'fetch', (url, options) => new Promise((resolve, reject) => {
    requests.push({ url, options });
    options.signal.addEventListener('abort', () => reject(options.signal.reason), { once: true });
  }));
  const first = artifactTarget('thumb', 'alcoler-artifact://shared/dispose');
  const second = artifactTarget('thumb', first.dataset.artifact);
  let changes = 0;
  hydrateThumbs({ querySelectorAll: () => [first, second] }, null, () => changes++);
  await flushArtifacts();
  assert.equal(requests.length, 1);
  releaseArtifacts({ querySelectorAll: () => [first] });
  first.isConnected = false;
  assert.equal(requests[0].options.signal.aborted, false);
  const root = container();
  root.querySelectorAll = () => [second];
  const transcript = new Transcript(root);
  transcript.dispose();
  second.isConnected = false;
  assert.equal(requests[0].options.signal.aborted, true);
  await flushArtifacts();
  assert.equal(changes, 0);
  assert.equal(first.children.length, 0);
  assert.equal(second.children.length, 0);
});

test('equivalent reconnect snapshots retain row, disclosure, Show-all, and playing media identity without DOM writes', () => withDom(async root => {
  const rows = [{ $type: 'user_message', rowId: 'user', text: 'Question' }, tool({ output: [
    { text: 'x'.repeat(2000) + ' retained tail' },
    { artifactUri: 'artifact:audio-identity', mimeType: 'audio/wav', fileName: 'audio.wav' },
  ] }), assistant('answer', 'Answer', { parts: [{ $type: 'reasoning', text: 'Reasoning' }, { $type: 'text', text: 'Answer' }] })];
  let changes = 0, captures = 0;
  const transcript = new Transcript(root, { onChange: () => changes++, onBeforeChange: () => captures++ });
  transcript.rebuild(rows);
  const toolNode = transcript.els.get('tool-1');
  setDisclosure(toolNode, 'tool', true);
  const button = toolNode.querySelector('.out-more[data-section="output-0"]');
  assert.ok(button);
  button.fire('click');
  const thinking = setDisclosure(transcript.els.get('answer'), 'thinking', true);
  const body = toolNode.querySelector('.tool-body');
  const audio = body.querySelector('audio');
  audio.src = '/playing';
  audio.currentTime = 37;
  const before = [...root.childNodes];
  const counts = [root.writes, changes, captures];
  transcript.rebuild(structuredClone(rows));
  assert.deepEqual(root.childNodes, before);
  before.forEach((node, index) => assert.equal(root.childNodes[index], node));
  assert.equal(transcript.els.get('tool-1'), toolNode);
  assert.equal(toolNode.querySelector('.tool-body'), body);
  assert.equal(body.querySelector('audio'), audio);
  assert.equal(audio.currentTime, 37);
  assert.equal(audio.src, '/playing');
  assert.equal(thinking.open, true);
  assert.match(root.textContent, /retained tail/);
  assert.deepEqual([root.writes, changes, captures], counts);
  transcript.dispose();
  assert.equal(audio.pauses, 1);
  assert.equal(audio.src, null);
  assert.equal(root.childNodes.length, 0);
}));

test('keyed snapshots update same-length and in-place corrections, reorder, insert, and remove exactly', () => withDom(async root => {
  const first = assistant('first', 'alpha');
  const second = assistant('second', 'fixed');
  const transcript = new Transcript(root);
  transcript.rebuild([first, second, tool()]);
  const fixed = transcript.els.get('second');
  const removed = transcript.els.get('tool-1');
  setDisclosure(removed, 'tool', true);
  transcript.rebuild([second, { ...first, parts: [{ $type: 'text', text: 'bravo' }] }, assistant('new', 'new')]);
  assert.deepEqual(root.childNodes.map(node => node.dataset.rowId), ['second', 'first', 'new']);
  assert.equal(root.childNodes[0], fixed);
  assert.equal(removed.isConnected, false);
  assert.equal(transcript.states.has('tool-1'), false);
  assert.match(root.textContent, /bravo/);
  assert.doesNotMatch(root.textContent, /alpha/);
  const corrected = assistant('first', 'gamma');
  transcript.upsert(corrected);
  corrected.parts[0].text = 'delta';
  transcript.rebuild([corrected, second]);
  assert.equal(root.childNodes[1], fixed);
  assert.match(root.textContent, /delta/);
  assert.doesNotMatch(root.textContent, /gamma/);
  transcript.rebuild([]);
  assert.equal(root.childNodes.length, 0);
  transcript.dispose();
}));

test('folded turns build only the question, summary control, and final answer with no process DOM or fetch', () => withDom(async root => {
  let requests = 0;
  const summary = turn({ answer: assistant('answer-1', '', { parts: [
    { $type: 'reasoning', text: 'Hidden final reasoning' }, { $type: 'text', text: '**Visible answer**' },
    { $type: 'file', artifactUri: 'artifact:answer-pdf', mimeType: 'application/pdf', fileName: 'answer.pdf' },
  ] }) });
  Object.defineProperty(summary, 'process', { enumerable: true, get() { throw new Error('must not inspect hidden process'); } });
  const transcript = new Transcript(root, { loadTurn: () => { requests++; throw new Error('must stay folded'); } });
  assert.equal(transcript.supportsCompact, true);
  transcript.rebuild([summary]);
  assert.equal(requests, 0);
  assert.equal(root.querySelectorAll('.row').length, 1);
  assert.equal(root.querySelector('.turn-body').childNodes.length, 0);
  assert.equal(root.querySelectorAll('.tool').length, 0);
  assert.equal(root.querySelectorAll('.thinking').length, 0);
  assert.match(root.textContent, /Visible question/);
  assert.match(root.textContent, /Worked for 1\.5s/);
  assert.match(root.innerHTML, /<strong>Visible answer<\/strong>/);
  assert.match(root.textContent, /answer\.pdf/);
  assert.doesNotMatch(root.innerHTML, /Hidden final reasoning|tool-body|first output/);
  transcript.dispose();
}));

test('process expansion fetches once, preserves pending identity across snapshots, and builds tools only as compact headers', () => withDom(async root => {
  const pending = deferred();
  const calls = [];
  const transcript = new Transcript(root, { loadTurn: (id, options) => { calls.push({ id, options }); return pending.promise; } });
  const summary = turn();
  transcript.rebuild([summary]);
  const node = transcript.els.get(summary.rowId);
  const detail = setDisclosure(node, 'turn', true);
  assert.match(detail.textContent, /Loading details/);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].id, 'user-1');
  assert.equal(calls[0].options.version, 'turn-v1');
  assert.equal(calls[0].options.signal.aborted, false);
  transcript.rebuild([structuredClone(summary)]);
  assert.equal(transcript.els.get(summary.rowId), node);
  assert.equal(calls.length, 1);
  pending.resolve({ version: 'turn-v1', sequence: 20, rows: [
    assistant('reason', '', { parts: [{ $type: 'reasoning', text: 'Fetched reasoning only' }] }), compactTool(),
  ] });
  await flushArtifacts();
  const process = detail.querySelector('.turn-body');
  assert.equal(process.querySelectorAll('.row').length, 2);
  assert.equal(process.querySelector('.tool-body').childNodes.length, 0);
  assert.match(process.textContent, /Fetched reasoning only/);
  assert.doesNotMatch(process.textContent, /first output/);
  setDisclosure(node, 'turn', false);
  assert.equal(process.childNodes.length, 0);
  assert.doesNotMatch(root.textContent, /Fetched reasoning only|Example\.cs/);
  assert.equal(transcript.loads.size, 0);
  setDisclosure(node, 'turn', true);
  assert.equal(calls.length, 1);
  assert.equal(process.querySelectorAll('.row').length, 2);
  transcript.dispose();
}));

test('deferred tools unwrap full-row envelopes, reuse lazy preview and Show-all, and release closed DOM and media', () => withDom(async root => {
  const pending = deferred();
  const calls = [];
  const transcript = new Transcript(root, { loadRow: (id, options) => { calls.push({ id, options }); return pending.promise; } });
  transcript.rebuild([compactTool()]);
  const node = transcript.els.get('tool-1');
  const detail = setDisclosure(node, 'tool', true);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].options.version, 'tool-v1');
  pending.resolve({ version: 'tool-v1', sequence: 12, row: tool({ output: [
    { text: 'x'.repeat(2000) + '<script>full fetched tail</script>' },
    { artifactUri: 'artifact:tool-video', mimeType: 'video/webm', fileName: 'clip.webm' },
  ] }) });
  await flushArtifacts();
  const body = detail.querySelector('.tool-body');
  assert.doesNotMatch(body.textContent, /full fetched tail/);
  const button = body.querySelector('.out-more[data-section="output-0"]');
  assert.ok(button);
  button.fire('click');
  assert.match(body.textContent, /<script>full fetched tail<\/script>/);
  assert.equal(body.querySelector('script'), null);
  const video = body.querySelector('video');
  video.src = '/playing';
  setDisclosure(node, 'tool', false);
  assert.equal(body.childNodes.length, 0);
  assert.equal(video.isConnected, false);
  assert.equal(video.src, null);
  assert.equal(video.pauses, 1);
  assert.equal(transcript.loads.size, 0);
  assert.equal(calls[0].options.signal.aborted, true);
  setDisclosure(node, 'tool', true);
  assert.equal(calls.length, 1);
  assert.match(body.textContent, /full fetched tail/);
  transcript.dispose();
}));

test('late turn requests cannot resurrect collapsed, changed-version, removed, or disposed process rows', () => withDom(async root => {
  const calls = [];
  let changes = 0;
  const transcript = new Transcript(root, { onChange: () => changes++, loadTurn: (id, options) => {
    const pending = deferred(); calls.push({ id, options, ...pending }); return pending.promise;
  } });
  transcript.rebuild([turn()]);
  setDisclosure(root, 'turn', true);
  setDisclosure(root, 'turn', false);
  assert.equal(calls[0].options.signal.aborted, true);
  calls[0].resolve({ version: 'turn-v1', rows: [assistant('late', 'collapsed secret')] });
  await flushArtifacts();
  assert.doesNotMatch(root.textContent, /collapsed secret/);
  setDisclosure(root, 'turn', true);
  transcript.rebuild([turn({ detailVersion: 'turn-v2' })]);
  assert.equal(calls[1].options.signal.aborted, true);
  assert.equal(calls[2].options.version, 'turn-v2');
  calls[1].resolve({ version: 'turn-v1', rows: [assistant('old', 'old version secret')] });
  calls[2].resolve({ version: 'turn-v2', rows: [assistant('new', 'current process')] });
  await flushArtifacts();
  assert.match(root.textContent, /current process/);
  assert.doesNotMatch(root.textContent, /old version secret/);
  transcript.rebuild([turn({ detailVersion: 'turn-v3' })]);
  transcript.remove('user-1');
  assert.equal(calls[3].options.signal.aborted, true);
  calls[3].resolve({ version: 'turn-v3', rows: [assistant('removed', 'removed secret')] });
  await flushArtifacts();
  assert.equal(root.childNodes.length, 0);
  transcript.rebuild([turn({ detailVersion: 'turn-v4' })]);
  setDisclosure(root, 'turn', true);
  transcript.dispose();
  const finalChanges = changes;
  assert.equal(calls[4].options.signal.aborted, true);
  calls[4].resolve({ version: 'turn-v4', rows: [assistant('disposed', 'disposed secret')] });
  await flushArtifacts();
  assert.equal(changes, finalChanges);
  assert.equal(root.childNodes.length, 0);
  assert.equal(transcript.cache.entries.size, 0);
}));

test('tool failures and mismatched versions stay local and offer retry without accepting stale payloads', () => withDom(async root => {
  const calls = [];
  const transcript = new Transcript(root, { loadRow: (id, options) => {
    const pending = deferred(); calls.push({ id, options, ...pending }); return pending.promise;
  } });
  transcript.rebuild([compactTool()]);
  setDisclosure(root, 'tool', true);
  calls[0].reject(Object.assign(new Error('stale_details'), { status: 409 }));
  await flushArtifacts();
  assert.match(root.textContent, /Details changed/);
  assert.equal(root.querySelector('.detail-error').getAttribute('role'), 'alert');
  root.querySelector('.detail-retry').fire('click');
  assert.equal(calls.length, 2);
  calls[1].resolve({ version: 'wrong-version', row: tool({ output: [{ text: 'must never render' }] }) });
  await flushArtifacts();
  assert.match(root.textContent, /Could not load details/);
  assert.doesNotMatch(root.textContent, /must never render/);
  assert.equal(transcript.cache.entries.size, 0);
  root.querySelector('.detail-retry').fire('click');
  calls[2].reject(Object.assign(new Error('gone'), { status: 404 }));
  await flushArtifacts();
  assert.match(root.textContent, /no longer available/);
  root.querySelector('.detail-retry').fire('click');
  calls[3].resolve({ version: 'tool-v1', row: tool({ output: [{ text: 'retried full output' }] }) });
  await flushArtifacts();
  assert.match(root.textContent, /retried full output/);
  assert.equal(root.querySelector('.detail-error'), null);
  transcript.dispose();
}));

test('loaded expanded tools re-fetch changed versions and ignore obsolete responses after collapse or replacement', () => withDom(async root => {
  const calls = [];
  const transcript = new Transcript(root, { loadRow: (id, options) => {
    const pending = deferred(); calls.push({ id, options, ...pending }); return pending.promise;
  } });
  transcript.rebuild([compactTool()]);
  setDisclosure(root, 'tool', true);
  calls[0].resolve({ version: 'tool-v1', row: tool({ output: [{ text: 'old body' }] }) });
  await flushArtifacts();
  const oldBody = root.querySelector('.tool-body');
  transcript.upsert(compactTool({ detailVersion: 'tool-v2' }));
  assert.equal(oldBody.isConnected, false);
  assert.equal(root.querySelector('details').open, true);
  assert.doesNotMatch(root.textContent, /old body/);
  assert.equal(calls[1].options.version, 'tool-v2');
  setDisclosure(root, 'tool', false);
  assert.equal(calls[1].options.signal.aborted, true);
  setDisclosure(root, 'tool', true);
  calls[1].resolve({ version: 'tool-v2', row: tool({ output: [{ text: 'obsolete body' }] }) });
  calls[2].resolve({ version: 'tool-v2', row: tool({ output: [{ text: 'new body' }] }) });
  await flushArtifacts();
  assert.match(root.textContent, /new body/);
  assert.doesNotMatch(root.textContent, /obsolete body|old body/);
  transcript.upsert(assistant('tool-1', 'replacement answer'));
  assert.equal(transcript.loads.size, 0);
  assert.equal(root.querySelector('.tool'), null);
  assert.match(root.textContent, /replacement answer/);
  transcript.dispose();
}));

test('turn load errors retry locally, and compact rows without callbacks fail safely instead of exposing header input as full detail', () => withDom(async root => {
  let attempts = 0;
  const transcript = new Transcript(root, { loadTurn: () => {
    attempts++;
    if (attempts === 1) throw new Error('offline');
    return { version: 'turn-v1', sequence: 3, rows: [] };
  } });
  transcript.rebuild([turn()]);
  setDisclosure(root, 'turn', true);
  assert.match(root.textContent, /Could not load details/);
  root.querySelector('.detail-retry').fire('click');
  await flushArtifacts();
  assert.match(root.textContent, /No process details/);
  assert.equal(attempts, 2);
  transcript.dispose();
  const legacy = new Transcript(root);
  legacy.rebuild([compactTool()]);
  setDisclosure(root, 'tool', true);
  assert.match(root.textContent, /Could not load details/);
  assert.doesNotMatch(root.textContent, /No output yet/);
  legacy.dispose();
}));

test('lightweight disclosure state restores before rebuild, including nested process tools, without retaining any payload', () => withDom(async root => {
  const full = tool({ output: [{ text: 'x'.repeat(2000) + 'sensitive output tail' }] });
  let turnLoads = 0, rowLoads = 0;
  const options = {
    loadTurn: () => { turnLoads++; return { version: 'turn-v1', rows: [compactTool(), assistant('reason', '', {
      parts: [{ $type: 'reasoning', text: 'sensitive reasoning payload' }],
    })] }; },
    loadRow: () => { rowLoads++; return { version: 'tool-v1', row: full }; },
  };
  const transcript = new Transcript(root, options);
  transcript.rebuild([turn()]);
  setDisclosure(root, 'turn', true);
  await flushArtifacts();
  const process = root.querySelector('.turn-body');
  setDisclosure(process, 'tool', true);
  setDisclosure(process, 'thinking', true);
  await flushArtifacts();
  process.querySelector('.out-more[data-section="output-0"]').fire('click');
  const state = transcript.exportState();
  assert.doesNotMatch(JSON.stringify(state), /sensitive|Visible|parts|input|artifactUri|detailVersion/);
  assert.deepEqual(state.rows[0].open, ['turn']);
  assert.ok(state.rows[0].details.rows.find(row => row.rowId === 'tool-1').all.includes('output-0'));
  setDisclosure(root, 'turn', false);
  const closedState = transcript.exportState();
  assert.equal(closedState.rows[0].open.length, 0);
  assert.equal(closedState.rows[0].details.rows.length, 2);
  assert.equal(process.childNodes.length, 0);
  transcript.dispose();
  const restored = new Transcript(root, options);
  restored.restoreState(state);
  assert.equal(root.childNodes.length, 0);
  restored.rebuild([turn()]);
  await flushArtifacts();
  assert.equal(turnLoads, 2);
  assert.equal(rowLoads, 2);
  assert.equal(root.querySelector('.turn-process').open, true);
  assert.equal(root.querySelector('.tool').open, true);
  assert.equal(root.querySelector('.thinking').open, true);
  assert.match(root.textContent, /sensitive output tail/);
  assert.equal(root.querySelector('.out-more[data-section="output-0"]'), null);
  restored.restoreState(closedState);
  assert.equal(root.querySelector('.turn-process').open, false);
  assert.equal(root.querySelector('.turn-body').childNodes.length, 0);
  assert.doesNotMatch(root.textContent, /sensitive/);
  restored.dispose();
}));

test('restoreState reapplies Show-all on live legacy rows and sanitizes unknown payload fields', () => withDom(async root => {
  const transcript = new Transcript(root);
  transcript.rebuild([tool({ output: [{ text: 'x'.repeat(2000) + 'restored tail' }] })]);
  transcript.restoreState({ rows: [{ rowId: 'tool-1', open: ['tool', 'unknown payload'], all: ['output-0', 'secret text'],
    text: 'do not retain', row: { output: 'do not retain' } }] });
  assert.equal(root.querySelector('.tool').open, true);
  assert.match(root.textContent, /restored tail/);
  assert.doesNotMatch(JSON.stringify(transcript.exportState()), /do not retain|secret|unknown/);
  transcript.restoreState({ rows: [{ rowId: 'tool-1', open: ['tool'], all: [] }] });
  assert.doesNotMatch(root.textContent, /restored tail/);
  assert.ok(root.querySelector('.out-more'));
  transcript.dispose();
}));

test('fetched detail caching is bounded by entry count and bytes, evicts older fetches, and clears on disposal', () => withDom(async root => {
  let calls = 0;
  const transcript = new Transcript(root, { loadRow: (id, { version }) => {
    calls++;
    return { version, row: tool({ rowId: id, output: [{ text: id === 'large' ? 'z'.repeat(600_000) : 'cached ' + id }] }) };
  } });
  const rows = [...Array.from({ length: 10 }, (_, i) => compactTool({ rowId: `tool-${i}` })), compactTool({ rowId: 'large' })];
  transcript.rebuild(rows);
  for (const row of rows) {
    const node = transcript.els.get(row.rowId);
    setDisclosure(node, 'tool', true);
    await flushArtifacts();
    setDisclosure(node, 'tool', false);
    assert.equal(transcript.loads.size, 0);
    assert.ok(transcript.cache.entries.size <= 8);
    assert.ok(transcript.cache.bytes <= 1024 * 1024);
    assert.equal(node.querySelector('.tool-body').childNodes.length, 0);
  }
  assert.equal(calls, 11);
  const recent = transcript.els.get('tool-9');
  setDisclosure(recent, 'tool', true);
  assert.equal(calls, 11);
  setDisclosure(recent, 'tool', false);
  setDisclosure(transcript.els.get('tool-0'), 'tool', true);
  await flushArtifacts();
  assert.equal(calls, 12);
  setDisclosure(transcript.els.get('large'), 'tool', true);
  await flushArtifacts();
  assert.equal(calls, 13);
  transcript.remove('tool-0');
  assert.equal([...transcript.cache.entries.values()].some(entry => entry.id === 'tool-0'), false);
  transcript.dispose();
  assert.equal(transcript.cache.bytes, 0);
  assert.equal(transcript.cache.entries.size, 0);
  assert.equal(transcript.loads.size, 0);
}));

test('parsed DOM streaming retires pending buffers on equivalent snapshots and never overwrites authoritative same-length final text', t => withDom(async root => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let now = 1000;
  t.mock.method(performance, 'now', () => now);
  const transcript = new Transcript(root);
  transcript.append(assistant('stream', 'start', { isStreaming: true }));
  transcript.appendText({ rowId: 'stream', delta: '!', at: 'first' });
  transcript.appendText({ rowId: 'stream', delta: '?', at: 'pending' });
  const visible = transcript.els.get('stream');
  const snapshot = assistant('stream', 'start!', { isStreaming: true, at: 'first' });
  transcript.rebuild([snapshot]);
  assert.equal(transcript.els.get('stream'), visible);
  assert.equal(transcript.timers.size, 0);
  assert.equal(transcript.textBuffers.size, 0);
  now += 500;
  t.mock.timers.tick(500);
  assert.equal(root.textContent, 'start!');
  transcript.appendText({ rowId: 'stream', delta: ' discarded', at: 'later' });
  transcript.upsert(assistant('stream', 'final!'));
  t.mock.timers.tick(500);
  assert.equal(root.textContent, 'final!');
  assert.equal(transcript.appendText({ rowId: 'stream', delta: 'invalid' }), false);
  transcript.dispose();
}));

test('async detail mutations notify viewport hooks before writes and after layout content changes', () => withDom(async root => {
  const pending = deferred();
  const events = [];
  const transcript = new Transcript(root, {
    onBeforeChange: () => events.push(['before', root.textContent]),
    onChange: () => events.push(['after', root.textContent]),
    loadTurn: () => pending.promise,
  });
  transcript.rebuild([turn()]);
  events.length = 0;
  setDisclosure(root, 'turn', true);
  assert.deepEqual(events.map(event => event[0]), ['before', 'after']);
  assert.doesNotMatch(events[0][1], /Loading details/);
  assert.match(events[1][1], /Loading details/);
  events.length = 0;
  pending.resolve({ version: 'turn-v1', rows: [assistant('process', 'asynchronous process')] });
  await flushArtifacts();
  assert.deepEqual(events.map(event => event[0]), ['before', 'after']);
  assert.match(events[0][1], /Loading details/);
  assert.doesNotMatch(events[0][1], /asynchronous process/);
  assert.match(events[1][1], /asynchronous process/);
  transcript.dispose();
}));

test('equivalent snapshots preserve thumbnail leases, changed rows preserve matching artifacts, and removal aborts hydration', t => withDom(async root => {
  const previous = globalThis.localStorage;
  globalThis.localStorage = { getItem: () => 'renderer-artifact-token' };
  t.after(() => { if (previous === undefined) delete globalThis.localStorage; else globalThis.localStorage = previous; });
  const requests = [];
  t.mock.method(globalThis, 'fetch', (_url, options) => new Promise((resolve, reject) => {
    requests.push({ options, resolve });
    options.signal.addEventListener('abort', () => reject(options.signal.reason), { once: true });
  }));
  let changes = 0;
  const row = { $type: 'user_message', rowId: 'image-user', text: 'original', images: [
    { artifactUri: 'alcoler-artifact://renderer/live-identity', mimeType: 'image/png', fileName: 'image.png' },
  ] };
  const transcript = new Transcript(root, { onChange: () => changes++ });
  transcript.rebuild([row]);
  await flushArtifacts();
  const thumb = root.querySelector('.thumb');
  assert.equal(requests.length, 1);
  transcript.rebuild([structuredClone(row)]);
  assert.equal(root.querySelector('.thumb'), thumb);
  assert.equal(requests[0].options.signal.aborted, false);
  transcript.upsert({ ...row, text: 'changed' });
  assert.equal(root.querySelector('.thumb'), thumb);
  assert.equal(requests.length, 1);
  transcript.remove(row.rowId);
  assert.equal(requests[0].options.signal.aborted, true);
  const stopped = changes;
  await flushArtifacts();
  assert.equal(changes, stopped);
  assert.equal(root.childNodes.length, 0);
  transcript.dispose();
}));

test('collapsing a process cancels nested tool requests and cannot refill its shared cache from late responses', () => withDom(async root => {
  const pending = deferred();
  let signal;
  const transcript = new Transcript(root, {
    loadTurn: () => ({ version: 'turn-v1', rows: [compactTool()] }),
    loadRow: (_id, options) => { signal = options.signal; return pending.promise; },
  });
  transcript.rebuild([turn()]);
  setDisclosure(root, 'turn', true);
  await flushArtifacts();
  setDisclosure(root.querySelector('.turn-body'), 'tool', true);
  const child = transcript.loads.get('user-1').child;
  setDisclosure(root, 'turn', false);
  assert.equal(signal.aborted, true);
  assert.equal(child.disposed, true);
  assert.equal(child.loads.size, 0);
  assert.equal(child.els.size, 0);
  pending.resolve({ version: 'tool-v1', row: tool({ output: [{ text: 'late nested secret' }] }) });
  await flushArtifacts();
  assert.doesNotMatch(root.textContent, /late nested secret/);
  assert.equal([...transcript.cache.entries.values()].some(entry => entry.id === 'tool-1'), false);
  transcript.dispose();
}));

test('disposing from a detail layout callback cannot retain the just-fetched payload', () => withDom(async root => {
  let transcript;
  transcript = new Transcript(root, {
    loadRow: () => ({ version: 'tool-v1', row: tool({ output: [{ text: 'close on load' }] }) }),
    onChange: () => { if (root.textContent.includes('close on load')) transcript.dispose(); },
  });
  transcript.rebuild([compactTool()]);
  setDisclosure(root, 'tool', true);
  await flushArtifacts();
  assert.equal(transcript.disposed, true);
  assert.equal(transcript.cache.entries.size, 0);
  assert.equal(transcript.loads.size, 0);
  assert.equal(root.childNodes.length, 0);
}));

test('malformed turn responses remain retryable and release partially built detail transcripts', () => withDom(async root => {
  const replies = [
    { version: 'turn-v1', rows: [null] },
    { version: 'turn-v1', rows: [assistant('broken', '', { parts: {} })] },
    { version: 'turn-v1', rows: [assistant('valid', 'valid process after retry')] },
  ];
  const transcript = new Transcript(root, { loadTurn: () => replies.shift() });
  transcript.rebuild([turn()]);
  setDisclosure(root, 'turn', true);
  await flushArtifacts();
  assert.ok(root.querySelector('.detail-retry'));
  root.querySelector('.detail-retry').fire('click');
  await flushArtifacts();
  assert.ok(root.querySelector('.detail-retry'));
  assert.equal(transcript.loads.get('user-1').child, null);
  assert.equal(transcript.cache.entries.size, 0);
  root.querySelector('.detail-retry').fire('click');
  await flushArtifacts();
  assert.match(root.textContent, /valid process after retry/);
  transcript.dispose();
}));

test('hasPendingDetails follows restored nested requests through success, failure, retry, changed versions, collapse, and disposal', () => withDom(async root => {
  const turns = [], tools = [], changes = [];
  let transcript;
  transcript = new Transcript(root, {
    loadTurn: (id, options) => { const pending = deferred(); turns.push({ id, options, ...pending }); return pending.promise; },
    loadRow: (id, options) => { const pending = deferred(); tools.push({ id, options, ...pending }); return pending.promise; },
    onChange: () => changes.push(transcript.hasPendingDetails()),
  });
  assert.equal(transcript.hasPendingDetails(), false);
  transcript.restoreState({ rows: [{ rowId: 'user-1', open: ['turn'], all: [], details: {
    rows: [{ rowId: 'tool-1', open: ['tool'], all: [] }],
  } }] });
  assert.equal(transcript.hasPendingDetails(), false);
  transcript.rebuild([turn()]);
  assert.equal(transcript.hasPendingDetails(), true);
  assert.equal(changes.at(-1), true);
  turns[0].resolve({ version: 'turn-v1', rows: [compactTool()] });
  await flushArtifacts();
  const firstChild = transcript.loads.get('user-1').child;
  assert.equal(transcript.loads.get('user-1').loading, false);
  assert.equal(firstChild.hasPendingDetails(), true);
  assert.equal(transcript.hasPendingDetails(), true);
  tools[0].reject(new Error('offline'));
  await flushArtifacts();
  assert.equal(firstChild.loads.size, 1);
  assert.equal(firstChild.hasPendingDetails(), false);
  assert.equal(transcript.hasPendingDetails(), false);
  assert.equal(changes.at(-1), false);
  root.querySelector('.detail-retry').fire('click');
  assert.equal(firstChild.hasPendingDetails(), true);
  assert.equal(transcript.hasPendingDetails(), true);
  tools[1].resolve({ version: 'tool-v1', row: tool() });
  await flushArtifacts();
  assert.equal(firstChild.hasPendingDetails(), false);
  assert.equal(transcript.hasPendingDetails(), false);
  assert.equal(changes.at(-1), false);
  transcript.rebuild([turn({ detailVersion: 'turn-v2' })]);
  assert.equal(firstChild.disposed, true);
  assert.equal(firstChild.hasPendingDetails(), false);
  assert.equal(transcript.hasPendingDetails(), true);
  turns[1].resolve({ version: 'turn-v2', rows: [compactTool({ detailVersion: 'tool-v2' })] });
  await flushArtifacts();
  const secondChild = transcript.loads.get('user-1').child;
  assert.equal(secondChild.hasPendingDetails(), true);
  assert.equal(transcript.hasPendingDetails(), true);
  setDisclosure(root, 'turn', false);
  assert.equal(tools[2].options.signal.aborted, true);
  assert.equal(secondChild.hasPendingDetails(), false);
  assert.equal(transcript.hasPendingDetails(), false);
  tools[2].resolve({ version: 'tool-v2', row: tool({ output: [{ text: 'late ignored details' }] }) });
  await flushArtifacts();
  assert.equal(transcript.hasPendingDetails(), false);
  assert.doesNotMatch(root.textContent, /late ignored details/);
  setDisclosure(root, 'turn', true);
  assert.equal(turns.length, 2);
  assert.equal(tools.length, 4);
  assert.equal(transcript.hasPendingDetails(), true);
  transcript.dispose();
  assert.equal(tools[3].options.signal.aborted, true);
  assert.equal(transcript.hasPendingDetails(), false);
  tools[3].reject(new DOMException('Aborted', 'AbortError'));
  await flushArtifacts();
  assert.equal(transcript.hasPendingDetails(), false);
  assert.equal(root.childNodes.length, 0);
}));

test('history pages preserve input order, existing DOM and disclosures, and ignore duplicate row IDs', () => withDom(async root => {
  const ids = () => root.childNodes.map(node => node.dataset.rowId);
  const events = [];
  const transcript = new Transcript(root, {
    onBeforeChange: () => events.push(['before', ids()]), onChange: () => events.push(['after', ids()]),
  });
  assert.equal(transcript.supportsHistory, true);
  transcript.rebuild([turn(), tool({ output: [{ text: 'x'.repeat(2000) + ' retained full tail' }] }),
    assistant('live', 'Current answer', { parts: [
      { $type: 'reasoning', text: 'Current reasoning' }, { $type: 'text', text: 'Current answer' },
    ] })]);
  const existing = [...root.childNodes];
  const detail = setDisclosure(transcript.els.get('tool-1'), 'tool', true);
  const body = detail.querySelector('.tool-body');
  body.querySelector('.out-more[data-section="output-0"]').fire('click');
  const thinking = setDisclosure(transcript.els.get('live'), 'thinking', true);
  const saved = transcript.exportState();
  events.length = 0;
  transcript.prepend([turn({ rowId: 'older-1' }), turn({ rowId: 'older-2' }),
    assistant('older-1', 'stale duplicate'), null, assistant('live', 'stale answer')]);
  assert.deepEqual(ids(), ['older-1', 'older-2', 'user-1', 'tool-1', 'live']);
  assert.deepEqual(events, [['before', ['user-1', 'tool-1', 'live']], ['after', ids()]]);
  events.length = 0;
  transcript.appendHistory([turn({ rowId: 'newer-1' }), tool({ output: [{ text: 'stale output' }] }),
    turn({ rowId: 'newer-2' }), turn({ rowId: 'newer-1', detailVersion: 'stale-version' })]);
  assert.deepEqual(ids(), ['older-1', 'older-2', 'user-1', 'tool-1', 'live', 'newer-1', 'newer-2']);
  assert.deepEqual(events, [['before', ['older-1', 'older-2', 'user-1', 'tool-1', 'live']], ['after', ids()]]);
  assert.deepEqual([...transcript.els.keys()], ids());
  existing.forEach((node, index) => assert.equal(root.childNodes[index + 2], node));
  assert.equal(transcript.els.get('tool-1').querySelector('.tool-body'), body);
  assert.equal(detail.open, true);
  assert.equal(thinking.open, true);
  assert.match(body.textContent, /retained full tail/);
  assert.equal(body.querySelector('.out-more[data-section="output-0"]'), null);
  assert.doesNotMatch(root.textContent, /stale/);
  assert.deepEqual(transcript.exportState(), saved);
  const writes = root.writes;
  events.length = 0;
  transcript.prepend([assistant('live', 'stale'), turn({ rowId: 'older-1' })]);
  transcript.appendHistory([tool(), turn({ rowId: 'newer-1' })]);
  transcript.prepend(null);
  transcript.appendHistory([]);
  assert.equal(root.writes, writes);
  assert.deepEqual(events, []);
  transcript.dispose();
  transcript.prepend([turn()]);
  transcript.appendHistory([turn()]);
  assert.deepEqual(events, []);
  assert.equal(root.childNodes.length, 0);
}));

test('prepending into an empty transcript retains row order and activates later appended rows normally', () => withDom(async root => {
  const transcript = new Transcript(root);
  transcript.prepend([turn({ rowId: 'first' }), turn({ rowId: 'second' })]);
  transcript.appendHistory([turn({ rowId: 'third' }), turn({ rowId: 'fourth' })]);
  assert.deepEqual(root.childNodes.map(node => node.dataset.rowId), ['first', 'second', 'third', 'fourth']);
  assert.deepEqual([...transcript.els.keys()], ['first', 'second', 'third', 'fourth']);
  transcript.dispose();
}));

test('history insertion retains pending live text chunks and active tool timers instead of replaying stale page rows', t => withDom(async root => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let now = 1000;
  t.mock.method(performance, 'now', () => now);
  const transcript = new Transcript(root);
  const stream = assistant('stream', '', { isStreaming: true, parts: [
    { $type: 'reasoning', text: 'Keep reasoning open' }, { $type: 'text', text: 'start' },
  ] });
  const active = tool({ rowId: 'active', status: 'Running', input: { description: 'initial tool' } });
  transcript.rebuild([turn(), active, stream]);
  transcript.appendText({ rowId: 'stream', delta: ' shown', at: 'shown' });
  transcript.appendText({ rowId: 'stream', delta: ' buffered', at: 'buffered' });
  transcript.upsert({ ...active, input: { description: 'shown tool' } });
  transcript.upsert({ ...active, input: { description: 'buffered tool' } });
  const liveNode = transcript.els.get('stream'), toolNode = transcript.els.get('active');
  const thinking = setDisclosure(liveNode, 'thinking', true);
  const buffer = transcript.textBuffers.get('stream');
  const textTimer = transcript.timers.get('stream'), toolTimer = transcript.timers.get('active');
  const staleStream = assistant('stream', 'stale sealed answer');
  transcript.prepend([turn({ rowId: 'older' }), staleStream, tool({ rowId: 'active', status: 'Failed' })]);
  transcript.appendHistory([staleStream, turn({ rowId: 'newer' })]);
  assert.equal(transcript.els.get('stream'), liveNode);
  assert.equal(transcript.els.get('active'), toolNode);
  assert.equal(transcript.textBuffers.get('stream'), buffer);
  assert.deepEqual(buffer.chunks, [' buffered']);
  assert.equal(transcript.timers.get('stream'), textTimer);
  assert.equal(transcript.timers.get('active'), toolTimer);
  assert.ok(textTimer.timer);
  assert.ok(toolTimer.timer);
  assert.equal(thinking.open, true);
  assert.doesNotMatch(root.textContent, /stale sealed|buffered tool|start shown buffered/);
  now += 150;
  t.mock.timers.tick(150);
  assert.match(root.textContent, /start shown buffered/);
  assert.match(root.textContent, /buffered tool/);
  assert.equal(transcript.els.get('stream').querySelector('.thinking').open, true);
  assert.equal(buffer.row.at, 'buffered');
  assert.equal(buffer.chunks.length, 0);
  transcript.dispose();
}));

test('history insertion keeps pending turn detail requests attached to the same disclosure and DOM', () => withDom(async root => {
  const pending = deferred();
  let calls = 0;
  const transcript = new Transcript(root, { loadTurn: () => { calls++; return pending.promise; } });
  transcript.rebuild([turn()]);
  const node = transcript.els.get('user-1');
  const detail = setDisclosure(node, 'turn', true);
  const body = detail.querySelector('.turn-body');
  const entry = transcript.loads.get('user-1');
  transcript.prepend([turn({ rowId: 'older' }), turn({ detailVersion: 'stale-version' })]);
  transcript.appendHistory([turn({ rowId: 'newer' }), turn({ detailVersion: 'another-stale-version' })]);
  assert.equal(transcript.els.get('user-1'), node);
  assert.equal(node.querySelector('.turn-process'), detail);
  assert.equal(detail.querySelector('.turn-body'), body);
  assert.equal(transcript.loads.get('user-1'), entry);
  assert.equal(entry.controller.signal.aborted, false);
  assert.equal(transcript.hasPendingDetails(), true);
  assert.equal(calls, 1);
  pending.resolve({ version: 'turn-v1', rows: [assistant('process', 'original request completed')] });
  await flushArtifacts();
  assert.match(body.textContent, /original request completed/);
  assert.equal(transcript.hasPendingDetails(), false);
  assert.equal(detail.open, true);
  transcript.dispose();
}));

test('history pages retain nested loaded details, cached payloads, playing media, and thumbnail leases', t => withDom(async root => {
  const previous = globalThis.localStorage;
  globalThis.localStorage = { getItem: () => 'history-artifact-token' };
  t.after(() => { if (previous === undefined) delete globalThis.localStorage; else globalThis.localStorage = previous; });
  const requests = [];
  t.mock.method(globalThis, 'fetch', (_url, options) => new Promise((resolve, reject) => {
    requests.push({ options });
    options.signal.addEventListener('abort', () => reject(options.signal.reason), { once: true });
  }));
  let turnLoads = 0, toolLoads = 0;
  const transcript = new Transcript(root, {
    loadTurn: () => { turnLoads++; return { version: 'turn-v1', rows: [compactTool()] }; },
    loadRow: () => { toolLoads++; return { version: 'tool-v1', row: tool({ output: [
      { text: 'x'.repeat(2000) + ' cached full tail' },
      { artifactUri: 'artifact:history-audio', mimeType: 'audio/wav', fileName: 'audio.wav' },
      { artifactUri: 'alcoler-artifact://history/thumbnail-lease', mimeType: 'image/png', fileName: 'image.png' },
    ] }) }; },
  });
  transcript.rebuild([turn()]);
  const node = transcript.els.get('user-1');
  setDisclosure(node, 'turn', true);
  await flushArtifacts();
  const turnEntry = transcript.loads.get('user-1'), child = turnEntry.child;
  setDisclosure(turnEntry.body, 'tool', true);
  await flushArtifacts();
  const toolEntry = child.loads.get('tool-1'), body = toolEntry.body;
  body.querySelector('.out-more[data-section="output-0"]').fire('click');
  const audio = body.querySelector('audio'), thumb = body.querySelector('.thumb');
  audio.src = '/playing-history-audio';
  audio.currentTime = 42;
  const cacheEntries = [...transcript.cache.entries];
  const cacheBytes = transcript.cache.bytes;
  assert.equal(cacheEntries.length, 2);
  assert.equal(requests.length, 1);
  transcript.prepend([turn({ rowId: 'older' }), turn({ detailVersion: 'stale-version' })]);
  transcript.appendHistory([turn({ rowId: 'newer' }), turn({ detailVersion: 'stale-version' })]);
  assert.equal(transcript.els.get('user-1'), node);
  assert.equal(transcript.loads.get('user-1'), turnEntry);
  assert.equal(turnEntry.child, child);
  assert.equal(child.loads.get('tool-1'), toolEntry);
  assert.equal(toolEntry.body, body);
  assert.equal(body.querySelector('audio'), audio);
  assert.equal(body.querySelector('.thumb'), thumb);
  assert.equal(audio.src, '/playing-history-audio');
  assert.equal(audio.currentTime, 42);
  assert.equal(audio.pauses, undefined);
  assert.equal(turnEntry.controller.signal.aborted, false);
  assert.equal(toolEntry.controller.signal.aborted, false);
  assert.equal(requests[0].options.signal.aborted, false);
  assert.equal(requests.length, 1);
  assert.equal(turnLoads, 1);
  assert.equal(toolLoads, 1);
  assert.deepEqual([...transcript.cache.entries], cacheEntries);
  assert.equal(transcript.cache.bytes, cacheBytes);
  assert.match(body.textContent, /cached full tail/);
  assert.equal(body.querySelector('.out-more[data-section="output-0"]'), null);
  transcript.dispose();
  assert.equal(requests[0].options.signal.aborted, true);
  assert.equal(audio.pauses, 1);
  assert.equal(audio.src, null);
  await flushArtifacts();
}));

test('off-window saved disclosures survive initial rebuild and activate with nested state at either history edge', () => withDom(async root => {
  const saved = { rows: ['older', 'newer', 'unloaded'].map(rowId => ({ rowId, open: ['turn'], all: [], details: {
    rows: [{ rowId: rowId + '-tool', open: ['tool'], all: ['output-0'] },
      { rowId: rowId + '-reason', open: ['thinking'], all: [] },
      { rowId: rowId + '-hidden', open: ['tool'], all: ['input'] }],
  } })) };
  const turns = [], tools = [];
  const transcript = new Transcript(root, {
    loadTurn: (id, { version }) => { turns.push(id); return { version, rows: [compactTool({ rowId: id + '-tool' }),
      assistant(id + '-reason', '', { parts: [{ $type: 'reasoning', text: 'Restored reasoning ' + id }] })] }; },
    loadRow: (id, { version }) => { tools.push(id); return { version, row: tool({ rowId: id,
      output: [{ text: 'x'.repeat(2000) + ' restored full tail ' + id }] }) }; },
  });
  transcript.restoreState(saved);
  transcript.rebuild([turn({ rowId: 'current' })], { retainState: true });
  transcript.rebuild([turn({ rowId: 'current' })], { retainState: true });
  assert.deepEqual(transcript.exportState(), saved);
  assert.deepEqual(turns, []);
  assert.deepEqual(tools, []);
  transcript.prepend([turn({ rowId: 'older' })]);
  await flushArtifacts();
  transcript.appendHistory([turn({ rowId: 'newer' })]);
  await flushArtifacts();
  assert.deepEqual(turns, ['older', 'newer']);
  assert.deepEqual(tools, ['older-tool', 'newer-tool']);
  for (const id of ['older', 'newer']) {
    const node = transcript.els.get(id);
    assert.equal(node.querySelector('.turn-process').open, true);
    assert.equal(node.querySelector('.tool').open, true);
    assert.equal(node.querySelector('.thinking').open, true);
    assert.match(node.textContent, new RegExp('restored full tail ' + id + '-tool'));
    assert.equal(node.querySelector('.out-more[data-section="output-0"]'), null);
    assert.equal(transcript.loads.get(id).child.exportState().rows.some(row => row.rowId === id + '-hidden'), false);
  }
  const exported = transcript.exportState();
  assert.deepEqual(exported.rows.find(row => row.rowId === 'unloaded'), saved.rows[2]);
  assert.doesNotMatch(JSON.stringify(exported), /restored full tail|Restored reasoning|detailVersion|parts/);
  transcript.dispose();
}));

test('off-window saved state retains its existing row bound and never needs transcript payloads', () => withDom(async root => {
  const transcript = new Transcript(root);
  transcript.restoreState({ rows: Array.from({ length: 2050 }, (_, index) => ({ rowId: 'saved-' + index,
    open: ['turn'], all: [], parts: [{ text: 'must not retain history payload' }] })) });
  transcript.rebuild([turn({ rowId: 'current' })], { retainState: true });
  const saved = transcript.exportState();
  assert.equal(saved.rows.length, 2000);
  assert.equal(saved.rows[0].rowId, 'saved-0');
  assert.equal(saved.rows.at(-1).rowId, 'saved-1999');
  assert.doesNotMatch(JSON.stringify(saved), /payload|parts/);
  transcript.dispose();
}));

test('paged rebuilds retain nested disclosures across latest and page-back without retaining old payloads or media', t => withDom(async root => {
  const previous = globalThis.localStorage;
  globalThis.localStorage = { getItem: () => 'history-roundtrip-token' };
  t.after(() => { if (previous === undefined) delete globalThis.localStorage; else globalThis.localStorage = previous; });
  const requests = [];
  t.mock.method(globalThis, 'fetch', (_url, options) => new Promise((resolve, reject) => {
    requests.push({ options });
    options.signal.addEventListener('abort', () => reject(options.signal.reason), { once: true });
  }));
  let turnLoads = 0, toolLoads = 0;
  const transcript = new Transcript(root, {
    loadTurn: () => { turnLoads++; return { version: 'turn-v1', rows: [compactTool(),
      assistant('reason', '', { parts: [{ $type: 'reasoning', text: 'Sensitive nested reasoning' }] })] }; },
    loadRow: () => { toolLoads++; return { version: 'tool-v1', row: tool({ output: [
      { text: 'x'.repeat(2000) + ' sensitive restored full tail' },
      { artifactUri: 'artifact:history-roundtrip-audio', mimeType: 'audio/wav', fileName: 'audio.wav' },
      { artifactUri: 'alcoler-artifact://history/roundtrip-thumbnail', mimeType: 'image/png', fileName: 'image.png' },
    ] }) }; },
  });
  const historical = turn({ rowId: 'historical' }), latest = turn({ rowId: 'latest' });
  transcript.rebuild([historical], { retainState: true });
  const oldNode = transcript.els.get('historical');
  setDisclosure(oldNode, 'turn', true);
  await flushArtifacts();
  const entry = transcript.loads.get('historical'), child = entry.child;
  setDisclosure(entry.body, 'tool', true);
  setDisclosure(entry.body, 'thinking', true);
  await flushArtifacts();
  const toolEntry = child.loads.get('tool-1');
  toolEntry.body.querySelector('.out-more[data-section="output-0"]').fire('click');
  const audio = toolEntry.body.querySelector('audio');
  audio.src = '/playing-history-roundtrip';
  audio.currentTime = 19;
  transcript.state('historical').open.add('unknown-disclosure');
  child.state('tool-1').all.add('unknown-section');
  const saved = transcript.exportState();
  assert.equal(transcript.cache.entries.size, 2);
  transcript.rebuild([structuredClone(historical)], { retainState: true });
  assert.equal(transcript.els.get('historical'), oldNode);
  assert.equal(transcript.loads.get('historical'), entry);
  assert.equal(transcript.cache.entries.size, 2);
  transcript.rebuild([latest], { retainState: true });
  await flushArtifacts();
  assert.deepEqual(root.childNodes.map(node => node.dataset.rowId), ['latest']);
  assert.equal(oldNode.isConnected, false);
  assert.equal(child.disposed, true);
  assert.equal(child.els.size, 0);
  assert.equal(child.loads.size, 0);
  assert.equal(entry.value, null);
  assert.equal(entry.child, null);
  assert.equal(toolEntry.value, null);
  assert.equal(entry.controller.signal.aborted, true);
  assert.equal(toolEntry.controller.signal.aborted, true);
  assert.equal(requests[0].options.signal.aborted, true);
  assert.equal(audio.src, null);
  assert.equal(audio.pauses, 1);
  assert.equal(transcript.loads.size, 0);
  assert.equal(transcript.cache.entries.size, 0);
  assert.equal(transcript.cache.bytes, 0);
  assert.deepEqual(transcript.exportState(), saved);
  assert.equal(transcript.states.get('historical').open.has('unknown-disclosure'), false);
  assert.doesNotMatch(JSON.stringify(transcript.turnStates.get('historical')), /sensitive|Sensitive|unknown|output"|parts|artifactUri|detailVersion/);
  transcript.rebuild([structuredClone(latest)], { retainState: true });
  transcript.prepend([historical]);
  await flushArtifacts();
  const restored = transcript.els.get('historical');
  assert.notEqual(restored, oldNode);
  assert.equal(restored.querySelector('.turn-process').open, true);
  assert.equal(restored.querySelector('.tool').open, true);
  assert.equal(restored.querySelector('.thinking').open, true);
  assert.match(restored.textContent, /sensitive restored full tail/);
  assert.equal(restored.querySelector('.out-more[data-section="output-0"]'), null);
  assert.equal(turnLoads, 2);
  assert.equal(toolLoads, 2);
  assert.equal(requests.length, 2);
  assert.equal(requests[1].options.signal.aborted, false);
  transcript.rebuild([latest], { retainState: true });
  transcript.remove('historical');
  assert.equal(transcript.states.has('historical'), false);
  assert.equal(transcript.turnStates.has('historical'), false);
  transcript.prepend([historical]);
  assert.equal(transcript.els.get('historical').querySelector('.turn-process').open, false);
  assert.equal(turnLoads, 2);
  assert.equal(toolLoads, 2);
  transcript.dispose();
  await flushArtifacts();
}));

test('paged rebuilds bound retained invisible disclosure metadata and sanitize nested state', () => withElements(() => {
  const transcript = new Transcript(container());
  const rows = Array.from({ length: 2050 }, (_, index) => ({ $type: 'system_notice', rowId: 'loaded-' + index, text: 'row' }));
  transcript.rebuild(rows);
  for (const row of rows) {
    transcript.state(row.rowId).open.add('turn');
    transcript.state(row.rowId).open.add('unknown-disclosure');
    transcript.turnStates.set(row.rowId, { rows: [{ rowId: row.rowId + '-tool', open: ['tool', 'unknown-disclosure'],
      all: ['output-0', 'unknown-section'], output: 'must not retain detail payload' }] });
  }
  for (let index = 0; index < 3; index++) {
    const current = { $type: 'system_notice', rowId: 'current-' + index, text: 'current' };
    transcript.rebuild([current], { retainState: true });
    assert.equal([...transcript.states.keys()].filter(id => !transcript.els.has(id)).length, 2000);
    assert.ok(transcript.turnStates.size <= 2000);
    assert.equal(transcript.els.size, 1);
    assert.equal(transcript.exportState().rows.length, 2000);
    assert.doesNotMatch(JSON.stringify([...transcript.turnStates]), /payload|unknown|"output"/);
    for (const [id, state] of transcript.states) {
      if (transcript.els.has(id)) continue;
      assert.equal(state.open.has('unknown-disclosure'), false);
    }
    transcript.state(current.rowId).open.add('thinking');
  }
  transcript.dispose();
}));

test('legacy complete rebuilds prune departing and unrelated restored state while explicit removals stay destructive', () => withDom(async root => {
  const transcript = new Transcript(root);
  const loaded = assistant('loaded', '', { parts: [{ $type: 'reasoning', text: 'Reasoning' }] });
  transcript.restoreState({ rows: [
    { rowId: 'loaded', open: ['thinking'], all: [] },
    { rowId: 'unloaded', open: ['turn'], all: [], details: {
      rows: [{ rowId: 'nested-tool', open: ['tool'], all: ['output-0'] }],
    } },
  ] });
  transcript.rebuild([loaded], { retainState: true });
  assert.equal(root.querySelector('.thinking').open, true);
  assert.equal(transcript.states.has('unloaded'), true);
  assert.equal(transcript.turnStates.has('unloaded'), true);
  transcript.rebuild([assistant('latest', 'Latest')]);
  assert.equal(transcript.states.has('loaded'), false);
  assert.equal(transcript.states.has('unloaded'), false);
  assert.equal(transcript.turnStates.size, 0);
  transcript.prepend([loaded]);
  setDisclosure(transcript.els.get('loaded'), 'thinking', true);
  transcript.rebuild([assistant('latest', 'Latest')], { retainState: true });
  assert.equal(transcript.states.has('loaded'), true);
  transcript.remove('loaded');
  assert.equal(transcript.states.has('loaded'), false);
  transcript.prepend([loaded]);
  assert.equal(transcript.els.get('loaded').querySelector('.thinking').open, false);
  setDisclosure(transcript.els.get('loaded'), 'thinking', true);
  transcript.remove('loaded');
  assert.equal(transcript.states.has('loaded'), false);
  assert.equal(transcript.turnStates.has('loaded'), false);
  transcript.dispose();
}));
