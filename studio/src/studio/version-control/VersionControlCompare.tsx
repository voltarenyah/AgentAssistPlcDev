import { useState } from 'react'
import { CheckCircle2, Loader2, ShieldAlert } from 'lucide-react'
import * as api from '@/api/client'
import FeatureValidationDialog from './FeatureValidationDialog'

type Props = { workbenchId: string; worktreeId: string; branch: string; onCommitted?: () => void }

export default function VersionControlCompare({ workbenchId, worktreeId, branch, onCommitted }: Props) {
  const [comparison, setComparison] = useState<api.WorkbenchConsistencyResult | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [plan, setPlan] = useState<api.FeatureImportPlan | null>(null)
  const [commitTitle, setCommitTitle] = useState('')
  const [lastCommitSha, setLastCommitSha] = useState<string | null>(null)

  const compare = async () => {
    setBusy(true); setError(null)
    try {
      setComparison(await api.compareMasterWithTia(workbenchId))
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
  const prepare = async () => {
    setBusy(true); setError(null)
    try { setPlan(await api.planFeatureImport(workbenchId, worktreeId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Feature import planning failed') }
    finally { setBusy(false) }
  }

  return (
    <section className="flex h-full min-h-0 flex-col" aria-label="Compare PLC source with TIA">
      <div className="flex shrink-0 items-center justify-between border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <div><div className="text-[10px] font-medium">Master vs TIA</div><div className="text-[8px] text-muted-foreground">Checksum fast gate, then exact object scan</div></div>
        <div className="flex gap-1"><button type="button" aria-label="Compare with TIA" className="rounded bg-accent px-2 py-1 text-[9px]" onClick={() => void compare()} disabled={busy}>{busy ? <Loader2 className="h-3 w-3 animate-spin" /> : 'Compare now'}</button>{branch && branch.toLowerCase() !== 'master' && <button type="button" className="rounded bg-chart-4 px-2 py-1 text-[9px] text-white" onClick={() => void prepare()} disabled={busy}>Prepare feature import</button>}</div>
      </div>
      {error && <div className="px-3 py-2 text-[9px] text-destructive">{error}</div>}
      {lastCommitSha && <div className="shrink-0 border-b px-3 py-2 text-[9px] text-emerald-600">Committed {lastCommitSha.slice(0, 8)}</div>}
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {!comparison ? <div className="py-8 text-center text-[9px] text-muted-foreground">Compare the connected TIA project with master to see PLC-object differences.</div> : comparison.differences.length === 0 ? (
          <div className="rounded border border-emerald-500/30 bg-emerald-500/5 p-3 text-[10px]"><div className="flex items-center gap-2 font-medium text-emerald-600"><CheckCircle2 className="h-4 w-4" />TIA matches master</div><div className="mt-2 text-[8px] text-muted-foreground">{comparison.fastGatePassed ? 'All device checksums match; no full object scan was required.' : 'A full object scan found no remaining differences.'}</div>{Object.entries(comparison.liveChecksums).map(([device, checksum]) => <div key={device} className="mt-1 font-mono text-[8px]">{device}: {checksum ?? 'Unavailable'}</div>)}</div>
        ) : (
          <div className="space-y-2"><div className="rounded border border-amber-500/30 bg-amber-500/5 p-2 text-[9px] text-amber-700">{comparison.state === 'Unavailable' ? 'TIA checksum unavailable.' : 'TIA differs from master; select supported changes to accept and commit.'}</div>{comparison.differences.map(diff => { const path = diff.relativePath; const disabled = !diff.supported || diff.kind === 'Deleted' || !path; return <label key={`${diff.deviceId}:${path}:${diff.identity}`} className={`flex items-start gap-2 rounded border p-2 ${disabled ? 'opacity-60' : 'hover:bg-accent/40'}`} style={{ borderColor: 'var(--border)' }}><input type="checkbox" disabled={disabled} checked={selected.has(path)} onChange={event => setSelected(previous => { const next = new Set(previous); if (event.target.checked) next.add(path); else next.delete(path); return next })} /><span className="min-w-0 flex-1"><span className="block text-[9px] font-medium">{diff.plcName} · {diff.identity || diff.relativePath}</span><span className="block truncate font-mono text-[8px] text-muted-foreground">{path || 'Source coverage unavailable'}</span><span className="block text-[8px] text-muted-foreground">{diff.supported ? diff.kind : 'Source coverage unavailable'}</span></span>{!diff.supported && <ShieldAlert className="h-3 w-3 text-amber-500" />}</label>})}{selected.size > 0 && <div className="space-y-1"><label className="block text-[8px] text-muted-foreground" htmlFor="tia-commit-title">Commit title</label><input id="tia-commit-title" aria-label="TIA commit title" value={commitTitle} onChange={event => setCommitTitle(event.currentTarget.value)} onInput={event => setCommitTitle(event.currentTarget.value)} placeholder="Describe the TIA change..." disabled={busy} className="w-full rounded border bg-muted px-2 py-1.5 text-[9px] outline-none" /><button type="button" aria-label="Accept selected TIA changes" className="w-full rounded bg-chart-2 px-2 py-1.5 text-[9px] text-white" onClick={() => void accept()} disabled={busy || !commitTitle.trim()}>Accept and commit selected ({selected.size})</button></div>}</div>
        )}
      </div>
      {plan && <FeatureValidationDialog workbenchId={workbenchId} featureWorktreeId={worktreeId} plan={plan} onClose={() => setPlan(null)} />}
    </section>
  )
}
