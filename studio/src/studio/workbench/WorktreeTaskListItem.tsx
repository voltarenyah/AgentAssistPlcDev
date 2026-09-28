import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import TaskBriefDisclosure, { type TaskBriefModel } from './TaskBriefDisclosure'

export type TaskListItemModel = {
  title: string
  status: string
  scope: string
  type: string
  deviceName?: string
  brief: TaskBriefModel
}

type Props = {
  model: TaskListItemModel
  statusControl?: ReactNode
  actions?: ReactNode
}

export default function WorktreeTaskListItem({ model, statusControl, actions }: Props) {
  return (
    <li data-testid="task-list-item">
      <div className="px-4 py-2.5">
        <div className="flex items-start gap-3">
          <div className="min-w-0 flex-1 truncate text-sm font-medium" title={model.title}>{model.title}</div>
          {statusControl ?? <Badge variant="outline">{model.status}</Badge>}
        </div>
        <div className="mt-1 flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
            <span>{model.type}</span><span aria-hidden="true">·</span><span>{model.scope} scope</span>
            {model.deviceName && <><span aria-hidden="true">·</span><span>PLC · {model.deviceName}</span></>}
          </div>
          {actions && <div className="flex flex-wrap items-center gap-1.5">{actions}</div>}
        </div>
      </div>
      <TaskBriefDisclosure taskTitle={model.title} brief={model.brief} />
    </li>
  )
}
