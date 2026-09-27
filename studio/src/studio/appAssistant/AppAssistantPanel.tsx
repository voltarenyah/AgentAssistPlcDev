import { useEffect, useMemo, useRef, useState } from 'react'
import { Loader2, Send, Sparkles, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import * as api from '@/api/client'
import {
  applyAssistantEvents,
  applyAssistantRuntimeSnapshot,
  initialAppAssistantState,
  type AppAssistantPanelState,
} from './appAssistantState'

type Props = {
  workbenchId: string | null
  workbenchName: string
  workbenches?: { workbenchId: string; name: string }[]
  devices?: api.DeviceSummary[]
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
  onBusyChange?: (busy: boolean) => void
  onClose?: () => void
  defaultExpanded?: boolean
  onSelectWorkbench?: (workbenchId: string) => Promise<void> | void
  onSelectWorktree?: (worktreeId: string) => Promise<void> | void
  onSelectDevice?: (deviceId: string) => Promise<void> | void
  onManagedChange?: (change: { kind: string; workbenchId: string; worktreeId?: string | null; taskId?: string | null; deviceId?: string | null }) => Promise<void> | void
}

const approvalName = (toolName: string): string => ({
  assistant_create_workbench: 'Create workbench',
  assistant_create_worktree: 'Create linked worktree',
  assistant_create_task: 'Create device task',
}[toolName] ?? toolName)

const approvalDetails = (argumentsJson: string): string => {
  try { return JSON.stringify(JSON.parse(argumentsJson), null, 2) }
  catch { return argumentsJson }
}

export default function AppAssistantPanel({
  workbenchId,
  workbenchName,
  workbenches = [],
  devices = [],
  runtime,
  confirmation,
  onConfirm,
  onSessionId,
  onBusyChange,
  onClose,
  defaultExpanded = true,
  onSelectWorkbench,
  onSelectWorktree,
  onSelectDevice,
  onManagedChange,
}: Props) {
  const [state, setState] = useState<AppAssistantPanelState>(() => initialAppAssistantState(runtime))
  const [draft, setDraft] = useState('')
  const [expanded, setExpanded] = useState(defaultExpanded)
  const [selectingWorktree, setSelectingWorktree] = useState<string | null>(null)
  const [busyLabel, setBusyLabel] = useState<string | null>(null)
  const conversationScroll = useRef<HTMLDivElement>(null)
  const latestRuntime = useRef<api.AppAssistantRuntimeSnapshot | null>(runtime)
  const assistantSessionId = useRef(`assistant-${Date.now()}-${Math.random().toString(36).slice(2)}`).current

  useEffect(() => { latestRuntime.current = runtime }, [runtime])

  useEffect(() => {
    let cancelled = false
    setBusyLabel('Loading workbench context…')
    setState(current => ({ ...current, runtime: null, busy: true }))
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
  useEffect(() => { if (confirmation) setExpanded(true) }, [confirmation])
  useEffect(() => {
    if (expanded && conversationScroll.current) {
      conversationScroll.current.scrollTop = conversationScroll.current.scrollHeight
    }
  }, [expanded, state.messages.length, confirmation])
  useEffect(() => {
    if (!expanded) return
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setExpanded(false)
    }
    document.addEventListener('keydown', onEscape)
    return () => document.removeEventListener('keydown', onEscape)
  }, [expanded])
  useEffect(() => {
    onBusyChange?.(state.busy)
    return () => onBusyChange?.(false)
  }, [onBusyChange, state.busy])

  useEffect(() => workbenchId ? api.subscribeAppAssistantRuntime(workbenchId, snapshot => {
    const previous = latestRuntime.current
    latestRuntime.current = snapshot
    setState(current => applyAssistantRuntimeSnapshot(current, snapshot, previous))
  }) : undefined, [workbenchId])

  // Unmount-only flag. A per-run `cancelled` flag cannot be used for the refresh below: that
  // effect sets busy and autoRefreshPending, which are its own dependencies, so React runs the
  // cleanup immediately after the request starts and cancels it. The `.then` that clears `busy`
  // is then skipped and busy stays true forever — the message box disabled and "Refreshing
  // workbench context…" on screen with no way out but closing the panel.
  // Unmount-only flag. It MUST be reset in the effect body: React's development StrictMode runs
  // effect setup, cleanup, then setup again, so a flag that is only ever set to true would stay
  // true for the whole session and the refresh below would return early every time, leaving busy
  // stuck and the message box disabled forever.
  const unmounted = useRef(false)
  useEffect(() => {
    unmounted.current = false
    return () => { unmounted.current = true }
  }, [])

  useEffect(() => {
    if (!state.autoRefreshPending || state.busy || confirmation) return
    setBusyLabel('Refreshing workbench context…')
    setState(current => ({ ...current, busy: true, autoRefreshPending: false }))
    // A refresh re-reads observed workbench state, which bootstrap already serves deterministically
    // and instantly. Routing it through a full agent turn instead left the message box disabled for
    // minutes after every consequential workbench change, because this panel runs the same
    // multi-round tool-calling loop as the device chat.
    void api.bootstrapAppAssistant(assistantSessionId)
      .then(events => {
        if (unmounted.current) return
        setBusyLabel(null)
        setState(current => ({ ...applyAssistantEvents(current, events), busy: false }))
      })
      .catch(error => {
        if (unmounted.current) return
        setBusyLabel(null)
        setState(current => ({
          ...current,
          busy: false,
          contextStale: true,
          messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Assistant refresh unavailable' }],
        }))
      })
  }, [assistantSessionId, confirmation, state.autoRefreshPending, state.busy, workbenchId])

  const send = async (message: string) => {
    const trimmed = message.trim()
    if (!trimmed) return
    setExpanded(true)
    setBusyLabel('Assistant is working…')
    setState(current => ({ ...current, busy: true, messages: [...current.messages, { role: 'user', content: trimmed }] }))
    try {
      const events = await api.chatAppAssistant(trimmed, assistantSessionId)
      setState(current => ({ ...applyAssistantEvents(current, events), busy: false }))
      const change = events.find(event => event.kind === 'state')?.data.change
      if (change && typeof change === 'object') {
        const value = change as { kind?: unknown; workbenchId?: unknown; worktreeId?: unknown; taskId?: unknown; deviceId?: unknown }
        if (typeof value.kind === 'string' && typeof value.workbenchId === 'string') {
          try {
            await onManagedChange?.({ kind: value.kind, workbenchId: value.workbenchId,
              worktreeId: typeof value.worktreeId === 'string' ? value.worktreeId : null,
              taskId: typeof value.taskId === 'string' ? value.taskId : null,
              deviceId: typeof value.deviceId === 'string' ? value.deviceId : null })
          } catch (error) {
            setState(current => ({ ...current, messages: [...current.messages, {
              role: 'error', content: `The action completed, but the view could not refresh: ${error instanceof Error ? error.message : 'unknown error'}`,
            }] }))
          }
        }
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

  const selectScope = async (select: () => Promise<void> | void, worktreeId: string | null = null) => {
    setSelectingWorktree(worktreeId)
    setBusyLabel('Updating workbench context…')
    setState(current => ({ ...current, busy: true }))
    try {
      await select()
      const events = await api.bootstrapAppAssistant(assistantSessionId)
      setState(current => ({ ...applyAssistantEvents(current, events), busy: false }))
    } catch (error) {
      setState(current => ({
        ...current,
        busy: false,
        messages: [...current.messages, { role: 'error', content: error instanceof Error ? error.message : 'Worktree selection failed' }],
      }))
    } finally {
      setBusyLabel(null)
      setSelectingWorktree(null)
    }
  }

  const chooseClarification = (option: { value: string; label: string }) => {
    void send(`For "${state.clarificationQuestion ?? 'your question'}", I choose "${option.label}" (value: ${option.value}). Continue the requested work.`)
  }

  const worktrees = useMemo(() => state.runtime?.worktrees ?? runtime?.worktrees ?? [], [runtime?.worktrees, state.runtime?.worktrees])
  const focusedWorktreeId = state.runtime?.focus?.worktreeId ?? runtime?.focus?.worktreeId ?? null
  const focusedDeviceId = state.runtime?.focus?.deviceId ?? runtime?.focus?.deviceId ?? null

  return (
    <TooltipProvider>
      <div
        className="w-full max-w-4xl min-w-0"
        data-app-assistant
        onMouseDown={event => event.stopPropagation()}
        onDoubleClick={event => event.stopPropagation()}
      >
        <form
          className="flex h-8 min-w-0 items-center gap-1 rounded-md border bg-background pl-2 pr-1 focus-within:ring-2 focus-within:ring-ring/40"
          onSubmit={event => { event.preventDefault(); void send(draft) }}
        >
          <Sparkles className="size-4 shrink-0 text-chart-4" aria-hidden="true" />
          <Input
            className="h-7 min-w-0 flex-1 select-text border-0 bg-transparent px-1 text-xs shadow-none focus-visible:ring-0"
            aria-label="Workbench Assistant message"
            value={draft}
            onFocus={() => setExpanded(true)}
            onChange={event => setDraft(event.target.value)}
            placeholder="Ask Workbench Assistant…"
            disabled={state.busy}
          />
          {confirmation && <span className="shrink-0 text-xs text-amber-700 dark:text-amber-300" role="status">Approval needed</span>}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                type="button"
                aria-label={confirmation ? 'Review Workbench Assistant approval' : expanded ? 'Hide Workbench Assistant conversation' : 'Open Workbench Assistant'}
                aria-expanded={expanded}
                aria-controls="workbench-assistant-conversation"
                onClick={() => setExpanded(previous => confirmation ? true : !previous)}
              >
                <Sparkles aria-hidden="true" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{confirmation ? 'Review approval' : expanded ? 'Collapse conversation' : 'Open conversation'}</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button size="icon-xs" aria-label="Send assistant message" type="submit" disabled={state.busy || !draft.trim()}>
                {state.busy ? <Loader2 className="animate-spin" aria-hidden="true" /> : <Send aria-hidden="true" />}
              </Button>
            </TooltipTrigger>
            <TooltipContent>Send message</TooltipContent>
          </Tooltip>
        </form>
        <aside
          id="workbench-assistant-conversation"
          className={`fixed bottom-10 left-1/2 z-50 h-[min(65vh,720px)] max-h-[calc(100vh-112px)] w-[min(920px,calc(100vw-32px))] -translate-x-1/2 flex-col overflow-hidden rounded-lg border bg-card text-foreground shadow-xl select-text ${expanded ? 'flex' : 'hidden'}`}
          data-app-assistant-panel
          aria-label="Workbench Assistant conversation"
          aria-hidden={!expanded}
        >
      <header className="flex items-center gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <Sparkles className="h-3.5 w-3.5 text-chart-4" />
        <div className="min-w-0 flex-1">
          <h2 className="text-xs font-semibold">Workbench Assistant</h2>
          <p className="truncate text-[9px] text-muted-foreground">{workbenchName}</p>
        </div>
        <Tooltip><TooltipTrigger asChild><Button variant="ghost" size="icon-xs" type="button" aria-label="Collapse Workbench Assistant" onClick={() => { setExpanded(false); onClose?.() }}><X aria-hidden="true" /></Button></TooltipTrigger><TooltipContent>Collapse conversation</TooltipContent></Tooltip>
      </header>
      <div className="border-b px-3 py-2 text-[9px] text-muted-foreground">
        Runtime revision {state.runtime?.workbenchRevision ?? runtime?.workbenchRevision ?? '—'} · selection stays with you
        {state.contextStale && <span data-assistant-context-stale> · {state.autoRefreshPending ? 'refreshing suggestion…' : 'context changed; refreshes before next request'}</span>}
        {state.busy && <div className="mt-1 flex items-center gap-1.5 text-chart-4" data-assistant-progress aria-live="polite"><Loader2 className="h-3 w-3 animate-spin" /> {busyLabel ?? 'Working…'}</div>}
      </div>
      <div ref={conversationScroll} className="scrollbar-sleek min-h-0 flex-1 space-y-2 overflow-y-auto p-3">
        {!workbenchId && workbenches.map(workbench => (
          <div key={workbench.workbenchId} className="rounded-md border px-2 py-1.5 text-[9px]" style={{ borderColor: 'var(--border)' }}>
            <div className="font-medium">{workbench.name}</div>
            {onSelectWorkbench && (
              <button className="secondary-button mt-1 h-6 px-2 text-[9px]" disabled={state.busy} onClick={() => void selectScope(() => onSelectWorkbench(workbench.workbenchId))}>
                Select project
              </button>
            )}
          </div>
        ))}
        {worktrees.map(worktree => (
          <div key={worktree.worktreeId} className="rounded-md border px-2 py-1.5 text-[9px]" style={{ borderColor: 'var(--border)' }}>
            <div className="font-medium">{worktree.name}</div>
            <div className="text-muted-foreground">{worktree.branch} · {worktree.todoCount} todo{worktree.todoCount === 1 ? '' : 's'} · {worktree.gitStatus}</div>
            {onSelectWorktree && focusedWorktreeId !== worktree.worktreeId && (
              <button
                className="secondary-button mt-1 h-6 px-2 text-[9px]"
                data-assistant-select-worktree={worktree.worktreeId}
                disabled={state.busy || selectingWorktree !== null}
                onClick={() => void selectScope(() => onSelectWorktree(worktree.worktreeId), worktree.worktreeId)}
              >
                {selectingWorktree === worktree.worktreeId ? 'Selecting…' : 'Select worktree'}
              </button>
            )}
          </div>
        ))}
        {focusedWorktreeId && devices.map(device => (
          <div key={device.deviceId} className="rounded-md border px-2 py-1.5 text-[9px]" style={{ borderColor: 'var(--border)' }}>
            <div className="font-medium">{device.plcName}</div>
            {onSelectDevice && focusedDeviceId !== device.deviceId && (
              <button className="secondary-button mt-1 h-6 px-2 text-[9px]" disabled={state.busy} onClick={() => void selectScope(() => onSelectDevice(device.deviceId))}>
                Select device
              </button>
            )}
          </div>
        ))}
        {state.messages.map((message, index) => (
          <div key={`${message.role}-${index}`} className={`break-words rounded-md px-2.5 py-2 text-[10px] leading-relaxed ${message.role === 'error' ? 'bg-red-500/10 text-red-700 dark:text-red-300' : message.role === 'user' ? 'ml-4 bg-accent' : 'bg-muted/50'}`}>
            {message.role === 'assistant'
              ? <div className="markdown-body"><ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown></div>
              : message.content}
          </div>
        ))}
        {state.clarificationOptions.length > 0 && (
          <div className="rounded-md border px-2.5 py-2 text-[9px]" data-assistant-clarification-options>
            <div className="mb-1 text-muted-foreground">{state.clarificationQuestion ?? 'Choose an option:'}</div>
            <div className="flex flex-wrap gap-1">
              {state.clarificationOptions.map(option => (
                <button
                  key={option.value}
                  className="secondary-button h-6 px-2 text-[9px]"
                  data-assistant-option={option.value}
                  disabled={state.busy}
                  title={option.description ?? undefined}
                  onClick={() => chooseClarification(option)}
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
            <div className="font-medium">Approval needed: {approvalName(confirmation.toolName)}</div>
            {confirmation.arguments && (
              <pre className="mt-1 whitespace-pre-wrap break-all rounded bg-muted/40 p-1.5 font-mono text-[8px] text-muted-foreground">{approvalDetails(confirmation.arguments)}</pre>
            )}
            <div className="mt-2 flex gap-2">
              <button className="primary-button h-6 px-2 text-[9px]" onClick={() => onConfirm?.('allowOnce')}>Approve</button>
              <button className="secondary-button h-6 px-2 text-[9px]" onClick={() => onConfirm?.('deny')}>Reject</button>
            </div>
          </div>
        )}
      </div>
        </aside>
      </div>
    </TooltipProvider>
  )
}
