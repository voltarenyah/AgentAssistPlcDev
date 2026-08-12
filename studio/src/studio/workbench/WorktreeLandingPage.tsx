import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, Cpu, FileCode2, GitBranch, LayoutDashboard, ListTodo, Loader2 } from 'lucide-react'
import * as api from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import InlineEdit from './InlineEdit'
import StatusBadge from './StatusBadge'
import WorktreeTasksPanel from './WorktreeTasksPanel'
import WorktreeVersionControlTimeline from './WorktreeVersionControlTimeline'
import { rememberDeviceSnapshot } from '@/studio/deviceSnapshot'

export type WorktreeLandingTab = 'overview' | 'tasks'

type Props = {
  workbenchId: string
  worktreeId: string
  tab: WorktreeLandingTab
  onTabChange: (tab: WorktreeLandingTab) => void
  onSelectDevice: (deviceId: string) => void
}

type ModifiedDevice = {
  deviceId: string
  plcName: string
  blocks: string[]
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

const formatDate = (value: string | null) =>
  value ? new Date(value).toLocaleString() : '—'

const worktreeTabs: Array<{ id: WorktreeLandingTab; label: string; icon: typeof GitBranch }> = [
  { id: 'overview', label: 'Overview', icon: LayoutDashboard },
  { id: 'tasks', label: 'Tasks', icon: ListTodo },
]

export default function WorktreeLandingPage({ workbenchId, worktreeId, tab, onTabChange, onSelectDevice }: Props) {
  const [detail, setDetail] = useState<api.WorktreeDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(true)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [tasks, setTasks] = useState<api.WorktreeTask[]>([])
  const [tasksLoading, setTasksLoading] = useState(true)
  const [tasksError, setTasksError] = useState<string | null>(null)
  const [modifiedDevices, setModifiedDevices] = useState<ModifiedDevice[] | null>(null)

  useEffect(() => {
    let cancelled = false
    setDetailLoading(true)
    setDetailError(null)
    void api.getWorktreeDetail(workbenchId, worktreeId)
      .then(result => {
        if (cancelled) return
        setDetail(result)
        setDetailLoading(false)
      })
      .catch(loadError => {
        if (cancelled) return
        setDetail(null)
        setDetailError(displayError(loadError))
        setDetailLoading(false)
      })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId])

  const reloadTasks = useCallback(async () => {
    try {
      const result = await api.listWorktreeTasks(workbenchId, worktreeId)
      setTasks(result.tasks)
      setTasksError(null)
    } catch (loadError) {
      setTasksError(displayError(loadError))
    } finally {
      setTasksLoading(false)
    }
  }, [workbenchId, worktreeId])

  useEffect(() => {
    let cancelled = false
    setTasksLoading(true)
    setTasksError(null)
    void api.listWorktreeTasks(workbenchId, worktreeId)
      .then(result => {
        if (cancelled) return
        setTasks(result.tasks)
        setTasksLoading(false)
      })
      .catch(loadError => {
        if (cancelled) return
        setTasksError(displayError(loadError))
        setTasksLoading(false)
      })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId])

  useEffect(() => {
    if (tab !== 'overview') return
    let cancelled = false
    setModifiedDevices(null)
    void api.listDevices(workbenchId, worktreeId)
      .then(devices => Promise.all(devices.map(async device => {
        try {
          const snapshot = await api.getDeviceInfo(workbenchId, worktreeId, device.deviceId)
          rememberDeviceSnapshot(snapshot)
          return {
            deviceId: device.deviceId,
            plcName: snapshot.plcName || device.plcName,
            blocks: snapshot.blocks.filter(block => block.modified).map(block => block.name),
          }
        } catch {
          return { deviceId: device.deviceId, plcName: device.plcName, blocks: [] }
        }
      })))
      .then(rows => {
        if (cancelled) return
        setModifiedDevices(rows.filter(row => row.blocks.length > 0))
      })
      .catch(() => {
        if (cancelled) return
        setModifiedDevices([])
      })
    return () => { cancelled = true }
  }, [workbenchId, worktreeId, tab])

  const taskCounts = useMemo(() => ({
    todo: tasks.filter(task => task.status === 'todo').length,
    inProgress: tasks.filter(task => task.status === 'inProgress').length,
    done: tasks.filter(task => task.status === 'done').length,
  }), [tasks])

  const saveDetailField = async (patch: { purpose?: string; owner?: string }) => {
    try {
      const updated = await api.updateWorktree(workbenchId, worktreeId, patch)
      setDetail(updated)
    } catch (saveError) {
      showErrorToast(`Worktree metadata could not be saved: ${displayError(saveError)}`)
    }
  }

  const changeStatus = async (status: api.WorktreeStatus) => {
    const updated = await api.updateWorktree(workbenchId, worktreeId, { status })
    setDetail(updated)
  }

  if (detailLoading && !detail) {
    return (
      <div className="grid h-full min-h-[520px] place-items-center p-8">
        <div className="flex items-center gap-2 text-[10px] text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading worktree...
        </div>
      </div>
    )
  }

  if (detailError && !detail) {
    return (
      <div className="grid h-full min-h-[520px] place-items-center p-8">
        <div className="max-w-md text-center">
          <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-2xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
            <AlertCircle className="h-7 w-7 text-red-500" />
          </div>
          <h2 className="text-base font-semibold">Worktree unavailable</h2>
          <p className="mt-2 text-[10px] leading-relaxed text-muted-foreground">{detailError}</p>
        </div>
      </div>
    )
  }

  if (!detail) return null

  return (
    <div className="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
      <div className="flex h-10 shrink-0 items-center gap-1 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        {worktreeTabs.map(worktreeTab => {
          const Icon = worktreeTab.icon
          return (
            <button
              key={worktreeTab.id}
              onClick={() => onTabChange(worktreeTab.id)}
              className={`flex h-7 items-center gap-1.5 rounded-md px-2.5 text-[9px] transition-colors ${tab === worktreeTab.id ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`}
            >
              <Icon className="h-3 w-3" /> {worktreeTab.label}
            </button>
          )
        })}
        <div className="flex-1" />
      </div>

      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto max-w-6xl space-y-5 p-5">
          <section
            data-testid="worktree-context"
            className={`rounded-xl border bg-card p-5 ${tab === 'overview' ? 'grid gap-5 sm:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)]' : ''}`}
            style={{ borderColor: 'var(--border)' }}
          >
            <div className="min-w-0">
              <div className="flex items-start gap-4">
                <div className="grid h-12 w-12 shrink-0 place-items-center rounded-xl bg-chart-4/10">
                  <GitBranch className="h-5 w-5 text-chart-4" />
                </div>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <h1 className="text-lg font-semibold">{detail.name}</h1>
                    <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[9px] text-muted-foreground">{detail.branch}</span>
                    <StatusBadge status={detail.status} onChange={changeStatus} />
                  </div>
                  <p className="mt-1 text-[9px] text-muted-foreground">
                    Created {formatDate(detail.createdAt)}
                    {detail.status === 'finished' && detail.finishedUtc ? ` · Finished ${formatDate(detail.finishedUtc)}` : ''}
                  </p>
                </div>
              </div>
              <div className="mt-4 grid grid-cols-[64px_1fr] items-start gap-x-3 gap-y-2">
                <span className="pt-1.5 text-[9px] uppercase tracking-wide text-muted-foreground">Purpose</span>
                <InlineEdit
                  ariaLabel="Worktree purpose"
                  placeholder="What is this worktree for?"
                  multiline
                  value={detail.purpose ?? ''}
                  onSave={purpose => saveDetailField({ purpose })}
                />
                <span className="self-center text-[9px] uppercase tracking-wide text-muted-foreground">Owner</span>
                <InlineEdit
                  ariaLabel="Worktree owner"
                  placeholder="Responsible person"
                  value={detail.owner ?? ''}
                  onSave={owner => saveDetailField({ owner })}
                />
              </div>
            </div>

            {tab === 'overview' && (
              <div data-testid="worktree-metadata" className="border-t pt-4 sm:border-l sm:border-t-0 sm:pl-5 sm:pt-0">
                <h2 className="text-sm font-semibold">Worktree metadata</h2>
                <dl className="mt-3 grid grid-cols-1 gap-x-6 gap-y-2 text-[10px] sm:grid-cols-2">
                  <div className="flex justify-between gap-3 border-b py-1" style={{ borderColor: 'var(--border)' }}>
                    <dt className="text-muted-foreground">Branch</dt>
                    <dd className="font-mono">{detail.branch}</dd>
                  </div>
                  <div className="flex justify-between gap-3 border-b py-1" style={{ borderColor: 'var(--border)' }}>
                    <dt className="text-muted-foreground">Created</dt>
                    <dd>{formatDate(detail.createdAt)}</dd>
                  </div>
                  <div className="flex justify-between gap-3 border-b py-1" style={{ borderColor: 'var(--border)' }}>
                    <dt className="text-muted-foreground">Base commit</dt>
                    <dd className="font-mono">{detail.baseCommit ?? '—'}</dd>
                  </div>
                  <div className="flex justify-between gap-3 border-b py-1" style={{ borderColor: 'var(--border)' }}>
                    <dt className="text-muted-foreground">Last reconciliation</dt>
                    <dd className="font-mono">{detail.lastReconciliationCommit ?? '—'}</dd>
                  </div>
                  <div className="flex justify-between gap-3 border-b py-1" style={{ borderColor: 'var(--border)' }}>
                    <dt className="text-muted-foreground">Source project</dt>
                    <dd className="truncate font-mono" title={detail.sourceProjectPath ?? undefined}>{detail.sourceProjectPath ?? '—'}</dd>
                  </div>
                  <div className="flex justify-between gap-3 border-b py-1" style={{ borderColor: 'var(--border)' }}>
                    <dt className="text-muted-foreground">PLC devices</dt>
                    <dd>{detail.deviceIds.length}</dd>
                  </div>
                </dl>
              </div>
            )}
          </section>

          {tab === 'overview' && (
            <>
              <WorktreeVersionControlTimeline workbenchId={workbenchId} worktreeId={worktreeId} />

              <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
                <div className="flex items-center gap-3">
                  <ListTodo className="h-4 w-4 text-chart-2" />
                  <h2 className="text-sm font-semibold">Tasks</h2>
                  <button className="secondary-button ml-auto h-7 text-[9px]" onClick={() => onTabChange('tasks')}>
                    Open task list
                  </button>
                </div>
                <div className="mt-3 flex flex-wrap gap-2">
                  {([
                    ['Todo', taskCounts.todo],
                    ['In Progress', taskCounts.inProgress],
                    ['Done', taskCounts.done],
                  ] as const).map(([label, count]) => (
                    <button
                      key={label}
                      onClick={() => onTabChange('tasks')}
                      className="flex items-center gap-2 rounded-lg border px-3 py-2 text-left hover:bg-accent/40"
                      style={{ borderColor: 'var(--border)' }}
                    >
                      <span className="text-sm font-semibold">{count}</span>
                      <span className="text-[9px] uppercase tracking-wide text-muted-foreground">{label}</span>
                    </button>
                  ))}
                </div>
              </section>

              <section className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
                <div className="flex items-center border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
                  <span className="text-[10px] font-semibold">Modified blocks</span>
                  <span className="ml-auto text-[9px] text-muted-foreground">
                    {modifiedDevices === null ? 'Loading…' : `${modifiedDevices.length} device${modifiedDevices.length === 1 ? '' : 's'} with overlay changes`}
                  </span>
                </div>
                {modifiedDevices === null ? (
                  <div className="flex items-center justify-center gap-2 p-6 text-[10px] text-muted-foreground">
                    <Loader2 className="h-3.5 w-3.5 animate-spin" /> Inspecting device overlays...
                  </div>
                ) : modifiedDevices.length === 0 ? (
                  <div className="p-6 text-center text-[10px] text-muted-foreground">
                    No overlay-modified blocks in this worktree.
                  </div>
                ) : (
                  <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
                    {modifiedDevices.map(device => (
                      <button
                        key={device.deviceId}
                        className="flex w-full items-start gap-3 px-4 py-2.5 text-left hover:bg-accent/40"
                        onClick={() => onSelectDevice(device.deviceId)}
                      >
                        <Cpu className="mt-0.5 h-3.5 w-3.5 shrink-0 text-chart-2" />
                        <span className="min-w-0 flex-1">
                          <span className="text-[10px] font-medium">
                            {device.plcName} — {device.blocks.length} modified block{device.blocks.length === 1 ? '' : 's'}
                          </span>
                          <span className="mt-1 flex flex-wrap gap-1">
                            {device.blocks.map(block => (
                              <span key={block} className="inline-flex items-center gap-1 rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">
                                <FileCode2 className="h-2.5 w-2.5" /> {block}
                              </span>
                            ))}
                          </span>
                        </span>
                      </button>
                    ))}
                  </div>
                )}
              </section>
            </>
          )}

          {tab === 'tasks' && (
            <WorktreeTasksPanel
              workbenchId={workbenchId}
              worktreeId={worktreeId}
              tasks={tasks}
              loading={tasksLoading}
              error={tasksError}
              onChanged={() => void reloadTasks()}
            />
          )}
        </div>
      </div>
    </div>
  )
}
