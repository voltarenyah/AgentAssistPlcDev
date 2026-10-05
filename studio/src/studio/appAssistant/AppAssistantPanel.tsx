import { useEffect, useMemo, useRef, useState } from 'react'
import { Loader2, MessageSquareText, Send, Sparkles, X } from 'lucide-react'
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

const clampConversationHeight = (height: number, top: number): number => {
  const maximum = Math.max(140, window.innerHeight - top - 40)
  return Math.min(maximum, Math.max(Math.min(220, maximum), height))
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
  const [panelHeight, setPanelHeight] = useState<number | null>(null)
  const [resizing, setResizing] = useState(false)
  const resizeStart = useRef<{ pointerId: number; y: number; height: number } | null>(null)
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
  const lastUserMessageIndex = state.messages.map(message => message.role).lastIndexOf('user')
  const compactReply = lastUserMessageIndex < 0 ? null : state.messages
    .slice(lastUserMessageIndex + 1).reverse()
    .find(message => message.role === 'assistant' || message.role === 'error')?.content.replace(/\s+/g, ' ').trim() ?? null

  return (
    <TooltipProvider>
      <div
        className="h-9 w-full max-w-4xl min-w-0"
        data-app-assistant
        onMouseDown={event => event.stopPropagation()}
        onDoubleClick={event => event.stopPropagation()}
      >
        <aside
          id="workbench-assistant-conversation"
          className="assistant-conversation absolute z-50 flex flex-col overflow-hidden border border-input bg-card text-foreground select-text"
          data-app-assistant-panel
          data-expanded={expanded}
          data-resizing={resizing}
          style={expanded && panelHeight !== null ? { height: panelHeight } : undefined}
          aria-label="Workbench Assistant conversation"
        >
          <div className="assistant-conversation-content flex min-h-0 flex-1 flex-col" aria-hidden={!expanded} inert={!expanded}>
          <header className="flex h-14 shrink-0 items-center gap-3 border-b px-5">
            <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-accent text-chart-4">
              <Sparkles className="size-4" aria-hidden="true" />
            </div>
            <div className="min-w-0 flex-1">
              <h2 className="text-sm font-semibold">Workbench Assistant</h2>
              <p className="truncate text-xs text-muted-foreground">{workbenchName}</p>
            </div>
            {state.busy && (
              <div className="flex items-center gap-2 text-xs text-muted-foreground" data-assistant-progress aria-live="polite">
                <Loader2 className="size-3.5 animate-spin" aria-hidden="true" />
                <span>{busyLabel ?? 'Working…'}</span>
              </div>
            )}
            <Tooltip>
              <TooltipTrigger asChild>
                <Button variant="ghost" size="icon-sm" type="button" aria-label="Collapse Workbench Assistant" onClick={() => { setExpanded(false); onClose?.() }}>
                  <X aria-hidden="true" />
                </Button>
              </TooltipTrigger>
              <TooltipContent>Collapse conversation</TooltipContent>
            </Tooltip>
          </header>
          {state.contextStale && (
            <div className="border-b bg-accent/40 px-5 py-2 text-xs text-muted-foreground" data-assistant-context-stale>
              {state.autoRefreshPending ? 'Refreshing workbench context…' : 'Workbench context changed; it will refresh before your next request.'}
            </div>
          )}
          <div ref={conversationScroll} className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto px-4 py-6 sm:px-8">
            <div className="mx-auto max-w-3xl space-y-5">
              {!workbenchId && workbenches.map(workbench => (
                <div key={workbench.workbenchId} className="flex items-center justify-between gap-4 rounded-lg border bg-background px-4 py-3">
                  <div className="min-w-0 text-sm font-medium">{workbench.name}</div>
                  {onSelectWorkbench && (
                    <Button variant="outline" size="xs" disabled={state.busy} onClick={() => void selectScope(() => onSelectWorkbench(workbench.workbenchId))}>
                      Select project
                    </Button>
                  )}
                </div>
              ))}
              {worktrees.map(worktree => (
                <div key={worktree.worktreeId} className="flex items-center justify-between gap-4 rounded-lg border bg-background px-4 py-3">
                  <div className="min-w-0">
                    <div className="text-sm font-medium">{worktree.name}</div>
                    <div className="text-xs text-muted-foreground">{worktree.branch} · {worktree.todoCount} todo{worktree.todoCount === 1 ? '' : 's'} · {worktree.gitStatus}</div>
                  </div>
                  {onSelectWorktree && focusedWorktreeId !== worktree.worktreeId && (
                    <Button
                      variant="outline"
                      size="xs"
                      data-assistant-select-worktree={worktree.worktreeId}
                      disabled={state.busy || selectingWorktree !== null}
                      onClick={() => void selectScope(() => onSelectWorktree(worktree.worktreeId), worktree.worktreeId)}
                    >
                      {selectingWorktree === worktree.worktreeId ? 'Selecting…' : 'Select worktree'}
                    </Button>
                  )}
                </div>
              ))}
              {focusedWorktreeId && devices.map(device => (
                <div key={device.deviceId} className="flex items-center justify-between gap-4 rounded-lg border bg-background px-4 py-3">
                  <div className="min-w-0 text-sm font-medium">{device.plcName}</div>
                  {onSelectDevice && focusedDeviceId !== device.deviceId && (
                    <Button variant="outline" size="xs" disabled={state.busy} onClick={() => void selectScope(() => onSelectDevice(device.deviceId))}>
                      Select device
                    </Button>
                  )}
                </div>
              ))}
              {state.messages.map((message, index) => (
                <div key={`${message.role}-${index}`} data-assistant-message-role={message.role} className={message.role === 'user' ? 'flex justify-end' : ''}>
                  <div className={message.role === 'user' ? 'max-w-[85%] rounded-2xl bg-muted px-4 py-3' : message.role === 'error' ? 'rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3' : 'px-1'}>
                    <div className="mb-1 text-xs font-semibold text-muted-foreground">
                      {message.role === 'user' ? 'You' : message.role === 'error' ? 'Assistant error' : 'Workbench Assistant'}
                    </div>
                    {message.role === 'assistant'
                      ? <div className="markdown-body assistant-markdown"><ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown></div>
                      : <div className="whitespace-pre-wrap break-words text-sm leading-6">{message.content}</div>}
                  </div>
                </div>
              ))}
              {state.clarificationOptions.length > 0 && (
                <div className="rounded-lg border bg-background p-4" data-assistant-clarification-options>
                  <div className="mb-3 text-sm font-medium">{state.clarificationQuestion ?? 'Choose an option:'}</div>
                  <div className="flex flex-wrap gap-2">
                    {state.clarificationOptions.map(option => (
                      <Button
                        key={option.value}
                        variant="outline"
                        size="xs"
                        data-assistant-option={option.value}
                        disabled={state.busy}
                        onClick={() => chooseClarification(option)}
                      >
                        {option.label}
                        {option.description && <span className="text-muted-foreground">({option.description})</span>}
                      </Button>
                    ))}
                  </div>
                </div>
              )}
              {confirmation && (
                <div className="rounded-lg border border-amber-500/50 bg-amber-500/10 p-4" data-app-assistant-confirmation={confirmation.id}>
                  <div className="text-sm font-semibold">Approval needed: {approvalName(confirmation.toolName)}</div>
                  {confirmation.arguments && (
                    <pre className="mt-3 max-h-40 overflow-auto whitespace-pre-wrap break-all rounded-md bg-background/70 p-3 font-mono text-xs text-muted-foreground">{approvalDetails(confirmation.arguments)}</pre>
                  )}
                  <div className="mt-4 flex gap-2">
                    <Button size="sm" onClick={() => onConfirm?.('allowOnce')}>Approve</Button>
                    <Button variant="outline" size="sm" onClick={() => onConfirm?.('deny')}>Reject</Button>
                  </div>
                </div>
              )}
            </div>
          </div>
          </div>
          <form
            className={`assistant-conversation-composer shrink-0 bg-card ${expanded ? 'px-4 sm:px-8' : ''}`}
            data-assistant-conversation-composer
            onDoubleClick={event => {
              if (!(event.target instanceof HTMLElement && event.target.closest('button'))) setExpanded(true)
            }}
            onSubmit={event => { event.preventDefault(); void send(draft) }}
          >
            <div className={`assistant-conversation-input mx-auto flex w-full ${expanded ? 'max-w-3xl' : 'max-w-none'} items-center gap-2 border border-input bg-background shadow-sm focus-within:border-ring focus-within:ring-2 focus-within:ring-ring/30`}>
              <Sparkles className="size-4 shrink-0 text-chart-4" aria-hidden="true" />
              <Input
                className="h-8 min-w-0 flex-1 select-text border-0 bg-transparent px-2 text-sm shadow-none focus-visible:ring-0"
                aria-label="Workbench Assistant message"
                value={draft}
                onChange={event => setDraft(event.target.value)}
                placeholder={expanded ? 'Ask Workbench Assistant…' : state.busy ? 'Assistant is working…' : compactReply?.slice(0, 160) ?? 'Ask Workbench Assistant…'}
                title={!expanded && compactReply ? compactReply : undefined}
                disabled={state.busy}
              />
              {confirmation && <span className="shrink-0 text-xs text-amber-700 dark:text-amber-300" role="status">Approval needed</span>}
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    type="button"
                    aria-label={confirmation ? 'Review Workbench Assistant approval' : expanded ? 'Hide Workbench Assistant conversation' : 'Open Workbench Assistant'}
                    aria-expanded={expanded}
                    aria-controls="workbench-assistant-conversation"
                    onClick={() => setExpanded(previous => confirmation ? true : !previous)}
                  >
                    <MessageSquareText aria-hidden="true" />
                  </Button>
                </TooltipTrigger>
                <TooltipContent>{confirmation ? 'Review approval' : expanded ? 'Collapse conversation' : 'Open conversation'}</TooltipContent>
              </Tooltip>
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button size="icon-sm" aria-label="Send assistant message" type="submit" disabled={state.busy || !draft.trim()}>
                    {state.busy ? <Loader2 className="animate-spin" aria-hidden="true" /> : <Send aria-hidden="true" />}
                  </Button>
                </TooltipTrigger>
                <TooltipContent>Send message</TooltipContent>
              </Tooltip>
            </div>
          </form>
          {expanded && (
            <div
              className="assistant-conversation-resize-handle flex shrink-0 items-center justify-center"
              data-assistant-resize-handle
              role="separator"
              aria-label="Resize Workbench Assistant conversation"
              aria-orientation="horizontal"
              aria-valuemin={220}
              aria-valuemax={Math.max(220, window.innerHeight - 88)}
              aria-valuenow={Math.round(panelHeight ?? Math.min(window.innerHeight * 0.65, 720, window.innerHeight - 88))}
              tabIndex={0}
              onPointerDown={event => {
                if (event.button !== 0) return
                event.preventDefault()
                resizeStart.current = {
                  pointerId: event.pointerId,
                  y: event.clientY,
                  height: event.currentTarget.parentElement!.getBoundingClientRect().height,
                }
                setResizing(true)
                event.currentTarget.setPointerCapture(event.pointerId)
              }}
              onPointerMove={event => {
                const start = resizeStart.current
                if (!start || start.pointerId !== event.pointerId) return
                const top = event.currentTarget.parentElement!.getBoundingClientRect().top
                setPanelHeight(clampConversationHeight(start.height + event.clientY - start.y, top))
              }}
              onPointerUp={event => {
                if (resizeStart.current?.pointerId !== event.pointerId) return
                resizeStart.current = null
                setResizing(false)
                event.currentTarget.releasePointerCapture(event.pointerId)
              }}
              onPointerCancel={() => {
                resizeStart.current = null
                setResizing(false)
              }}
              onKeyDown={event => {
                const current = panelHeight ?? event.currentTarget.parentElement!.getBoundingClientRect().height
                const top = event.currentTarget.parentElement!.getBoundingClientRect().top
                const next = event.key === 'ArrowUp' ? current - 24 : event.key === 'ArrowDown' ? current + 24 : null
                if (next === null) return
                event.preventDefault()
                setPanelHeight(clampConversationHeight(next, top))
              }}
            >
              <span className="h-1 w-10 rounded-full bg-border" aria-hidden="true" />
            </div>
          )}
        </aside>
      </div>
    </TooltipProvider>
  )
}
