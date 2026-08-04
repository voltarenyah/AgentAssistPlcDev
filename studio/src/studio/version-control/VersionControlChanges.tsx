import { useMemo, useState } from 'react'
import * as api from '@/api/client'

export type VersionControlSourceState = 'Modified' | 'Added' | 'Deleted' | 'Unauthorized'

export type VersionControlSourceEntry = {
  filePath: string
  deviceId: string
  plcName: string
  category: string
  objectName: string
  state: VersionControlSourceState
  authorizedOnMaster: boolean
}

export type VersionControlChangesProps = {
  workbenchId: string
  worktreeId: string
  entries: VersionControlSourceEntry[]
  onSelectionChange?: (entry: VersionControlSourceEntry | null) => void
  onCommitted?: (result: { sha: string; message: string; files: string[] }) => void | Promise<void>
}

const categoryOrder = ['Block', 'DB', 'Udt', 'Tags']

const stateLabel: Record<VersionControlSourceState, string> = {
  Modified: 'Modified',
  Added: 'Added',
  Deleted: 'Deleted',
  Unauthorized: 'Direct master edit',
}

const stateColor: Record<VersionControlSourceState, string> = {
  Modified: '#eab308',
  Added: '#22c55e',
  Deleted: '#ef4444',
  Unauthorized: '#f97316',
}

const isUnauthorized = (entry: VersionControlSourceEntry) =>
  entry.state === 'Unauthorized' || !entry.authorizedOnMaster

const sortEntries = (left: VersionControlSourceEntry, right: VersionControlSourceEntry) => {
  const plc = left.plcName.localeCompare(right.plcName)
  if (plc !== 0) return plc
  const leftCategory = categoryOrder.indexOf(left.category)
  const rightCategory = categoryOrder.indexOf(right.category)
  const category = (leftCategory < 0 ? categoryOrder.length : leftCategory) - (rightCategory < 0 ? categoryOrder.length : rightCategory)
  if (category !== 0) return category
  return left.objectName.localeCompare(right.objectName) || left.filePath.localeCompare(right.filePath)
}

const groupKey = (entry: VersionControlSourceEntry) => `${entry.plcName}/${entry.category}`

export default function VersionControlChanges({
  workbenchId,
  worktreeId,
  entries,
  onSelectionChange,
  onCommitted,
}: VersionControlChangesProps) {
  const [selectedPaths, setSelectedPaths] = useState<Set<string>>(new Set())
  const [commitMessage, setCommitMessage] = useState('')
  const [operating, setOperating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const groups = useMemo(() => {
    const grouped = new Map<string, VersionControlSourceEntry[]>()
    for (const entry of [...entries].sort(sortEntries)) {
      const key = groupKey(entry)
      const group = grouped.get(key) ?? []
      group.push(entry)
      grouped.set(key, group)
    }
    return [...grouped.entries()].map(([key, group]) => ({ key, entries: group }))
  }, [entries])

  const eligibleEntries = entries.filter(entry => !isUnauthorized(entry))
  const selectedEntries = entries.filter(entry => selectedPaths.has(entry.filePath) && !isUnauthorized(entry))
  const canCommit = selectedEntries.length > 0 && commitMessage.trim().length > 0 && !operating

  const setPathSelected = (entry: VersionControlSourceEntry, selected: boolean) => {
    if (isUnauthorized(entry)) return
    setSelectedPaths(previous => {
      const next = new Set(previous)
      if (selected) next.add(entry.filePath)
      else next.delete(entry.filePath)
      return next
    })
  }

  const setEntriesSelected = (groupEntries: VersionControlSourceEntry[], selected: boolean) => {
    setSelectedPaths(previous => {
      const next = new Set(previous)
      for (const entry of groupEntries) {
        if (isUnauthorized(entry)) continue
        if (selected) next.add(entry.filePath)
        else next.delete(entry.filePath)
      }
      return next
    })
  }

  const commitSelected = async () => {
    if (!canCommit) return
    setOperating(true)
    setError(null)
    try {
      const result = await api.commitVcPaths(
        workbenchId,
        worktreeId,
        selectedEntries.map(entry => entry.filePath),
        commitMessage.trim(),
      )
      const committed = new Set(result.files)
      setSelectedPaths(previous => {
        const next = new Set(previous)
        for (const path of committed) next.delete(path)
        return next
      })
      setCommitMessage('')
      await onCommitted?.(result)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Commit failed')
    } finally {
      setOperating(false)
    }
  }

  return (
    <div className="flex h-full flex-col" data-testid="version-control-changes">
      <div className="flex items-center justify-between gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <div>
          <div className="text-[11px] font-semibold" style={{ color: 'var(--foreground)' }}>PLC source changes</div>
          <div className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>{entries.length} object{entries.length === 1 ? '' : 's'}</div>
        </div>
        <label className="flex items-center gap-1 text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
          <input
            type="checkbox"
            aria-label="Select all visible PLC objects"
            checked={eligibleEntries.length > 0 && eligibleEntries.every(entry => selectedPaths.has(entry.filePath))}
            disabled={eligibleEntries.length === 0 || operating}
            onChange={event => setEntriesSelected(eligibleEntries, event.currentTarget.checked)}
          />
          All visible
        </label>
      </div>

      {error && <div className="px-3 py-2 text-[9px]" style={{ color: 'var(--destructive)' }}>{error}</div>}

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {groups.length === 0 ? (
          <div className="p-4 text-center text-[10px]" style={{ color: 'var(--muted-foreground)' }}>No PLC source changes</div>
        ) : groups.map(group => {
          const selectable = group.entries.filter(entry => !isUnauthorized(entry))
          const allSelected = selectable.length > 0 && selectable.every(entry => selectedPaths.has(entry.filePath))
          return (
            <section key={group.key} className="mb-2 rounded border" style={{ borderColor: 'var(--border)' }}>
              <div className="flex items-center gap-2 border-b px-2 py-1.5" style={{ borderColor: 'var(--border)', background: 'var(--muted)' }}>
                <input
                  type="checkbox"
                  aria-label={`Select all ${group.key.replace('/', ' ')} objects`}
                  checked={allSelected}
                  disabled={selectable.length === 0 || operating}
                  onChange={event => setEntriesSelected(group.entries, event.currentTarget.checked)}
                />
                <span className="text-[10px] font-semibold" style={{ color: 'var(--foreground)' }}>{group.key.replace('/', ' · ')}</span>
                <span className="ml-auto text-[9px]" style={{ color: 'var(--muted-foreground)' }}>{group.entries.length}</span>
              </div>
              {group.entries.map(entry => {
                const unauthorized = isUnauthorized(entry)
                return (
                  <div
                    key={entry.filePath}
                    data-testid="plc-source-row"
                    className="flex cursor-pointer items-center gap-2 border-b px-2 py-1.5 last:border-b-0 hover:bg-accent/40"
                    style={{ borderColor: 'var(--border)' }}
                    onClick={() => onSelectionChange?.(entry)}
                  >
                    <input
                      type="checkbox"
                      aria-label={`Select ${entry.objectName}`}
                      checked={!unauthorized && selectedPaths.has(entry.filePath)}
                      disabled={unauthorized || operating}
                      onClick={event => event.stopPropagation()}
                      onChange={event => setPathSelected(entry, event.currentTarget.checked)}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[10px]" style={{ color: 'var(--foreground)' }}>{entry.objectName}</span>
                      <span className="block truncate font-mono text-[8px]" style={{ color: 'var(--muted-foreground)' }}>{entry.filePath}</span>
                    </span>
                    <span className="shrink-0 rounded px-1 py-0.5 text-[8px]" style={{ color: stateColor[entry.state], background: `${stateColor[entry.state]}1a` }}>
                      {unauthorized ? stateLabel.Unauthorized : stateLabel[entry.state]}
                    </span>
                  </div>
                )
              })}
            </section>
          )
        })}
      </div>

      <div className="shrink-0 space-y-1.5 border-t px-2.5 py-2" style={{ borderColor: 'var(--border)' }}>
        <label className="block text-[9px]" style={{ color: 'var(--muted-foreground)' }} htmlFor="vc-commit-message">Commit message</label>
        <div className="flex gap-1.5">
          <input
            id="vc-commit-message"
            aria-label="Commit message"
            value={commitMessage}
            onChange={event => setCommitMessage(event.currentTarget.value)}
            onInput={event => setCommitMessage(event.currentTarget.value)}
            placeholder="Describe this PLC change..."
            disabled={operating}
            className="min-w-0 flex-1 rounded px-2 py-1 text-[10px] outline-none"
            style={{ background: 'var(--muted)', color: 'var(--foreground)', border: '1px solid var(--border)' }}
          />
          <button
            type="button"
            aria-label={`Commit selected (${selectedEntries.length})`}
            disabled={!canCommit}
            onClick={() => void commitSelected()}
            className="rounded px-2 py-1 text-[9px]"
            style={{ background: '#22c55e', color: '#fff', opacity: canCommit ? 1 : 0.5 }}
          >
            {operating ? 'Committing…' : `Commit selected (${selectedEntries.length})`}
          </button>
        </div>
        <div className="text-[8px]" style={{ color: 'var(--muted-foreground)' }}>
          {selectedEntries.length} selected · unauthorized master edits require a separate decision
        </div>
      </div>
    </div>
  )
}
