import { useEffect, useMemo, useState } from 'react'
import { Download, Ellipsis, LayoutGrid, List, Loader2, MessageSquareText, Pencil, Search, Trash2, X } from 'lucide-react'
import * as api from '@/api/client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { conversationTitle, useSessionOperations } from './SessionOperations'
import WorktreeSessionCard, { type SessionCardModel } from './WorktreeSessionCard'
import WorktreeSessionListItem, { type SessionListItemModel } from './WorktreeSessionListItem'

export type SessionViewMode = 'cards' | 'list'

type Props = {
  workbenchId: string
  worktreeId: string
  /**
   * The worktree's tasks, used to name the tasks a conversation is related to. The picker offers the
   * device-bound subset of this list, because a session resolves through a device.
   */
  tasks: api.EngineeringTask[]
  viewMode: SessionViewMode
  onViewModeChange: (mode: SessionViewMode) => void
  onOpenSession?: (session: api.ChatSessionInfo) => void
  onRenameSession?: (session: api.ChatSessionInfo, title: string) => void | Promise<void>
  onExportSession?: (session: api.ChatSessionInfo) => void | Promise<void>
  onSetSessionTasks?: (session: api.ChatSessionInfo, taskIds: string[], primaryTaskId: string | null) => void | Promise<void>
  onDeleteSession?: (session: api.ChatSessionInfo) => void | Promise<void>
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

/** Every task a conversation names, primary first, resolved to the title the task surface shows. */
const sessionTasks = (session: api.ChatSessionInfo, tasksById: Map<string, api.EngineeringTask>) => {
  const ids = api.sessionTaskIds(session)
  const primary = session.taskId && ids.includes(session.taskId) ? session.taskId : null
  return [...(primary ? [primary] : []), ...ids.filter(id => id !== primary)]
    .map(taskId => ({ taskId, title: tasksById.get(taskId)?.title ?? taskId }))
}

const sessionCardModel = (session: api.ChatSessionInfo, tasksById: Map<string, api.EngineeringTask>, deviceName?: string): SessionCardModel => ({
  title: conversationTitle(session),
  tasks: sessionTasks(session, tasksById),
  deviceName,
  messageCount: session.messageCount,
  turnCount: session.turnCount,
  updatedAt: session.updatedAt,
})

const sessionListItemModel = (model: SessionCardModel): SessionListItemModel => ({
  title: model.title,
  taskTitles: model.tasks.map(task => task.title),
  deviceName: model.deviceName,
  messageCount: model.messageCount,
  turnCount: model.turnCount,
  updatedAt: model.updatedAt,
})

/** The search a conversation row matches on: its stored title and the first thing the user asked. */
const matchesQuery = (session: api.ChatSessionInfo, query: string) => {
  const needle = query.trim().toLowerCase()
  if (!needle) return true
  return `${conversationTitle(session)} ${session.firstUserMessage ?? ''}`.toLowerCase().includes(needle)
}

/**
 * The worktree's whole conversation list: every conversation it holds, whichever device owns each
 * one, in the two forms the worktree's task list already offers. It is the surface a worktree with
 * many conversations is browsed and searched from, and it owns its own load the way the task panel
 * does rather than reading the navigator's device-scoped one.
 */
export default function WorktreeSessionsPanel({
  workbenchId, worktreeId, tasks, viewMode, onViewModeChange,
  onOpenSession, onRenameSession, onExportSession, onSetSessionTasks, onDeleteSession,
}: Props) {
  const [sessions, setSessions] = useState<api.ChatSessionInfo[]>([])
  const [devices, setDevices] = useState<api.DeviceSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [reloadToken, setReloadToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    void api.listWorktreeSessions(workbenchId, worktreeId)
      .then(result => {
        if (cancelled) return
        setSessions(result)
        setLoading(false)
      })
      .catch(loadError => {
        if (cancelled) return
        setSessions([])
        setError(displayError(loadError))
        setLoading(false)
      })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId, reloadToken])

  useEffect(() => {
    let cancelled = false
    void api.listDevices(workbenchId, worktreeId)
      .then(result => { if (!cancelled) setDevices(result) })
      .catch(() => { if (!cancelled) setDevices([]) })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId])

  /**
   * The tab's own copy of the list is re-read once each operation settles, so a rename, a re-binding
   * or a delete made here is what the row shows next; the operation itself is the shell's, which
   * refreshes the lists it owns.
   */
  const reloadAfter = (action: void | Promise<void>) => {
    void Promise.resolve(action).then(() => setReloadToken(token => token + 1))
  }

  const operations = useSessionOperations({
    tasks,
    onRename: (session, title) => reloadAfter(onRenameSession?.(session, title)),
    onSetTasks: (session, taskIds, primaryTaskId) => reloadAfter(onSetSessionTasks?.(session, taskIds, primaryTaskId)),
    onDelete: session => reloadAfter(onDeleteSession?.(session)),
  })

  const tasksById = useMemo(() => new Map(tasks.map(task => [task.taskId, task])), [tasks])
  const devicesById = useMemo(() => new Map(devices.map(device => [device.deviceId, device])), [devices])
  const visibleSessions = sessions.filter(session => matchesQuery(session, query))
  const deviceNameOf = (session: api.ChatSessionInfo) => session.deviceId
    ? devicesById.get(session.deviceId)?.plcName ?? session.deviceId
    : undefined

  const actionsOf = (session: api.ChatSessionInfo) => (
    <>
      <Button type="button" variant="secondary" size="xs" aria-label={`Open conversation ${conversationTitle(session)}`} onClick={() => onOpenSession?.(session)}>
        Open
      </Button>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-xs" aria-label={`Conversation actions ${conversationTitle(session)}`}>
            <Ellipsis className="h-3.5 w-3.5" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuLabel>{conversationTitle(session)}</DropdownMenuLabel>
          <DropdownMenuItem onSelect={() => onOpenSession?.(session)}>
            <MessageSquareText className="h-3.5 w-3.5" />
            Open conversation
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => operations.openRename(session)}>
            <Pencil className="h-3.5 w-3.5" />
            Rename conversation
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => onExportSession?.(session)}>
            <Download className="h-3.5 w-3.5" />
            Export conversation
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={() => operations.openBindTasks(session)}>
            <MessageSquareText className="h-3.5 w-3.5" />
            Tasks…
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem variant="destructive" onSelect={() => operations.confirmDelete(session)}>
            <Trash2 className="h-3.5 w-3.5" />
            Delete conversation
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </>
  )

  const renderSession = (session: api.ChatSessionInfo) => {
    const model = sessionCardModel(session, tasksById, deviceNameOf(session))
    const key = session.sessionId
    return viewMode === 'cards'
      ? <WorktreeSessionCard
        key={key}
        model={model}
        actions={actionsOf(session)}
        onOpen={onOpenSession ? () => onOpenSession(session) : undefined}
      />
      : <WorktreeSessionListItem
        key={key}
        model={sessionListItemModel(model)}
        actions={actionsOf(session)}
        onOpen={onOpenSession ? () => onOpenSession(session) : undefined}
      />
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center gap-2 p-10 text-xs text-muted-foreground">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading conversations...
      </div>
    )
  }

  if (error) {
    return <div className="p-10 text-center text-xs text-muted-foreground">Conversations could not be loaded: {error}</div>
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <ToggleGroup type="single" value={viewMode} variant="outline" size="sm" aria-label="Conversation display mode" onValueChange={value => {
          if (value === 'cards' || value === 'list') onViewModeChange(value)
        }}>
          <ToggleGroupItem value="cards" aria-label="Card view" className="text-xs"><LayoutGrid className="h-3.5 w-3.5" /> Cards</ToggleGroupItem>
          <ToggleGroupItem value="list" aria-label="List view" className="text-xs"><List className="h-3.5 w-3.5" /> List</ToggleGroupItem>
        </ToggleGroup>
        <div className="relative min-w-0 flex-1 sm:max-w-xs">
          <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            aria-label="Search conversations"
            placeholder="Search conversations"
            className="h-8 pl-7 pr-7 text-xs"
            value={query}
            onChange={event => setQuery(event.target.value)}
          />
          {query && (
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label="Clear conversation search"
              className="absolute right-1 top-1/2 -translate-y-1/2"
              onClick={() => setQuery('')}
            >
              <X className="h-3 w-3" />
            </Button>
          )}
        </div>
      </div>

      <div className="text-xs text-muted-foreground" role="status">
        {sessions.length === 0
          ? 'This worktree has no conversations yet.'
          : `${visibleSessions.length} of ${sessions.length} conversation${sessions.length === 1 ? '' : 's'}`}
      </div>

      {sessions.length === 0 ? (
        <div className="grid place-items-center rounded-xl border border-dashed p-10 text-center" style={{ borderColor: 'var(--border)' }}>
          <MessageSquareText className="mb-3 h-6 w-6 text-muted-foreground" />
          <p className="text-xs text-muted-foreground">
            No conversations in this worktree yet. Select a device and start one from the navigator's SESSIONS section.
          </p>
        </div>
      ) : visibleSessions.length === 0 ? (
        <div className="rounded-xl border border-dashed p-6 text-center text-xs text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
          No conversation matches “{query.trim()}”.
        </div>
      ) : (
        viewMode === 'cards'
          ? <div className="grid items-start gap-3 [grid-template-columns:repeat(auto-fill,minmax(min(100%,22rem),1fr))]">{visibleSessions.map(renderSession)}</div>
          : <div className="overflow-x-auto rounded-xl border bg-card">
            <table className="w-full min-w-[720px] table-fixed border-collapse text-xs">
              <colgroup>
                <col className="w-[32%]" /><col className="w-[22%]" /><col className="w-[16%]" />
                <col className="w-[8%]" /><col className="w-[10%]" /><col className="w-[12%]" />
              </colgroup>
              <thead className="bg-muted/40 text-left text-muted-foreground">
                <tr className="border-b">
                  <th scope="col" className="px-2 py-2 font-medium">Conversation</th>
                  <th scope="col" className="px-2 py-2 font-medium">Tasks</th>
                  <th scope="col" className="px-2 py-2 font-medium">PLC</th>
                  <th scope="col" className="px-2 py-2 text-center font-medium">Turns</th>
                  <th scope="col" className="px-2 py-2 font-medium">Updated</th>
                  <th scope="col" className="px-2 py-2 text-right font-medium">Actions</th>
                </tr>
              </thead>
              <tbody>{visibleSessions.map(renderSession)}</tbody>
            </table>
          </div>
      )}

      {operations.dialogs}
    </div>
  )
}
