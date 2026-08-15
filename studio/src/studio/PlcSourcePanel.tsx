import { useMemo, useState } from 'react'
import {
  AlertCircle,
  Boxes,
  ChevronDown,
  ChevronRight,
  Code2,
  Database,
  FileCode2,
  GitCompareArrows,
  Loader2,
  MessageSquare,
  Search,
  SquareArrowOutUpRight,
  Table2,
} from 'lucide-react'
import { toast } from 'sonner'
import * as api from '@/api/client'
import type { SourceObjectComparison, SourceObjectInfo } from '@/api/client'
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuLabel,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu'
import { showErrorToast } from '@/components/ui/toast'
import type { DeviceViewState } from './deviceSnapshot'
import {
  countSourceObjectsByType,
  filterSourceObjects,
  resolveSourceObjects,
  SOURCE_TYPE_FILTERS,
  type SourceTypeFilter,
} from './plcSourceState'
import PlcSourceCompareDialog from './PlcSourceCompareDialog'

type Props = {
  workbenchId: string
  worktreeId: string
  deviceId: string
  deviceView: DeviceViewState | null
  onChatWithAgent: (item: SourceObjectInfo) => void
  onSnapshotReload: () => void
}

const errorMessage = (error: unknown) => error instanceof Error ? error.message : String(error)

const categoryIcon = (category: string) => {
  switch (category) {
    case 'DB': return Database
    case 'Tags': return Table2
    case 'UDT': return Boxes
    default: return FileCode2
  }
}

const formatDate = (value: string | null) => {
  if (!value) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

export default function PlcSourcePanel({
  workbenchId,
  worktreeId,
  deviceId,
  deviceView,
  onChatWithAgent,
  onSnapshotReload,
}: Props) {
  const [typeFilter, setTypeFilter] = useState<SourceTypeFilter>('all')
  const [query, setQuery] = useState('')
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [pendingAction, setPendingAction] = useState<string | null>(null)
  const [comparison, setComparison] = useState<SourceObjectComparison | null>(null)

  const items = useMemo(
    () => resolveSourceObjects(deviceView?.sourceObjects, deviceView?.blocks),
    [deviceView],
  )
  const counts = useMemo(() => countSourceObjectsByType(items), [items])
  const visible = useMemo(() => filterSourceObjects(items, typeFilter, query), [items, typeFilter, query])

  const openInTia = async (item: SourceObjectInfo) => {
    setPendingAction(`open:${item.id}`)
    try {
      await api.openSourceInTia(workbenchId, worktreeId, deviceId, item.relativePath)
      toast.success(`Opened ${item.category} "${item.name}" in TIA Portal.`)
    } catch (error) {
      showErrorToast(errorMessage(error))
    } finally {
      setPendingAction(null)
    }
  }

  const compareWithTia = async (item: SourceObjectInfo) => {
    setPendingAction(`compare:${item.id}`)
    try {
      setComparison(await api.compareSourceWithTia(workbenchId, worktreeId, deviceId, item.relativePath))
    } catch (error) {
      showErrorToast(errorMessage(error))
    } finally {
      setPendingAction(null)
    }
  }

  return (
    <div className="mx-auto flex h-full min-h-0 w-full max-w-6xl flex-col gap-4 p-5">
      <section className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
        <div className="border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center gap-2">
            <Code2 className="h-4 w-4 text-chart-3" />
            <span className="text-[10px] font-semibold">PLC source objects</span>
            <span className="ml-auto text-[9px] text-muted-foreground">{items.length} items</span>
          </div>
          <div className="mt-2 flex flex-wrap gap-1">
            {SOURCE_TYPE_FILTERS.map(filter => (
              <button
                key={filter.id}
                type="button"
                className={`flex h-6 items-center gap-1 rounded-md border px-2 text-[9px] ${typeFilter === filter.id ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50'}`}
                style={{ borderColor: 'var(--border)' }}
                onClick={() => setTypeFilter(filter.id)}
              >
                {filter.label}
                <span className="text-[8px] text-muted-foreground">{counts[filter.id]}</span>
              </button>
            ))}
          </div>
          <div className="relative mt-2">
            <Search className="pointer-events-none absolute left-2 top-1/2 h-3 w-3 -translate-y-1/2 text-muted-foreground" />
            <input
              className="field-input w-full pl-7"
              value={query}
              onChange={event => setQuery(event.target.value)}
              placeholder="Filter by name, path, or type…"
            />
          </div>
        </div>
        {deviceView?.diagnostics.map(diagnostic => (
          <div
            key={diagnostic}
            className="flex items-start gap-2 border-b bg-amber-500/8 px-4 py-2 text-[9px] text-amber-700 dark:text-amber-300"
            style={{ borderColor: 'var(--border)' }}
          >
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span className="break-all">{diagnostic}</span>
          </div>
        ))}
        {items.length === 0 ? (
          <div className="p-8 text-center text-[10px] text-muted-foreground">
            No PLC source objects. Export or refresh the device first.
          </div>
        ) : visible.length === 0 ? (
          <div className="p-8 text-center text-[10px] text-muted-foreground">No source objects match this filter.</div>
        ) : (
          <div className="scrollbar-sleek min-h-0 flex-1 divide-y overflow-y-auto" style={{ borderColor: 'var(--border)' }}>
            {visible.map(item => {
              const Icon = categoryIcon(item.category)
              const expanded = expandedId === item.id
              const busyAction = pendingAction?.endsWith(`:${item.id}`) ? pendingAction : null
              return (
                <div key={item.id}>
                  <ContextMenu>
                    <ContextMenuTrigger asChild>
                      <div
                        className="flex w-full cursor-pointer items-center gap-3 px-4 py-2 text-left hover:bg-accent/40"
                        onClick={() => setExpandedId(expanded ? null : item.id)}
                      >
                        {expanded
                          ? <ChevronDown className="h-3 w-3 shrink-0 text-muted-foreground" />
                          : <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground" />}
                        <Icon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                        <span className="min-w-0 flex-1 truncate text-[10px]">{item.name}</span>
                        {busyAction && <Loader2 className="h-3 w-3 shrink-0 animate-spin text-muted-foreground" />}
                        <span className="font-mono text-[9px] text-muted-foreground">
                          {item.category}{item.number ?? ''}
                        </span>
                        {item.programmingLanguage && (
                          <span className="text-[9px] text-muted-foreground">{item.programmingLanguage}</span>
                        )}
                        {item.groupPath && (
                          <span className="max-w-[160px] truncate text-[9px] text-muted-foreground">{item.groupPath}</span>
                        )}
                      </div>
                    </ContextMenuTrigger>
                    <ContextMenuContent>
                      <ContextMenuLabel>{item.category} · {item.name}</ContextMenuLabel>
                      <ContextMenuItem disabled={Boolean(pendingAction)} onSelect={() => void openInTia(item)}>
                        <SquareArrowOutUpRight className="h-3.5 w-3.5" />
                        Open in TIA
                      </ContextMenuItem>
                      <ContextMenuItem disabled={Boolean(pendingAction)} onSelect={() => void compareWithTia(item)}>
                        <GitCompareArrows className="h-3.5 w-3.5" />
                        Compare with TIA
                      </ContextMenuItem>
                      <ContextMenuSeparator />
                      <ContextMenuItem onSelect={() => onChatWithAgent(item)}>
                        <MessageSquare className="h-3.5 w-3.5" />
                        Chat with Agent
                      </ContextMenuItem>
                    </ContextMenuContent>
                  </ContextMenu>
                  {expanded && (
                    <div
                      className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 border-t bg-muted/20 px-11 py-2 text-[9px]"
                      style={{ borderColor: 'var(--border)' }}
                    >
                      <span className="text-muted-foreground">Path</span>
                      <span className="break-all font-mono">{item.relativePath}</span>
                      {item.groupPath && (
                        <>
                          <span className="text-muted-foreground">Group</span>
                          <span>{item.groupPath}</span>
                        </>
                      )}
                      {formatDate(item.modifiedDate) && (
                        <>
                          <span className="text-muted-foreground">Modified</span>
                          <span>{formatDate(item.modifiedDate)}</span>
                        </>
                      )}
                      {item.status && (
                        <>
                          <span className="text-muted-foreground">Export status</span>
                          <span>{item.status}</span>
                        </>
                      )}
                      {item.isKnowHowProtected != null && (
                        <>
                          <span className="text-muted-foreground">Know-how protected</span>
                          <span>{item.isKnowHowProtected ? 'Yes' : 'No'}</span>
                        </>
                      )}
                      {item.contentHash && (
                        <>
                          <span className="text-muted-foreground">Content hash</span>
                          <span className="break-all font-mono">{item.contentHash.slice(0, 16)}…</span>
                        </>
                      )}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </section>
      {comparison && (
        <PlcSourceCompareDialog
          workbenchId={workbenchId}
          worktreeId={worktreeId}
          deviceId={deviceId}
          comparison={comparison}
          onClose={() => setComparison(null)}
          onAccepted={() => {
            setComparison(null)
            onSnapshotReload()
          }}
        />
      )}
    </div>
  )
}
