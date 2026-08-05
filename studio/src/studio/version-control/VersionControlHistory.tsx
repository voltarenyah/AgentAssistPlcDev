import { useEffect, useState } from 'react'
import { CheckCircle2, GitCommitHorizontal, History, Loader2, RotateCcw, ShieldCheck } from 'lucide-react'
import * as api from '@/api/client'

type Props = {
  workbenchId: string
  worktreeId: string
  commits?: api.VcCommitEntry[]
  onCommitSelect?: (commit: api.VcCommitEntry) => void
  onObjectSelect?: (commit: api.VcCommitEntry, relativePath: string) => void
}

type EvidenceState = {
  loading: boolean
  evidence: api.VcValidationEvidence | null
  error: string | null
}

const emptyEvidence: EvidenceState = { loading: false, evidence: null, error: null }

function objectName(relativePath: string): string {
  const fileName = relativePath.split('/').pop() ?? relativePath
  return fileName.replace(/\.xml$/i, '')
}

function validationLabel(commit: api.VcCommitEntry): string {
  if (commit.validationState === 'Validated') return 'TIA validated'
  if (commit.validationState === 'Invalid') return 'Validation invalid'
  return 'No validation evidence'
}

function validationColor(commit: api.VcCommitEntry): string {
  if (commit.validationState === 'Validated') return '#22c55e'
  if (commit.validationState === 'Invalid') return '#ef4444'
  return 'var(--muted-foreground)'
}

function evidenceLabel(evidenceKind: string | null): string {
  if (evidenceKind === 'feature-merge') return 'Feature merge'
  if (evidenceKind === 'tia-sync') return 'TIA sync'
  return 'Commit'
}

function shortSha(sha: string): string {
  return sha.slice(0, 7)
}

function defaultRollbackName(sha: string): string {
  return `rollback-${shortSha(sha)}`
}

export default function VersionControlHistory({
  workbenchId,
  worktreeId,
  commits,
  onCommitSelect,
  onObjectSelect,
}: Props) {
  const [history, setHistory] = useState<api.VcCommitEntry[]>(commits ?? [])
  const [loading, setLoading] = useState(commits === undefined)
  const [error, setError] = useState<string | null>(null)
  const [selectedCommit, setSelectedCommit] = useState<api.VcCommitEntry | null>(null)
  const [selectedPaths, setSelectedPaths] = useState<string[]>([])
  const [evidence, setEvidence] = useState<EvidenceState>(emptyEvidence)
  const [featureName, setFeatureName] = useState('')
  const [creatingRollback, setCreatingRollback] = useState(false)
  const [rollbackCreated, setRollbackCreated] = useState(false)

  useEffect(() => {
    if (commits !== undefined) {
      setHistory(commits)
      setLoading(false)
      return
    }

    let cancelled = false
    setLoading(true)
    setError(null)
    api.getWorktreeVcLog(workbenchId, worktreeId, 50)
      .then(result => { if (!cancelled) setHistory(result.commits) })
      .catch(reason => {
        if (!cancelled) setError(reason instanceof Error ? reason.message : 'Failed to load version history')
      })
      .finally(() => { if (!cancelled) setLoading(false) })

    return () => { cancelled = true }
  }, [commits, workbenchId, worktreeId])

  const selectCommit = async (commit: api.VcCommitEntry) => {
    setSelectedCommit(commit)
    setSelectedPaths([])
    setFeatureName(defaultRollbackName(commit.sha))
    setRollbackCreated(false)
    onCommitSelect?.(commit)

    if (commit.validationState !== 'Validated') {
      setEvidence(emptyEvidence)
      return
    }

    setEvidence({ loading: true, evidence: null, error: null })
    try {
      const result = await api.getVcValidation(workbenchId, worktreeId, commit.sha)
      setEvidence({ loading: false, evidence: result, error: null })
    } catch (reason) {
      setEvidence({
        loading: false,
        evidence: null,
        error: reason instanceof Error ? reason.message : 'Failed to load validation evidence',
      })
    }
  }

  const selectObject = (commit: api.VcCommitEntry, relativePath: string) => {
    setSelectedCommit(commit)
    setSelectedPaths([relativePath])
    setFeatureName(defaultRollbackName(commit.sha))
    setRollbackCreated(false)
    onObjectSelect?.(commit, relativePath)
  }

  const createRollback = async () => {
    if (!selectedCommit || selectedPaths.length === 0 || !featureName.trim()) return
    setCreatingRollback(true)
    setError(null)
    setRollbackCreated(false)
    try {
      await api.createRollbackFeature(
        workbenchId,
        selectedCommit.sha,
        selectedPaths,
        featureName.trim(),
      )
      setRollbackCreated(true)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Failed to create rollback feature')
    } finally {
      setCreatingRollback(false)
    }
  }

  return (
    <section className="flex h-full min-h-0 w-full flex-col bg-card" aria-label="Version control history">
      <header className="flex shrink-0 items-center gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <History className="h-3.5 w-3.5 text-chart-4" />
        <div className="min-w-0 flex-1">
          <h2 className="text-[10px] font-semibold">PLC history</h2>
          <p className="text-[8px] text-muted-foreground">Commits, validation evidence, and safe recovery</p>
        </div>
      </header>

      {error && <div className="shrink-0 px-3 py-2 text-[9px] text-destructive">{error}</div>}

      <div className="min-h-0 flex-1 space-y-2 overflow-y-auto p-2">
        {loading ? (
          <div className="flex items-center justify-center gap-2 py-6 text-[9px] text-muted-foreground">
            <Loader2 className="h-3 w-3 animate-spin" /> Loading history...
          </div>
        ) : history.length === 0 ? (
          <div className="py-6 text-center text-[9px] text-muted-foreground">No commits yet</div>
        ) : (
          history.map(commit => (
            <article
              key={commit.sha}
              className="rounded-md border bg-background"
              style={{ borderColor: selectedCommit?.sha === commit.sha ? 'var(--ring)' : 'var(--border)' }}
            >
              <button
                type="button"
                data-testid={`commit-${shortSha(commit.sha)}`}
                onClick={() => void selectCommit(commit)}
                className="flex w-full items-start gap-2 p-2 text-left hover:bg-accent/40"
              >
                <GitCommitHorizontal className="mt-0.5 h-3.5 w-3.5 shrink-0 text-chart-3" />
                <span className="min-w-0 flex-1">
                  <span className="flex items-center gap-1.5">
                    <span className="font-mono text-[9px] text-chart-3">{shortSha(commit.sha)}</span>
                    <span className="truncate text-[10px] font-medium">{commit.message}</span>
                  </span>
                  <span className="mt-0.5 flex flex-wrap gap-x-2 text-[8px] text-muted-foreground">
                    <span>{commit.author}</span>
                    <span>{commit.timestamp ? new Date(commit.timestamp).toLocaleString() : 'Unknown time'}</span>
                    <span>{commit.files.length} changed object{commit.files.length === 1 ? '' : 's'}</span>
                  </span>
                </span>
                <span className="flex shrink-0 flex-col items-end gap-1">
                  <span className="rounded px-1 py-0.5 text-[8px]" style={{ color: validationColor(commit), background: `${validationColor(commit)}18` }}>
                    {validationLabel(commit)}
                  </span>
                  <span className="text-[8px] text-muted-foreground">{evidenceLabel(commit.evidenceKind)}</span>
                </span>
              </button>

              <div className="border-t px-2 pb-2 pt-1.5" style={{ borderColor: 'var(--border)' }}>
                <div className="mb-1 text-[8px] uppercase tracking-[0.12em] text-muted-foreground">Changed PLC objects</div>
                <div className="flex flex-wrap gap-1">
                  {commit.files.map(path => (
                    <button
                      key={path}
                      type="button"
                      data-testid={`object-${objectName(path)}`}
                      onClick={() => selectObject(commit, path)}
                      className="rounded border px-1.5 py-1 text-left text-[9px] hover:bg-accent"
                      style={{ borderColor: 'var(--border)' }}
                      title={path}
                    >
                      {objectName(path)}
                    </button>
                  ))}
                </div>
              </div>
            </article>
          ))
        )}

        {selectedCommit && (
          <div className="space-y-2 rounded-md border bg-background p-2" style={{ borderColor: 'var(--border)' }}>
            <div className="flex items-center gap-1.5 text-[9px] font-medium">
              <ShieldCheck className="h-3 w-3 text-chart-2" /> Validation evidence
            </div>
            {evidence.loading && <div className="text-[9px] text-muted-foreground">Loading permanent evidence...</div>}
            {evidence.error && <div className="text-[9px] text-destructive">{evidence.error}</div>}
            {evidence.evidence && (
              <div className="space-y-1 rounded bg-muted/40 p-1.5 text-[8px]">
                <div className="flex items-center gap-1 text-chart-2"><CheckCircle2 className="h-3 w-3" /> Permanent evidence</div>
                <div>Confirmed by {evidence.evidence.confirmedBy}</div>
                <div>Machine validated: {evidence.evidence.machineValidated ? 'Yes' : 'No'}</div>
                {evidence.evidence.devices.map(device => (
                  <div key={device.deviceId} className="border-t pt-1" style={{ borderColor: 'var(--border)' }}>
                    <div className="font-medium">{device.plcName}</div>
                    <div className="font-mono text-muted-foreground">Checksum: {device.projectChecksum}</div>
                    <div className="text-muted-foreground">{device.objects.length} validated object{device.objects.length === 1 ? '' : 's'}</div>
                  </div>
                ))}
              </div>
            )}
            {selectedCommit.validationState !== 'Validated' && (
              <div className="text-[9px] text-muted-foreground">This commit has no validated TIA evidence.</div>
            )}

            {selectedPaths.length > 0 && (
              <div className="space-y-1.5 border-t pt-2" style={{ borderColor: 'var(--border)' }}>
                <div className="flex items-center gap-1 text-[9px] font-medium"><RotateCcw className="h-3 w-3 text-chart-5" /> Recover selected history as a feature</div>
                <div className="text-[8px] text-muted-foreground">Create a new feature containing the selected historical XML. Master is never reset.</div>
                <input
                  aria-label="Rollback feature name"
                  value={featureName}
                  onChange={event => setFeatureName(event.target.value)}
                  onInput={event => setFeatureName(event.currentTarget.value)}
                  className="w-full rounded border bg-muted px-2 py-1 text-[9px] outline-none"
                  style={{ borderColor: 'var(--border)' }}
                  placeholder="Rollback feature name"
                />
                <button
                  type="button"
                  data-testid="create-rollback-feature"
                  onClick={() => void createRollback()}
                  disabled={creatingRollback || !featureName.trim()}
                  className="flex w-full items-center justify-center gap-1 rounded bg-chart-5 px-2 py-1 text-[9px] text-white disabled:opacity-50"
                >
                  {creatingRollback ? <Loader2 className="h-3 w-3 animate-spin" /> : <RotateCcw className="h-3 w-3" />}
                  {creatingRollback ? 'Creating...' : 'Create rollback feature'}
                </button>
                {rollbackCreated && <div className="text-[9px] text-chart-2">Rollback feature created. Switch to it and commit the generated changes.</div>}
              </div>
            )}
          </div>
        )}
      </div>
    </section>
  )
}
