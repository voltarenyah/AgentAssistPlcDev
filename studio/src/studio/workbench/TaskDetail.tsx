import { AlertCircle, ExternalLink, Loader2, RefreshCw } from 'lucide-react'
import type { EngineeringTaskDetail } from '@/api/client'

export type TraceabilityItem = { id: string; edgeId: string; provenance: string; isPrimary: boolean }

type TraceabilitySectionProps = {
  title: string
  items: TraceabilityItem[]
  emptyLabel: string
  onNavigate?: (id: string) => void
  onRemove?: (item: TraceabilityItem) => void
}

const provenanceLabel = (value: string) => {
  const normalized = value.toLowerCase()
  if (normalized === 'manual') return 'Manual'
  if (normalized === 'evidence') return 'Evidence-derived'
  if (normalized === 'default') return 'Default'
  return 'Unassigned'
}

export function TraceabilitySection({ title, items, emptyLabel, onNavigate, onRemove }: TraceabilitySectionProps) {
  return (
    <section className="overflow-hidden rounded-lg border bg-card" aria-label={title} style={{ borderColor: 'var(--border)' }}>
      <header className="flex items-center border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <h3 className="text-[10px] font-semibold">{title}</h3>
        <span className="ml-auto rounded bg-muted px-1.5 py-0.5 font-mono text-[9px] text-muted-foreground">{items.length}</span>
      </header>
      {items.length === 0 ? (
        <p className="px-3 py-3 text-[9px] text-muted-foreground">{emptyLabel}</p>
      ) : (
        <ul className="divide-y" style={{ borderColor: 'var(--border)' }}>
          {items.map(item => (
            <li key={item.id} className="flex items-center gap-2 px-3 py-2 text-[9px]">
              {onNavigate ? (
                <button type="button" className="min-w-0 flex-1 truncate text-left font-mono underline-offset-2 hover:underline focus-visible:underline" aria-label={`Open ${title} ${item.id}`} onClick={() => onNavigate(item.id)}>{item.id}</button>
              ) : <span className="min-w-0 flex-1 truncate font-mono">{item.id}</span>}
              {item.isPrimary && <span className="rounded bg-muted px-1 py-0.5 text-[8px]">Primary</span>}
              <span className="shrink-0 text-muted-foreground">{provenanceLabel(item.provenance)}</span>
              {onRemove && item.provenance.toLowerCase() === 'manual' && <button type="button" className="secondary-button h-6 px-2" aria-label={`Remove ${title} ${item.id}`} onClick={() => onRemove(item)}>Remove</button>}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

type Props = {
  detail: EngineeringTaskDetail | null
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  onNavigate?: (kind: string, id: string) => void
  onRemove?: (kind: string, item: TraceabilityItem) => void
}

export default function TaskDetail({ detail, loading = false, error = null, onRetry, onNavigate, onRemove }: Props) {
  if (loading) return <div className="flex items-center justify-center gap-2 p-10 text-[10px] text-muted-foreground" role="status"><Loader2 className="h-4 w-4 animate-spin" /> Loading task traceability...</div>
  if (error) return <div className="flex items-center gap-3 rounded-lg border p-5 text-[10px] text-muted-foreground" role="alert" style={{ borderColor: 'var(--border)' }}><AlertCircle className="h-4 w-4 shrink-0 text-red-500" /><span className="min-w-0 flex-1">Task traceability could not be loaded: {error}</span>{onRetry && <button type="button" className="secondary-button h-7 text-[9px]" onClick={onRetry}><RefreshCw className="mr-1 inline h-3 w-3" /> Retry</button>}</div>
  if (!detail) return null
  const sections: Array<[string, string, TraceabilityItem[]]> = [
    ['Sessions', 'session', detail.sessions],
    ['Commits', 'commit', detail.commits],
    ['Source objects', 'sourceObject', detail.sourceObjects],
    ['SVN revisions', 'svnRevision', detail.svnRevisions],
  ]
  return <article className="space-y-4" aria-label={`Task detail: ${detail.task.title}`}>
    <header className="rounded-xl border bg-card p-4" style={{ borderColor: 'var(--border)' }}>
      <div className="flex items-start gap-2"><div className="min-w-0 flex-1"><h2 className="text-sm font-semibold">{detail.task.title}</h2><p className="mt-1 text-[9px] text-muted-foreground">{detail.task.scope === 'project' ? 'Project' : 'Worktree'} scope · {detail.task.type} · {detail.task.status}{detail.task.deviceId ? ` · PLC ${detail.task.deviceId}` : ''}</p></div><span className="inline-flex items-center gap-1 text-[9px] text-muted-foreground"><ExternalLink className="h-3 w-3" /> Traceability</span></div>
      {detail.task.description && <p className="mt-2 text-[10px] text-muted-foreground">{detail.task.description}</p>}
    </header>
    <div className="grid gap-3 md:grid-cols-2">{sections.map(([title, kind, items]) => <TraceabilitySection key={kind} title={title} items={items} emptyLabel={`No linked ${title.toLowerCase()} yet.`} onNavigate={onNavigate ? id => onNavigate(kind, id) : undefined} onRemove={onRemove ? item => onRemove(kind, item) : undefined} />)}</div>
  </article>
}
