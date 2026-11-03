export function prepareSubmission(text, command, { planEnabled = false, workflows = null, busy = false } = {}) {
  const result = { text, action: 'send', planEnabled, workflows, workflowsChanged: false };
  if (!command) return result;
  if (busy) throw new Error('Stop the current turn before using a built-in command.');

  result.text = command.prompt;
  if (command.kind === 'plan') {
    result.planEnabled = true;
    if (!command.prompt) result.action = 'local';
  } else if (command.kind === 'orchestrator') {
    const active = Array.isArray(workflows) ? workflows : [];
    const contains = active.some(id => id.toLowerCase() === 'orchestrator');
    if (!command.prompt) {
      result.action = 'workflows';
      result.workflows = contains
        ? active.filter(id => id.toLowerCase() !== 'orchestrator')
        : [...active, 'orchestrator'];
      result.workflowsChanged = true;
    } else if (!contains) {
      result.workflows = [...active, 'orchestrator'];
      result.workflowsChanged = true;
    }
  }
  return result;
}
