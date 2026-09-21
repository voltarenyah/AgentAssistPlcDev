import { Download, Edit3, MessageSquare, Plus, Trash2, X } from 'lucide-react'
import { Button, Input } from '@notion-kit/ui/primitives'
import { useState } from 'react'
import type { ChatSessionInfo } from '@/api/client'

type Props = {
  sessions: ChatSessionInfo[]
  activeSessionId: string | null
  busy: boolean
  hidden: boolean
  onCreate: () => void
  onActivate: (sessionId: string) => void
  onRename: (sessionId: string, title: string) => void
  onRemove: (sessionId: string) => void
  onExport: (sessionId: string) => void
  onSetTask: (sessionId: string, taskId: string | null) => void
}

const displayDate = (value: string) => {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

export default function SessionDock({
  sessions,
  activeSessionId,
  busy,
  hidden,
  onCreate,
  onActivate,
  onRename,
  onRemove,
  onExport,
  onSetTask,
}: Props) {
  const [editingId, setEditingId] = useState<string | null>(null)
  const [removeId, setRemoveId] = useState<string | null>(null)

  return (
    <aside
      hidden={hidden}
      className="flex h-full w-full shrink-0 flex-col border-l bg-sidebar"
      style={{ borderColor: 'var(--border)' }}
    >
      <div className="flex h-12 items-center gap-2 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        <MessageSquare className="h-3.5 w-3.5 text-chart-3" />
        <h2 className="text-[10px] font-semibold">AI sessions</h2>
        <span className="ml-auto text-[9px] text-muted-foreground">{sessions.length}</span>
        <Button variant="nav-icon" disabled={busy} onClick={onCreate} aria-label="New session">
          <Plus className="h-3.5 w-3.5" />
        </Button>
      </div>
      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
        {sessions.length === 0 ? (
          <div className="grid h-full place-items-center px-5 text-center text-[10px] text-muted-foreground">
            <div>
              <MessageSquare className="mx-auto mb-2 h-5 w-5" />
              No saved sessions
            </div>
          </div>
        ) : (
          <div className="space-y-1.5">
            {sessions.map(session => {
              const active = session.sessionId === activeSessionId
              return (
                <div
                  key={session.sessionId}
                  className={`rounded-md border p-2 ${active ? 'bg-accent' : 'bg-background'}`}
                  style={{ borderColor: 'var(--border)' }}
                >
                  {editingId === session.sessionId ? (
                    <form
                      data-session-rename={session.sessionId}
                      className="flex gap-1"
                      onSubmit={event => {
                        event.preventDefault()
                        const data = new FormData(event.currentTarget)
                        const title = data.get('session-title')?.toString().trim() ?? ''
                        if (!title) return
                        onRename(session.sessionId, title)
                        setEditingId(null)
                      }}
                    >
                      <Input
                        name="session-title"
                        className="h-7! min-w-0 flex-1"
                        defaultValue={session.title}
                        autoFocus
                      />
                      {/* notion-kit's Button sets its own type, so the implicit form submit is
                          made explicit here. */}
                      <Button type="submit" variant="primary" size="sm" className="h-7! px-2!" disabled={busy}>
                        Save
                      </Button>
                      <Button type="button" variant="nav-icon" onClick={() => setEditingId(null)}>
                        <X className="h-3.5 w-3.5" />
                      </Button>
                    </form>
                  ) : (
                    <>
                      <div className="flex items-start gap-1">
                        <button
                          data-session-id={session.sessionId}
                          className="min-w-0 flex-1 text-left"
                          disabled={busy}
                          onClick={() => onActivate(session.sessionId)}
                        >
                          <div className="truncate text-[10px] font-medium">{session.title}</div>
                          <div className="mt-1 text-[8px] text-muted-foreground">
                            {session.turnCount} turn{session.turnCount === 1 ? '' : 's'} · {displayDate(session.updatedAt)}
                          </div>
                        </button>
                        <Button variant="nav-icon" className="h-6! w-6!" aria-label={`Rename ${session.title}`} disabled={busy} onClick={() => setEditingId(session.sessionId)}>
                          <Edit3 className="h-3 w-3" />
                        </Button>
                        <Button variant="nav-icon" className="h-6! w-6!" aria-label={`Export ${session.title}`} disabled={busy} onClick={() => onExport(session.sessionId)}>
                          <Download className="h-3 w-3" />
                        </Button>
                        <Button variant="nav-icon" className="h-6! w-6!" aria-label={`Delete ${session.title}`} disabled={busy} onClick={() => setRemoveId(session.sessionId)}>
                          <Trash2 className="h-3 w-3" />
                        </Button>
                      </div>
                      <div className="mt-2 flex items-center gap-1 text-[9px]">
                        <span aria-label={`Task for ${session.title}`} className="min-w-0 flex-1 truncate text-muted-foreground">
                          {session.taskId ? `Task: ${session.taskId} (${session.taskProvenance === 'manual' ? 'Manual' : 'Default'})` : 'Unassigned legacy session'}
                        </span>
                        <Button variant="primary" size="sm" className="h-6! px-2!" disabled={busy} aria-label={`${session.taskId ? 'Reassign' : 'Attach'} task for ${session.title}`} onClick={() => {
                          const taskId = window.prompt('Task ID (leave blank to clear)', session.taskId ?? '')?.trim() ?? ''
                          onSetTask(session.sessionId, taskId || null)
                        }}>{session.taskId ? 'Reassign' : 'Attach'}</Button>
                        {session.taskId && <Button variant="primary" size="sm" className="h-6! px-2!" disabled={busy} aria-label={`Remove task from ${session.title}`} onClick={() => onSetTask(session.sessionId, null)}>Remove</Button>}
                      </div>
                      {removeId === session.sessionId && (
                        <div className="mt-2 flex items-center gap-1 border-t pt-2" style={{ borderColor: 'var(--border)' }}>
                          <span className="min-w-0 flex-1 text-[9px] text-muted-foreground">Delete?</span>
                          <Button variant="primary" size="sm" className="h-6! px-2!" onClick={() => setRemoveId(null)}>Cancel</Button>
                          <Button variant="blue" size="sm" className="h-6! px-2!" onClick={() => {
                            onRemove(session.sessionId)
                            setRemoveId(null)
                          }}>
                            Delete
                          </Button>
                        </div>
                      )}
                    </>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </div>
    </aside>
  )
}
