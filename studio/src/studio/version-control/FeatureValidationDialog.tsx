import { useState } from 'react'
import { CheckCircle2, Loader2, X } from 'lucide-react'
import * as api from '@/api/client'

type Props = { workbenchId: string; featureWorktreeId: string; plan: api.FeatureImportPlan; onClose: () => void }

export default function FeatureValidationDialog({ workbenchId, featureWorktreeId, plan, onClose }: Props) {
  const [selected, setSelected] = useState(() => new Set(plan.objects.filter(item => item.importable).map(item => item.relativePath)))
  const [session, setSession] = useState<api.FeatureImportSession | null>(null)
  const [validation, setValidation] = useState<api.ValidatedMergeResult | null>(null)
  const [machineValidated, setMachineValidated] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const toggle = (path: string, checked: boolean) => setSelected(previous => {
    const next = new Set(previous)
    if (checked) next.add(path)
    else next.delete(path)
    return next
  })
  const run = async (action: () => Promise<void>) => { setBusy(true); setError(null); try { await action() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Version control operation failed') } finally { setBusy(false) } }
  const importSelected = () => void run(async () => setSession(await api.importFeaturePaths(workbenchId, plan.planId, [...selected])))
  const validate = () => void run(async () => { if (!session) return; setValidation(await api.validateFeatureMerge(workbenchId, featureWorktreeId, session.sessionId, machineValidated, 'Studio user')) })
  const merge = () => void run(async () => { if (!validation?.validationId || validation.state !== 'Ready') return; await api.mergeValidatedFeature(workbenchId, validation.validationId); onClose() })
  return <div className="absolute inset-0 z-20 flex items-center justify-center bg-black/30 p-4">
    <section className="w-full max-w-lg rounded-lg border bg-card shadow-xl" style={{ borderColor: 'var(--border)' }} aria-label="Feature validation">
      <header className="flex items-center gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}><div className="min-w-0 flex-1"><div className="text-[11px] font-semibold">Validate feature import</div><div className="font-mono text-[8px] text-muted-foreground">{plan.featureSha.slice(0, 8)} → master {plan.masterSha.slice(0, 8)}</div></div><button type="button" className="icon-button" onClick={onClose}><X className="h-3.5 w-3.5" /></button></header>
      <div className="max-h-[55vh] space-y-2 overflow-y-auto p-3">{error && <div className="text-[9px] text-destructive">{error}</div>}{plan.objects.map(item => <label key={item.relativePath} className={`flex items-start gap-2 rounded border p-2 ${item.importable ? '' : 'opacity-60'}`} style={{ borderColor: 'var(--border)' }}><input type="checkbox" disabled={!item.importable || !!session} checked={selected.has(item.relativePath)} onChange={event => toggle(item.relativePath, event.target.checked)} /><span className="min-w-0 flex-1"><span className="block font-mono text-[9px]">{item.relativePath}</span><span className="block text-[8px] text-muted-foreground">{item.importable ? 'Importable' : item.reason ?? 'Blocked'}</span></span>{session?.objects.find(outcome => outcome.relativePath === item.relativePath)?.state === 'Imported' && <CheckCircle2 className="h-3 w-3 text-emerald-500" />}</label>)}{session && <div className="rounded bg-muted/40 p-2 text-[9px]">{session.objects.map(item => <div key={item.relativePath}>{item.relativePath}: {typeof item.state === 'number' ? item.state : item.state}{item.error ? ` — ${item.error}` : ''}</div>)}</div>}{session && <label className="flex items-center gap-2 rounded border p-2 text-[9px]" style={{ borderColor: 'var(--border)' }}><input type="checkbox" aria-label="Machine validation completed" checked={machineValidated} onChange={event => setMachineValidated(event.target.checked)} /> Machine validation completed: the complete PLC software compiled and was tested on the machine.</label>}{validation && <div className={`rounded p-2 text-[9px] ${validation.state === 'Ready' ? 'bg-emerald-500/10 text-emerald-700' : 'bg-amber-500/10 text-amber-700'}`}>Validation: {validation.state}{validation.error ? ` — ${validation.error}` : ''}</div>}</div>
      <footer className="flex justify-end gap-2 border-t px-3 py-2" style={{ borderColor: 'var(--border)' }}>{!session && <button type="button" className="rounded bg-chart-4 px-3 py-1.5 text-[9px] text-white" disabled={busy || selected.size === 0} onClick={importSelected}>{busy ? <Loader2 className="h-3 w-3 animate-spin" /> : 'Import selected'}</button>}{session && !validation && <button type="button" className="rounded bg-chart-4 px-3 py-1.5 text-[9px] text-white" disabled={busy || !machineValidated} onClick={validate}>{busy ? <Loader2 className="h-3 w-3 animate-spin" /> : 'Compile all devices'}</button>}{validation?.state === 'Ready' && <button type="button" className="rounded bg-emerald-600 px-3 py-1.5 text-[9px] text-white" disabled={busy || !machineValidated} onClick={merge}>{busy ? <Loader2 className="h-3 w-3 animate-spin" /> : 'Merge validated feature'}</button>}</footer>
    </section>
  </div>
}
