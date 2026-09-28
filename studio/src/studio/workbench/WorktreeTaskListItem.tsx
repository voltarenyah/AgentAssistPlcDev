import { Fragment, useState, type ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { TaskBriefContent, type TaskBriefModel } from './TaskBriefDisclosure'

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
  const [briefOpen, setBriefOpen] = useState(false)

  return (
    <Fragment>
      <tr data-testid="task-list-item" className="border-b hover:bg-accent/40">
        <th scope="row" className="px-2 py-2 text-left font-medium">
          <span className="block truncate" title={model.title}>{model.title}</span>
          <span className="mt-0.5 flex items-center gap-1 text-muted-foreground">
            <span className="truncate font-normal" title={`${model.scope} scope`}>{model.scope}</span>
            <span aria-hidden="true">·</span>
            <Button type="button" variant="ghost" size="xs" aria-label={`${briefOpen ? 'Hide' : 'View'} brief for ${model.title}`} aria-expanded={briefOpen} onClick={() => setBriefOpen(!briefOpen)}>{briefOpen ? 'Hide brief' : 'View brief'}</Button>
          </span>
        </th>
        <td className="px-2 py-2">{statusControl ?? <Badge variant="outline">{model.status}</Badge>}</td>
        <td className="truncate px-2 py-2 text-muted-foreground" title={model.type}>{model.type}</td>
        <td className="truncate px-2 py-2 text-muted-foreground" title={model.deviceName}>{model.deviceName || '—'}</td>
        <td className="px-2 py-2"><div className="flex items-center justify-end gap-1 whitespace-nowrap">{actions}</div></td>
      </tr>
      {briefOpen && <tr className="border-b bg-muted/30"><td colSpan={5} className="px-4 py-3 text-xs"><TaskBriefContent brief={model.brief} /></td></tr>}
    </Fragment>
  )
}
