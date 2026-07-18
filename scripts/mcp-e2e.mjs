// Minimal stateful MCP stdio client for E2E checks of Mcp.Engineering.exe.
// Usage: node scripts/mcp-e2e.mjs <server-exe> <steps.json>
// steps.json: [{ "tool": "name", "args": {...}, "timeout": ms? }, ...]
import { spawn } from 'node:child_process';
import readline from 'node:readline';
import { readFileSync } from 'node:fs';

const [, , exe, stepsFile] = process.argv;
if (!exe || !stepsFile) {
  console.error('usage: node scripts/mcp-e2e.mjs <server-exe> <steps.json>');
  process.exit(2);
}
const steps = JSON.parse(readFileSync(stepsFile, 'utf8'));

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'inherit'] });
const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
let failed = false;

rl.on('line', (line) => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; } // not JSON-RPC (should not happen on stdout)
  if (msg.id !== undefined && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
});

function request(method, params, timeoutMs = 120000) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        reject(new Error(`timeout after ${timeoutMs}ms: ${method}`));
      }
    }, timeoutMs);
  });
}

try {
  const init = await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'mcp-e2e', version: '0.1.0' },
  });
  console.log('initialize:', JSON.stringify(init.result?.serverInfo ?? init));
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

  for (const step of steps) {
    if (step.argsFrom === 'firstSession') {
      // Resolve a live session id dynamically (attached-mode testing).
      const probe = await request('tools/call', { name: 'list_sessions', arguments: {} }, step.timeout);
      const sessions = JSON.parse(probe.result.content[0].text);
      const match = step.projectPathContains
        ? sessions.find((s) => s.projectPath?.includes(step.projectPathContains))
        : sessions[0];
      if (!match) throw new Error(`argsFrom=firstSession: no matching session (found ${sessions.length})`);
      step.args = { ...(step.args ?? {}), sessionId: match.id };
      console.log(`resolved sessionId=${match.id} (${match.projectPath ?? 'no project'})`);
    }
    const res = await request('tools/call', { name: step.tool, arguments: step.args ?? {} }, step.timeout);
    const isError = res.result?.isError === true;
    if (isError) failed = true;
    const text = res.result?.content?.map((c) => c.text).join('\n') ?? JSON.stringify(res);
    console.log(`--- ${step.tool}${isError ? ' (ERROR)' : ''}:\n${text}`);
    if (isError && step.stopOnError !== false) {
      console.log('stopping sequence on error');
      break;
    }
  }
} catch (err) {
  failed = true;
  console.error('FATAL:', err.message);
} finally {
  child.stdin.end();
}

const exitCode = await new Promise((resolve) => {
  const kill = setTimeout(() => { child.kill(); resolve('killed'); }, 30000);
  child.on('exit', (code) => { clearTimeout(kill); resolve(code); });
});
console.log('server exit code:', exitCode);
process.exit(failed || exitCode !== 0 ? 1 : 0);
