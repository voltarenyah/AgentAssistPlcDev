import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { formatRelativeTime } from './TaskSessionsDisclosure'

export type SessionCardModel = {
  title: string
  /**
   * The tasks the conversation is related to, primary first. The id is what identifies one, because
   * two tasks of a worktree can carry the same title.
   */
  tasks: Array<{ taskId: string; title: string }>
  deviceName?: string
  messageCount: number
  turnCount: number
  updatedAt: string
}

type Props = {
  model: SessionCardModel
  actions?: ReactNode
  onOpen?: () => void
}

/**
 * One conversation as a card, the card half of the worktree's conversation list. It shows what every
 * surface that lists a conversation shows — its title, how long ago it changed, and the device and
 * tasks it belongs to — so a conversation reads the same wherever it appears.
 */
export default function WorktreeSessionCard({ model, actions, onOpen }: Props) {
  return (
    <Card data-testid="session-card" className="w-full max-w-md min-w-0 gap-0 py-0">
      <CardHeader className="gap-2 px-4 py-3">
        <div className="flex flex-wrap items-start gap-3">
          <CardTitle className="min-w-0 flex-1 break-words text-sm leading-5">
            {onOpen
              ? <button type="button" className="text-left hover:underline" onClick={onOpen}>{model.title}</button>
              : model.title}
          </CardTitle>
          <time className="shrink-0 text-[10px] text-muted-foreground" dateTime={model.updatedAt} title={model.updatedAt}>
            {formatRelativeTime(model.updatedAt)}
          </time>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {model.tasks.length === 0
            ? <Badge variant="outline">No task</Badge>
            : model.tasks.map(task => <Badge key={task.taskId} variant="secondary" className="max-w-full truncate">{task.title}</Badge>)}
          {model.deviceName && <span className="text-xs text-muted-foreground">PLC · {model.deviceName}</span>}
          <span className="text-xs text-muted-foreground">
            {model.turnCount} turn{model.turnCount === 1 ? '' : 's'} · {model.messageCount} message{model.messageCount === 1 ? '' : 's'}
          </span>
        </div>
      </CardHeader>
      {actions && <CardFooter className="justify-end gap-2 border-t px-4 py-2 [.border-t]:pt-2">{actions}</CardFooter>}
    </Card>
  )
}
