import assert from 'node:assert/strict';
import { test } from 'node:test';
import { prepareSubmission } from '../../../src/Alcoler.Remote/wwwroot/submission.js';

const command = (kind, prompt = '') => ({ name: kind, kind, prompt });

test('ordinary chat and skills preserve slash text, Unicode arguments, and inherited intent', () => {
  for (const text of ['ordinary chat', '/report 比较 A 与 B\nkeep "quoted arguments"', 'Before /plan After', '/planning']) {
    assert.deepEqual(prepareSubmission(text, null), {
      text, action: 'send', planEnabled: false, workflows: null, workflowsChanged: false,
    });
  }
});

test('bare plan only enables the local overlay, including when it is already enabled', () => {
  for (const planEnabled of [false, true]) {
    assert.deepEqual(prepareSubmission('/PLAN', command('plan'), { planEnabled }), {
      text: '', action: 'local', planEnabled: true, workflows: null, workflowsChanged: false,
    });
  }
});

test('plan with a prompt sends the stripped prompt and retains unrelated workflows', () => {
  const workflows = ['review'];
  assert.deepEqual(prepareSubmission('/plan design this', command('plan', 'design this'), { workflows }), {
    text: 'design this', action: 'send', planEnabled: true, workflows, workflowsChanged: false,
  });
  assert.deepEqual(workflows, ['review']);
});

test('a bare orchestrator toggles case-insensitively and preserves other workflows', () => {
  const original = ['review', 'ORCHESTRATOR', 'accept'];
  assert.deepEqual(prepareSubmission('/orchestrator', command('orchestrator'), { workflows: original }), {
    text: '', action: 'workflows', planEnabled: false, workflows: ['review', 'accept'], workflowsChanged: true,
  });
  assert.deepEqual(original, ['review', 'ORCHESTRATOR', 'accept']);
  assert.deepEqual(prepareSubmission('/orchestrator', command('orchestrator'), { workflows: ['review'] }).workflows,
    ['review', 'orchestrator']);
});

test('orchestrator with a prompt arms without toggling an already active workflow off', () => {
  for (const workflows of [null, [], ['review'], ['review', 'Orchestrator']]) {
    const result = prepareSubmission('/orchestrator fix this', command('orchestrator', 'fix this'), {
      planEnabled: true, workflows,
    });
    assert.equal(result.action, 'send');
    assert.equal(result.text, 'fix this');
    assert.equal(result.planEnabled, true);
    assert.deepEqual(result.workflows, workflows?.includes('Orchestrator')
      ? workflows : [...(workflows || []), 'orchestrator']);
    assert.equal(result.workflowsChanged, !workflows?.includes('Orchestrator'));
  }
});

test('built-ins cannot queue into an active turn, while skills remain ordinary queueable text', () => {
  for (const kind of ['plan', 'orchestrator']) {
    for (const prompt of ['', 'do work']) {
      assert.throws(() => prepareSubmission(`/${kind}`, command(kind, prompt), { busy: true }),
        /Stop the current turn/);
    }
  }
  const text = '/skill-writer 中文 arguments';
  assert.equal(prepareSubmission(text, null, { busy: true }).text, text);
});

test('a second slash command in a builtin prompt is not recursively interpreted', () => {
  const result = prepareSubmission('/plan /orchestrator task', command('plan', '/orchestrator task'));
  assert.equal(result.text, '/orchestrator task');
  assert.equal(result.planEnabled, true);
  assert.equal(result.workflowsChanged, false);
});

test('prepared execution intent leaves permission, model, images, and queue delivery with the caller', () => {
  const original = { mode: 'Edit', planEnabled: false, modelSelection: { providerId: 'p', modelId: 'm' },
    images: [{ mimeType: 'image/png', base64Data: 'aW1hZ2U=' }], workflows: ['review'], delivery: 'Guide' };
  const result = prepareSubmission('/orchestrator run', command('orchestrator', 'run'), original);
  const sent = { ...original, text: result.text, planEnabled: result.planEnabled, workflows: result.workflows };
  assert.equal(sent.mode, 'Edit');
  assert.equal(sent.delivery, 'Guide');
  assert.equal(sent.modelSelection, original.modelSelection);
  assert.equal(sent.images, original.images);
  assert.deepEqual(sent.workflows, ['review', 'orchestrator']);
  assert.deepEqual(original.workflows, ['review']);
});
