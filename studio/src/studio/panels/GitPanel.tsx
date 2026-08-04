import { useState, useEffect } from 'react'
import { RefreshCw, GitBranch, FileText, Loader2, Plus, Undo2, Check, X, AlertTriangle } from 'lucide-react'
import * as api from '@/api/client'

const stateIcon: Record<string, string> = {
  Modified: '📝',
  Added: '➕',
  Deleted: '❌',
  Untracked: '📄',
  Staged: '✅',
  RenamedInWorkdir: '📎',
  Conflicted: '⚠️',
}

const stateColor: Record<string, string> = {
  Modified: '#eab308',
  Added: '#22c55e',
  Deleted: '#ef4444',
  Untracked: '#6b7280',
  Staged: '#3b82f6',
  RenamedInWorkdir: '#a855f7',
  Conflicted: '#f97316',
}

interface GitPanelProps {
  workbenchId: string
  worktreeId: string
  deviceId: string
}

export default function GitPanel({ workbenchId, worktreeId, deviceId }: GitPanelProps) {
  const [status, setStatus] = useState<api.VcStatusResult | null>(null)
  const [log, setLog] = useState<api.VcCommitEntry[]>([])
  const [tab, setTab] = useState(0) // 0=Changes, 1=History, 2=Diff
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [, setSelectedFile] = useState<string | null>(null)
  const [diff, setDiff] = useState<api.VcDiffResult | null>(null)
  const [diffLoading, setDiffLoading] = useState(false)
  const [selectedPaths, setSelectedPaths] = useState<Set<string>>(new Set())

  // Commit
  const [commitMsg, setCommitMsg] = useState('')
  const [operating, setOperating] = useState(false)

  // Confirm restore
  const [confirmRestore, setConfirmRestore] = useState<{ filePath?: string; sourceSha?: string } | null>(null)

  const fetchAll = async () => {
    setLoading(true)
    setError(null)
    try {
      const [s, l] = await Promise.all([
        api.getWorktreeVcStatus(workbenchId, worktreeId),
        api.getWorktreeVcLog(workbenchId, worktreeId, 20).catch(() => ({ repoPath: '', commits: [] })),
      ])
      setStatus(s)
      setLog(l.commits)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load git data')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchAll()
  }, [workbenchId, worktreeId])

  // ── Actions ───────────────────────────────────────────

  const handleStageAll = async () => {
    setOperating(true)
    try {
      setSelectedPaths(new Set(status?.entries.map(e => e.filePath) ?? []))
      await fetchAll()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Stage failed')
    } finally {
      setOperating(false)
    }
  }

  const handleStageFile = async (filePath: string) => {
    setOperating(true)
    try {
      setSelectedPaths(previous => {
        const next = new Set(previous)
        if (next.has(filePath)) next.delete(filePath)
        else next.add(filePath)
        return next
      })
      await fetchAll()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Stage failed')
    } finally {
      setOperating(false)
    }
  }

  const handleCommit = async () => {
    if (!commitMsg.trim() || selectedPaths.size === 0) return
    setOperating(true)
    try {
      await api.commitVcPaths(workbenchId, worktreeId, [...selectedPaths], commitMsg.trim())
      setCommitMsg('')
      setDiff(null)
      setSelectedFile(null)
      setSelectedPaths(new Set())
      await fetchAll()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Commit failed')
    } finally {
      setOperating(false)
    }
  }

  const handleRestoreFile = (filePath: string) => {
    setConfirmRestore({ filePath })
  }

  const confirmRestoreAction = async () => {
    if (!confirmRestore) return
    setOperating(true)
    setConfirmRestore(null)
    try {
      await api.postVcRestore(workbenchId, worktreeId, deviceId, confirmRestore.filePath, confirmRestore.sourceSha)
      setDiff(null)
      setSelectedFile(null)
      await fetchAll()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Restore failed')
    } finally {
      setOperating(false)
    }
  }

  const handleFileClick = async (filePath: string) => {
    setSelectedFile(filePath)
    setTab(2)
    setDiffLoading(true)
    try {
      const d = await api.getWorktreeVcDiff(workbenchId, worktreeId, filePath)
      setDiff(d)
    } catch {
      setDiff(null)
    } finally {
      setDiffLoading(false)
    }
  }

  /* ── Confirmation overlay ─────────────────────────── */

  const renderConfirm = () => {
    if (!confirmRestore) return null
    return (
      <div className="absolute inset-0 z-10 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.45)' }}>
        <div className="mx-3 w-full max-w-[260px] rounded-lg p-3" style={{ background: 'var(--card)', border: '1px solid var(--border)' }}>
          <div className="flex items-center gap-2 mb-2">
            <AlertTriangle className="h-4 w-4 shrink-0" style={{ color: '#ef4444' }} />
            <span className="text-[10px] font-semibold" style={{ color: 'var(--foreground)' }}>Discard changes?</span>
          </div>
          <p className="text-[9px] mb-3" style={{ color: 'var(--muted-foreground)' }}>
            {confirmRestore.filePath
              ? `Restore "${confirmRestore.filePath}" — working-tree changes will be lost.`
              : 'Restore ALL files — working-tree changes will be lost.'}
          </p>
          <div className="flex gap-2 justify-end">
            <button onClick={() => setConfirmRestore(null)} disabled={operating}
              className="flex items-center gap-1 rounded px-2.5 py-1 text-[9px]" style={{ background: 'var(--accent)', color: 'var(--accent-foreground)' }}>
              <X className="h-3 w-3" /> Cancel
            </button>
            <button onClick={confirmRestoreAction} disabled={operating}
              className="flex items-center gap-1 rounded px-2.5 py-1 text-[9px]" style={{ background: '#ef4444', color: '#fff' }}>
              {operating ? <Loader2 className="h-3 w-3 animate-spin" /> : <Check className="h-3 w-3" />}
              {' '}Restore
            </button>
          </div>
        </div>
      </div>
    )
  }

  /* ── Changes tab ──────────────────────────────────── */

  const renderChanges = () => {
    const hasStaged = selectedPaths.size > 0
    const stagedCount = selectedPaths.size
    const staged = status?.entries.filter(e => selectedPaths.has(e.filePath)) ?? []
    const unstaged = status?.entries.filter(e => !selectedPaths.has(e.filePath)) ?? []

    if (!status?.entries.length) {
      return (
        <div className="flex flex-col items-center justify-center h-full text-[10px]" style={{ color: 'var(--muted-foreground)' }}>
          {loading ? 'Loading...' : 'Working tree clean'}
        </div>
      )
    }

    return (
      <div className="space-y-0.5">
        {/* Staged section */}
        {hasStaged && (
          <>
            <div className="text-[9px] font-medium px-1 mb-0.5 mt-1 flex items-center gap-1">
              <Check className="h-2.5 w-2.5" style={{ color: '#3b82f6' }} />
              <span style={{ color: 'var(--muted-foreground)' }}>Selected — {stagedCount}</span>
            </div>
            {staged.map(entry => (
              <div key={entry.filePath}
                className="flex items-center rounded px-2 py-1" style={{ background: 'rgba(59,130,246,0.05)' }}>
                <span className="text-[10px] mr-1.5">{stateIcon[entry.state] ?? '📄'}</span>
                <span className="text-[9px] font-mono truncate flex-1" style={{ color: stateColor[entry.state] ?? 'var(--foreground)' }}>
                  {entry.filePath}
                </span>
                <button onClick={(e) => { e.stopPropagation(); setSelectedPaths(previous => { const next = new Set(previous); next.delete(entry.filePath); return next }) }}
                  disabled={operating}
                  className="flex h-5 w-5 items-center justify-center rounded hover:bg-accent shrink-0" title="Unselect">
                  <Undo2 className="h-3 w-3" style={{ color: 'var(--muted-foreground)' }} />
                </button>
              </div>
            ))}
          </>
        )}

        {/* Unstaged section */}
        {unstaged.length > 0 && (
          <>
            <div className="text-[9px] font-medium px-1 mb-0.5 mt-1 flex items-center gap-1">
              <span style={{ color: 'var(--muted-foreground)' }}>Changes — {unstaged.length}</span>
            </div>
            {unstaged.map(entry => (
              <div key={entry.filePath}
                onClick={() => handleFileClick(entry.filePath)}
                className="flex cursor-pointer items-center gap-1 rounded px-2 py-1 hover:bg-accent/50 group">
                <span className="text-[10px]">{stateIcon[entry.state] ?? '📄'}</span>
                <span className="text-[9px] font-mono truncate flex-1" style={{ color: stateColor[entry.state] ?? 'var(--foreground)' }}>
                  {entry.filePath}
                </span>
                <button onClick={(e) => { e.stopPropagation(); handleStageFile(entry.filePath) }}
                  disabled={operating}
                  className="flex h-5 w-5 items-center justify-center rounded opacity-0 group-hover:opacity-100 hover:bg-accent shrink-0" title="Select">
                  <Plus className="h-3 w-3" style={{ color: 'var(--muted-foreground)' }} />
                </button>
                <button onClick={(e) => { e.stopPropagation(); handleRestoreFile(entry.filePath) }}
                  disabled={operating}
                  className="flex h-5 w-5 items-center justify-center rounded opacity-0 group-hover:opacity-100 hover:bg-accent shrink-0" title="Discard">
                  <Undo2 className="h-3 w-3" style={{ color: '#ef4444' }} />
                </button>
              </div>
            ))}
          </>
        )}
      </div>
    )
  }

  /* ── Commit bar ───────────────────────────────────── */

  const renderCommitBar = () => {
    const stagedCount = status?.entries.filter(e => e.staged).length ?? 0
    const anyChanges = (status?.entries.length ?? 0) > 0

    return (
      <div className="shrink-0 border-t px-2.5 py-2 space-y-1.5" style={{ borderColor: 'var(--border)' }}>
        {/* Select All button */}
        {anyChanges && (
          <button onClick={handleStageAll} disabled={operating}
            className="flex w-full items-center justify-center gap-1 rounded py-1 text-[9px]" style={{ background: 'var(--accent)', color: 'var(--accent-foreground)' }}>
            {operating ? <Loader2 className="h-3 w-3 animate-spin" /> : <Plus className="h-3 w-3" />}
            {' '}Select All
          </button>
        )}

        {/* Commit input + button */}
        {selectedPaths.size > 0 && (
          <div className="flex gap-1.5 pt-1 border-t" style={{ borderColor: 'var(--border)' }}>
            <input
              value={commitMsg}
              onChange={e => setCommitMsg(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter' && commitMsg.trim()) handleCommit() }}
              placeholder="Commit message..."
              disabled={operating}
              className="flex-1 rounded px-2 py-1 text-[9px] font-mono outline-none"
              style={{ background: 'var(--muted)', color: 'var(--foreground)', border: '1px solid var(--border)' }}
            />
            <button onClick={handleCommit} disabled={operating || !commitMsg.trim()}
              className="flex items-center gap-1 rounded px-2.5 py-1 text-[9px]" style={{ background: '#22c55e', color: '#fff', opacity: commitMsg.trim() ? 1 : 0.5 }}>
              {operating ? <Loader2 className="h-3 w-3 animate-spin" /> : <Check className="h-3 w-3" />}
              {' '}Commit
            </button>
          </div>
        )}

        {/* Summary */}
        {status && status.entries.length > 0 && (
          <div className="text-[8px] text-center" style={{ color: 'var(--muted-foreground)' }}>
            {stagedCount} selected · {status.entries.filter(e => !selectedPaths.has(e.filePath) && e.state !== 'Untracked').length} changed · {status.entries.filter(e => !selectedPaths.has(e.filePath) && e.state === 'Untracked').length} untracked
          </div>
        )}
      </div>
    )
  }

  /* ── History tab ──────────────────────────────────── */

  const renderHistory = () => {
    if (!log.length) {
      return (
        <div className="flex items-center justify-center h-full text-[10px]" style={{ color: 'var(--muted-foreground)' }}>
          {loading ? 'Loading...' : 'No commits yet'}
        </div>
      )
    }

    return (
      <div className="space-y-1">
        {log.map(commit => (
          <div key={commit.sha} className="rounded px-2 py-1.5 hover:bg-accent/50">
            <div className="flex items-center gap-1.5">
              <span className="font-mono text-[9px]" style={{ color: 'var(--chart-3)' }}>
                {commit.sha.slice(0, 7)}
              </span>
              <span className="flex-1 text-[10px] truncate" style={{ color: 'var(--foreground)' }}>
                {commit.message}
              </span>
            </div>
            <div className="flex gap-2 text-[8px] mt-0.5" style={{ color: 'var(--muted-foreground)' }}>
              <span>{commit.author}</span>
              <span>{commit.timestamp ? new Date(commit.timestamp).toLocaleString() : ''}</span>
            </div>
          </div>
        ))}
      </div>
    )
  }

  /* ── Diff tab ─────────────────────────────────────── */

  const renderDiff = () => {
    if (diffLoading) {
      return (
        <div className="flex items-center justify-center h-full">
          <Loader2 className="h-4 w-4 animate-spin" style={{ color: 'var(--muted-foreground)' }} />
        </div>
      )
    }

    if (!diff) {
      return (
        <div className="flex items-center justify-center h-full text-[10px]" style={{ color: 'var(--muted-foreground)' }}>
          Select a changed file to view its diff
        </div>
      )
    }

    return (
      <div className="font-mono text-[9px] leading-relaxed">
        <div className="flex items-center gap-1.5 px-1 py-1 border-b" style={{ borderColor: 'var(--border)' }}>
          <FileText className="h-3 w-3" style={{ color: 'var(--muted-foreground)' }} />
          <span className="truncate" style={{ color: 'var(--foreground)' }}>{diff.filePath}</span>
        </div>
        {diff.binary ? (
          <div className="px-2 py-4 text-center text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
            Binary file
          </div>
        ) : diff.hunks.length === 0 ? (
          <div className="px-2 py-4 text-center text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
            No changes
          </div>
        ) : (
          diff.hunks.map((hunk, hi) => (
            <div key={hi}>
              <div className="sticky top-0 px-2 py-0.5 text-[8px]" style={{ background: 'var(--card)', color: 'var(--muted-foreground)' }}>
                @@ -{hunk.oldStart} +{hunk.newStart} @@
              </div>
              {hunk.lines.map((line, li) => (
                <div key={li} className="flex gap-1 px-2" style={{
                  background: line.type === 'addition' ? 'rgba(34,197,94,0.08)' : line.type === 'deletion' ? 'rgba(239,68,68,0.08)' : 'transparent',
                }}>
                  <span className="w-4 shrink-0 text-right" style={{ color: 'var(--muted-foreground)' }}>
                    {line.type === 'addition' ? '+' : line.type === 'deletion' ? '-' : ' '}
                  </span>
                  <span className="whitespace-pre-wrap break-all" style={{ color: 'var(--foreground)' }}>
                    {line.content}
                  </span>
                </div>
              ))}
            </div>
          ))
        )}
        <div className="px-2 py-1 text-[8px]" style={{ color: 'var(--muted-foreground)' }}>
          {diff.hunks.reduce((s, h) => s + h.lines.filter(l => l.type === 'addition').length, 0)} additions,{' '}
          {diff.hunks.reduce((s, h) => s + h.lines.filter(l => l.type === 'deletion').length, 0)} deletions
        </div>
      </div>
    )
  }

  /* ── Render ───────────────────────────────────────── */

  return (
    <div className="flex flex-col h-full relative">
      {/* Header */}
      <div className="flex items-center gap-2 px-2.5 py-2 border-b shrink-0" style={{ borderColor: 'var(--border)' }}>
        <GitBranch className="h-3.5 w-3.5" style={{ color: 'var(--chart-4)' }} />
        <span className="text-[10px] font-semibold truncate flex-1" style={{ color: 'var(--foreground)' }}>
          {status?.branch || 'git'}
        </span>
        <button onClick={fetchAll} disabled={loading}
          className="flex h-6 w-6 cursor-default items-center justify-center rounded hover:bg-accent" title="Refresh">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} style={{ color: 'var(--muted-foreground)' }} />
        </button>
      </div>

      {error && (
        <div className="px-3 py-2 text-[9px] shrink-0" style={{ color: 'var(--destructive)' }}>
          {error}
        </div>
      )}

      {/* Tab bar */}
      <div className="flex h-7 items-center border-b px-2 gap-0.5 shrink-0" style={{ borderColor: 'var(--border)' }}>
        {['Changes', 'History', 'Diff'].map((label, i) => (
          <div key={label} onClick={() => setTab(i)}
            className="flex h-6 cursor-default items-center rounded-t px-2 text-[9px]"
            style={{
              background: tab === i ? 'var(--background)' : 'transparent',
              color: tab === i ? 'var(--foreground)' : 'var(--muted-foreground)',
            }}>
            {label}
          </div>
        ))}
        <div className="flex-1" />
        {status && (
          <span className="h-2 w-2 rounded-full" style={{ background: status.entries.length > 0 ? '#eab308' : '#22c55e' }}
            title={status.entries.length > 0 ? `${status.entries.length} changes` : 'Clean'} />
        )}
      </div>

      {/* Content */}
      <div className="flex-1 overflow-y-auto scrollbar-sleek py-1">
        {tab === 0 && renderChanges()}
        {tab === 1 && renderHistory()}
        {tab === 2 && renderDiff()}
      </div>

      {/* Commit bar (only on Changes tab) */}
      {tab === 0 && renderCommitBar()}

      {/* Confirm overlay */}
      {renderConfirm()}
    </div>
  )
}
