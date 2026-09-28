import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import TaskBriefDisclosure, { type TaskBriefModel } from './TaskBriefDisclosure'

export type TaskCardModel = {
  title: string
  status: string
  scope: string
  type: string
  deviceName?: string
  brief: TaskBriefModel
}

type Props = {
  model: TaskCardModel
  statusControl?: ReactNode
  actions?: ReactNode
}

export default function WorktreeTaskCard({ model, statusControl, actions }: Props) {
  return (
    <Card data-testid="task-card" className="gap-0 py-0">
      <CardHeader className="gap-2 px-4 py-3">
        <div className="flex flex-wrap items-start gap-3">
          <CardTitle className="min-w-0 flex-1 break-words text-sm leading-5">{model.title}</CardTitle>
          {statusControl ?? <Badge variant="outline">{model.status}</Badge>}
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          <Badge variant="secondary">{model.type}</Badge>
          <Badge variant="outline">{model.scope} scope</Badge>
          {model.deviceName && <span className="text-xs text-muted-foreground">PLC · {model.deviceName}</span>}
        </div>
      </CardHeader>
      <TaskBriefDisclosure taskTitle={model.title} brief={model.brief} />
      {actions && <CardFooter className="justify-end gap-2 border-t px-4 py-2 [.border-t]:pt-2">{actions}</CardFooter>}
    </Card>
  )
}
