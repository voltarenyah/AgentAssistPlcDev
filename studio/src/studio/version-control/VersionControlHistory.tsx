import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'
import { ChevronRight, Copy, Download, Loader2 } from 'lucide-react'
import { toast } from 'sonner'
import * as api from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'

export type VcTimelineItem =
  | {
      kind: 'commit'
      sha: string
      message: string
      author: string
      timestamp: string
      files: string[]
      tiaChecksum: string | null
      svnRevision: number | null
      validationState: api.VcValidationState
    }
  | {
      kind: 'savepoint'
      revision: number
      message: string
      author: string
      timestamp: string
      tiaChecksum: string | null
      gitCommitSha: string
    }

type Props = {
  workbenchId: string
  worktreeId: string
  branch: string
  items: VcTimelineItem[]
  loading: boolean
}

type MenuState = { x: number; y: number; item: Extract<VcTimelineItem, { kind: 'savepoint' }> }

const itemKey = (item: VcTimelineItem) => item.kind === 'commit' ? `c:${item.sha}` : `s:${item.revision}`

const formatTime = (value: string) => {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

// TIA checksums arrive as "PLC_1:AA BB; PLC_2:CC DD" — one left-aligned row
// per device so multi-device projects stay scannable.
const ChecksumRows = ({ value }: { value: string }) => (
  <div className="space-y-0.5" data-testid="checksum-rows">
    {value.split(';').map(part => part.trim()).filter(Boolean).map(part => (
      <div key={part} className="font-mono text-[10px] text-sky-500">Σ {part}</div>
    ))}
  </div>
)

export default function VersionControlHistory({ workbenchId, worktreeId, branch, items, loading }: Props) {
  const [openKeys, setOpenKeys] = useState<Set<string>>(new Set())
  const [menu, setMenu] = useState<MenuState | null>(null)
  const [exporting, setExporting] = useState(false)
  const [rollbackPaths, setRollbackPaths] = useState<Set<string>>(new Set())
  const [rollbackName, setRollbackName] = useState('')
  const [creatingRollback, setCreatingRollback] = useState(false)

  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    window.addEventListener('pointerdown', close)
    window.addEventListener('blur', close)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('blur', close)
    }
  }, [menu])

  const toggleOpen = (key: string, sha: string | null) => {
    setOpenKeys(previous => {
      const next = new Set(previous)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
    // Opening a commit starts a fresh rollback selection for that commit;
    // other open items stay expanded so details can be compared side by side.
    if (sha) {
      setRollbackPaths(new Set())
      setRollbackName(`rollback-${sha.slice(0, 7)}`)
    }
  }

  const toggleRollbackPath = (path: string) => {
    setRollbackPaths(previous => {
      const next = new Set(previous)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
  }

  const createRollback = async (sha: string) => {
    if (rollbackPaths.size === 0 || !rollbackName.trim() || creatingRollback) return
    setCreatingRollback(true)
    try {
      await api.createRollbackFeature(workbenchId, sha, [...rollbackPaths], rollbackName.trim())
      toast.success(`Rollback feature “${rollbackName.trim()}” created — switch to it and commit the generated changes`)
      setRollbackPaths(new Set())
    } catch (cause) {
      showErrorToast(`Rollback failed: ${cause instanceof Error ? cause.message : 'Unexpected failure'}`)
    } finally {
      setCreatingRollback(false)
    }
  }

  const exportSavepoint = async (item: Extract<VcTimelineItem, { kind: 'savepoint' }>) => {
    setMenu(null)
    if (exporting) return
    setExporting(true)
    try {
      const result = await api.restoreTiaProject(workbenchId, worktreeId, item.gitCommitSha)
      toast.success(`Exported r${item.revision} to ${result.restoredDirectory} — live project untouched`)
    } catch (cause) {
      showErrorToast(`Export failed: ${cause instanceof Error ? cause.message : 'Unexpected failure'}`)
    } finally {
      setExporting(false)
    }
  }

  const copyRevision = (item: Extract<VcTimelineItem, { kind: 'savepoint' }>) => {
    setMenu(null)
    void navigator.clipboard?.writeText(`r${item.revision}`).then(() => toast.success(`Copied r${item.revision}`))
  }

  const headKey = items.find(item => item.kind === 'commit') ? itemKey(items.find(item => item.kind === 'commit')!) : null

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="version-control-history">
      <div className="flex shrink-0 items-center gap-1.5 px-3.5 pb-1.5 pt-3.5 text-[11px] font-bold uppercase tracking-wide">
        <span>Timeline</span>
        <span className="font-normal normal-case tracking-normal text-muted-foreground">{items.length}</span>
      </div>
      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto px-2.5 pb-3">
        {loading ? (
          <div className="flex items-center justify-center gap-2 py-6 text-[10px] text-muted-foreground">
            <Loader2 className="h-3 w-3 animate-spin" /> Loading timeline...
          </div>
        ) : items.length === 0 ? (
          <div className="py-6 text-center text-[10px] text-muted-foreground">No commits yet</div>
        ) : (
          items.map((item, index) => {
            const key = itemKey(item)
            const open = openKeys.has(key)
            const isSavepoint = item.kind === 'savepoint'
            return (
              <div
                key={key}
                className={`relative pl-6 before:absolute before:bottom-0 before:left-[5.5px] before:top-0 before:w-0.5 ${isSavepoint ? 'before:bg-violet-500/35' : 'before:bg-blue-600/50'} ${index === 0 ? 'before:top-[15px]' : ''} ${index === items.length - 1 ? 'before:bottom-[calc(100%-15px)]' : ''}`}
              >
                <span
                  className={`absolute left-0 top-[9px] z-10 h-[13px] w-[13px] border-2 ${isSavepoint
                    ? 'rounded-[3px] border-violet-400 bg-violet-400'
                    : `rounded-full border-blue-500 ${key === headKey ? 'bg-card' : 'bg-blue-500'}`}`}
                />
                <button
                  type="button"
                  data-testid={isSavepoint ? `savepoint-r${item.revision}` : `commit-${item.sha.slice(0, 7)}`}
                  className="flex w-full items-center gap-1.5 rounded-lg px-2 py-[7px] text-left hover:bg-white/5"
                  onClick={() => toggleOpen(key, item.kind === 'commit' ? item.sha : null)}
                  onContextMenu={isSavepoint ? event => {
                    event.preventDefault()
                    setMenu({ x: event.clientX, y: event.clientY, item })
                  } : undefined}
                >
                  <ChevronRight className={`h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform ${open ? 'rotate-90' : ''}`} />
                  <span className={`min-w-0 flex-1 truncate text-[12px] ${isSavepoint ? 'text-violet-400' : ''}`}>{item.message}</span>
                  {isSavepoint ? (
                    <span className="shrink-0 rounded-full border border-violet-400/35 bg-violet-400/10 px-2 py-0.5 font-mono text-[10px] font-bold text-violet-400">r{item.revision}</span>
                  ) : key === headKey && branch ? (
                    <span className="max-w-[130px] shrink-0 truncate rounded-full border bg-white/5 px-2 py-0.5 font-mono text-[10px]" style={{ borderColor: 'var(--border)' }}>{branch}</span>
                  ) : null}
                </button>
                {open && (
                  <div className="pb-2.5 pl-[27px] pr-2" data-testid="timeline-detail">
                    {item.kind === 'commit' ? (
                      <>
                        <div className="mb-1.5 text-[10px] text-muted-foreground"><b className="font-medium text-foreground">{item.author}</b> · {formatTime(item.timestamp)}</div>
                        <div className="mb-1.5 flex flex-wrap gap-1">
                          {item.svnRevision !== null && <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-violet-400" title="Linked SVN revision">r{item.svnRevision}</span>}
                          <span className={`rounded px-1.5 py-0.5 font-mono text-[10px] ${item.validationState === 'Validated' ? 'bg-emerald-500/10 text-emerald-500' : item.validationState === 'Invalid' ? 'bg-red-500/10 text-red-500' : 'bg-muted text-muted-foreground'}`}>
                            {item.validationState === 'Validated' ? '✓ TIA validated' : item.validationState === 'Invalid' ? 'Validation invalid' : 'No evidence'}
                          </span>
                        </div>
                        {item.tiaChecksum && <div className="mb-1.5"><ChecksumRows value={item.tiaChecksum} /></div>}
                        <div className="mb-0.5 text-[9px] uppercase tracking-widest text-muted-foreground">Changed files · {item.files.length}</div>
                        {item.files.map(file => (
                          <button
                            key={file}
                            type="button"
                            data-testid={`object-${file.split('/').pop()}`}
                            data-selected={rollbackPaths.has(file)}
                            title={`${file} — select for rollback`}
                            onClick={() => toggleRollbackPath(file)}
                            className={`block w-full truncate rounded px-1 py-0.5 text-left font-mono text-[10px] hover:bg-white/5 ${rollbackPaths.has(file) ? 'bg-blue-500/10 text-foreground' : ''}`}
                          >
                            {file}
                          </button>
                        ))}
                        {rollbackPaths.size > 0 && (
                          <div className="mt-1.5 space-y-1.5 border-t pt-2" style={{ borderColor: 'var(--border)' }}>
                            <div className="text-[9px] text-muted-foreground">Recover {rollbackPaths.size} selected file{rollbackPaths.size === 1 ? '' : 's'} as a new feature — master is never reset.</div>
                            <input
                              aria-label="Rollback feature name"
                              value={rollbackName}
                              onChange={event => setRollbackName(event.currentTarget.value)}
                              className="h-7 w-full rounded-lg border bg-white/[0.03] px-2.5 text-[11px] outline-none focus:border-white/20"
                              style={{ borderColor: 'var(--border)' }}
                              placeholder="Rollback feature name"
                            />
                            <button
                              type="button"
                              data-testid="create-rollback-feature"
                              disabled={!rollbackName.trim() || creatingRollback}
                              onClick={() => void createRollback(item.sha)}
                              className="flex h-7 w-full items-center justify-center gap-1.5 rounded-lg bg-chart-5 text-[11px] font-semibold text-white disabled:opacity-40"
                            >
                              {creatingRollback && <Loader2 className="h-3 w-3 animate-spin" />}
                              Create rollback feature
                            </button>
                          </div>
                        )}
                      </>
                    ) : (
                      <>
                        <div className="mb-1.5 text-[10px] text-muted-foreground"><b className="font-medium text-foreground">SVN savepoint</b> · {formatTime(item.timestamp)}</div>
                        <div className="mb-1.5 flex flex-wrap gap-1">
                          <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-violet-400">r{item.revision}</span>
                          <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground" title="Linked git commit">{item.gitCommitSha.slice(0, 7)}</span>
                        </div>
                        {item.tiaChecksum && <div className="mb-1.5"><ChecksumRows value={item.tiaChecksum} /></div>}
                        <div className="text-[9px] italic text-muted-foreground">Right-click to export this saved project</div>
                      </>
                    )}
                  </div>
                )}
              </div>
            )
          })
        )}
      </div>

      {menu && createPortal(
        <div
          className="fixed z-50 min-w-[190px] rounded-lg border bg-popover p-1 shadow-2xl"
          style={{ left: Math.min(menu.x, window.innerWidth - 210), top: Math.min(menu.y, window.innerHeight - 130), borderColor: 'var(--border)' }}
          data-testid="savepoint-menu"
          onPointerDown={event => event.stopPropagation()}
        >
          <div className="px-2.5 pb-0.5 pt-1 text-[9px] uppercase tracking-widest text-muted-foreground">SVN savepoint r{menu.item.revision}</div>
          <button
            type="button"
            data-testid="savepoint-export"
            className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-[11px] hover:bg-accent disabled:opacity-40"
            disabled={exporting}
            onClick={() => void exportSavepoint(menu.item)}
          >
            {exporting ? <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" /> : <Download className="h-3.5 w-3.5 text-muted-foreground" />}
            Export saved project
          </button>
          <button
            type="button"
            className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-[11px] hover:bg-accent"
            onClick={() => copyRevision(menu.item)}
          >
            <Copy className="h-3.5 w-3.5 text-muted-foreground" />
            Copy revision number
          </button>
        </div>,
        document.body,
      )}
    </div>
  )
}
