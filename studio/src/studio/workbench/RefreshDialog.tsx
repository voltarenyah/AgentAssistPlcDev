import { AlertTriangle, CheckCircle2, FileDiff, Loader2, X } from 'lucide-react'
import { useState } from 'react'
import type { ReconciliationEntry, ReconciliationPreview } from '@/api/client'
import {
  actionableEntries,
  comparedEntries,
  comparisonState,
  toggleApprovedPath,
} from '@/studio/reconciliationPresentation'

type Props = {
  preview: ReconciliationPreview
  busy: boolean
  onClose: () => void
  onApply: (approvedPaths: string[]) => Promise<void>
}

const nameOf = (kind: ReconciliationEntry['kind']) => {
  if (kind === 0 || kind === 'Added') return 'Added'
  if (kind === 1 || kind === 'Changed') return 'Changed'
  if (kind === 2 || kind === 'Removed') return 'Removed'
  return 'Unchanged'
}

const colorOf = (kind: ReconciliationEntry['kind']) => ({
  Added: 'text-emerald-500',
  Changed: 'text-amber-500',
  Removed: 'text-red-500',
  Unchanged: 'text-muted-foreground',
}[nameOf(kind)])

export default function RefreshDialog({ preview, busy, onClose, onApply }: Props) {
  const [approvedPaths, setApprovedPaths] = useState<Set<string>>(() => new Set())
  const compared = comparedEntries(preview.entries)
  const actionable = actionableEntries(preview.entries)
  const counts = preview.entries.reduce<Record<string, number>>((result, entry) => {
    const name = nameOf(entry.kind)
    result[name] = (result[name] ?? 0) + 1
    return result
  }, {})

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="flex max-h-[86vh] w-full max-w-[760px] flex-col overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-amber-500/10">
            <FileDiff className="h-4 w-4 text-amber-500" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">TIA comparison</h2>
            <p className="text-[10px] text-muted-foreground">Live source was exported to temporary staging. This comparison is non-destructive; tracked source changes only after explicit approval.</p>
          </div>
          <button className="icon-button" onClick={onClose}><X className="h-4 w-4" /></button>
        </div>

        <div className="grid grid-cols-4 gap-px border-b bg-border" style={{ borderColor: 'var(--border)' }}>
          {(['Added', 'Changed', 'Removed', 'Unchanged'] as const).map(label => (
            <div key={label} className="bg-card px-4 py-3 text-center">
              <div className={`text-lg font-semibold tabular-nums ${colorOf(label)}`}>{counts[label] ?? 0}</div>
              <div className="text-[9px] uppercase tracking-[0.14em] text-muted-foreground">{label}</div>
            </div>
          ))}
        </div>

        <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-3">
          {compared.length === 0 ? (
            <div className="grid min-h-40 place-items-center text-center">
              <div>
                <CheckCircle2 className="mx-auto mb-2 h-8 w-8 text-emerald-500" />
                <div className="text-xs font-medium">Baseline is already current</div>
                <div className="mt-1 text-[9px] text-muted-foreground">No tracked files will be rewritten and no Git commit will be created.</div>
              </div>
            </div>
          ) : (
            <div className="space-y-1">
              {compared.map(entry => (
                <div key={entry.relativePath} className="rounded-md border px-3 py-2" style={{ borderColor: 'var(--border)' }}>
                  <div className="flex items-center gap-3">
                    {nameOf(entry.kind) !== 'Unchanged' && (
                      <input
                        type="checkbox"
                        aria-label={`Apply ${entry.relativePath}`}
                        checked={approvedPaths.has(entry.relativePath)}
                        onChange={event => {
                          setApprovedPaths(current => {
                            return toggleApprovedPath(
                              current,
                              entry.relativePath,
                              event.target.checked,
                            )
                          })
                        }}
                      />
                    )}
                    <span className={`w-16 text-[9px] font-semibold uppercase ${colorOf(entry.kind)}`}>{nameOf(entry.kind)}</span>
                    <span className="min-w-0 flex-1 truncate font-mono text-[10px]">{entry.relativePath}</span>
                    <span className="text-[8px] uppercase text-muted-foreground">
                      {comparisonState(entry)}
                    </span>
                    {entry.componentIdentity && <span className="max-w-36 truncate text-[8px] text-muted-foreground">{entry.componentIdentity}</span>}
                  </div>
                  <div className="mt-2 grid grid-cols-2 gap-3 border-t pt-2 font-mono text-[8px]" style={{ borderColor: 'var(--border)' }}>
                    <div className="min-w-0">
                      <div className="mb-0.5 font-sans uppercase tracking-wide text-muted-foreground">Stored fingerprint</div>
                      <div className="break-all">{entry.storedFingerprints ?? 'Unavailable'}</div>
                    </div>
                    <div className="min-w-0">
                      <div className="mb-0.5 font-sans uppercase tracking-wide text-muted-foreground">Live fingerprint</div>
                      <div className="break-all">{entry.liveFingerprints ?? 'Unavailable'}</div>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="flex items-center gap-3 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          {actionable.length > 0 ? (
            <div className="flex flex-1 items-center gap-2 text-[9px] text-amber-500">
              <AlertTriangle className="h-3.5 w-3.5" />
              Select every source change you want to apply ({approvedPaths.size}/{actionable.length} selected).
            </div>
          ) : <div className="flex-1" />}
          {actionable.length > 0 && (
            <>
              <button
                className="secondary-button"
                disabled={busy || approvedPaths.size === actionable.length}
                onClick={() => setApprovedPaths(new Set(actionable.map(entry => entry.relativePath)))}
              >
                Check all
              </button>
              <button
                className="secondary-button"
                disabled={busy || approvedPaths.size === 0}
                onClick={() => setApprovedPaths(new Set())}
              >
                Uncheck all
              </button>
            </>
          )}
          <button className="secondary-button" onClick={onClose} disabled={busy}>Reject</button>
          <button
            className="primary-button"
            disabled={busy || (actionable.length > 0 && approvedPaths.size === 0)}
            onClick={() => onApply([...approvedPaths])}
          >
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            {actionable.length === 0 ? 'Confirm no changes' : `Apply ${approvedPaths.size} selected`}
          </button>
        </div>
      </div>
    </div>
  )
}
