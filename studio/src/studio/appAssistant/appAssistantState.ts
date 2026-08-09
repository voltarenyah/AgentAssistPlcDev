import type { AppAssistantEvent, AppAssistantRuntimeSnapshot } from '@/api/client'

export type AppAssistantMessage = {
  role: 'user' | 'assistant' | 'error'
  content: string
}

export type AppAssistantPanelState = {
  messages: AppAssistantMessage[]
  runtime: AppAssistantRuntimeSnapshot | null
  pendingApproval: Record<string, unknown> | null
  busy: boolean
}

export const initialAppAssistantState = (runtime: AppAssistantRuntimeSnapshot | null): AppAssistantPanelState => ({
  messages: [],
  runtime,
  pendingApproval: null,
  busy: false,
})

export const applyAssistantEvents = (
  state: AppAssistantPanelState,
  events: AppAssistantEvent[],
): AppAssistantPanelState => {
  let next = state
  for (const event of events) {
    if (event.kind === 'answer' && typeof event.data.answer === 'string') {
      next = { ...next, messages: [...next.messages, { role: 'assistant', content: event.data.answer }] }
    } else if (event.kind === 'state' && event.data.runtimeSnapshot) {
      next = { ...next, runtime: event.data.runtimeSnapshot as AppAssistantRuntimeSnapshot }
    } else if (event.kind === 'error') {
      next = { ...next, messages: [...next.messages, { role: 'error', content: String(event.data.message ?? event.data.error ?? 'Assistant unavailable') }] }
    } else if (event.kind === 'interrupt' || event.kind === 'runtime-state') {
      next = { ...next, pendingApproval: event.data as Record<string, unknown> }
    }
  }
  return next
}
