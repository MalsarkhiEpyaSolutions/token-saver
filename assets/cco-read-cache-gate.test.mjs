// Self-check for the read-cache agent-context gate.
//
// Regression guard for the Windows bug: the gate used process.ppid, but every
// hook invocation is spawned in a fresh shell, so the ppid differed on every
// Read and NO redundant read was ever blocked (measured live: 54 sessions,
// 112 redundant reads, ~470K tokens, 0 blocks).
//
// Each spawn below is a separate process — exactly the real condition. Two
// identical Reads with the same transcript_path must block the second one.
//
//   node assets/cco-read-cache-gate.test.mjs

import { spawnSync } from 'child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';
import { randomUUID } from 'crypto';
import assert from 'assert';

const HOOK = join(import.meta.dirname, 'cco', 'src', 'read-cache.js');

function read(sessionId, transcript, filePath) {
  const event = {
    hook_event_name: 'PreToolUse',
    tool_name: 'Read',
    session_id: sessionId,
    transcript_path: transcript,
    tool_input: { file_path: filePath, offset: 0, limit: 100 },
  };
  // shell:true is load-bearing, not incidental: it puts an intermediate cmd.exe
  // between us and node, so process.ppid differs on every call — exactly how a
  // hook runner invokes this. Spawning node directly would hide the bug, since
  // then the ppid stays constant and even the broken gate appears to work.
  const r = spawnSync(`"${process.execPath}" "${HOOK}"`, {
    input: JSON.stringify(event), encoding: 'utf8', shell: true,
  });
  try { return JSON.parse(r.stdout || '{}'); } catch { return {}; }
}

const dir = mkdtempSync(join(tmpdir(), 'cco-gate-'));
const file = join(dir, 'sample.js');
writeFileSync(file, 'export function hello(){ return 1; }\n');

try {
  // Same agent context (one transcript), two separate processes.
  const session = randomUUID();
  const transcript = join(dir, `${session}.jsonl`);

  const first = read(session, transcript, file);
  assert.notStrictEqual(first.decision, 'block', 'first read must be allowed');

  const second = read(session, transcript, file);
  assert.strictEqual(second.decision, 'block',
    'a redundant read in the SAME agent context must be blocked — ' +
    'if this fails the gate is keyed on something unstable again');

  // A different agent context (subagent) must still get its own copy.
  const subagent = read(session, join(dir, 'subagent.jsonl'), file);
  assert.notStrictEqual(subagent.decision, 'block',
    'a different agent context must be allowed — it has its own window');

  console.log('read-cache gate: PASS (redundant blocked, subagent allowed)');
} finally {
  rmSync(dir, { recursive: true, force: true });
}
