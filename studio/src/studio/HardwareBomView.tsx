import {
  ChevronDown,
  ChevronRight,
  ClipboardList,
  ListFilter,
  Rows3,
} from 'lucide-react'
import { useMemo, useState } from 'react'
import type { HardwareBomItem, HardwareBomView } from '@/api/client'

type Props = {
  view: HardwareBomView | null
}

function EmptyBom({ message }: { message: string }) {
  return (
    <div className="grid h-full min-h-[520px] place-items-center p-8">
      <div className="max-w-md text-center">
        <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-2xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
          <ClipboardList className="h-7 w-7 text-chart-2" />
        </div>
        <h2 className="text-base font-semibold">BOM list</h2>
        <p className="mt-2 text-[10px] leading-relaxed text-muted-foreground">{message}</p>
      </div>
    </div>
  )
}

const matchesFilter = (item: HardwareBomItem, filter: string) => {
  const needle = filter.trim().toLowerCase()
  if (!needle) return true
  return [item.name, item.position, item.typeName, item.typeIdentifier, item.orderNumber, item.firmwareVersion]
    .some(value => value?.toLowerCase().includes(needle))
}

const slotLabel = (item: HardwareBomItem) =>
  item.positionNumber === null ? '—' : `Slot ${item.positionNumber}`

export default function HardwareBomView({ view }: Props) {
  const [filter, setFilter] = useState('')
  const [grouped, setGrouped] = useState(false)
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(() => new Set())

  const items = useMemo(() => view?.items ?? [], [view])
  const filtered = useMemo(() => items.filter(item => matchesFilter(item, filter)), [items, filter])
  const groups = useMemo(() => {
    const byType = new Map<string, HardwareBomItem[]>()
    for (const item of filtered) {
      const list = byType.get(item.typeIdentifier) ?? []
      list.push(item)
      byType.set(item.typeIdentifier, list)
    }
    return [...byType.entries()]
      .map(([typeIdentifier, groupItems]) => ({ typeIdentifier, items: groupItems }))
      .sort((a, b) => a.typeIdentifier.localeCompare(b.typeIdentifier))
  }, [filtered])

  if (!view || view.state !== 'available') {
    return <EmptyBom message={view?.message ?? 'Loading hardware bill of materials...'} />
  }

  const distinctTypes = new Set(items.map(item => item.typeIdentifier)).size

  const toggleType = (typeIdentifier: string) => {
    setExpandedTypes(current => {
      const next = new Set(current)
      if (next.has(typeIdentifier)) next.delete(typeIdentifier)
      else next.add(typeIdentifier)
      return next
    })
  }

  return (
    <div className="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
      <header className="flex shrink-0 items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
        <div className="grid h-10 w-10 place-items-center rounded-xl bg-chart-2/10">
          <ClipboardList className="h-5 w-5 text-chart-2" />
        </div>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <h1 className="text-sm font-semibold">BOM list</h1>
            <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-[8px] font-medium uppercase tracking-[0.12em] text-emerald-600 dark:text-emerald-400">AML loaded</span>
          </div>
          <p className="mt-1 text-[9px] text-muted-foreground">Every typed hardware component in the project, with its mounting position and version.</p>
        </div>
        <div className="ml-auto flex items-center gap-3 text-[9px] text-muted-foreground">
          <span>{items.length} component{items.length === 1 ? '' : 's'}</span>
          <span>{distinctTypes} type{distinctTypes === 1 ? '' : 's'}</span>
          {view.exportedAt && <span>{new Date(view.exportedAt).toLocaleString()}</span>}
        </div>
      </header>

      <div className="flex shrink-0 items-center gap-2 border-b px-5 py-2" style={{ borderColor: 'var(--border)' }}>
        <ListFilter className="h-3.5 w-3.5 text-muted-foreground" />
        <input
          aria-label="Filter components"
          className="field-input h-7 w-64 text-[10px]"
          placeholder="Filter by name, type, order number..."
          value={filter}
          onChange={event => setFilter(event.target.value)}
        />
        <div className="flex-1" />
        <button
          aria-label="Toggle group by type"
          className={`secondary-button h-7 text-[9px] ${grouped ? 'bg-accent' : ''}`}
          onClick={() => setGrouped(current => !current)}
        >
          <Rows3 className="h-3 w-3" /> Group by type
        </button>
      </div>

      <div className="scrollbar-sleek min-h-0 flex-1 overflow-auto">
        {filtered.length === 0 ? (
          <div className="p-8 text-center text-[10px] text-muted-foreground">No components match the current filter.</div>
        ) : grouped ? (
          <table className="w-full text-[10px]">
            <thead className="sticky top-0 bg-card">
              <tr className="border-b text-left text-[9px] uppercase tracking-wide text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
                <th className="px-4 py-2 font-medium">Qty</th>
                <th className="px-4 py-2 font-medium">Order number</th>
                <th className="px-4 py-2 font-medium">Type identifier</th>
                <th className="px-4 py-2 font-medium">Type name</th>
                <th className="px-4 py-2 font-medium">Firmware</th>
              </tr>
            </thead>
            <tbody>
              {groups.map(group => {
                const first = group.items[0]
                const expanded = expandedTypes.has(group.typeIdentifier)
                const typeNames = [...new Set(group.items.map(item => item.typeName).filter(Boolean))].join(', ')
                const firmwares = [...new Set(group.items.map(item => item.firmwareVersion).filter(Boolean))].join(', ')
                return [
                  <tr
                    key={group.typeIdentifier}
                    className="cursor-pointer border-b hover:bg-accent/40"
                    style={{ borderColor: 'var(--border)' }}
                    onClick={() => toggleType(group.typeIdentifier)}
                  >
                    <td className="px-4 py-1.5">
                      <span className="inline-flex items-center gap-1.5">
                        {expanded ? <ChevronDown className="h-3 w-3 text-muted-foreground" /> : <ChevronRight className="h-3 w-3 text-muted-foreground" />}
                        <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[9px]">{group.items.length}</span>
                      </span>
                    </td>
                    <td className="px-4 py-1.5 font-mono">{first.orderNumber ?? '—'}</td>
                    <td className="px-4 py-1.5 font-mono text-muted-foreground">{group.typeIdentifier}</td>
                    <td className="px-4 py-1.5">{typeNames || '—'}</td>
                    <td className="px-4 py-1.5 font-mono">{firmwares || '—'}</td>
                  </tr>,
                  ...(expanded
                    ? group.items.map(item => (
                        <tr key={item.id} className="border-b bg-muted/30 text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
                          <td className="px-4 py-1" />
                          <td className="px-4 py-1" colSpan={4}>
                            <span className="font-medium text-foreground">{item.name}</span>
                            <span className="mx-2">·</span>
                            <span>{item.position || '—'}</span>
                            <span className="mx-2">·</span>
                            <span>{slotLabel(item)}</span>
                          </td>
                        </tr>
                      ))
                    : []),
                ]
              })}
            </tbody>
          </table>
        ) : (
          <table className="w-full text-[10px]">
            <thead className="sticky top-0 bg-card">
              <tr className="border-b text-left text-[9px] uppercase tracking-wide text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
                <th className="px-4 py-2 font-medium">Position</th>
                <th className="px-4 py-2 font-medium">Slot</th>
                <th className="px-4 py-2 font-medium">Name</th>
                <th className="px-4 py-2 font-medium">Type name</th>
                <th className="px-4 py-2 font-medium">Order number</th>
                <th className="px-4 py-2 font-medium">Type identifier</th>
                <th className="px-4 py-2 font-medium">Firmware</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(item => (
                <tr key={item.id} className="border-b hover:bg-accent/40" style={{ borderColor: 'var(--border)' }}>
                  <td className="px-4 py-1.5 text-muted-foreground">{item.position || '—'}</td>
                  <td className="px-4 py-1.5 font-mono">{item.positionNumber ?? '—'}</td>
                  <td className="px-4 py-1.5 font-medium">{item.name}</td>
                  <td className="px-4 py-1.5">{item.typeName ?? '—'}</td>
                  <td className="px-4 py-1.5 font-mono">{item.orderNumber ?? '—'}</td>
                  <td className="px-4 py-1.5 font-mono text-muted-foreground">{item.typeIdentifier}</td>
                  <td className="px-4 py-1.5 font-mono">{item.firmwareVersion ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
