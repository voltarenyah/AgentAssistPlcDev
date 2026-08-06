import { useCallback, useEffect, useMemo, useState } from 'react'
import { GitBranch, Loader2, RefreshCw, ShieldCheck } from 'lucide-react'
import * as api from '@/api/client'
import VersionControlChanges, { type VersionControlSourceEntry } from './VersionControlChanges'
import VersionControlCompare from './VersionControlCompare'
import VersionControlHistory from './VersionControlHistory'
import NativeStorePanel from './NativeStorePanel'

export type VersionControlPanelProps = {
  workbenchId: string
  worktreeId: string
  onSelectionChange?: (selection: unknown) => void
}

function sourceEntry(entry: api.VcStatusEntry, branch: string): VersionControlSourceEntry | null {
  const parts = entry.filePath.replace(/\\/g, '/').split('/')
  if (parts.length < 5 || parts[0] !== 'devices' || parts[2] !== 'source' || !entry.filePath.toLowerCase().endsWith('.xml')) return null
  const category = parts[3] === 'Blocks' ? 'Block' : parts[3] === 'DB' ? 'DB' : parts[3] === 'UDT' ? 'Udt' : parts[3] === 'Tags' ? 'Tags' : parts[3]
  const file = parts.at(-1) ?? entry.filePath
  return {
    filePath: entry.filePath,
    deviceId: parts[1],
    plcName: parts[1],
    category,
    objectName: file.replace(/\.xml$/i, ''),
    state: branch.toLowerCase() === 'master' ? 'Unauthorized' : entry.state === 'Added' || entry.state === 'Untracked' ? 'Added' : entry.state === 'Deleted' ? 'Deleted' : 'Modified',
    authorizedOnMaster: branch.toLowerCase() !== 'master',
  }
}

export default function VersionControlPanel({ workbenchId, worktreeId, onSelectionChange }: VersionControlPanelProps) {
  const [status, setStatus] = useState<api.VcStatusResult | null>(null)
  const [history, setHistory] = useState<api.VcCommitEntry[]>([])
  const [tab, setTab] = useState<'changes' | 'compare' | 'history' | 'native'>('changes')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [nextStatus, nextLog] = await Promise.all([
        api.getWorktreeVcStatus(workbenchId, worktreeId),
        api.getWorktreeVcLog(workbenchId, worktreeId, 50),
      ])
      setStatus(nextStatus)
      setHistory(nextLog.commits)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Failed to load version control state')
    } finally {
      setLoading(false)
    }
  }, [workbenchId, worktreeId])

  useEffect(() => { void refresh() }, [refresh])

  const entries = useMemo(() => (status?.entries ?? []).map(entry => sourceEntry(entry, status?.branch ?? '')).filter((entry): entry is VersionControlSourceEntry => entry !== null), [status])
  const validation = history[0]?.validationState ?? 'Unlabeled'
  const validationLabel = validation === 'Validated' ? 'TIA validated' : validation === 'Invalid' ? 'Validation evidence invalid' : 'Full scan required'

  return (
    <section className="flex h-full min-h-0 w-full flex-col bg-card" aria-label="Version control workspace">
      <header className="flex shrink-0 items-center gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--border)' }}>
        <GitBranch className="h-3.5 w-3.5 text-chart-4" />
        <div className="min-w-0 flex-1">
          <h2 className="truncate text-[11px] font-semibold">{status?.branch ?? 'Version control'}</h2>
          <div className="flex flex-wrap gap-2 text-[8px] text-muted-foreground">
            <span>{status?.branch.toLowerCase() === 'master' ? 'Master' : 'Feature'}</span>
            <span>{entries.length} source change{entries.length === 1 ? '' : 's'}</span>
            <span className="inline-flex items-center gap-1"><ShieldCheck className="h-2.5 w-2.5" />{validationLabel}</span>
          </div>
        </div>
        <button type="button" className="icon-button" title="Refresh version control" onClick={() => void refresh()} disabled={loading}>
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
        </button>
      </header>
      {error && <div className="shrink-0 px-3 py-2 text-[9px] text-destructive">{error}</div>}
      <nav className="flex shrink-0 gap-1 border-b px-2 py-1" style={{ borderColor: 'var(--border)' }}>
        {(['changes', 'compare', 'history', 'native'] as const).map(item => (
          <button key={item} type="button" className={`rounded px-2 py-1 text-[9px] ${tab === item ? 'bg-accent font-medium' : 'text-muted-foreground hover:bg-accent/50'}`} onClick={() => setTab(item)}>
            {item === 'changes' ? 'Changes' : item === 'compare' ? 'Compare with TIA' : item === 'history' ? 'History' : 'Native (SVN)'}
          </button>
        ))}
      </nav>
      <div className="min-h-0 flex-1 overflow-hidden">
        {tab === 'changes' && <VersionControlChanges workbenchId={workbenchId} worktreeId={worktreeId} entries={entries} onSelectionChange={entry => onSelectionChange?.(entry ? { kind: 'source', entry } : null)} onCommitted={() => void refresh()} />}
        {tab === 'compare' && <VersionControlCompare workbenchId={workbenchId} worktreeId={worktreeId} branch={status?.branch ?? ''} onCommitted={() => void refresh()} />}
        {tab === 'history' && <VersionControlHistory workbenchId={workbenchId} worktreeId={worktreeId} commits={history} onCommitSelect={commit => onSelectionChange?.({ kind: 'commit', commit })} onObjectSelect={(commit, path) => onSelectionChange?.({ kind: 'commit', commit, path })} />}
        {tab === 'native' && <NativeStorePanel workbenchId={workbenchId} worktreeId={worktreeId} />}
      </div>
    </section>
  )
}
