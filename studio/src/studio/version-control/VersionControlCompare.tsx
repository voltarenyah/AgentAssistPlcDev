import { useEffect, useRef, useState } from 'react'
import { Loader2, ShieldAlert } from 'lucide-react'
import * as api from '@/api/client'
import FeatureValidationDialog from './FeatureValidationDialog'

type Props = {
  workbenchId: string
  worktreeId: string
  branch: string
  /** Increment to trigger a comparison (the panel's Compare with TIA action). */
  signal: number
  /** The changes page commit message — reused as the title for accept actions. */
  commitMessage: string
  /** Reports the TIA paths selected for the global commit action. */
  onSelectionChanged?: (comparisonId: string | null, paths: string[]) => void
  /** Reports whether the completed comparison has anything to display. */
  onComparisonStateChanged?: (hasDifferences: boolean) => void
  /** Incremented by the global commit flow after selected TIA sources are committed. */
  selectionResetSignal?: number
  onCommitted?: () => void | Promise<void>
  /** Starts a title-bar operation and returns its id so the full compare reports live export progress. */
  onBeginOperation?: (kind: string, label: string) => string
}

const displayError = (error: unknown) => error instanceof Error ? error.message : 'Unexpected operation failure'

export default function VersionControlCompare({ workbenchId, worktreeId, branch, signal, commitMessage, onSelectionChanged, onComparisonStateChanged, selectionResetSignal = 0, onCommitted, onBeginOperation }: Props) {
  const [started, setStarted] = useState(false)
  const [comparison, setComparison] = useState<api.WorkbenchConsistencyResult | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [plan, setPlan] = useState<api.FeatureImportPlan | null>(null)
  const [needsCompileConfirmation, setNeedsCompileConfirmation] = useState(false)
  const handledSignal = useRef(0)

  const compare = async (allowCompile = false) => {
    setBusy(true); setError(null); setNeedsCompileConfirmation(false)
    const operationId = onBeginOperation?.('compare-tia', 'Comparing master with TIA Portal...')
    try {
      const nextComparison = await (allowCompile
        ? api.compareMasterWithTia(workbenchId, operationId, true)
        : api.compareMasterWithTia(workbenchId, operationId))
      setComparison(nextComparison)
      onComparisonStateChanged?.(nextComparison.state === 'Unavailable' || nextComparison.differences.length > 0 || nextComparison.hardware?.state === 'changed')
      setSelected(new Set())
      onSelectionChanged?.(nextComparison.comparisonId, [])
    } catch (reason) {
      if (reason instanceof api.WorkbenchApiError && reason.code === 'PLC_CHECKSUM_UNAVAILABLE') {
        setNeedsCompileConfirmation(true)
      } else {
        setError(displayError(reason))
      }
    }
    finally { setBusy(false) }
  }

  useEffect(() => {
    if (signal === 0 || signal === handledSignal.current) return
    handledSignal.current = signal
    setStarted(true)
    void compare()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signal])

  useEffect(() => {
    if (selectionResetSignal === 0) return
    setSelected(new Set())
    setComparison(null)
    setStarted(false)
    setError(null)
    setNeedsCompileConfirmation(false)
    onSelectionChanged?.(null, [])
    onComparisonStateChanged?.(false)
    // The reset signal is the only trigger; the parent callback is intentionally a render-local handler.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectionResetSignal])

  const toggleSelection = (path: string, checked: boolean) => {
    const next = new Set(selected)
    if (checked) next.add(path)
    else next.delete(path)
    setSelected(next)
    if (comparison) onSelectionChanged?.(comparison.comparisonId, [...next])
  }

  const acceptHardware = async () => {
    if (!comparison?.hardware || comparison.hardware.state === 'in-sync' || !commitMessage.trim()) return
    setBusy(true); setError(null)
    try {
      await api.overwriteHardwareConfiguration(workbenchId, worktreeId, true, undefined, commitMessage.trim())
      setComparison(null)
      setStarted(false)
      onComparisonStateChanged?.(false)
      await onCommitted?.()
    }
    catch (reason) { setError(displayError(reason)) }
    finally { setBusy(false) }
  }
  const prepare = async () => {
    setBusy(true); setError(null)
    try { setPlan(await api.planFeatureImport(workbenchId, worktreeId)) }
    catch (reason) { setError(displayError(reason)) }
    finally { setBusy(false) }
  }

  if (!started) return null

  const hardwareDiffers = comparison?.hardware != null && comparison.hardware.state !== 'in-sync'
  const differences = comparison?.differences ?? []
  const titleMissing = commitMessage.trim().length === 0
  const cleanComparison = comparison != null && comparison.state !== 'Unavailable' && differences.length === 0 && !hardwareDiffers

  if (cleanComparison) return null

  return (
    <div className="shrink-0" data-testid="vc-compare-result">
      <div className="px-2.5 pb-2.5">
        {busy && !comparison && (
          <div className="flex items-center gap-2 py-2 text-[10px] text-muted-foreground">
            <Loader2 className="h-3.5 w-3.5 animate-spin" /> Comparing the connected TIA project with master...
          </div>
        )}
        {error && <div className="py-1 text-[10px] text-destructive">{error}</div>}
        {needsCompileConfirmation && (
          <div className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-2.5 text-[10px] text-amber-600">
            <div className="font-medium">TIA has no compiled PLC checksum</div>
            <div className="mt-1 text-[9px]">Compile and save the connected TIA project automatically, then compare again?</div>
            <button
              type="button"
              aria-label="Compile and save in TIA, then compare"
              className="mt-1.5 rounded-md bg-chart-2 px-2 py-1 text-[10px] font-medium text-white disabled:opacity-40"
              disabled={busy}
              onClick={() => void compare(true)}
            >
              Compile and save, then compare
            </button>
          </div>
        )}

        {comparison && (
          <div className="space-y-2">
            {hardwareDiffers && comparison.hardware && (
              <div className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-2.5 text-[10px] text-amber-600">
                <div className="font-medium">Project hardware differs from TIA</div>
                <div className="mt-1 text-[9px]">{comparison.hardware.message}</div>
                <button
                  type="button"
                  aria-label="Accept TIA hardware configuration"
                  className="mt-1.5 rounded-md bg-chart-2 px-2 py-1 text-[10px] font-medium text-white disabled:opacity-40"
                  disabled={busy || titleMissing}
                  title={titleMissing ? 'Type a commit message above first' : undefined}
                  onClick={() => void acceptHardware()}
                >
                  Accept TIA hardware configuration
                </button>
                {titleMissing && <div className="mt-1 text-[9px]">Type a commit message above to accept.</div>}
              </div>
            )}

            {differences.length === 0 ? (
              <div className="px-3.5 py-5 text-center text-[10px] text-muted-foreground" data-testid="vc-clean-state">
                <div className={`font-medium ${hardwareDiffers ? 'text-muted-foreground' : 'text-emerald-600'}`}>
                  {hardwareDiffers ? 'Tracked PLC source matches master' : 'TIA matches master'}
                </div>
                <div className="mt-1 text-[9px]">
                  {comparison.fastGatePassed ? 'All device checksums match; no full object scan was required.' : 'A full object scan found no remaining differences.'}
                </div>
              </div>
            ) : (
              <>
                {differences.map(diff => {
                  const path = diff.relativePath
                  const disabled = !diff.supported || diff.kind === 'Deleted' || !path
                  return (
                    <label key={`${diff.deviceId}:${path}:${diff.identity}`} className={`flex items-start gap-2 rounded-lg border p-2 ${disabled ? 'opacity-60' : 'cursor-pointer hover:bg-white/5'}`} style={{ borderColor: 'var(--border)' }}>
                      <input
                        type="checkbox"
                        disabled={disabled}
                        checked={path ? selected.has(path) : false}
                        onChange={event => path && toggleSelection(path, event.target.checked)}
                      />
                      <span className="min-w-0 flex-1">
                        <span className="block text-[10px] font-medium">{diff.plcName} · {diff.identity || diff.relativePath}</span>
                        <span className="block truncate font-mono text-[9px] text-muted-foreground">{path || 'Source coverage unavailable'}</span>
                        <span className="block text-[9px] text-muted-foreground">{diff.supported ? diff.kind : 'Source coverage unavailable'}</span>
                      </span>
                      {!diff.supported && <ShieldAlert className="h-3 w-3 text-amber-500" />}
                    </label>
                  )
                })}
                {selected.size > 0 && titleMissing && (
                  <div className="text-[9px] text-muted-foreground">Type a commit message above to include the selected TIA sources in the global commit.</div>
                )}
              </>
            )}

            {branch && branch.toLowerCase() !== 'master' && (
              <button
                type="button"
                className="h-7 w-full rounded-lg border text-[10px] font-semibold hover:bg-white/5 disabled:opacity-40"
                style={{ borderColor: 'var(--border)' }}
                onClick={() => void prepare()}
                disabled={busy}
              >
                Prepare feature import
              </button>
            )}
          </div>
        )}
      </div>

      {plan && <FeatureValidationDialog workbenchId={workbenchId} featureWorktreeId={worktreeId} plan={plan} onClose={() => setPlan(null)} />}
    </div>
  )
}
