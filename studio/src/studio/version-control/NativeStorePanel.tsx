import { useCallback, useEffect, useState } from 'react'
import { HardDrive, Loader2, RefreshCw } from 'lucide-react'
import * as api from '@/api/client'

export type NativeStorePanelProps = {
  workbenchId: string
  worktreeId: string
}

function Row({ label, value, mono = true }: { label: string; value: string | null | undefined; mono?: boolean }) {
  return (
    <div className="flex items-baseline gap-2 px-3 py-0.5 text-[9px]">
      <span className="w-28 shrink-0 text-muted-foreground">{label}</span>
      <span className={`min-w-0 flex-1 break-all ${mono ? 'font-mono' : ''}`}>{value ?? '—'}</span>
    </div>
  )
}

function savepointLabel(savepoint: api.SavepointInfo): string {
  const revision = savepoint.svnRevision != null ? `r${savepoint.svnRevision}` : 'no-svn'
  const checksum = savepoint.projectChecksum ?? 'no-checksum'
  const sha = savepoint.sha.slice(0, 7)
  return `${revision} · ${checksum} · ${sha} · ${savepoint.message}`
}

export default function NativeStorePanel({ workbenchId, worktreeId }: NativeStorePanelProps) {
  const [state, setState] = useState<api.WorktreeEngineeringState | null>(null)
  const [savepoints, setSavepoints] = useState<api.SavepointInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selectedSha, setSelectedSha] = useState('')
  const [restoring, setRestoring] = useState(false)
  const [restoreResult, setRestoreResult] = useState<api.RestoreTiaProjectResult | null>(null)
  const [restoreError, setRestoreError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [nextState, nextSavepoints] = await Promise.all([
        api.getWorktreeEngineeringState(workbenchId, worktreeId),
        api.getWorktreeSavepoints(workbenchId, worktreeId).catch(() => [] as api.SavepointInfo[]),
      ])
      setState(nextState)
      setSavepoints(nextSavepoints)
      setSelectedSha(current => current || nextSavepoints[0]?.sha || '')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Failed to load engineering state')
    } finally {
      setLoading(false)
    }
  }, [workbenchId, worktreeId])

  useEffect(() => { void refresh() }, [refresh])

  const restore = async () => {
    setRestoring(true)
    setRestoreError(null)
    setRestoreResult(null)
    try {
      setRestoreResult(await api.restoreTiaProject(workbenchId, worktreeId, selectedSha || undefined))
    } catch (reason) {
      setRestoreError(reason instanceof Error ? reason.message : 'Restore failed')
    } finally {
      setRestoring(false)
    }
  }

  const revision = state?.revision ?? null

  return (
    <div className="flex h-full min-h-0 flex-col overflow-y-auto py-1" aria-label="Native TIA store">
      <div className="flex shrink-0 items-center gap-2 px-3 py-1">
        <HardDrive className="h-3.5 w-3.5 text-chart-4" />
        <span className="flex-1 text-[10px] font-semibold">Native TIA store (SVN)</span>
        <button type="button" className="icon-button" title="Refresh engineering state" onClick={() => void refresh()} disabled={loading}>
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
        </button>
      </div>
      {error && <div className="px-3 py-1 text-[9px] text-destructive">{error}</div>}
      {!error && !loading && !revision && (
        <div className="px-3 py-1 text-[9px] text-muted-foreground">
          No engineering-state/revision.json in this worktree (pre-SVN workbench or not committed yet).
        </div>
      )}
      {state?.pendingCommit && (
        <div className="mx-3 my-1 rounded border border-amber-500/50 bg-amber-500/10 px-2 py-1 text-[9px] text-amber-600">
          PENDING_GIT_COMMIT — an SVN commit completed but its Git commit failed. Retry the commit to finish the savepoint; do not commit natively again.
        </div>
      )}
      {revision && (
        <div className="shrink-0 py-1">
          <Row label="SVN url" value={revision.svn?.url} />
          <Row label="SVN revision" value={revision.svn != null ? String(revision.svn.revision) : null} />
          <Row label="TIA checksum" value={revision.tia?.projectChecksum} />
          <Row label="F-signature" value={revision.safety?.fSignature} />
          <Row label="Compile status" value={revision.validation?.compileStatus} mono={false} />
          <Row label="Branch url" value={state?.svnUrl} />
          <Row label="Base SVN rev" value={state?.baseSvnRevision != null ? String(state.baseSvnRevision) : null} />
          <Row label="Managed project" value={state?.managedTiaProjectPath} />
        </div>
      )}
      <div className="mt-1 shrink-0 border-t px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <div className="pb-1 text-[10px] font-semibold">Restore native state from SVN</div>
        <label className="block pb-1 text-[9px] text-muted-foreground">
          Savepoint (revision · checksum · commit)
          <select
            className="mt-0.5 w-full rounded border bg-background px-2 py-1 font-mono text-[9px]"
            style={{ borderColor: 'var(--border)' }}
            value={selectedSha}
            onChange={event => setSelectedSha(event.target.value)}
            disabled={savepoints.length === 0}
          >
            {savepoints.length === 0 && <option value="">No savepoints</option>}
            {savepoints.map(savepoint => (
              <option key={savepoint.sha} value={savepoint.sha}>{savepointLabel(savepoint)}</option>
            ))}
          </select>
        </label>
        <div className="pb-1 text-[8px] text-muted-foreground">
          Exports a lean copy (no .svn) to &lt;workbench&gt;/export/&lt;checksum&gt;/ — the live project is never touched.
        </div>
        <button
          type="button"
          className="rounded bg-accent px-2 py-1 text-[9px] font-medium disabled:opacity-50"
          disabled={restoring || savepoints.length === 0 || !selectedSha}
          onClick={() => void restore()}
        >
          {restoring ? 'Restoring…' : 'Restore from SVN'}
        </button>
        {restoreError && <div className="pt-1 text-[9px] text-destructive">{restoreError}</div>}
        {restoreResult && (
          <div className="pt-1 text-[9px]">
            <Row label="Restored rev" value={`${restoreResult.svnUrl}@${restoreResult.svnRevision}`} />
            <Row label="Git commit" value={restoreResult.gitCommit} />
            <Row label="Project" value={restoreResult.restoredProjectPath ?? restoreResult.restoredDirectory} />
          </div>
        )}
      </div>
    </div>
  )
}
