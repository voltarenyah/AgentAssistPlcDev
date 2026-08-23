import { useCallback, useEffect, useMemo, useState } from 'react'
import { ArrowUpRight, FileCheck2, GitBranch, GitCompare, History, Loader2, RefreshCw } from 'lucide-react'
import * as api from '@/api/client'
import VersionControlChanges, { type VersionControlSourceEntry } from './VersionControlChanges'
import VersionControlHistory, { type VcTimelineItem } from './VersionControlHistory'

export type VersionControlPanelProps = {
  workbenchId: string
  worktreeId: string
  /** Starts a title-bar operation and returns its id, so the full TIA compare shows live export progress. */
  onBeginOperation?: (kind: string, label: string) => string
}

type VersionControlTab = 'changes' | 'history'

const panelTabs: Array<{ id: VersionControlTab; label: string; icon: typeof FileCheck2 }> = [
  { id: 'changes', label: 'Changes', icon: FileCheck2 },
  { id: 'history', label: 'History', icon: History },
]

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

export default function VersionControlPanel({ workbenchId, worktreeId, onBeginOperation }: VersionControlPanelProps) {
  const [status, setStatus] = useState<api.VcStatusResult | null>(null)
  const [log, setLog] = useState<api.VcCommitEntry[]>([])
  const [timeline, setTimeline] = useState<api.VersionControlTimelineResult | null>(null)
  const [savepoints, setSavepoints] = useState<api.SavepointInfo[]>([])
  const [tab, setTab] = useState<VersionControlTab>('changes')
  const [compareSignal, setCompareSignal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
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
  }, [workbenchId, worktreeId])

  useEffect(() => { void refresh() }, [refresh])
  useEffect(() => { setCompareSignal(0) }, [workbenchId, worktreeId])

  const branch = status?.branch ?? ''
  const isMaster = branch.toLowerCase() === 'master'

  const entries = useMemo(() => (status?.entries ?? []).map(entry => sourceEntry(entry, branch)).filter((entry): entry is VersionControlSourceEntry => entry !== null), [status, branch])

  // Merged timeline for the history page: git commits (joined with the
  // validation log by sha) plus SVN savepoints, newest first.
  const timelineItems = useMemo<VcTimelineItem[]>(() => {
    const validationBySha = new Map(log.map(commit => [commit.sha, commit.validationState]))
    const commits: VcTimelineItem[] = (timeline?.gitCommits ?? []).map(commit => ({
      kind: 'commit',
      sha: commit.sha,
      message: commit.message,
      author: commit.author,
      timestamp: commit.timestamp,
      files: commit.files,
      tiaChecksum: commit.tiaChecksum,
      svnRevision: commit.svnRevision,
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
    }))
    return [...commits, ...revisions].sort((left, right) => right.timestamp.localeCompare(left.timestamp))
  }, [timeline, log])

  // Snapshot area data: newest savepoint, drift since it, hardware change flag.
  const lastSavepoint = savepoints[0] ?? null
  const commitsSinceSavepoint = useMemo(() => {
    if (!lastSavepoint) return null
    const index = log.findIndex(commit => commit.sha === lastSavepoint.sha)
    return index >= 0 ? index : log.length
  }, [lastSavepoint, log])
  const hardwareDiffers = useMemo(() => entries.some(entry => entry.category === 'Hardware'), [entries])


  return (
    <section className="flex h-full min-h-0 w-full flex-col bg-card" aria-label="Version control workspace">
      <nav className="flex shrink-0 items-center justify-center gap-1 border-b px-2 pt-1" style={{ borderColor: 'var(--border)' }} aria-label="Version control sections">
        {panelTabs.map(panelTab => {
          const Icon = panelTab.icon
          const active = tab === panelTab.id
          return (
            <button
              key={panelTab.id}
              type="button"
              title={panelTab.label}
              aria-label={panelTab.label}
              aria-pressed={active}
              data-testid={`vc-tab-${panelTab.id}`}
              onClick={() => setTab(panelTab.id)}
              className={`flex h-8 w-11 items-center justify-center rounded-t-md border-b-2 transition-colors ${active ? 'border-foreground text-foreground' : 'border-transparent text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`}
            >
              <Icon className="h-4 w-4" />
            </button>
          )
        })}
      </nav>

      <div className="flex shrink-0 items-center gap-1.5 px-3.5 pb-1.5 pt-2.5">
        <button
          type="button"
          data-testid="vc-compare-open"
          className="flex h-[30px] items-center gap-1.5 rounded-lg bg-primary px-3.5 text-[12px] font-semibold text-primary-foreground hover:opacity-90"
          onClick={() => { setTab('changes'); setCompareSignal(signal => signal + 1) }}
        >
          <GitCompare className="h-3.5 w-3.5" /> Compare with TIA
        </button>
        <div className="flex-1" />
        <button type="button" className="icon-button" title="Refresh version control" aria-label="Refresh version control" onClick={() => void refresh()} disabled={loading}>
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
        </button>
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
      {error && <div className="shrink-0 px-3.5 py-2 text-[10px] text-destructive">{error}</div>}

      {/* Both pages stay mounted (hidden when inactive) so tab switches do not
          remount them — a remount would re-run the TIA comparison and drop
          local UI state like the compare result or expanded timeline items. */}
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        <div className={tab === 'changes' ? 'flex min-h-0 flex-1 flex-col overflow-hidden' : 'hidden'}>
          <VersionControlChanges
            workbenchId={workbenchId}
            worktreeId={worktreeId}
            branch={branch}
            entries={entries}
            compareSignal={compareSignal}
            snapshot={{
              revision: lastSavepoint?.svnRevision ?? null,
              commitsSince: commitsSinceSavepoint,
              hardwareDiffers,
            }}
            onCommitted={() => void refresh()}
            onBeginOperation={onBeginOperation}
          />
        </div>
        <div className={tab === 'history' ? 'flex min-h-0 flex-1 flex-col overflow-hidden' : 'hidden'}>
          <VersionControlHistory
            workbenchId={workbenchId}
            worktreeId={worktreeId}
            branch={branch}
            items={timelineItems}
            loading={loading && timeline === null}
          />
        </div>
      </div>
    </section>
  )
}
