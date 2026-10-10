import type { ReactNode } from 'react'
import { formatRelativeTime } from './TaskSessionsDisclosure'

export type SessionListItemModel = {
  title: string
  /** The tasks the conversation is related to. Empty means it is related to none. */
  taskTitles: string[]
  deviceName?: string
  messageCount: number
  turnCount: number
  updatedAt: string
}

type Props = {
  model: SessionListItemModel
  actions?: ReactNode
  onOpen?: () => void
}

/** One conversation as a row of the worktree's conversation table, the list half of the same view. */
export default function WorktreeSessionListItem({ model, actions, onOpen }: Props) {
  return <tr data-testid="session-list-item" className="border-b hover:bg-accent/40">
    <th scope="row" className="max-w-0 truncate px-2 py-2 text-left font-medium" title={model.title}>
      {onOpen
        ? <button type="button" className="max-w-full truncate text-left hover:underline" onClick={onOpen}>{model.title}</button>
        : model.title}
    </th>
    <td className="max-w-0 truncate px-2 py-2 text-muted-foreground" title={model.taskTitles.join(', ')}>
      {model.taskTitles.length === 0 ? 'No task' : model.taskTitles.join(', ')}
    </td>
    <td className="truncate px-2 py-2 text-muted-foreground" title={model.deviceName}>{model.deviceName || '—'}</td>
    <td className="px-2 py-2 text-center tabular-nums">{model.turnCount}</td>
    <td className="px-2 py-2 text-muted-foreground" title={model.updatedAt}>{formatRelativeTime(model.updatedAt)}</td>
    <td className="px-2 py-2"><div className="flex items-center justify-end gap-1 whitespace-nowrap">{actions}</div></td>
  </tr>
}
