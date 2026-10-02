import type { EngineeringTask } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'

type Props = {
  open: boolean
  /** The selected target's tasks, any of which the new conversation may be bound to. */
  tasks: EngineeringTask[]
  onChoose: (task: EngineeringTask) => void
  onClose: () => void
}

/**
 * Asks which of a target's tasks a new conversation belongs to.
 *
 * The navigator's `SESSIONS` header starts a conversation for the selected target, and a conversation
 * has to be bound to a task to appear in that section at all, so when the worktree's active task is not
 * one of this target's tasks the user says which task it is (ADR-0009).
 */
export default function ChooseConversationTaskDialog({ open, tasks, onChoose, onClose }: Props) {
  return (
    <Dialog open={open} onOpenChange={value => { if (!value) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Start a conversation</DialogTitle>
          <DialogDescription>
            Choose the task this conversation belongs to. Its title is what the task shows, and a
            conversation without a task is not listed in the navigator.
          </DialogDescription>
        </DialogHeader>
        <div className="flex max-h-72 flex-col gap-1 overflow-y-auto">
          {tasks.map(task => (
            <Button
              key={task.taskId}
              type="button"
              variant="ghost"
              className="justify-start"
              data-conversation-task={task.taskId}
              onClick={() => onChoose(task)}
            >
              {task.title}
            </Button>
          ))}
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
