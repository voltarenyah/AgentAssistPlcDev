import { useEffect, useMemo, useState } from 'react'
import { Loader2, Send, Sparkles, X } from 'lucide-react'
import * as api from '@/api/client'
import {
  applyAssistantEvents,
  initialAppAssistantState,
  type AppAssistantPanelState,
} from './appAssistantState'

type Props = {
  workbenchId: string
  workbenchName: string
  runtime: api.AppAssistantRuntimeSnapshot | null
  onClose?: () => void
}

export default function AppAssistantPanel({ workbenchId, workbenchName, runtime, onClose }: Props) {
  const [state, setState] = useState<AppAssistantPanelState>(() => initialAppAssistantState(runtime))
  const [draft, setDraft] = useState('')

  useEffect(() => {
    let cancelled = false
    setState(initialAppAssistantState(runtime))
    void api.bootstrapAppAssistant().then(events => {
      if (!cancelled) setState(current => applyAssistantEvents(current, events))
    }).catch(error => {
      if (!cancelled) setState(current => ({ ...current, messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Assistant unavailable' }] }))
    })
    return () => { cancelled = true }
  }, [workbenchId])

  useEffect(() => api.subscribeAppAssistantRuntime(workbenchId, snapshot => {
    setState(current => ({ ...current, runtime: snapshot }))
  }), [workbenchId])

  const send = async (message: string, approval?: Record<string, unknown>) => {
    const trimmed = message.trim()
    if (!trimmed && !approval) return
    setState(current => ({
      ...current,
      busy: true,
      messages: trimmed ? [...current.messages, { role: 'user', content: trimmed }] : current.messages,
    }))
    try {
      const events = approval
        ? await api.chatAppAssistant(trimmed || 'Approve the proposed worktree creation.', approval)
        : await api.chatAppAssistant(trimmed)
      setState(current => ({ ...applyAssistantEvents(current, events), busy: false, pendingApproval: null }))
      setDraft('')
    } catch (error) {
      setState(current => ({
        ...current,
        busy: false,
        messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Assistant unavailable' }],
      }))
    }
  }

  const worktrees = useMemo(() => state.runtime?.worktrees ?? runtime?.worktrees ?? [], [runtime?.worktrees, state.runtime?.worktrees])

  return (
    <aside className="flex h-full w-[320px] shrink-0 flex-col border-l bg-card" data-app-assistant-panel>
      <header className="flex items-center gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <Sparkles className="h-3.5 w-3.5 text-chart-4" />
        <div className="min-w-0 flex-1">
          <h2 className="text-xs font-semibold">Workbench Assistant</h2>
          <p className="truncate text-[9px] text-muted-foreground">{workbenchName}</p>
        </div>
        {onClose && <button className="icon-button h-6 w-6" aria-label="Close Workbench Assistant" onClick={onClose}><X className="h-3 w-3" /></button>}
      </header>
      <div className="border-b px-3 py-2 text-[9px] text-muted-foreground">
        Runtime revision {state.runtime?.workbenchRevision ?? runtime?.workbenchRevision ?? '—'} · selection stays with you
      </div>
      <div className="scrollbar-sleek min-h-0 flex-1 space-y-2 overflow-y-auto p-3">
        {worktrees.map(worktree => (
          <div key={worktree.worktreeId} className="rounded-md border px-2 py-1.5 text-[9px]" style={{ borderColor: 'var(--border)' }}>
            <div className="font-medium">{worktree.name}</div>
            <div className="text-muted-foreground">{worktree.branch} · {worktree.todoCount} todo{worktree.todoCount === 1 ? '' : 's'} · {worktree.gitStatus}</div>
          </div>
        ))}
        {state.messages.map((message, index) => (
          <div key={`${message.role}-${index}`} className={`rounded-md px-2.5 py-2 text-[10px] leading-relaxed ${message.role === 'error' ? 'bg-red-500/10 text-red-700 dark:text-red-300' : message.role === 'user' ? 'ml-4 bg-accent' : 'bg-muted/50'}`}>
            {message.content}
          </div>
        ))}
        {state.pendingApproval && (
          <div className="rounded-md border border-amber-500/50 bg-amber-500/10 p-2.5 text-[10px]">
            <div className="font-medium">Approve worktree creation?</div>
            <div className="mt-1 text-muted-foreground">{String(state.pendingApproval.name ?? 'new worktree')} · {String(state.pendingApproval.branch ?? 'new branch')}</div>
            <div className="mt-2 flex gap-2">
              <button className="primary-button h-6 px-2 text-[9px]" disabled={state.busy} onClick={() => void send('', { decision: 'approve' })}>Approve</button>
              <button className="secondary-button h-6 px-2 text-[9px]" disabled={state.busy} onClick={() => void send('', { decision: 'reject' })}>Reject</button>
            </div>
          </div>
        )}
      </div>
      <form className="flex gap-2 border-t p-2" style={{ borderColor: 'var(--border)' }} onSubmit={event => { event.preventDefault(); void send(draft) }}>
        <input className="field-input min-w-0 flex-1 text-[10px]" aria-label="Workbench Assistant message" value={draft} onChange={event => setDraft(event.target.value)} placeholder="Ask about this workbench…" disabled={state.busy} />
        <button className="primary-button h-8 w-8 justify-center px-0" aria-label="Send assistant message" type="submit" disabled={state.busy || !draft.trim()}>{state.busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}</button>
      </form>
    </aside>
  )
}
