import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ChevronDown, ChevronRight, Loader2, ShieldAlert } from 'lucide-react'
import * as api from '@/api/client'
import FeatureValidationDialog from './FeatureValidationDialog'
import OperationTimingList, { formatElapsed } from '@/studio/workbench/OperationTimingList'

type Props = {
  workbenchId: string
  worktreeId: string
  branch: string
  /** Increment to trigger a comparison (the panel's Compare with TIA action). */
  signal: number
  /**
   * Which comparison the signal runs: the whole project (`full`, the default) or only the active
   * task's staged source objects (`task`). A task result is never a project-wide verdict.
   */
  mode?: 'full' | 'task'
  /** The active worktree task whose staged objects a `task` comparison reads. */
  taskId?: string | null
  /** Names the covered task in the task-compare heading. */
  taskTitle?: string | null
  /** Whether the user wants the optional project AML/network verification. */
  verifyHardware?: boolean
  /** The changes page commit message — reused as the title for accept actions. */
  commitMessage: string
  /** Reports the TIA paths selected for the active worktree commit action. */
  onSelectionChanged?: (comparisonId: string | null, paths: string[], safetyPaths?: string[]) => void
  /** Reports whether the completed comparison has anything to display. */
  onComparisonStateChanged?: (hasDifferences: boolean) => void
  /** Incremented by the commit flow after selected TIA sources are committed. */
  selectionResetSignal?: number
  onCommitted?: () => void | Promise<void>
  /** Starts a title-bar operation and returns its id so the full compare reports live export progress. */
  onBeginOperation?: (kind: string, label: string) => string
  /** The polling status for this compare, including active and completed phase timings. */
  operationStatus?: api.OperationStatus | null
  /** Notifies the parent while a comparison is running so commit controls can be hidden. */
  onComparisonBusyChanged?: (busy: boolean) => void
}

const displayError = (error: unknown) => error instanceof Error ? error.message : 'Unexpected operation failure'

const safetyKindLabel = (kind: api.SafetyBlockDifference['kind']) => {
  if (typeof kind === 'string') return kind
  return ['Changed', 'Added', 'Removed', 'Invalidated'][kind] ?? 'Changed'
}

const shortFingerprint = (value: string) => value.slice(0, 7)

/**
 * Explanations for the backend's task-stage problem codes. These describe only the staged objects
 * of one task; none of them is a statement about the rest of the project. `TASK_STAGE_EMPTY` and
 * `TASK_STAGE_INVALID` arrive as request failures, the others as problem entries in a 200 result.
 */
const taskStageProblemText: Record<string, { title: string; detail: string }> = {
  TASK_STAGE_EMPTY: {
    title: 'No source objects are staged on this task',
    detail: 'Add source objects to the task before comparing it with TIA.',
  },
  TASK_STAGE_BASELINE_MISSING: {
    title: 'No committed baseline yet',
    detail: 'This staged object has no fingerprint baseline yet because it has no committed Git content. Commit the object once; task comparison reports it until then.',
  },
  TASK_STAGE_BASELINE_INVALID: {
    title: 'Committed baseline cannot be read',
    detail: "This staged object's fingerprint baseline cannot be read. Commit the object again to record a fresh baseline.",
  },
  TASK_STAGE_MISSING: {
    title: 'Missing or unreadable in TIA',
    detail: 'The staged object is gone, renamed, or unreadable in TIA. Recreate or re-export it in TIA, then compare again.',
  },
  TASK_STAGE_INVALID: {
    title: 'Staged object is outside the task device',
    detail: 'A staged source object does not belong to the task device. Stage an object of the task device instead.',
  },
}

type TaskStageProblemState = { code: string; message: string }

export default function VersionControlCompare({ workbenchId, worktreeId, branch, signal, mode = 'full', taskId = null, taskTitle = null, verifyHardware = true, commitMessage, onSelectionChanged, onComparisonStateChanged, selectionResetSignal = 0, onCommitted, onBeginOperation, operationStatus = null, onComparisonBusyChanged }: Props) {
  const [started, setStarted] = useState(false)
  const [comparison, setComparison] = useState<api.WorkbenchConsistencyResult | null>(null)
  // A stage-scope failure that arrives as a request error — an empty or foreign stage list — which no
  // comparison result can carry, because the request never produced one.
  const [requestProblem, setRequestProblem] = useState<TaskStageProblemState | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [selectedSafety, setSelectedSafety] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [plan, setPlan] = useState<api.FeatureImportPlan | null>(null)
  const [needsCompileConfirmation, setNeedsCompileConfirmation] = useState(false)
  const [timingsCollapsed, setTimingsCollapsed] = useState(false)
  const handledSignal = useRef(0)

  const compare = async (allowCompile = false) => {
    setBusy(true); onComparisonBusyChanged?.(true); setError(null); setNeedsCompileConfirmation(false)
    const operationLabel = branch.toLowerCase() === 'master'
      ? 'Comparing master with TIA Portal...'
      : 'Comparing master with the selected TIA project...'
    const operationId = onBeginOperation?.('compare-tia', operationLabel)
    try {
      const compareSelectedProject = (compile: boolean, hardware: boolean) => {
        if (branch.toLowerCase() === 'master') {
          if (!hardware) return api.compareMasterWithTia(workbenchId, operationId, compile, false)
          if (compile) return api.compareMasterWithTia(workbenchId, operationId, true)
          return api.compareMasterWithTia(workbenchId, operationId)
        }
        return api.compareMasterWithTia(workbenchId, operationId, compile, hardware, worktreeId)
      }
      const nextComparison = await compareSelectedProject(allowCompile, verifyHardware)
      setComparison(nextComparison)
      onComparisonStateChanged?.(nextComparison.state === 'Unavailable' || nextComparison.differences.length > 0 || nextComparison.hardware?.state === 'changed' || nextComparison.safetyChanged === true)
      setSelected(new Set())
      setSelectedSafety(new Set())
      onSelectionChanged?.(nextComparison.comparisonId, [])
    } catch (reason) {
      if (reason instanceof api.WorkbenchApiError
        && (reason.code === 'PLC_CHECKSUM_UNAVAILABLE' || reason.code === 'PLC_NOT_COMPILED')) {
        setNeedsCompileConfirmation(true)
      } else {
        setError(displayError(reason))
      }
    }
    finally { setBusy(false); onComparisonBusyChanged?.(false) }
  }

  /**
   * Compares only the active task's staged source objects. The result is a task-scoped comparison —
   * the same selectable, committable difference rows a project-wide scan produces — so the user
   * reviews and commits from it exactly as they do for a full scan. It never claims the project is
   * clean, and the backend refuses to certify the managed-source baseline from it (ADR-0003).
   */
  const compareActiveTask = async () => {
    if (!taskId) return
    setBusy(true); onComparisonBusyChanged?.(true); setError(null); setRequestProblem(null); setNeedsCompileConfirmation(false)
    const operationId = onBeginOperation?.('compare-tia', 'Comparing this task’s staged source objects with TIA Portal...')
    try {
      const nextComparison = await api.compareTaskWithTia(workbenchId, worktreeId, taskId, operationId)
      setComparison(nextComparison)
      setSelected(new Set())
      setSelectedSafety(new Set())
      onSelectionChanged?.(nextComparison.comparisonId, [])
      onComparisonStateChanged?.((nextComparison.stageProblems?.length ?? 0) > 0 || nextComparison.differences.length > 0)
    } catch (reason) {
      if (reason instanceof api.WorkbenchApiError && taskStageProblemText[reason.code]) {
        setRequestProblem({ code: reason.code, message: reason.message })
      } else {
        setError(displayError(reason))
      }
      onSelectionChanged?.(null, [])
      onComparisonStateChanged?.(true)
    } finally { setBusy(false); onComparisonBusyChanged?.(false) }
  }

  useLayoutEffect(() => {
    if (signal === 0 || signal === handledSignal.current) return
    handledSignal.current = signal
    setStarted(true)
    if (mode === 'task') void compareActiveTask()
    else void compare()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signal])

  // Switching scope — or switching the task a task scope covers — must not leave the previous
  // result on screen as if it described the new one.
  const previousMode = useRef(mode)
  const previousTaskId = useRef(taskId)
  useEffect(() => {
    const modeChanged = previousMode.current !== mode
    const taskChanged = mode === 'task' && previousTaskId.current !== taskId
    if (!modeChanged && !taskChanged) return
    previousMode.current = mode
    previousTaskId.current = taskId
    setComparison(null)
    setRequestProblem(null)
    setStarted(false)
    setError(null)
    setNeedsCompileConfirmation(false)
    onComparisonBusyChanged?.(false)
    onComparisonStateChanged?.(false)
    // Only a scope or covered-task change resets, so the parent callbacks stay render-local handlers.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, taskId])

  useEffect(() => {
    if (selectionResetSignal === 0) return
    setSelected(new Set())
    setSelectedSafety(new Set())
    setComparison(null)
    setRequestProblem(null)
    setStarted(false)
    onComparisonBusyChanged?.(false)
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
    if (comparison) {
      if (selectedSafety.size > 0) onSelectionChanged?.(comparison.comparisonId, [...next], [...selectedSafety])
      else onSelectionChanged?.(comparison.comparisonId, [...next])
    }
  }

  const toggleSafetySelection = (key: string, checked: boolean) => {
    const next = new Set(selectedSafety)
    if (checked) next.add(key)
    else next.delete(key)
    setSelectedSafety(next)
    if (comparison) onSelectionChanged?.(comparison.comparisonId, [...selected], [...next])
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

  const taskMode = mode === 'task'
  const hardware = comparison?.hardware ?? null
  const hardwareDiffers = hardware != null && hardware.state !== 'in-sync'
  const hardwareChecked = comparison?.hardwareChecked !== false
  const fastGatePassed = comparison?.fastGatePassed ?? false
  const safetyChanges = comparison?.safety?.filter(entry => entry.changed) ?? []
  const differences = comparison?.differences ?? []
  const timings = comparison?.timings ?? []
  const titleMissing = commitMessage.trim().length === 0
  const stageProblems = comparison?.stageProblems ?? []
  const stageProblemGroups = new Map<string, string[]>()
  for (const problem of stageProblems) {
    stageProblemGroups.set(problem.code, [...(stageProblemGroups.get(problem.code) ?? []), problem.sourceObjectId])
  }
  // A task-scoped result is in sync only when nothing was left uncompared. With a stage problem the
  // problems are the verdict — "in sync" would claim something the comparison never established.
  const taskClean = taskMode && stageProblems.length === 0 && requestProblem === null

  return (
    <div className="shrink-0" data-testid="vc-compare-result">
      <div className="px-3.5 pb-2.5">
        {busy && (
          <div className="rounded-lg border border-chart-2/30 bg-chart-2/5 p-2.5 text-[10px] text-muted-foreground" data-testid="vc-compare-progress" role="status" aria-live="polite">
            <div className="flex items-center gap-2">
              <Loader2 className="h-3.5 w-3.5 animate-spin text-chart-2" />
              <span className="font-medium text-foreground">
                {taskMode
                  ? 'Comparing this task’s staged source objects with TIA Portal...'
                  : `Comparing ${branch.toLowerCase() === 'master' ? 'the connected TIA project with master' : 'master with the selected TIA project'}...`}
              </span>
            </div>
            <div className="mt-2 h-1 overflow-hidden rounded-full bg-chart-2/15" aria-hidden="true">
              <div className="h-full w-2/5 animate-pulse rounded-full bg-chart-2" />
            </div>
            <OperationTimingList status={operationStatus} className="mt-2" />
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

        {!busy && (comparison !== null || (taskMode && requestProblem !== null)) && (
          <div className="space-y-2" data-testid="vc-compare-differences">
            {taskMode && (
              <div className="text-[10px] font-medium" data-testid="vc-task-compare-heading">
                Task compare{taskTitle ? `: ${taskTitle}` : ''}
              </div>
            )}
            {taskMode && requestProblem && (
              <div className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-2.5 text-[10px] text-amber-600" data-testid="vc-task-problem" data-problem-code={requestProblem.code}>
                <div className="font-medium">{taskStageProblemText[requestProblem.code]?.title ?? requestProblem.message}</div>
                {taskStageProblemText[requestProblem.code] && <div className="mt-1 text-[9px]">{taskStageProblemText[requestProblem.code].detail}</div>}
                <div className="mt-1 break-all font-mono text-[8px] text-muted-foreground">{requestProblem.code}</div>
              </div>
            )}
            {[...stageProblemGroups.entries()].map(([code, sourceObjectIds]) => (
              <div key={code} className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-2.5 text-[10px] text-amber-600" data-testid="vc-task-problem" data-problem-code={code}>
                <div className="font-medium">{taskStageProblemText[code]?.title ?? 'This staged object could not be compared with TIA'}</div>
                <div className="mt-1 text-[9px]">{taskStageProblemText[code]?.detail ?? 'The staged object could not be compared with TIA.'}</div>
                <ul className="mt-1 space-y-0.5 font-mono text-[8px] text-muted-foreground">
                  {sourceObjectIds.map(sourceObjectId => <li key={sourceObjectId} className="break-all">{sourceObjectId}</li>)}
                </ul>
              </div>
            ))}
            {timings.length > 0 && (
              <section className="rounded-lg border border-border/70 bg-muted/25 p-2.5 text-[9px]" data-comparison-timings aria-label="TIA comparison timings">
                <button
                  type="button"
                  className="flex w-full items-center gap-1.5 text-left text-[8px] font-semibold uppercase tracking-[0.14em] text-muted-foreground hover:text-foreground"
                  aria-expanded={!timingsCollapsed}
                  aria-label={`${timingsCollapsed ? 'Expand' : 'Collapse'} comparison timings`}
                  onClick={() => setTimingsCollapsed(previous => !previous)}
                >
                  {timingsCollapsed ? <ChevronRight className="h-3 w-3" aria-hidden="true" /> : <ChevronDown className="h-3 w-3" aria-hidden="true" />}
                  <span>Comparison timings</span>
                  <span className="ml-auto font-normal normal-case tracking-normal">{timings.length}</span>
                </button>
                {!timingsCollapsed && (
                  <ol className="mt-1 space-y-1" data-comparison-timings-list>
                    {timings.map((timing, index) => (
                      <li key={`${timing.phase}:${timing.plcName ?? 'project'}:${index}`} className="flex items-start gap-2">
                        <span className="min-w-0 flex-1">
                          <span className="block text-foreground">{timing.purpose}</span>
                          <span className="block text-[8px] text-muted-foreground">
                            {timing.plcName ? `${timing.plcName} · ` : ''}{timing.outcome}
                          </span>
                        </span>
                        <time className="shrink-0 font-mono text-foreground/80">{formatElapsed(timing.elapsedMilliseconds)}</time>
                      </li>
                    ))}
                  </ol>
                )}
              </section>
            )}
            {safetyChanges.length > 0 && (
              <div className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-2.5 text-[10px] text-amber-600" data-testid="vc-safety-diff">
                <div className="font-medium">Safety program changed (F-signature)</div>
                {safetyChanges.map(entry => (
                  <div key={entry.deviceId} className="mt-1 text-[9px]">
                    <div className="font-medium">{entry.plcName}</div>
                    {entry.blockDifferences ? (
                      <div className="mt-1 space-y-1">
                        {entry.blockDifferences.map(diff => {
                          const key = `${entry.deviceId}:${diff.path}`
                          return <label key={key} className="flex cursor-pointer items-start gap-2 rounded border border-amber-500/25 bg-amber-500/5 p-1.5">
                            <input type="checkbox" checked={selectedSafety.has(key)} onChange={event => toggleSafetySelection(key, event.target.checked)} />
                            <span className="min-w-0 flex-1">
                              <span className="block break-all font-mono">{diff.path}</span>
                              <span className="block text-[8px] text-muted-foreground">Safety change · {safetyKindLabel(diff.kind)}</span>
                              <span className="block break-all font-mono text-[8px]">Baseline: {diff.baselineSignature ?? 'not present'}</span>
                              <span className="block break-all font-mono text-[8px]">Current: {diff.currentSignature ?? 'not present'}</span>
                            </span>
                          </label>
                        })}
                      </div>
                    ) : entry.changedBlocks ? (
                      <ul className="mt-0.5 space-y-0.5 font-mono">
                        {entry.changedBlocks.map(path => <li key={path} className="break-all">{path}</li>)}
                      </ul>
                    ) : (
                      <div className="text-muted-foreground">Block-level detail unavailable — the baseline predates per-block signature records. Baseline: {entry.baselineFSignature ?? 'unavailable'}; current: {entry.fSignature ?? 'unavailable'}.</div>
                    )}
                  </div>
                ))}
                <div className="mt-1 text-[9px]">Select safety items to record their F-signature evidence in Git.</div>
              </div>
            )}

            {hardwareDiffers && hardware && (
              <div className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-2.5 text-[10px] text-amber-600">
                <div className="font-medium">Project hardware differs from TIA</div>
                <div className="mt-1 text-[9px]">{hardware.message}</div>
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

            {differences.length > 0 ? (
              <>
                {differences.map(diff => {
                  const path = diff.relativePath
                  const disabled = !diff.supported || diff.kind === 'Deleted' || !path
                  const changedComponents = Object.entries(diff.fingerprintComponents ?? {})
                    .filter(([, component]) => component.matches === false)
                  const changedComponentTitle = changedComponents
                    .map(([name, component]) => `${name}\nBaseline: ${component.stored ?? 'Unavailable'}\nCurrent: ${component.live ?? 'Unavailable'}`)
                    .join('\n\n')
                  const tagHashes = diff.evidenceKind === 'tag-table'
                    && diff.masterFingerprint != null
                    && diff.tiaFingerprint != null
                    ? { master: diff.masterFingerprint, tia: diff.tiaFingerprint }
                    : null
                  const fingerprintDetailsUnavailable = diff.kind === 'Changed'
                    && diff.evidenceKind !== 'tag-table'
                    && changedComponents.length === 0
                    && (!diff.fingerprintComponents || Object.values(diff.fingerprintComponents).some(component => component.matches == null))
                  const tagHashUnavailable = diff.kind === 'Changed'
                    && diff.evidenceKind === 'tag-table'
                    && !tagHashes
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
                        {changedComponents.length > 0 && (
                          <span
                            className="mt-1 inline-flex rounded border border-amber-500/40 px-1.5 py-0.5 text-[8px] text-amber-500"
                            title={changedComponentTitle}
                          >
                            Changed: {changedComponents.map(([name]) => name).join(', ')}
                          </span>
                        )}
                        {tagHashes && (
                          <span
                            className="mt-1 inline-flex rounded border border-amber-500/40 px-1.5 py-0.5 font-mono text-[8px] text-amber-500"
                            title={`Baseline: ${tagHashes.master}\nCurrent: ${tagHashes.tia}`}
                          >
                            Content hash: {shortFingerprint(tagHashes.master)} → {shortFingerprint(tagHashes.tia)}
                          </span>
                        )}
                        {fingerprintDetailsUnavailable && <span className="mt-1 block text-[8px] text-muted-foreground">Detailed fingerprint evidence unavailable</span>}
                        {tagHashUnavailable && <span className="mt-1 block text-[8px] text-muted-foreground">Content hash evidence unavailable</span>}
                      </span>
                      {!diff.supported && <ShieldAlert className="h-3 w-3 text-amber-500" />}
                    </label>
                  )
                })}
                {selected.size > 0 && titleMissing && (
                  <div className="text-[9px] text-muted-foreground">Type a commit message above to commit the selected TIA sources to the active worktree.</div>
                )}
              </>
            ) : taskMode ? (
              // A task-scoped result is in sync only when every staged object was compared. With a
              // stage problem on screen the problems are the verdict, so no clean claim is made.
              taskClean && (
                <div className="px-3.5 py-5 text-center text-[10px] text-muted-foreground" data-testid="vc-task-clean-state">
                  <div className="font-medium text-emerald-600">This task is in sync</div>
                  <div className="mt-1 text-[9px]">Every staged source object matches the baseline recorded in the task.</div>
                  <div className="mt-1 text-[9px]">Only this task’s staged objects were compared; nothing else was checked.</div>
                </div>
              )
            ) : (
              <div className="px-3.5 py-5 text-center text-[10px] text-muted-foreground" data-testid="vc-clean-state">
                <div className={`font-medium ${hardwareDiffers || safetyChanges.length > 0 ? 'text-muted-foreground' : 'text-emerald-600'}`}>
                  {!hardwareChecked ? 'Managed source and safety match master' : hardwareDiffers || safetyChanges.length > 0 ? 'Tracked PLC source matches master' : 'TIA matches master'}
                </div>
                <div className="mt-1 text-[9px]">
                  {!hardwareChecked
                    ? 'Hardware configuration was not checked.'
                    : fastGatePassed ? 'All device checksums match; no full object scan was required.' : 'A full object scan found no remaining differences.'}
                </div>
                <div className="mt-1 text-[9px]">
                  {safetyChanges.length > 0 ? 'Select Safety change items to commit their F-signature evidence.' : 'Some TIA changes leave no git diff — tick “Untrackable change” above to record a message-only commit.'}
                </div>
              </div>
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
