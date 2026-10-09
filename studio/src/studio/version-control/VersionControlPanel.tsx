import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ArrowUpRight, GitBranch, GitCompare } from 'lucide-react'
import * as api from '@/api/client'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { showErrorToast } from '@/components/ui/toast'
import VersionControlChanges, { type VersionControlSourceEntry } from './VersionControlChanges'
import VersionControlHistory, { type VcTimelineItem } from './VersionControlHistory'

export type VersionControlPanelProps = {
  workbenchId: string
  worktreeId: string
  /** Which half of the version-control surface this instance renders; the shell's rail owns the switch. */
  section: 'changes' | 'history'
  /** Bumped by the page header's refresh control, which replaces the panel's own refresh button. */
  refreshSignal?: number
  /** Reports the uncommitted source objects on screen, so the rail can badge the Changes page. */
  onUncommittedCountChange?: (count: number) => void
  /** Starts a title-bar operation and returns its id, so the full TIA compare shows live export progress. */
  onBeginOperation?: (kind: string, label: string) => string
  operationStatus?: api.OperationStatus | null
  onNavigateTask?: (taskId: string) => void
  onNavigateEntity?: (kind: string, id: string) => void
  selectedTraceabilityTarget?: { kind: string; id: string } | null
}

type CompareMode = 'full' | 'task'

function sourceEntry(entry: api.VcStatusEntry, branch: string): VersionControlSourceEntry | null {
  const parts = entry.filePath.replace(/\\/g, '/').split('/')
  const state = branch.toLowerCase() === 'master' ? 'Unauthorized' : entry.state === 'Added' || entry.state === 'Untracked' ? 'Added' : entry.state === 'Deleted' ? 'Deleted' : 'Modified'
  if (parts[0] === 'hardware' && parts.length >= 2 && parts[1] !== 'staging') {
    const file = parts.at(-1) ?? entry.filePath
    return {
      filePath: entry.filePath,
      deviceId: 'project',
      plcName: 'Hardware',
      category: 'Hardware',
      objectName: file,
      state,
      authorizedOnMaster: true,
    }
  }
  if (parts.length < 5 || parts[0] !== 'devices' || parts[2] !== 'source' || !entry.filePath.toLowerCase().endsWith('.xml')) return null
  const category = parts[3] === 'Blocks' ? 'Block' : parts[3] === 'DB' ? 'DB' : parts[3] === 'UDT' ? 'Udt' : parts[3] === 'Tags' ? 'Tags' : parts[3]
  const file = parts.at(-1) ?? entry.filePath
  return {
    filePath: entry.filePath,
    deviceId: parts[1],
    plcName: parts[1],
    category,
    objectName: file.replace(/\.xml$/i, ''),
    state,
    authorizedOnMaster: branch.toLowerCase() !== 'master',
  }
}

export default function VersionControlPanel({ workbenchId, worktreeId, section, refreshSignal = 0, onUncommittedCountChange, onBeginOperation, operationStatus = null }: VersionControlPanelProps) {
  const [status, setStatus] = useState<api.VcStatusResult | null>(null)
  const [log, setLog] = useState<api.VcCommitEntry[]>([])
  const [timeline, setTimeline] = useState<api.VersionControlTimelineResult | null>(null)
  const [savepoints, setSavepoints] = useState<api.SavepointInfo[]>([])
  const [compareSignal, setCompareSignal] = useState(0)
  // Task-only is the default scope; the readiness read below decides whether this worktree can
  // actually scope a comparison to its task.
  const [compareMode, setCompareMode] = useState<CompareMode>('task')
  const [activeTask, setActiveTask] = useState<api.EngineeringTask | null>(null)
  const [activeTaskUnreadable, setActiveTaskUnreadable] = useState(false)
  const [taskStageCount, setTaskStageCount] = useState<number | null>(null)
  const [taskStagesUnreadable, setTaskStagesUnreadable] = useState(false)
  const [taskScopeChecked, setTaskScopeChecked] = useState(false)
  /** Device-bound worktree tasks: the only tasks that can own a source stage, and so take a commit. */
  const [commitTasks, setCommitTasks] = useState<api.EngineeringTask[]>([])
  const [switchingTask, setSwitchingTask] = useState(false)
  const [taskCheckSignal, setTaskCheckSignal] = useState(0)
  const [verifyHardware, setVerifyHardware] = useState(true)
  // A scope the user picked by hand is never overridden by the default.
  const modeChosen = useRef(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      if (section === 'history') {
        const [nextLog, nextTimeline, nextSavepoints] = await Promise.all([
          api.getWorktreeVcLog(workbenchId, worktreeId, 50),
          api.getWorktreeVersionControlTimeline(workbenchId, worktreeId, 0, 50),
          api.getWorktreeSavepoints(workbenchId, worktreeId).catch(() => [] as api.SavepointInfo[]),
        ])
        setLog(nextLog.commits)
        setTimeline(nextTimeline)
        setSavepoints(nextSavepoints)
        return
      }
      // The changes half reads the timeline as well: the untrackable-pending warning is decided by
      // the untrackable-change markers only the timeline carries, not by the commit log.
      const [nextStatus, nextLog, nextTimeline, nextSavepoints] = await Promise.all([
        api.getWorktreeVcStatus(workbenchId, worktreeId),
        api.getWorktreeVcLog(workbenchId, worktreeId, 50),
        api.getWorktreeVersionControlTimeline(workbenchId, worktreeId, 0, 50),
        api.getWorktreeSavepoints(workbenchId, worktreeId).catch(() => [] as api.SavepointInfo[]),
      ])
      setStatus(nextStatus)
      setLog(nextLog.commits)
      setTimeline(nextTimeline)
      setSavepoints(nextSavepoints)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Failed to load version control state')
    } finally {
      setLoading(false)
    }
  }, [workbenchId, worktreeId, section])

  useEffect(() => { void refresh() }, [refresh])

  // The page header owns refresh now, so a signal change is what used to be the panel's own button:
  // reload the section's data and re-read the task scope that only the changes half uses.
  const lastRefreshSignal = useRef(refreshSignal)
  useEffect(() => {
    if (refreshSignal === lastRefreshSignal.current) return
    lastRefreshSignal.current = refreshSignal
    void refresh()
    if (section === 'changes') setTaskCheckSignal(signal => signal + 1)
  }, [refreshSignal, refresh, section])

  useEffect(() => {
    if (section === 'history') return
    setCompareSignal(0)
    modeChosen.current = false
    setCompareMode('task')
    setTaskScopeChecked(false)
    setVerifyHardware(true)
  }, [workbenchId, worktreeId, section])

  // The active task — the task selected for this worktree, which MainStudio records through
  // setActiveWorktreeTask — supplies the staged source objects a task-only comparison reads. It is
  // read in either scope because it also decides the default scope. The backend rejects an empty
  // stage list with TASK_STAGE_EMPTY, so the state explains the scope instead of firing a comparison
  // that cannot answer anything. The history half compares nothing, so it reads none of this.
  useEffect(() => {
    if (section === 'history') return
    let cancelled = false
    setActiveTask(null)
    setActiveTaskUnreadable(false)
    setTaskStageCount(null)
    setTaskStagesUnreadable(false)
    setTaskScopeChecked(false)
    void (async () => {
      let task: api.EngineeringTask | null
      try {
        task = (await api.getActiveWorktreeTask(workbenchId, worktreeId)).activeTask
      } catch {
        if (!cancelled) { setActiveTaskUnreadable(true); setTaskScopeChecked(true) }
        return
      }
      if (cancelled) return
      setActiveTask(task)
      // The commit-target picker lists the same worktree's device-bound tasks. A failure here only
      // means there is nothing to choose from, never that the scope is unusable.
      void api.listGraphWorktreeTasks(workbenchId, worktreeId)
        .then(items => {
          if (!cancelled) {
            setCommitTasks(items.filter(item =>
              item.scope === 'worktree' && item.worktreeId === worktreeId && Boolean(item.deviceId)))
          }
        })
        .catch(() => { if (!cancelled) setCommitTasks([]) })
      // A project-scope or hardware task cannot own stages, so it never reaches the stage read.
      if (!task || task.scope !== 'worktree' || task.worktreeId !== worktreeId || !task.deviceId) {
        setTaskScopeChecked(true)
        return
      }
      try {
        const stages = await api.listTaskSourceStages(workbenchId, worktreeId, task.taskId)
        if (!cancelled) { setTaskStageCount(stages.length); setTaskScopeChecked(true) }
      } catch {
        if (!cancelled) { setTaskStagesUnreadable(true); setTaskScopeChecked(true) }
      }
    })()
    return () => { cancelled = true }
  }, [workbenchId, worktreeId, taskCheckSignal, section])

  const taskCompareReason = compareMode !== 'task'
    ? null
    : activeTaskUnreadable
      ? 'The worktree’s active task could not be read.'
      : !activeTask
        ? 'Select a task for this worktree to compare only its staged source objects.'
        : activeTask.scope !== 'worktree' || activeTask.worktreeId !== worktreeId
          ? 'The active task is not a task of this worktree.'
          : !activeTask.deviceId
            ? 'The active task is not bound to a PLC device, so it has no staged source objects to compare.'
            : taskStagesUnreadable
              ? 'This task’s staged source objects could not be read.'
              : taskStageCount === null
                ? 'Checking this task’s staged source objects...'
                : taskStageCount === 0
                  ? 'This task has no staged source objects yet. Add source objects to the task first.'
                  : null
  const taskCompareReady = taskCompareReason === null
  const activeTaskId = activeTask?.taskId ?? null

  /**
   * The commit's task *is* the worktree's active task: attribution, the Task only compare scope, and
   * the agent's task context all follow it. So choosing a different target means making it active,
   * and the panel re-reads the task context afterwards.
   */
  const switchCommitTask = async (nextTaskId: string) => {
    if (!nextTaskId || nextTaskId === activeTaskId) return
    setSwitchingTask(true)
    try {
      await api.setActiveWorktreeTask(workbenchId, worktreeId, nextTaskId)
      setTaskCheckSignal(signal => signal + 1)
    } catch (reason) {
      showErrorToast(reason instanceof Error ? reason.message : 'Failed to switch the worktree task')
    } finally {
      setSwitchingTask(false)
    }
  }

  // Task-only is the default scope: a task's staged objects are the quick, in-scope comparison. A
  // worktree whose task cannot scope one falls back to the project-wide scan, and a scope the user
  // picked by hand is left alone so the reason it is unavailable stays readable.
  useEffect(() => {
    if (!taskScopeChecked || modeChosen.current || compareMode !== 'task' || taskCompareReason === null) return
    setCompareMode('full')
  }, [taskScopeChecked, compareMode, taskCompareReason])

  const branch = status?.branch ?? ''
  const isMaster = branch.toLowerCase() === 'master'

  const entries = useMemo(() => (status?.entries ?? []).map(entry => sourceEntry(entry, branch)).filter((entry): entry is VersionControlSourceEntry => entry !== null), [status, branch])

  // The rail badges the Changes item, so only the half that shows uncommitted objects reports —
  // a history instance reporting its own empty count would clear that badge.
  const uncommittedCount = section === 'changes' && worktreeId ? entries.length : 0
  useEffect(() => {
    if (section !== 'changes') return
    onUncommittedCountChange?.(uncommittedCount)
  }, [onUncommittedCountChange, uncommittedCount, section])

  // Merged timeline for the history page: git commits (joined with the
  // validation log by sha) plus SVN savepoints, newest first.
  const timelineItems = useMemo<VcTimelineItem[]>(() => {
    const validationBySha = new Map(log.map(commit => [commit.sha, commit.validationState]))
    const savepointBySha = new Map(savepoints.map(savepoint => [savepoint.sha, savepoint]))
    const commits: VcTimelineItem[] = (timeline?.gitCommits ?? []).map(commit => ({
      kind: 'commit',
      sha: commit.sha,
      message: commit.message,
      author: commit.author,
      timestamp: commit.timestamp,
      files: commit.files,
      tiaChecksum: commit.tiaChecksum ?? null,
      svnRevision: commit.svnRevision ?? savepointBySha.get(commit.sha)?.svnRevision ?? null,
      untrackableChange: commit.untrackableChange ?? false,
      safetyChange: commit.safetyChange ?? false,
      validationState: validationBySha.get(commit.sha) ?? 'Unlabeled',
    }))
    const revisions: VcTimelineItem[] = (timeline?.svnRevisions ?? []).map(revision => ({
      kind: 'savepoint',
      revision: revision.revision,
      message: revision.message,
      author: revision.author,
      timestamp: revision.timestamp,
      tiaChecksum: revision.tiaChecksum,
      gitCommitSha: revision.gitCommitSha,
      safetyChanged: savepointBySha.get(revision.gitCommitSha)?.safetyChanged ?? false,
      safetyReadState: savepointBySha.get(revision.gitCommitSha)?.safetyReadState ?? null,
    }))
    return [...commits, ...revisions].sort((left, right) => right.timestamp.localeCompare(left.timestamp))
  }, [timeline, log, savepoints])

  // Ordinary Git commits inherit revision.json, so the newest savepoint entry
  // is not necessarily a new native snapshot. Use the commit where the
  // current SVN revision first appeared as the snapshot boundary.
  const lastSavepoint = useMemo(() => {
    const currentRevision = savepoints[0]?.svnRevision
    if (currentRevision === null || currentRevision === undefined) return null
    return savepoints.find((savepoint, index) => (
      savepoint.svnRevision === currentRevision
      && (index === savepoints.length - 1 || savepoints[index + 1]?.svnRevision !== currentRevision)
    )) ?? null
  }, [savepoints])
  const commitsSinceSavepoint = useMemo(() => {
    if (!lastSavepoint) return null
    const index = log.findIndex(commit => commit.sha === lastSavepoint.sha)
    return index >= 0 ? index : log.length
  }, [lastSavepoint, log])
  // An untrackable commit is only covered once a savepoint newer than it
  // exists; log is newest-first, so anything before the boundary is uncovered.
  const untrackablePendingSavepoint = useMemo(() => {
    const untrackableShas = (timeline?.gitCommits ?? []).filter(commit => commit.untrackableChange === true).map(commit => commit.sha)
    if (untrackableShas.length === 0) return false
    if (commitsSinceSavepoint === null) return true
    return untrackableShas.some(sha => {
      const index = log.findIndex(commit => commit.sha === sha)
      return index >= 0 && index < commitsSinceSavepoint
    })
  }, [timeline, commitsSinceSavepoint, log])
  const hardwareDiffers = useMemo(() => entries.some(entry => entry.category === 'Hardware'), [entries])

  return (
    <div className="flex h-full min-h-0 w-full flex-col">
      {section === 'changes' && (
        <>
          <div className="flex shrink-0 flex-wrap items-center gap-1.5 px-3.5 pb-1.5 pt-2.5">
            <button
              type="button"
              data-testid="vc-compare-open"
              className="flex h-[30px] items-center gap-1.5 rounded-lg bg-primary px-3.5 text-[12px] font-semibold text-primary-foreground hover:opacity-90 disabled:opacity-40"
              disabled={compareMode === 'task' && !taskCompareReady}
              onClick={() => setCompareSignal(signal => signal + 1)}
            >
              <GitCompare className="h-3.5 w-3.5" /> Compare
            </button>
            <ToggleGroup
              type="single"
              value={compareMode}
              variant="outline"
              size="sm"
              className="ml-auto"
              aria-label="Compare scope"
              data-testid="vc-compare-mode"
              onValueChange={value => {
                if (value !== 'full' && value !== 'task') return
                modeChosen.current = true
                setCompareMode(value)
              }}
            >
              <ToggleGroupItem value="full" aria-label="Full scan" data-testid="vc-compare-mode-full" className="px-2 text-[10px]">Full scan</ToggleGroupItem>
              <ToggleGroupItem value="task" aria-label="Task only" data-testid="vc-compare-mode-task" className="px-2 text-[10px]">Task only</ToggleGroupItem>
            </ToggleGroup>
          </div>
          <div className="shrink-0 px-3.5 pb-1.5">
            <label className={`flex items-center gap-1.5 whitespace-nowrap text-[10px] text-muted-foreground ${compareMode === 'task' ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}>
              <input
                type="checkbox"
                data-testid="vc-verify-hardware"
                checked={compareMode === 'task' ? false : verifyHardware}
                disabled={compareMode === 'task'}
                onChange={event => setVerifyHardware(event.target.checked)}
              />
              Verify hardware configuration
              {compareMode === 'task' && <span>· not checked in a task-only compare</span>}
            </label>
            {compareMode === 'task' && taskCompareReason && (
              <div className="mt-1 text-[10px] text-muted-foreground" data-testid="vc-compare-task-unavailable">
                {taskCompareReason}
              </div>
            )}
          </div>

          <div className="shrink-0 border-b px-3.5 pb-2.5 pt-1" style={{ borderColor: 'var(--border)' }}>
            <div className="flex items-baseline gap-2">
              <GitBranch className="h-3 w-3 self-center text-chart-4" />
              <span className="truncate font-mono text-[12px] font-bold" title={branch} data-testid="vc-branch-name">{branch || 'Version control'}</span>
            </div>
            {!isMaster && <div className="mt-0.5 flex items-center gap-1.5 pl-[18px] font-mono text-[11px] font-semibold text-muted-foreground">
              <ArrowUpRight className="h-3 w-3" /> master
            </div>}
          </div>
        </>
      )}
      {error && <div className="shrink-0 px-3.5 py-2 text-[10px] text-destructive">{error}</div>}

      {/* Each rail page mounts its own instance, so this panel renders only the half it was asked
          for; the other half is not kept mounted here. */}
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        {section === 'changes' ? (
          <VersionControlChanges
            workbenchId={workbenchId}
            worktreeId={worktreeId}
            branch={branch}
            entries={entries}
            compareSignal={compareSignal}
            compareMode={compareMode}
            activeTaskId={activeTaskId}
            activeTaskTitle={activeTask?.title ?? null}
            commitTasks={commitTasks}
            commitTaskId={activeTaskId}
            switchingCommitTask={switchingTask}
            onCommitTaskChanged={taskId => void switchCommitTask(taskId)}
            verifyHardware={verifyHardware}
            snapshot={{
              revision: lastSavepoint?.svnRevision ?? null,
              commitsSince: commitsSinceSavepoint,
              hardwareDiffers,
            }}
            untrackablePendingSavepoint={untrackablePendingSavepoint}
            onCommitted={() => void refresh()}
            onBeginOperation={onBeginOperation}
            operationStatus={operationStatus}
          />
        ) : (
          <VersionControlHistory
            workbenchId={workbenchId}
            worktreeId={worktreeId}
            branch={branch}
            items={timelineItems}
            loading={loading && timeline === null}
          />
        )}
      </div>
    </div>
  )
}
