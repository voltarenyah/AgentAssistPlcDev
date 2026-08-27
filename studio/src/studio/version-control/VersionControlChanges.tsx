import { useMemo, useState } from 'react'
import { Check, ChevronDown, FileCode2, Folder, HardDrive, Loader2, Plus } from 'lucide-react'
import * as api from '@/api/client'
import { toast } from 'sonner'
import { showErrorToast } from '@/components/ui/toast'
import VersionControlCompare from './VersionControlCompare'

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

export type VersionControlSnapshotInfo = {
  revision: number | null
  commitsSince: number | null
  hardwareDiffers: boolean
}

export type VersionControlChangesProps = {
  workbenchId: string
  worktreeId: string
  branch: string
  entries: VersionControlSourceEntry[]
  compareSignal: number
  snapshot: VersionControlSnapshotInfo
  /** True when an untrackable-change commit exists that no SVN savepoint covers yet. */
  untrackablePendingSavepoint?: boolean
  onCommitted?: () => void | Promise<void>
  /** Starts a title-bar operation and returns its id so the full compare reports live export progress. */
  onBeginOperation?: (kind: string, label: string) => string
}

const categoryOrder = ['Block', 'DB', 'Udt', 'Tags', 'Hardware']

const stateBadge: Record<VersionControlSourceState, { letter: string; className: string; label: string }> = {
  Modified: { letter: 'M', className: 'text-amber-500', label: 'Modified' },
  Added: { letter: 'A', className: 'text-emerald-500', label: 'Added' },
  Deleted: { letter: 'D', className: 'text-red-500', label: 'Deleted' },
  Unauthorized: { letter: 'U', className: 'text-orange-500', label: 'Direct master edit' },
}

const sortEntries = (left: VersionControlSourceEntry, right: VersionControlSourceEntry) => {
  const plc = left.plcName.localeCompare(right.plcName)
  if (plc !== 0) return plc
  const leftCategory = categoryOrder.indexOf(left.category)
  const rightCategory = categoryOrder.indexOf(right.category)
  const category = (leftCategory < 0 ? categoryOrder.length : leftCategory) - (rightCategory < 0 ? categoryOrder.length : rightCategory)
  if (category !== 0) return category
  return left.objectName.localeCompare(right.objectName) || left.filePath.localeCompare(right.filePath)
}

const groupLabel = (entry: VersionControlSourceEntry) =>
  entry.category === 'Hardware' ? 'Hardware' : `${entry.plcName} · ${entry.category}`

const displayError = (error: unknown) => error instanceof Error ? error.message : 'Unexpected operation failure'

export default function VersionControlChanges({ workbenchId, worktreeId, branch, entries, compareSignal, snapshot, untrackablePendingSavepoint = false, onCommitted, onBeginOperation }: VersionControlChangesProps) {
  const [selectedPaths, setSelectedPaths] = useState<Set<string>>(new Set())
  const [tiaSelection, setTiaSelection] = useState<{ comparisonId: string; paths: string[] } | null>(null)
  const [tiaHasDifferences, setTiaHasDifferences] = useState<boolean | null>(null)
  const [tiaSelectionResetSignal, setTiaSelectionResetSignal] = useState(0)
  const [message, setMessage] = useState('')
  const [untrackable, setUntrackable] = useState(false)
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [commitMenuOpen, setCommitMenuOpen] = useState(false)
  const [snapshotMessage, setSnapshotMessage] = useState('')
  const [busy, setBusy] = useState(false)

  const groups = useMemo(() => {
    const grouped = new Map<string, VersionControlSourceEntry[]>()
    for (const entry of [...entries].sort(sortEntries)) {
      const key = groupLabel(entry)
      const group = grouped.get(key) ?? []
      group.push(entry)
      grouped.set(key, group)
    }
    return [...grouped.entries()].map(([key, groupEntries]) => ({ key, entries: groupEntries }))
  }, [entries])

  const selectedCommitPaths = new Set([...selectedPaths, ...(tiaSelection?.paths ?? [])])
  const canCommit = (selectedCommitPaths.size > 0 || untrackable) && message.trim().length > 0 && !busy

  const togglePath = (filePath: string) => {
    setSelectedPaths(previous => {
      const next = new Set(previous)
      if (next.has(filePath)) next.delete(filePath)
      else next.add(filePath)
      return next
    })
  }

  const toggleGroup = (key: string) => {
    setCollapsed(previous => {
      const next = new Set(previous)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const commit = async (localSelection = [...selectedPaths]) => {
    const tiaPaths = tiaSelection?.paths ?? []
    const localPaths = localSelection.filter(path => !tiaPaths.includes(path))
    if ((!untrackable && tiaPaths.length === 0 && localPaths.length === 0) || !message.trim() || busy) return
    setBusy(true)
    setCommitMenuOpen(false)
    try {
      let committedFiles: string[] = []
      let commitSha: string | null = null
      if (tiaSelection && tiaPaths.length > 0) {
        const result = await api.acceptTiaSynchronization(workbenchId, tiaSelection.comparisonId, tiaPaths, message.trim())
        committedFiles = [...committedFiles, ...tiaPaths]
        commitSha = result.commitSha ?? null
        setTiaSelection(null)
        setTiaSelectionResetSignal(previous => previous + 1)
      }
      if (localPaths.length > 0 || untrackable) {
        const result = await api.commitVcPaths(workbenchId, worktreeId, localPaths, message.trim(), untrackable)
        committedFiles = [...committedFiles, ...result.files]
        commitSha = result.sha
      }
      const committed = new Set(committedFiles)
      setSelectedPaths(previous => new Set([...previous].filter(path => !committed.has(path))))
      setMessage('')
      setUntrackable(false)
      if (commitSha) toast.success(`Committed ${commitSha.slice(0, 8)}`)
      await onCommitted?.()
    } catch (cause) {
      showErrorToast(`Commit failed: ${displayError(cause)}`)
    } finally {
      setBusy(false)
    }
  }

  const createSnapshot = async () => {
    if (!snapshotMessage.trim() || busy) return
    setBusy(true)
    try {
      const result = await api.createSvnSavepoint(workbenchId, worktreeId, snapshotMessage.trim())
      setSnapshotMessage('')
      toast.success(`TIA snapshot committed as ${result.sha.slice(0, 8)}`)
      await onCommitted?.()
    } catch (cause) {
      showErrorToast(`Snapshot failed: ${displayError(cause)}`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="version-control-changes">
      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
        {entries.length === 0 && compareSignal === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-2 p-6 text-center" data-testid="vc-changes-empty">
            <div className="grid h-10 w-10 place-items-center rounded-full border border-emerald-500/35 bg-emerald-500/10">
              <Check className="h-4.5 w-4.5 text-emerald-500" />
            </div>
            <h3 className="text-[13px] font-semibold">No changes on this branch</h3>
            <p className="max-w-[230px] text-[11px] leading-relaxed text-muted-foreground">
              This workspace is clean — no PLC source changes are waiting to be committed.
            </p>
          </div>
        ) : (
          <>
            <div data-testid="vc-commit-controls" className="px-3.5 pt-2.5">
              <textarea
                aria-label="Commit message"
                placeholder="Message"
                value={message}
                onChange={event => setMessage(event.currentTarget.value)}
                className="h-[52px] w-full resize-none rounded-[9px] border bg-white/[0.03] px-2.5 py-2 text-[11px] outline-none placeholder:text-neutral-500 focus:border-white/20"
                style={{ borderColor: 'var(--border)' }}
              />
              <label className="mt-1.5 flex cursor-pointer items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/5 p-2">
                <input
                  type="checkbox"
                  data-testid="vc-untrackable-change"
                  checked={untrackable}
                  onChange={event => setUntrackable(event.target.checked)}
                />
                <span className="min-w-0 flex-1">
                  <span className="block text-[10px] font-medium text-amber-600">Untrackable change</span>
                  <span className="block text-[9px] text-muted-foreground">Record a TIA change that leaves no git file diff — the commit stores only this message.</span>
                </span>
              </label>
              <div className="relative mt-1.5 flex rounded-[9px] border bg-white/[0.03]" style={{ borderColor: 'var(--border)' }}>
                <button
                  type="button"
                  data-testid="vc-commit-selected"
                  disabled={!canCommit}
                  onClick={() => void commit()}
                  className="flex h-8 flex-1 items-center justify-center gap-1.5 rounded-l-[9px] text-[11px] font-semibold hover:bg-white/5 disabled:opacity-40 disabled:hover:bg-transparent"
                >
                  {busy ? <Loader2 className="h-3 w-3 animate-spin" /> : <Plus className="h-3 w-3" />}
                  Commit selected ({selectedCommitPaths.size})
                </button>
                <button
                  type="button"
                  aria-label="Commit options"
                  className="flex w-8 items-center justify-center rounded-r-[9px] border-l text-muted-foreground hover:bg-white/5 hover:text-foreground"
                  style={{ borderColor: 'var(--border)' }}
                  onClick={() => setCommitMenuOpen(open => !open)}
                >
                  <ChevronDown className="h-3 w-3" />
                </button>
                {commitMenuOpen && (
                  <div className="absolute right-0 top-9 z-20 min-w-[180px] rounded-lg border bg-popover p-1 shadow-xl" style={{ borderColor: 'var(--border)' }}>
                    <button
                      type="button"
                      data-testid="vc-commit-all"
                      disabled={message.trim().length === 0 || busy}
                      className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-[11px] hover:bg-accent disabled:opacity-40"
                      onClick={() => void commit(entries.map(entry => entry.filePath))}
                    >
                      Commit all changes ({entries.length})
                    </button>
                  </div>
                )}
              </div>
            </div>

            {(entries.length > 0 || tiaHasDifferences !== false) && (
              <div className="flex items-center gap-1.5 px-3.5 pb-1.5 pt-3.5 text-[11px] font-bold uppercase tracking-wide">
                <span>Changes</span>
                <span className="font-normal text-muted-foreground">{entries.length}</span>
                <button
                  type="button"
                  className="ml-auto text-[11px] font-normal normal-case tracking-normal text-muted-foreground hover:text-foreground"
                  onClick={() => setCollapsed(previous => previous.size === 0 ? new Set(groups.map(group => group.key)) : new Set())}
                >
                  {collapsed.size === 0 ? 'Collapse all' : 'Expand all'}
                </button>
              </div>
            )}

            <VersionControlCompare
              workbenchId={workbenchId}
              worktreeId={worktreeId}
              branch={branch}
              signal={compareSignal}
              commitMessage={message}
              onSelectionChanged={(comparisonId, paths) => setTiaSelection(comparisonId && paths.length > 0 ? { comparisonId, paths } : null)}
              onComparisonStateChanged={setTiaHasDifferences}
              selectionResetSignal={tiaSelectionResetSignal}
              onCommitted={onCommitted}
              onBeginOperation={onBeginOperation}
            />

            <div className="px-2.5 pb-2.5">
              {groups.map(group => {
                const isCollapsed = collapsed.has(group.key)
                return (
                  <div key={group.key}>
                    <button
                      type="button"
                      className="flex w-full items-center gap-1.5 rounded-md px-2 py-1.5 text-left text-[12px] hover:bg-white/5"
                      onClick={() => toggleGroup(group.key)}
                    >
                      <ChevronDown className={`h-3.5 w-3.5 text-muted-foreground transition-transform ${isCollapsed ? '-rotate-90' : ''}`} />
                      <Folder className="h-3.5 w-3.5 text-muted-foreground" />
                      {group.key}
                      <span className="ml-auto text-[10px] text-muted-foreground">{group.entries.length}</span>
                    </button>
                    {!isCollapsed && group.entries.map(entry => {
                      const badge = stateBadge[entry.state]
                      const selected = selectedPaths.has(entry.filePath)
                      return (
                        <button
                          key={entry.filePath}
                          type="button"
                          data-testid="plc-source-row"
                          data-selected={selected}
                          title={`${entry.filePath} — ${badge.label}`}
                          onClick={() => togglePath(entry.filePath)}
                          className={`ml-3.5 flex w-[calc(100%-14px)] items-center gap-1.5 rounded-md px-2 py-1.5 text-left text-[12px] hover:bg-white/5 ${selected ? 'bg-blue-500/10' : ''}`}
                        >
                          <FileCode2 className="h-3.5 w-3.5 shrink-0 text-sky-500" />
                          <span className="min-w-0 flex-1 truncate">{entry.objectName}{entry.category === 'Hardware' ? '' : '.xml'}</span>
                          <span className={`w-3.5 shrink-0 text-center font-mono text-[10px] font-bold ${badge.className}`}>{badge.letter}</span>
                        </button>
                      )
                    })}
                  </div>
                )
              })}
            </div>
          </>
        )}
      </div>

      <div className="shrink-0 border-t bg-white/[0.015] px-3.5 pb-3 pt-2.5" style={{ borderColor: 'var(--border)' }} data-testid="vc-snapshot-area">
        <div className="flex items-center gap-1.5">
          <HardDrive className="h-3.5 w-3.5 text-muted-foreground" />
          {snapshot.revision !== null ? (
            <span className="rounded-full border border-violet-400/35 bg-violet-400/10 px-2 py-0.5 font-mono text-[10px] font-bold text-violet-400" data-testid="vc-snapshot-revision">r{snapshot.revision}</span>
          ) : (
            <span className="text-[10px] text-muted-foreground">No savepoint yet</span>
          )}
          {snapshot.commitsSince !== null && (
            <span className="ml-auto rounded bg-amber-500/10 px-1.5 py-0.5 font-mono text-[10px] text-amber-500" data-testid="vc-snapshot-drift">
              {snapshot.commitsSince} commit{snapshot.commitsSince === 1 ? '' : 's'} since
            </span>
          )}
          {snapshot.hardwareDiffers && (
            <span className={`rounded bg-red-500/10 px-1.5 py-0.5 font-mono text-[10px] text-red-500 ${snapshot.commitsSince === null ? 'ml-auto' : ''}`} data-testid="vc-hardware-differs">
              hardware different
            </span>
          )}
        </div>
        {untrackablePendingSavepoint && (
          <div className="mt-2 rounded-lg border border-amber-500/30 bg-amber-500/5 p-2 text-[10px] text-amber-600" data-testid="vc-untrackable-savepoint-warning">
            An untrackable change is not covered by any SVN savepoint — create a savepoint as soon as possible to keep this TIA state restorable.
          </div>
        )}
        <div className="mt-2 flex gap-1.5">
          <input
            aria-label="Description for TIA snapshot"
            placeholder="Description for TIA snapshot"
            value={snapshotMessage}
            onChange={event => setSnapshotMessage(event.currentTarget.value)}
            className="h-7 min-w-0 flex-1 rounded-lg border bg-white/[0.03] px-2.5 text-[11px] outline-none placeholder:text-neutral-500 focus:border-white/20"
            style={{ borderColor: 'var(--border)' }}
          />
          <button
            type="button"
            data-testid="vc-create-snapshot"
            disabled={!snapshotMessage.trim() || busy}
            onClick={() => void createSnapshot()}
            className="h-7 whitespace-nowrap rounded-lg border bg-muted px-3 text-[11px] font-semibold hover:bg-accent disabled:opacity-40"
            style={{ borderColor: 'var(--border)' }}
          >
            Snapshot
          </button>
        </div>
      </div>
    </div>
  )
}
