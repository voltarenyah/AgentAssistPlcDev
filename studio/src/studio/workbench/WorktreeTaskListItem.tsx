import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'

export type TaskListItemModel = {
  title: string
  status: string
  type: string
  deviceName?: string
  sessionsCount: number
}

type Props = {
  model: TaskListItemModel
  statusControl?: ReactNode
  actions?: ReactNode
}

export default function WorktreeTaskListItem({ model, statusControl, actions }: Props) {
  return <tr data-testid="task-list-item" className="border-b hover:bg-accent/40">
    <th scope="row" className="max-w-0 truncate px-2 py-2 text-left font-medium" title={model.title}>{model.title}</th>
    <td className="px-2 py-2">{statusControl ?? <Badge variant="outline">{model.status}</Badge>}</td>
    <td className="truncate px-2 py-2 text-muted-foreground" title={model.type}>{model.type}</td>
    <td className="truncate px-2 py-2 text-muted-foreground" title={model.deviceName}>{model.deviceName || '—'}</td>
    <td className="px-2 py-2 text-center tabular-nums">{model.sessionsCount}</td>
    <td className="px-2 py-2"><div className="flex items-center justify-end gap-1 whitespace-nowrap">{actions}</div></td>
  </tr>
}
