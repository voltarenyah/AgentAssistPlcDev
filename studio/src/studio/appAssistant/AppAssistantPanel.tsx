import { useEffect, useMemo, useRef, useState } from 'react'
import { Loader2, Send, Sparkles, X } from 'lucide-react'
import * as api from '@/api/client'
import {
  applyAssistantEvents,
  applyAssistantRuntimeSnapshot,
  initialAppAssistantState,
  type AppAssistantPanelState,
} from './appAssistantState'

type Props = {
  workbenchId: string
  workbenchName: string
  runtime: api.AppAssistantRuntimeSnapshot | null
  /**
   * The panel's own pending destructive-tool confirmation. A destructive call suspends the
   * assistant turn, so the approval cannot arrive in the turn's response body; it is read from the
   * shared server log by the shell and handed down here already filtered to this panel's session.
   */
  confirmation?: api.PendingConfirmation | null
  onConfirm?: (decision: 'allowOnce' | 'deny') => void
  /** Reports the panel's server session id so its own confirmations can be told apart. */
  onSessionId?: (sessionId: string | null) => void
  onClose?: () => void
  onSelectWorktree?: (worktreeId: string) => Promise<void> | void
  onWorkbenchCreated?: (workbenchId: string) => Promise<void> | void
}

export default function AppAssistantPanel({
  workbenchId,
  workbenchName,
  runtime,
  confirmation,
  onConfirm,
  onSessionId,
  onClose,
  onSelectWorktree,
  onWorkbenchCreated,
}: Props) {
  const [state, setState] = useState<AppAssistantPanelState>(() => initialAppAssistantState(runtime))
  const [draft, setDraft] = useState('')
  const [selectingWorktree, setSelectingWorktree] = useState<string | null>(null)
  const [busyLabel, setBusyLabel] = useState<string | null>(null)
  const latestRuntime = useRef<api.AppAssistantRuntimeSnapshot | null>(runtime)
  const assistantSessionId = useRef(`assistant-${Date.now()}-${Math.random().toString(36).slice(2)}`).current

  useEffect(() => {
    let cancelled = false
    latestRuntime.current = runtime
    setBusyLabel('Loading workbench context…')
    setState({ ...initialAppAssistantState(runtime), busy: true })
    void api.bootstrapAppAssistant(assistantSessionId).then(events => {
      if (!cancelled) {
        setBusyLabel(null)
        setState(current => ({ ...applyAssistantEvents(current, events), busy: false }))
      }
    }).catch(error => {
      if (!cancelled) {
        setBusyLabel(null)
        setState(current => ({
          ...current,
          busy: false,
          messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Assistant unavailable' }],
        }))
      }
    })
    return () => { cancelled = true }
  }, [assistantSessionId, workbenchId])

  useEffect(() => { onSessionId?.(state.sessionId) }, [onSessionId, state.sessionId])

  useEffect(() => api.subscribeAppAssistantRuntime(workbenchId, snapshot => {
    const previous = latestRuntime.current
    latestRuntime.current = snapshot
    setState(current => applyAssistantRuntimeSnapshot(current, snapshot, previous))
  }), [workbenchId])

  useEffect(() => {
    if (!state.autoRefreshPending || state.busy || confirmation) return
    setBusyLabel('Refreshing workbench context…')
    let cancelled = false
    setState(current => ({ ...current, busy: true, autoRefreshPending: false }))
    void api.chatAppAssistant('The workbench changed. Re-read the current state and suggest the next useful move.', assistantSessionId)
      .then(events => {
        if (!cancelled) {
          setBusyLabel(null)
          setState(current => ({ ...applyAssistantEvents(current, events), busy: false }))
        }
      })
      .catch(error => {
        if (!cancelled) {
          setBusyLabel(null)
          setState(current => ({
            ...current,
            busy: false,
            contextStale: true,
            messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Assistant refresh unavailable' }],
          }))
        }
      })
    return () => { cancelled = true }
  }, [assistantSessionId, confirmation, state.autoRefreshPending, state.busy, workbenchId])

  const send = async (message: string) => {
    const trimmed = message.trim()
    if (!trimmed) return
    setBusyLabel('Assistant is working…')
    setState(current => ({ ...current, busy: true, messages: [...current.messages, { role: 'user', content: trimmed }] }))
    try {
      const events = await api.chatAppAssistant(trimmed, assistantSessionId)
      setState(current => ({ ...applyAssistantEvents(current, events), busy: false }))
      if (onWorkbenchCreated) {
        const createdId = events
          .find(event => event.kind === 'state' && event.data.detail && typeof event.data.detail === 'object')
          ?.data.detail as { mutation?: { workbench?: { workbenchId?: unknown } } } | undefined
        const created = createdId?.mutation?.workbench?.workbenchId
        if (typeof created === 'string') await onWorkbenchCreated(created)
      }
      setDraft('')
      setBusyLabel(null)
    } catch (error) {
      setBusyLabel(null)
      setState(current => ({
        ...current,
        busy: false,
        messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Assistant unavailable' }],
      }))
    }
  }

  const selectWorktree = async (worktreeId: string) => {
    if (!onSelectWorktree) return
    setSelectingWorktree(worktreeId)
    try {
      await onSelectWorktree(worktreeId)
    } catch (error) {
      setState(current => ({
        ...current,
        messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Worktree selection failed' }],
      }))
    } finally {
      setSelectingWorktree(null)
    }
  }

  const chooseClarification = (option: string) => {
    void send(`Use '${option}' as the selected base worktree.`)
  }

  const worktrees = useMemo(() => state.runtime?.worktrees ?? runtime?.worktrees ?? [], [runtime?.worktrees, state.runtime?.worktrees])
  const focusedWorktreeId = state.runtime?.focus?.worktreeId ?? runtime?.focus?.worktreeId ?? null

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
        {state.contextStale && <span data-assistant-context-stale> · {state.autoRefreshPending ? 'refreshing suggestion…' : 'context changed; refreshes before next request'}</span>}
        {state.busy && <div className="mt-1 flex items-center gap-1.5 text-chart-4" data-assistant-progress aria-live="polite"><Loader2 className="h-3 w-3 animate-spin" /> {busyLabel ?? 'Working…'}</div>}
      </div>
      <div className="scrollbar-sleek min-h-0 flex-1 space-y-2 overflow-y-auto p-3">
        {worktrees.map(worktree => (
          <div key={worktree.worktreeId} className="rounded-md border px-2 py-1.5 text-[9px]" style={{ borderColor: 'var(--border)' }}>
            <div className="font-medium">{worktree.name}</div>
            <div className="text-muted-foreground">{worktree.branch} · {worktree.todoCount} todo{worktree.todoCount === 1 ? '' : 's'} · {worktree.gitStatus}</div>
            {onSelectWorktree && focusedWorktreeId !== worktree.worktreeId && (
              <button
                className="secondary-button mt-1 h-6 px-2 text-[9px]"
                data-assistant-select-worktree={worktree.worktreeId}
                disabled={selectingWorktree !== null}
                onClick={() => void selectWorktree(worktree.worktreeId)}
              >
                {selectingWorktree === worktree.worktreeId ? 'Selecting…' : 'Select worktree'}
              </button>
            )}
          </div>
        ))}
        {state.messages.map((message, index) => (
          <div key={`${message.role}-${index}`} className={`rounded-md px-2.5 py-2 text-[10px] leading-relaxed ${message.role === 'error' ? 'bg-red-500/10 text-red-700 dark:text-red-300' : message.role === 'user' ? 'ml-4 bg-accent' : 'bg-muted/50'}`}>
            {message.content}
          </div>
        ))}
        {state.clarificationOptions.length > 0 && (
          <div className="rounded-md border px-2.5 py-2 text-[9px]" data-assistant-clarification-options>
            <div className="mb-1 text-muted-foreground">Choose an option:</div>
            <div className="flex flex-wrap gap-1">
              {state.clarificationOptions.map(option => (
                <button
                  key={option.value}
                  className="secondary-button h-6 px-2 text-[9px]"
                  data-assistant-option={option.value}
                  disabled={state.busy}
                  title={option.description ?? undefined}
                  onClick={() => chooseClarification(option.value)}
                >
                  {option.label}
                  {option.description && <span className="ml-1 text-muted-foreground">({option.description})</span>}
                </button>
              ))}
            </div>
          </div>
        )}
        {confirmation && (
          <div className="rounded-md border border-amber-500/50 bg-amber-500/10 p-2.5 text-[10px]" data-app-assistant-confirmation={confirmation.id}>
            <div className="font-medium">Approval needed: <span className="font-mono">{confirmation.toolName}</span></div>
            {confirmation.arguments && (
              <pre className="mt-1 whitespace-pre-wrap break-all rounded bg-muted/40 p-1.5 font-mono text-[8px] text-muted-foreground">{confirmation.arguments}</pre>
            )}
            <div className="mt-2 flex gap-2">
              <button className="primary-button h-6 px-2 text-[9px]" disabled={state.busy} onClick={() => onConfirm?.('allowOnce')}>Approve</button>
              <button className="secondary-button h-6 px-2 text-[9px]" disabled={state.busy} onClick={() => onConfirm?.('deny')}>Reject</button>
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
