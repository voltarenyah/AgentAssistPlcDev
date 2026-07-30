// Parses the plain-text progress narration emitted by the agent loop
// (src/Agent/Chat/AgentLoop.cs) into display-friendly entries:
//   round N: calling model
//   usage: X prompt + Y completion (Z reasoning) tokens
//   → toolName(args…)
//     ✗/⛔ toolName: error…

export type ProgressEntry =
  | { kind: 'note'; text: string }
  | { kind: 'tool-call'; name: string; args: string }
  | { kind: 'tool-error'; name: string; message: string; denied: boolean }

const toolCallPattern = /^→\s*([^\s(]+)\((.*)\)\s*$/
const toolErrorPattern = /^\s*(✗|⛔)\s*([^:]+?):\s*(.*)$/

export const parseProgressLine = (line: string): ProgressEntry => {
  const call = toolCallPattern.exec(line)
  if (call) return { kind: 'tool-call', name: call[1], args: call[2] }
  const error = toolErrorPattern.exec(line)
  if (error) {
    return { kind: 'tool-error', name: error[2], message: error[3], denied: error[1] === '⛔' }
  }
  return { kind: 'note', text: line.trim() }
}

export const parseProgressContent = (content: string): ProgressEntry[] =>
  content
    .split('\n')
    .filter(line => line.trim().length > 0)
    .map(parseProgressLine)

export const progressTitle = (entries: ProgressEntry[]): string => {
  const tool = entries.find(entry => entry.kind !== 'note')
  return tool ? tool.name : 'Progress'
}
