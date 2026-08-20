import { useState } from 'react'
import { CheckCircle2, Loader2, ShieldAlert } from 'lucide-react'
import * as api from '@/api/client'
import FeatureValidationDialog from './FeatureValidationDialog'

type Props = {
  workbenchId: string
  worktreeId: string
  branch: string
  onCommitted?: () => void
  /** Starts a title-bar operation and returns its id so the full compare reports live export progress. */
  onBeginOperation?: (kind: string, label: string) => string
}

const normalizeChecksum = (value: string) => value.replace(/\s+/g, '').toUpperCase()
const storedChecksumValues = (aggregate: string | null | undefined): string[] =>
  (aggregate ?? '')
    .split(';')
    .map(entry => entry.split(':')[1] ?? '')
    .map(normalizeChecksum)
    .filter(Boolean)
    .sort()

export default function VersionControlCompare({ workbenchId, worktreeId, branch, onCommitted, onBeginOperation }: Props) {
  const [comparison, setComparison] = useState<api.WorkbenchConsistencyResult | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [plan, setPlan] = useState<api.FeatureImportPlan | null>(null)
  const [commitTitle, setCommitTitle] = useState('')
  const [lastCommitSha, setLastCommitSha] = useState<string | null>(null)
  const [pushOutcomes, setPushOutcomes] = useState<api.PushToTiaOutcome[] | null>(null)
  const [storedChecksum, setStoredChecksum] = useState<string | null>(null)
  const [savepointMessage, setSavepointMessage] = useState('')
  const [savepointSha, setSavepointSha] = useState<string | null>(null)
  const [hardwareMessage, setHardwareMessage] = useState('')
  const [hardwareCommitSha, setHardwareCommitSha] = useState<string | null>(null)

  const compare = async () => {
    setBusy(true); setError(null); setPushOutcomes(null); setSavepointSha(null)
    const operationId = onBeginOperation?.('compare-tia', 'Comparing master with TIA Portal...')
    try {
      const [nextComparison, engineeringState] = await Promise.all([
        api.compareMasterWithTia(workbenchId, operationId),
        api.getWorktreeEngineeringState(workbenchId, worktreeId).catch(() => null),
      ])
      setComparison(nextComparison)
      setStoredChecksum(engineeringState?.revision?.tia?.projectChecksum ?? null)
      setSelected(new Set())
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'TIA comparison failed') }
    finally { setBusy(false) }
  }
  const accept = async () => {
    if (!comparison || selected.size === 0 || !commitTitle.trim()) return
    setBusy(true); setError(null)
    try {
      const result = await api.acceptTiaSynchronization(workbenchId, comparison.comparisonId, [...selected], commitTitle.trim())
      setLastCommitSha(result.commitSha ?? null)
      setCommitTitle('')
      await compare()
      onCommitted?.()
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'TIA synchronization failed') }
    finally { setBusy(false) }
  }
  const push = async () => {
    if (!comparison || selected.size === 0) return
    setBusy(true); setError(null); setPushOutcomes(null)
    try {
      const result = await api.pushSourcesToTia(workbenchId, comparison.comparisonId, [...selected])
      setPushOutcomes(result.outcomes)
      onCommitted?.()
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Push to TIA failed') }
    finally { setBusy(false) }
  }
  const savepoint = async () => {
    if (!savepointMessage.trim()) return
    setBusy(true); setError(null); setSavepointSha(null)
    try {
      const result = await api.createSvnSavepoint(workbenchId, worktreeId, savepointMessage.trim())
      setSavepointSha(result.sha.slice(0, 8))
      setSavepointMessage('')
      onCommitted?.()
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Savepoint failed') }
    finally { setBusy(false) }
  }
  const acceptHardware = async () => {
    if (!comparison?.hardware || comparison.hardware.state === 'in-sync' || !hardwareMessage.trim()) return
    setBusy(true); setError(null); setHardwareCommitSha(null)
    try {
      const result = await api.overwriteHardwareConfiguration(workbenchId, worktreeId, true, undefined, hardwareMessage.trim())
      setHardwareCommitSha(result.commitSha ?? null)
      setHardwareMessage('')
      await compare()
      onCommitted?.()
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Hardware synchronization failed') }
    finally { setBusy(false) }
  }
  const prepare = async () => {
    setBusy(true); setError(null)
    try { setPlan(await api.planFeatureImport(workbenchId, worktreeId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Feature import planning failed') }
    finally { setBusy(false) }
  }

  const liveChecksumValues = comparison
    ? Object.values(comparison.liveChecksums).filter((value): value is string => Boolean(value)).map(normalizeChecksum).sort()
    : []
  const checksumDrift = comparison != null
    && !comparison.fastGatePassed
    && storedChecksum != null
    && storedChecksumValues(storedChecksum).join('|') !== liveChecksumValues.join('|')
  const hardwareDiffers = comparison?.hardware != null && comparison.hardware.state !== 'in-sync'

  return (
    <section className="flex h-full min-h-0 flex-col" aria-label="Compare PLC source with TIA">
      <div className="flex shrink-0 items-center justify-between border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <div><div className="text-[10px] font-medium">Master vs TIA</div><div className="text-[8px] text-muted-foreground">Checksum fast gate, then exact object scan</div></div>
        <div className="flex gap-1"><button type="button" aria-label="Compare with TIA" className="rounded bg-accent px-2 py-1 text-[9px]" onClick={() => void compare()} disabled={busy}>{busy ? <Loader2 className="h-3 w-3 animate-spin" /> : 'Compare now'}</button>{branch && branch.toLowerCase() !== 'master' && <button type="button" className="rounded bg-chart-4 px-2 py-1 text-[9px] text-white" onClick={() => void prepare()} disabled={busy}>Prepare feature import</button>}</div>
      </div>
      {error && <div className="px-3 py-2 text-[9px] text-destructive">{error}</div>}
      {lastCommitSha && <div className="shrink-0 border-b px-3 py-2 text-[9px] text-emerald-600">Committed {lastCommitSha.slice(0, 8)}</div>}
      {savepointSha && <div className="shrink-0 border-b px-3 py-2 text-[9px] text-emerald-600">SVN savepoint committed {savepointSha}</div>}
      {hardwareCommitSha && <div className="shrink-0 border-b px-3 py-2 text-[9px] text-emerald-600">Hardware committed {hardwareCommitSha.slice(0, 8)}</div>}
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {!comparison ? <div className="py-8 text-center text-[9px] text-muted-foreground">Compare the connected TIA project with master to see PLC-object differences.</div> : (
          <div className="space-y-2">
            {hardwareDiffers && comparison.hardware && (
              <div className="rounded border border-amber-500/30 bg-amber-500/5 p-3 text-[9px] text-amber-700">
                <div className="font-medium">Project hardware differs from TIA</div>
                <div className="mt-1 text-[8px]">{comparison.hardware.message}</div>
                <div className="mt-2 space-y-0.5 text-[8px]">{comparison.hardware.artifacts.filter(artifact => artifact.state !== 'same').map(artifact => <div key={`${artifact.scope}:${artifact.deviceName ?? 'project'}`}>{artifact.scope === 'project' ? 'Project hardware' : artifact.deviceName} — {artifact.state}</div>)}</div>
                <label className="mt-2 block text-[8px] text-muted-foreground" htmlFor="hardware-commit-message">Hardware commit message</label>
                <input id="hardware-commit-message" aria-label="Hardware commit message" value={hardwareMessage} onChange={event => setHardwareMessage(event.currentTarget.value)} onInput={event => setHardwareMessage(event.currentTarget.value)} placeholder="Explain the hardware change..." disabled={busy} className="mt-0.5 w-full rounded border bg-muted px-2 py-1.5 text-[9px] outline-none" style={{ borderColor: 'var(--border)' }} />
                <button type="button" aria-label="Accept TIA hardware configuration" className="mt-1 rounded bg-chart-2 px-2 py-1.5 text-[9px] text-white disabled:opacity-50" onClick={() => void acceptHardware()} disabled={busy || !hardwareMessage.trim()}>Accept TIA hardware configuration</button>
              </div>
            )}
            {comparison.differences.length === 0 ? <>
            <div className={`rounded border p-3 text-[10px] ${hardwareDiffers ? 'border-border bg-muted/30' : 'border-emerald-500/30 bg-emerald-500/5'}`}><div className={`flex items-center gap-2 font-medium ${hardwareDiffers ? 'text-muted-foreground' : 'text-emerald-600'}`}>{hardwareDiffers ? 'Tracked PLC source matches master' : <><CheckCircle2 className="h-4 w-4" />TIA matches master</>}</div><div className="mt-2 text-[8px] text-muted-foreground">{comparison.fastGatePassed ? 'All device checksums match; no full object scan was required.' : 'A full object scan found no remaining differences.'}</div>{Object.entries(comparison.liveChecksums).map(([device, checksum]) => <div key={device} className="mt-1 font-mono text-[8px]">{device}: {checksum ?? 'Unavailable'}</div>)}</div>
            {checksumDrift && !hardwareDiffers ? (
              <div className="rounded border border-amber-500/30 bg-amber-500/5 p-3 text-[9px] text-amber-700">
                <div className="font-medium">TIA changed outside the tracked source</div>
                <div className="mt-1 text-[8px]">The exported XML matches, but the TIA checksum differs from the last savepoint — something untrackable changed (hardware, safety, settings). Record it with an SVN savepoint; without one, there is nothing to commit.</div>
                <label className="mt-2 block text-[8px] text-muted-foreground" htmlFor="savepoint-message">Savepoint message</label>
                <input id="savepoint-message" aria-label="Savepoint message" value={savepointMessage} onChange={event => setSavepointMessage(event.currentTarget.value)} placeholder="What changed in TIA..." disabled={busy} className="mt-0.5 w-full rounded border bg-muted px-2 py-1.5 text-[9px] outline-none" style={{ borderColor: 'var(--border)' }} />
                <button type="button" aria-label="Create SVN savepoint" className="mt-1 rounded bg-chart-2 px-2 py-1.5 text-[9px] text-white disabled:opacity-50" onClick={() => void savepoint()} disabled={busy || !savepointMessage.trim()}>Create SVN savepoint</button>
              </div>
            ) : (
              <div className="text-[8px] text-muted-foreground">{hardwareDiffers ? 'No tracked PLC source differences were found. Review and accept the project hardware change above.' : 'Checksums match the last savepoint and no source differences were found — no need to commit.'}</div>
            )}
            </> : (
          <div className="space-y-2">
            <div className="rounded border border-amber-500/30 bg-amber-500/5 p-2 text-[9px] text-amber-700">{comparison.state === 'Unavailable' ? 'TIA checksum unavailable.' : 'TIA differs from master. Accept TIA changes into the local repo, or push local objects back into TIA.'}</div>
            {comparison.differences.map(diff => { const path = diff.relativePath; const disabled = !diff.supported || diff.kind === 'Deleted' || !path; return <label key={`${diff.deviceId}:${path}:${diff.identity}`} className={`flex items-start gap-2 rounded border p-2 ${disabled ? 'opacity-60' : 'hover:bg-accent/40'}`} style={{ borderColor: 'var(--border)' }}><input type="checkbox" disabled={disabled} checked={selected.has(path)} onChange={event => setSelected(previous => { const next = new Set(previous); if (event.target.checked) next.add(path); else next.delete(path); return next })} /><span className="min-w-0 flex-1"><span className="block text-[9px] font-medium">{diff.plcName} · {diff.identity || diff.relativePath}</span><span className="block truncate font-mono text-[8px] text-muted-foreground">{path || 'Source coverage unavailable'}</span><span className="block text-[8px] text-muted-foreground">{diff.supported ? diff.kind : 'Source coverage unavailable'}</span></span>{!diff.supported && <ShieldAlert className="h-3 w-3 text-amber-500" />}</label>})}
            {selected.size > 0 && (
              <div className="space-y-2">
                <div className="space-y-1 rounded border p-2" style={{ borderColor: 'var(--border)' }}>
                  <div className="text-[8px] font-medium text-muted-foreground">TIA → local repo (commit)</div>
                  <label className="block text-[8px] text-muted-foreground" htmlFor="tia-commit-title">Commit title</label>
                  <input id="tia-commit-title" aria-label="TIA commit title" value={commitTitle} onChange={event => setCommitTitle(event.currentTarget.value)} onInput={event => setCommitTitle(event.currentTarget.value)} placeholder="Describe the TIA change..." disabled={busy} className="w-full rounded border bg-muted px-2 py-1.5 text-[9px] outline-none" />
                  <button type="button" aria-label="Accept selected TIA changes" className="w-full rounded bg-chart-2 px-2 py-1.5 text-[9px] text-white disabled:opacity-50" onClick={() => void accept()} disabled={busy || !commitTitle.trim()}>Accept {selected.size} into local repo</button>
                </div>
                <div className="space-y-1 rounded border p-2" style={{ borderColor: 'var(--border)' }}>
                  <div className="text-[8px] font-medium text-muted-foreground">Local repo → TIA (overwrite TIA objects)</div>
                  <button type="button" aria-label="Push selected local changes to TIA" className="w-full rounded bg-chart-4 px-2 py-1.5 text-[9px] text-white disabled:opacity-50" onClick={() => void push()} disabled={busy}>Push {selected.size} to TIA</button>
                  <div className="text-[8px] text-muted-foreground">Imports the local XML into TIA (overwrites those objects). Compile and create a savepoint afterwards to record the state.</div>
                </div>
              </div>
            )}
            {pushOutcomes && (
              <div className="space-y-0.5 rounded border p-2 text-[8px]" style={{ borderColor: 'var(--border)' }}>
                {pushOutcomes.map(outcome => (
                  <div key={outcome.path} className={outcome.success ? 'text-emerald-600' : 'text-destructive'}>
                    {outcome.success ? '✓' : '✗'} {outcome.path}{outcome.message ? ` — ${outcome.message}` : ''}
                  </div>
                ))}
              </div>
            )}
          </div>
            )}
          </div>
        )}
      </div>
      {plan && <FeatureValidationDialog workbenchId={workbenchId} featureWorktreeId={worktreeId} plan={plan} onClose={() => setPlan(null)} />}
    </section>
  )
}
