export type LaneId = 'studio' | 'apiHost' | 'langGraph' | 'gateway'
export type FlowPathId = 'all' | 'orientation' | 'read-only' | 'mutation'

export type FlowNode = {
  id: string
  lane: LaneId
  label: string
  kind: string
  summary: string
  detail: string
  reference: string
  inputs: string[]
  outputs: string[]
  safety?: string
  event?: string
  position: { x: number; y: number }
}

export type FlowEdge = {
  id: string
  from: string
  to: string
  label: string
  condition?: string
  paths: FlowPathId[]
}

export type FlowPath = {
  id: FlowPathId
  label: string
  description: string
  nodeIds: string[]
  edgeIds: string[]
  events: string[]
}

export type LifecycleEvent = {
  id: string
  label: string
  detail: string
  tone: 'neutral' | 'active' | 'approval'
}

export const laneMeta: Array<{ id: LaneId; label: string; eyebrow: string }> = [
  { id: 'studio', label: 'Studio', eyebrow: 'user surface' },
  { id: 'apiHost', label: 'ApiHost', eyebrow: 'SSE bridge' },
  { id: 'langGraph', label: 'LangGraph', eyebrow: 'orchestration' },
  { id: 'gateway', label: 'Gateway', eyebrow: 'C# execution' },
]

export const flowNodes: FlowNode[] = [
  {
    id: 'studio-bootstrap', lane: 'studio', label: 'bootstrapAppAssistant()', kind: 'request',
    summary: 'Orient the assistant to the selected workbench.',
    detail: 'Opening the Assistant sends an empty bootstrap request. The UI does not infer a command or execute a tool; it asks LangGraph to produce an orientation proposal first.',
    reference: 'studio/src/api/client.ts · bootstrapAppAssistant', inputs: ['workbench selection'], outputs: ['POST /api/app-assistant/bootstrap'], position: { x: 28, y: 90 },
  },
  {
    id: 'studio-chat', lane: 'studio', label: 'chatAppAssistant()', kind: 'request',
    summary: 'Send an explicit user command.',
    detail: 'A chat turn carries the user message. The assistant model must choose exactly one safe outcome: answer, clarification, read-only tool, or mutation proposal.',
    reference: 'studio/src/api/client.ts · chatAppAssistant', inputs: ['user message'], outputs: ['POST /api/app-assistant/chat'], position: { x: 28, y: 252 },
  },
  {
    id: 'studio-approval', lane: 'studio', label: 'Approve / Reject', kind: 'human gate',
    summary: 'A person decides whether a mutation may run.',
    detail: 'The UI renders the pending proposal as an approval card. No worktree or workbench mutation occurs until the user explicitly approves it.',
    reference: 'studio/src/studio/appAssistant/AppAssistantPanel.tsx', inputs: ['pendingApproval'], outputs: ['approval decision'], safety: 'Human approval is the mutation boundary.', position: { x: 28, y: 486 },
  },
  {
    id: 'studio-sse', lane: 'studio', label: 'applyAssistantEvents()', kind: 'event consumer',
    summary: 'Turn SSE events into visible assistant state.',
    detail: 'The React state reducer consumes progress, state, interrupt, and answer events. An interrupt becomes pendingApproval while the answer becomes an assistant message.',
    reference: 'studio/src/studio/appAssistant/appAssistantState.ts', inputs: ['SSE event stream'], outputs: ['messages · runtime · pendingApproval'], event: 'progress → state → interrupt → answer', position: { x: 28, y: 648 },
  },
  {
    id: 'api-progress', lane: 'apiHost', label: 'progress', kind: 'SSE event',
    summary: 'Tell the UI that current state is being read.',
    detail: 'ApiHost writes this event immediately so the UI can show work in progress before the sidecar returns a result.',
    reference: 'src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs · StreamAssistantAsync', inputs: ['HTTP request'], outputs: ['event: progress'], event: '1 / 4', position: { x: 310, y: 90 },
  },
  {
    id: 'api-state', lane: 'apiHost', label: 'state', kind: 'SSE event',
    summary: 'Forward the LangGraph response envelope.',
    detail: 'The payload includes runtimeSnapshot, decision, detail, proposedAction, assistantMetadata, and the current context revision.',
    reference: 'src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs · _response', inputs: ['sidecar JSON'], outputs: ['event: state'], event: '2 / 4', position: { x: 310, y: 252 },
  },
  {
    id: 'api-interrupt', lane: 'apiHost', label: 'interrupt', kind: 'SSE event',
    summary: 'Forward a pending approval proposal.',
    detail: 'When the sidecar returns pendingApproval, ApiHost emits a dedicated interrupt event so the Studio can render approval controls without guessing from the answer text.',
    reference: 'src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs · pendingApproval', inputs: ['pendingApproval object'], outputs: ['event: interrupt'], event: '3 / 4', safety: 'Only appears for mutation proposals.', position: { x: 310, y: 486 },
  },
  {
    id: 'api-resume', lane: 'apiHost', label: 'resume', kind: 'HTTP bridge',
    summary: 'Send the approval decision back to the sidecar.',
    detail: 'The next chat request contains the approval object. ApiHost passes it to the LangGraph endpoint, which resumes the checkpointed thread with Command(resume=...).',
    reference: 'src/ApiHost/AppAssistant/AppAssistantClient.cs · SendAsync', inputs: ['approval decision'], outputs: ['sidecar resume request'], position: { x: 310, y: 648 },
  },
  {
    id: 'api-answer', lane: 'apiHost', label: 'answer', kind: 'SSE event',
    summary: 'Deliver the human-readable result.',
    detail: 'ApiHost always ends the stream with an answer event. For an interrupt it replaces the model answer with a clear “proposal is ready” message.',
    reference: 'src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs · WriteEventAsync', inputs: ['answer string'], outputs: ['event: answer'], event: '4 / 4', position: { x: 310, y: 790 },
  },
  {
    id: 'lg-start', lane: 'langGraph', label: 'START', kind: 'graph boundary',
    summary: 'LangGraph begins a checkpointed run.',
    detail: 'Each workbench maps to a stable thread ID: app-assistant:{workbenchId}. This lets LangGraph resume an approval on the same conversation state.',
    reference: 'agent-service/app_assistant/graph.py · build_graph', inputs: ['request state'], outputs: ['bootstrap_context'], position: { x: 592, y: 12 },
  },
  {
    id: 'lg-bootstrap', lane: 'langGraph', label: 'bootstrap_context', kind: 'graph node',
    summary: 'Refresh authoritative workbench context.',
    detail: 'The gateway snapshot becomes runtime_snapshot and context_revision. The revision is captured before any proposal so stale approvals can be rejected safely.',
    reference: 'agent-service/app_assistant/graph.py · _bootstrap_async', inputs: ['workbench_id'], outputs: ['runtime_snapshot · context_revision'], position: { x: 592, y: 90 },
  },
  {
    id: 'lg-orient', lane: 'langGraph', label: 'orient_with_llm', kind: 'conditional node',
    summary: 'Describe the current workbench without tools.',
    detail: 'Orientation calls the model with a proposal-only prompt. It returns observations, likely intent, and a suggested next step, then ends without reading details or mutating state.',
    reference: 'agent-service/app_assistant/graph.py · _orient_async', inputs: ['runtime_snapshot'], outputs: ['orientation_proposal · answer'], safety: 'No gateway detail calls.', position: { x: 592, y: 205 },
  },
  {
    id: 'lg-decide', lane: 'langGraph', label: 'decide_with_llm', kind: 'conditional node',
    summary: 'Classify the explicit command into one safe decision.',
    detail: 'The command prompt receives runtime context and the user message. The validated decision routes to an answer, clarification, allowlisted read tool, or mutation proposal.',
    reference: 'agent-service/app_assistant/graph.py · _decide_async', inputs: ['runtime_snapshot · messages'], outputs: ['decision'], safety: 'Invalid model output becomes clarification.', position: { x: 592, y: 330 },
  },
  {
    id: 'lg-answer', lane: 'langGraph', label: 'answer_decision', kind: 'terminal node',
    summary: 'Return an answer or clarification directly.',
    detail: 'Answer and clarification decisions share one terminal node. No gateway call is needed because the model response is already the intended result.',
    reference: 'agent-service/app_assistant/graph.py · _decision_answer', inputs: ['decision'], outputs: ['answer → END'], position: { x: 592, y: 470 },
  },
  {
    id: 'lg-read', lane: 'langGraph', label: 'execute_read_tool', kind: 'tool node',
    summary: 'Call one allowlisted read-only gateway operation.',
    detail: 'The graph resolves the focused worktree and calls exactly one of read_worktree_todos, read_commit_history, read_svn_history, or read_svn_state.',
    reference: 'agent-service/app_assistant/graph.py · _read_detail', inputs: ['decision.toolName'], outputs: ['tool_result · detail'], safety: 'Requires a selected worktree.', position: { x: 592, y: 610 },
  },
  {
    id: 'lg-summarize', lane: 'langGraph', label: 'summarize_tool_result', kind: 'terminal node',
    summary: 'Ground the answer in observed gateway data.',
    detail: 'The model may summarize the tool result, but the fallback formatter can answer deterministically when the model is unavailable.',
    reference: 'agent-service/app_assistant/graph.py · _summarize_async', inputs: ['tool_result'], outputs: ['answer → END'], position: { x: 592, y: 748 },
  },
  {
    id: 'lg-propose', lane: 'langGraph', label: 'propose_mutation', kind: 'mutation node',
    summary: 'Build a typed proposal and pause for approval.',
    detail: 'The graph captures the expected revision and request ID, then calls interrupt(proposal). Only a resume value with decision=approve crosses into mutation execution.',
    reference: 'agent-service/app_assistant/graph.py · _propose_mutation', inputs: ['mutation · context_revision'], outputs: ['pending approval · mutation result'], safety: 'No mutation call before approval.', position: { x: 592, y: 880 },
  },
  {
    id: 'lg-interrupt', lane: 'langGraph', label: 'interrupt()', kind: 'checkpoint gate',
    summary: 'Pause the thread and persist the proposal.',
    detail: 'LangGraph returns __interrupt__ to the sidecar response. The checkpoint preserves the graph position so the same thread can resume after a human decision.',
    reference: 'agent-service/app_assistant/graph.py · interrupt', inputs: ['proposal'], outputs: ['__interrupt__'], safety: 'Approval is explicit and resumable.', position: { x: 592, y: 1015 },
  },
  {
    id: 'lg-end', lane: 'langGraph', label: 'END', kind: 'graph boundary',
    summary: 'Return the accumulated state to ApiHost.',
    detail: 'Every path terminates by returning answer and any detail/runtime state to the sidecar server, which shapes the response envelope.',
    reference: 'agent-service/app_assistant/graph.py · END edges', inputs: ['graph state'], outputs: ['response envelope'], position: { x: 592, y: 1160 },
  },
  {
    id: 'gw-context', lane: 'gateway', label: 'get_context()', kind: 'read-only gateway',
    summary: 'Read the authoritative workbench snapshot.',
    detail: 'The C# gateway supplies focus, worktrees, available actions, operation status, and the current workbench revision to LangGraph.',
    reference: 'agent-service/app_assistant/gateway.py · get_context', inputs: ['workbench_id'], outputs: ['runtime snapshot'], position: { x: 874, y: 90 },
  },
  {
    id: 'gw-read', lane: 'gateway', label: 'read detail', kind: 'read-only gateway',
    summary: 'Read todos, Git history, or SVN state.',
    detail: 'The gateway executes the selected read operation and returns structured evidence. LangGraph never invents a worktree target when focus is missing.',
    reference: 'agent-service/app_assistant/gateway.py · get_todos/get_history/get_svn', inputs: ['worktree_id · tool name'], outputs: ['structured detail'], safety: 'Read-only path only.', position: { x: 874, y: 610 },
  },
  {
    id: 'gw-mutation', lane: 'gateway', label: 'create_*()', kind: 'mutation gateway',
    summary: 'Execute only after the proposal is approved.',
    detail: 'The gateway validates workbench ID, request ID, parameters, and expected revision before invoking the existing coordinator for a worktree or workbench creation.',
    reference: 'agent-service/app_assistant/gateway.py · create_worktree/create_workbench', inputs: ['approved proposal'], outputs: ['mutation result'], safety: 'Revision-checked and idempotent.', position: { x: 874, y: 1015 },
  },
  {
    id: 'gw-refresh', lane: 'gateway', label: 'refresh context', kind: 're-plan',
    summary: 'Recover when the captured revision is stale.',
    detail: 'A stale approval is not applied. The gateway refreshes context and LangGraph tells the user to review the new state and request the action again.',
    reference: 'agent-service/app_assistant/graph.py · GatewayStaleError', inputs: ['stale revision'], outputs: ['new runtime_snapshot'], safety: 'Prevents stale writes.', position: { x: 874, y: 1160 },
  },
]

export const flowEdges: FlowEdge[] = [
  { id: 'studio-bootstrap-api', from: 'studio-bootstrap', to: 'api-progress', label: 'bootstrap', paths: ['orientation'] },
  { id: 'studio-chat-api', from: 'studio-chat', to: 'api-progress', label: 'command', paths: ['read-only', 'mutation'] },
  { id: 'api-progress-start', from: 'api-progress', to: 'lg-start', label: 'proxy', paths: ['orientation', 'read-only', 'mutation'] },
  { id: 'lg-start-bootstrap', from: 'lg-start', to: 'lg-bootstrap', label: 'START', paths: ['orientation', 'read-only', 'mutation'] },
  { id: 'lg-bootstrap-context', from: 'lg-bootstrap', to: 'gw-context', label: 'get_context', paths: ['orientation', 'read-only', 'mutation'] },
  { id: 'gw-context-bootstrap', from: 'gw-context', to: 'lg-bootstrap', label: 'snapshot', paths: ['orientation', 'read-only', 'mutation'] },
  { id: 'lg-bootstrap-orient', from: 'lg-bootstrap', to: 'lg-orient', label: 'orientation', condition: 'request_mode = orientation', paths: ['orientation'] },
  { id: 'lg-bootstrap-decide', from: 'lg-bootstrap', to: 'lg-decide', label: 'command', condition: 'request_mode = command', paths: ['read-only', 'mutation'] },
  { id: 'lg-orient-end', from: 'lg-orient', to: 'lg-end', label: 'END', paths: ['orientation'] },
  { id: 'lg-decide-answer', from: 'lg-decide', to: 'lg-answer', label: 'answer / clarification', condition: 'decision.kind ∈ {answer, clarification}', paths: ['read-only'] },
  { id: 'lg-decide-read', from: 'lg-decide', to: 'lg-read', label: 'read_tool', condition: 'decision.kind = read_tool', paths: ['read-only'] },
  { id: 'lg-read-gateway', from: 'lg-read', to: 'gw-read', label: 'read detail', paths: ['read-only'] },
  { id: 'gw-read-summarize', from: 'gw-read', to: 'lg-summarize', label: 'tool_result', paths: ['read-only'] },
  { id: 'lg-summarize-end', from: 'lg-summarize', to: 'lg-end', label: 'END', paths: ['read-only'] },
  { id: 'lg-decide-propose', from: 'lg-decide', to: 'lg-propose', label: 'mutation_proposal', condition: 'decision.kind = mutation_proposal', paths: ['mutation'] },
  { id: 'lg-propose-interrupt', from: 'lg-propose', to: 'lg-interrupt', label: 'interrupt()', paths: ['mutation'] },
  { id: 'lg-interrupt-api', from: 'lg-interrupt', to: 'api-interrupt', label: '__interrupt__', paths: ['mutation'] },
  { id: 'api-interrupt-studio', from: 'api-interrupt', to: 'studio-approval', label: 'pendingApproval', paths: ['mutation'] },
  { id: 'studio-approval-resume', from: 'studio-approval', to: 'api-resume', label: 'approve', paths: ['mutation'] },
  { id: 'api-resume-propose', from: 'api-resume', to: 'lg-propose', label: 'Command(resume)', paths: ['mutation'] },
  { id: 'lg-propose-mutation', from: 'lg-propose', to: 'gw-mutation', label: 'approved proposal', paths: ['mutation'] },
  { id: 'gw-mutation-end', from: 'gw-mutation', to: 'lg-end', label: 'mutation result', paths: ['mutation'] },
  { id: 'gw-mutation-refresh', from: 'gw-mutation', to: 'gw-refresh', label: 'stale revision', condition: 'GatewayStaleError', paths: ['mutation'] },
  { id: 'gw-refresh-end', from: 'gw-refresh', to: 'lg-end', label: 're-plan', paths: ['mutation'] },
  { id: 'lg-end-state', from: 'lg-end', to: 'api-state', label: 'response envelope', paths: ['orientation', 'read-only', 'mutation'] },
  { id: 'api-state-answer', from: 'api-state', to: 'api-answer', label: 'answer', paths: ['orientation', 'read-only', 'mutation'] },
  { id: 'api-answer-studio', from: 'api-answer', to: 'studio-sse', label: 'event: answer', paths: ['orientation', 'read-only', 'mutation'] },
]

export const flowPaths: FlowPath[] = [
  {
    id: 'orientation', label: 'Orientation', description: 'Bootstrap context, then explain the workbench without calling detail tools.',
    nodeIds: ['studio-bootstrap', 'api-progress', 'lg-start', 'lg-bootstrap', 'gw-context', 'lg-orient', 'lg-end', 'api-state', 'api-answer', 'studio-sse'],
    edgeIds: ['studio-bootstrap-api', 'api-progress-start', 'lg-start-bootstrap', 'lg-bootstrap-context', 'gw-context-bootstrap', 'lg-bootstrap-orient', 'lg-orient-end', 'lg-end-state', 'api-state-answer', 'api-answer-studio'],
    events: ['progress', 'state', 'answer'],
  },
  {
    id: 'read-only', label: 'Read-only', description: 'Choose one allowlisted read tool, ground the answer in gateway evidence, then return it.',
    nodeIds: ['studio-chat', 'api-progress', 'lg-start', 'lg-bootstrap', 'gw-context', 'lg-decide', 'lg-read', 'gw-read', 'lg-summarize', 'lg-end', 'api-state', 'api-answer', 'studio-sse'],
    edgeIds: ['studio-chat-api', 'api-progress-start', 'lg-start-bootstrap', 'lg-bootstrap-context', 'gw-context-bootstrap', 'lg-bootstrap-decide', 'lg-decide-read', 'lg-read-gateway', 'gw-read-summarize', 'lg-summarize-end', 'lg-end-state', 'api-state-answer', 'api-answer-studio'],
    events: ['progress', 'state', 'answer'],
  },
  {
    id: 'mutation', label: 'Mutation + approval', description: 'Build a revision-bound proposal, pause at interrupt(), then resume only after approval.',
    nodeIds: ['studio-chat', 'api-progress', 'lg-start', 'lg-bootstrap', 'gw-context', 'lg-decide', 'lg-propose', 'lg-interrupt', 'api-interrupt', 'studio-approval', 'api-resume', 'gw-mutation', 'gw-refresh', 'lg-end', 'api-state', 'api-answer', 'studio-sse'],
    edgeIds: ['studio-chat-api', 'api-progress-start', 'lg-start-bootstrap', 'lg-bootstrap-context', 'gw-context-bootstrap', 'lg-bootstrap-decide', 'lg-decide-propose', 'lg-propose-interrupt', 'lg-interrupt-api', 'api-interrupt-studio', 'studio-approval-resume', 'api-resume-propose', 'lg-propose-mutation', 'gw-mutation-end', 'gw-mutation-refresh', 'gw-refresh-end', 'lg-end-state', 'api-state-answer', 'api-answer-studio'],
    events: ['progress', 'state', 'interrupt', 'answer'],
  },
]

export const lifecycleEvents: LifecycleEvent[] = [
  { id: 'progress', label: 'progress', detail: 'ApiHost tells Studio it is reading current workbench state.', tone: 'neutral' },
  { id: 'state', label: 'state', detail: 'The graph result and runtime snapshot arrive as one envelope.', tone: 'active' },
  { id: 'interrupt', label: 'interrupt', detail: 'Only mutation paths expose pendingApproval for a human decision.', tone: 'approval' },
  { id: 'answer', label: 'answer', detail: 'The final assistant-facing text is appended to the conversation.', tone: 'active' },
]

const allPath = (ids: string[]) => ids

export const getActiveFlow = (pathId: FlowPathId) => {
  if (pathId === 'all') {
    return { nodeIds: allPath(flowNodes.map(node => node.id)), edgeIds: allPath(flowEdges.map(edge => edge.id)) }
  }
  const path = flowPaths.find(item => item.id === pathId) ?? flowPaths[0]
  return { nodeIds: path.nodeIds, edgeIds: path.edgeIds }
}

export const findFlowItem = (id: string) => flowNodes.find(node => node.id === id) ?? flowEdges.find(edge => edge.id === id) ?? null
