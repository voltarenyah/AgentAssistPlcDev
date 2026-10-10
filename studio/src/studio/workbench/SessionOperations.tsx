import { useState, type ReactNode } from 'react'
import { Check } from 'lucide-react'
import type { ChatSessionInfo, EngineeringTask } from '@/api/client'
import { sessionTaskIds } from '@/api/client'
import { Button } from '@/components/ui/button'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'

/** The title a conversation shows, falling back the way its task surface already does. */
export const conversationTitle = (session: ChatSessionInfo) =>
  session.title?.trim() || session.firstUserMessage?.trim() || 'Untitled conversation'

/** The picker's value for "related to these tasks, assigned to none of them". */
const NO_ASSIGNMENT = 'none'

export type SessionOperationsOptions = {
  /**
   * The worktree's tasks. The picker offers the ones that can own a conversation at all — a session
   * resolves through a device, so a hardware or untargeted task is never a valid relation (ADR-0009).
   */
  tasks: EngineeringTask[]
  onRename?: (session: ChatSessionInfo, title: string) => void
  /**
   * Writes the conversation's whole relation set. The picker always names the assignment it is
   * showing, including naming none — a caller that receives `null` writes a conversation related to
   * its tasks and assigned to none of them, rather than leaving the server to choose one
   * (ADR-0014, AC-019).
   */
  onSetTasks?: (session: ChatSessionInfo, taskIds: string[], primaryTaskId: string | null) => void
  onDelete?: (session: ChatSessionInfo) => void
}

export type SessionOperations = {
  /** Opens the rename dialog on the conversation. */
  openRename: (session: ChatSessionInfo) => void
  /** Opens the task-relation picker on the conversation. */
  openBindTasks: (session: ChatSessionInfo) => void
  /** Asks for confirmation, then deletes. A conversation no task is related to says so. */
  confirmDelete: (session: ChatSessionInfo) => void
  /** The dialogs themselves. Render them once per surface that calls the operations above. */
  dialogs: ReactNode
}

/**
 * The operations every surface that lists a conversation performs on one — renaming it, editing the
 * set of tasks it is related to, and deleting it — held once so a second surface that lists
 * conversations cannot offer a diverging copy of them.
 *
 * The picker opens on the conversation's current relations and on the task it is assigned to, so every
 * check and the assignment both reflect state the conversation already has, an apply that changes
 * nothing writes the same thing back, and a relation the picker cannot offer — which the write path
 * would refuse to create — is preserved instead of being dropped by a set that never showed it. One
 * apply writes the whole set, which is what keeps a conversation from being left half-linked
 * (ADR-0014, AC-019).
 */
export function useSessionOperations({ tasks, onRename, onSetTasks, onDelete }: SessionOperationsOptions): SessionOperations {
  const [renameSession, setRenameSession] = useState<ChatSessionInfo | null>(null)
  const [renameTitle, setRenameTitle] = useState('')
  const [bindSession, setBindSession] = useState<ChatSessionInfo | null>(null)
  const [bindQuery, setBindQuery] = useState('')
  const [bindSelection, setBindSelection] = useState<string[]>([])
  /** The task the conversation is assigned to as the picker is showing it, or null for none. */
  const [bindPrimary, setBindPrimary] = useState<string | null>(null)
  const bindableTasks = tasks.filter(task => task.deviceId)
  const checkedTasks = bindableTasks.filter(task => bindSelection.includes(task.taskId))

  const openRename = (session: ChatSessionInfo) => {
    setRenameTitle(conversationTitle(session))
    setRenameSession(session)
  }

  const saveRename = () => {
    if (!renameSession || !renameTitle.trim()) return
    onRename?.(renameSession, renameTitle.trim())
    setRenameSession(null)
  }

  const openBindTasks = (session: ChatSessionInfo) => {
    setBindQuery('')
    setBindSelection(sessionTaskIds(session))
    setBindPrimary(session.taskId ?? null)
    setBindSession(session)
  }

  /**
   * A click sets a check; a second click on a checked task clears it (AC-019). Clearing the task the
   * conversation is assigned to leaves it assigned to nothing rather than handing the assignment to
   * whichever task happens to be next: being assigned is a statement the user makes, so the picker
   * never makes it on their behalf.
   */
  const toggleBindTask = (taskId: string) => {
    setBindSelection(previous => previous.includes(taskId)
      ? previous.filter(id => id !== taskId)
      : [...previous, taskId])
    setBindPrimary(current => current === taskId ? null : current)
  }

  const closeBindTasks = () => {
    setBindSession(null)
    setBindSelection([])
    setBindPrimary(null)
  }

  /** One apply writes the set and the assignment, so the conversation is never half-linked. */
  const applyBindTasks = () => {
    if (!bindSession) return
    onSetTasks?.(bindSession, bindSelection, bindPrimary)
    closeBindTasks()
  }

  /**
   * Deleting a related conversation also removes the graph edges that record which tasks it belonged
   * to, so say what it costs. A conversation no task is related to has no such links to lose, and one
   * several tasks share loses all of them (AC-017).
   */
  const confirmDelete = (session: ChatSessionInfo) => {
    const named = conversationTitle(session)
    const cost = sessionTaskIds(session).length === 0
      ? ' A deleted conversation cannot be recovered.'
      : ' Its links to the tasks it is related to are lost, and a deleted conversation cannot be recovered.'
    if (!window.confirm(`Delete "${named}"?${cost}`)) return
    onDelete?.(session)
  }

  const dialogs = (
    <>
      <Dialog open={renameSession !== null} onOpenChange={open => { if (!open) setRenameSession(null) }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Rename conversation</DialogTitle>
            <DialogDescription>Choose the title this conversation shows in the navigator and on its task.</DialogDescription>
          </DialogHeader>
          <form className="space-y-4" onSubmit={event => { event.preventDefault(); saveRename() }}>
            <Input aria-label="Conversation title" value={renameTitle} onChange={event => setRenameTitle(event.target.value)} autoFocus />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setRenameSession(null)}>Cancel</Button>
              <Button type="submit">Save</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
      {/*
        The conversation's task relations are chosen from this worktree's tasks, never typed in. One
        check per task that can own a conversation: a click sets it, a second click clears it, and one
        apply writes the whole set (ADR-0014, AC-019). The footer sits inside the dialog's cmdk root but
        outside its list, so it takes no part in the search and stays put while the list scrolls.
      */}
      <CommandDialog
        open={bindSession !== null}
        onOpenChange={open => { if (!open) closeBindTasks() }}
        title="Conversation tasks"
        description={bindSession
          ? `Choose the tasks “${conversationTitle(bindSession)}” is related to.`
          : 'Choose the tasks this conversation is related to.'}
      >
        <CommandInput
          value={bindQuery}
          onValueChange={setBindQuery}
          placeholder="Search this worktree's tasks"
          aria-label="Search this worktree's tasks"
        />
        <CommandList>
          <CommandEmpty>No matching tasks.</CommandEmpty>
          {bindableTasks.map(task => {
            const checked = bindSelection.includes(task.taskId)
            return (
              <CommandItem
                key={task.taskId}
                value={`${task.title} ${task.taskId}`}
                onSelect={() => toggleBindTask(task.taskId)}
                aria-checked={checked}
                aria-label={`${checked ? 'Uncheck' : 'Check'} ${task.title}`}
              >
                <Check className={`h-3.5 w-3.5 shrink-0 ${checked ? 'opacity-100' : 'opacity-0'}`} aria-hidden="true" />
                {task.title}
              </CommandItem>
            )
          })}
        </CommandList>
        {/*
          Relation and assignment are two statements, so they are two controls. A check is "this
          conversation is related to that task"; this is "this is the task it is working on", which the
          user names — including naming none, which is the state a conversation that recorded tasks
          without ever being assigned one stays in (ADR-0014, AC-019).
        */}
        <div className="flex flex-wrap items-center gap-2 border-t px-3 py-2" style={{ borderColor: 'var(--border)' }}>
          <span className="text-xs text-muted-foreground">Assigned task</span>
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            aria-label="Assigned task"
            value={bindPrimary ?? NO_ASSIGNMENT}
            onValueChange={value => setBindPrimary(!value || value === NO_ASSIGNMENT ? null : value)}
          >
            <ToggleGroupItem value={NO_ASSIGNMENT} aria-label="Assigned to no task" className="text-xs">
              None
            </ToggleGroupItem>
            {checkedTasks.map(task => (
              <ToggleGroupItem key={task.taskId} value={task.taskId} aria-label={`Assign to ${task.title}`} className="max-w-[12rem] text-xs">
                <span className="truncate">{task.title}</span>
              </ToggleGroupItem>
            ))}
          </ToggleGroup>
        </div>
        <div className="flex items-center gap-2 border-t px-3 py-2" style={{ borderColor: 'var(--border)' }}>
          <span className="mr-auto text-xs text-muted-foreground" role="status">
            {bindSelection.length === 0 ? 'No tasks checked' : `${bindSelection.length} checked`}
          </span>
          <Button type="button" variant="outline" size="sm" onClick={closeBindTasks}>Cancel</Button>
          <Button type="button" size="sm" onClick={applyBindTasks}>Apply</Button>
        </div>
      </CommandDialog>
    </>
  )

  return { openRename, openBindTasks, confirmDelete, dialogs }
}
