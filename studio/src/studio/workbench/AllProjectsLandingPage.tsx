import { useMemo, useRef, useState } from 'react'
import { AlertCircle, Camera, GitBranch, Loader2, Plus } from 'lucide-react'
import * as api from '@/api/client'
import TagFilter from './tags/TagFilter'

export type AllProjectsLandingPageProps = {
  projects: api.WorkbenchLandingCard[] | null
  loading?: boolean
  error?: string | null
  tagNodes?: api.TagNode[]
  selectedTagIds?: string[]
  matchingWorkbenchIds?: string[] | null
  onSelectedTagIdsChange?: (ids: string[]) => void
  onRetry?: () => void
  onCreateWorkbench?: () => void
  onSelectWorkbench: (workbenchId: string) => void
  onSelectWorktree: (workbenchId: string, worktreeId: string) => void
}

const formatDate = (value: string | null) => value ? new Date(value).toLocaleDateString() : 'Unknown'

export default function AllProjectsLandingPage({
  projects, loading = false, error, tagNodes = [], selectedTagIds = [], matchingWorkbenchIds = null,
  onSelectedTagIdsChange, onRetry, onCreateWorkbench, onSelectWorkbench, onSelectWorktree,
}: AllProjectsLandingPageProps) {
  const [sort, setSort] = useState<'modified' | 'created'>('modified')
  const [coverError, setCoverError] = useState<string | null>(null)
  const [coverNotice, setCoverNotice] = useState<string | null>(null)
  const [uploadingProject, setUploadingProject] = useState<string | null>(null)
  const [uploadedCovers, setUploadedCovers] = useState<Record<string, string>>({})
  const fileRef = useRef<HTMLInputElement>(null)
  const coverProjectRef = useRef<string | null>(null)
  const ordered = useMemo(() => {
    const values = (projects ?? []).filter(project => matchingWorkbenchIds === null || matchingWorkbenchIds.includes(project.workbenchId))
    return [...values].sort((left, right) => {
      const l = Date.parse(sort === 'created' ? left.createdAt : left.modifiedAt ?? left.createdAt)
      const r = Date.parse(sort === 'created' ? right.createdAt : right.modifiedAt ?? right.createdAt)
      return (Number.isFinite(r) ? r : 0) - (Number.isFinite(l) ? l : 0) || left.name.localeCompare(right.name)
    })
  }, [projects, matchingWorkbenchIds, sort])

  if (loading && projects === null) return <div className="grid h-full place-items-center text-sm text-muted-foreground"><Loader2 className="mr-2 h-4 w-4 animate-spin" /> Loading Projects…</div>
  if (error && projects === null) return <div className="grid h-full place-items-center p-8"><div className="text-center"><AlertCircle className="mx-auto mb-3 h-7 w-7 text-destructive" /><p className="text-sm">{error}</p>{onRetry && <button className="primary-button mt-4" onClick={onRetry}>Retry</button>}</div></div>

  return <div className="min-h-0 flex-1 overflow-y-auto">
    <div className="mx-auto max-w-7xl space-y-4 p-5">
      <div className="flex flex-wrap items-center gap-3">
        <div><h1 className="text-xl font-semibold">All Projects</h1><p className="text-xs text-muted-foreground">Choose a Project or worktree to continue.</p></div>
        <div className="ml-auto flex items-center gap-2">
          <label className="text-xs text-muted-foreground" htmlFor="landing-sort">Sort</label>
          <select id="landing-sort" className="h-8 rounded-md border bg-background px-2 text-xs" value={sort} onChange={event => setSort(event.target.value as typeof sort)}><option value="modified">Recently modified</option><option value="created">Created</option></select>
        </div>
      </div>
      {tagNodes.length > 0 && onSelectedTagIdsChange && <TagFilter nodes={tagNodes} selectedTagIds={selectedTagIds} onSelectedTagIdsChange={onSelectedTagIdsChange} />}
      {ordered.length === 0 ? <div className="rounded-xl border bg-card p-10 text-center"><p className="text-sm text-muted-foreground">No Projects yet.</p>{onCreateWorkbench && <button className="primary-button mt-4" onClick={onCreateWorkbench}><Plus className="h-3.5 w-3.5" /> Create Project</button>}</div> : <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{ordered.map(project => {
        const coverAssetId = uploadedCovers[project.workbenchId] ?? project.coverAssetId
        return <article key={project.workbenchId} className="overflow-hidden rounded-xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
        <button type="button" className="block w-full p-4 text-left hover:bg-accent/30" onClick={() => onSelectWorkbench(project.workbenchId)}>
          <div className="flex items-start gap-3"><div className="grid h-11 w-11 shrink-0 place-items-center overflow-hidden rounded-lg bg-surface-muted">{coverAssetId ? <img src={`${api.workbenchCoverUrl(project.workbenchId)}?asset=${encodeURIComponent(coverAssetId)}`} alt={`${project.name} cover`} className="h-full w-full object-cover" /> : <GitBranch className="h-5 w-5 text-chart-2" />}</div><div className="min-w-0"><h2 className="truncate text-sm font-semibold">{project.name}</h2><p className="mt-1 text-xs text-muted-foreground">Modified {formatDate(project.modifiedAt ?? project.createdAt)}</p><div className="mt-2 flex flex-wrap gap-1" aria-label={`${project.name} tags`}>{project.effectiveTagIds.map(tagId => <span key={tagId} className="rounded bg-surface-muted px-1.5 py-0.5 text-[10px] text-muted-foreground">{tagNodes.find(node => node.tagId === tagId)?.name ?? tagId}</span>)}</div></div></div>
        </button>
        <div className="flex items-center gap-2 border-t px-4 py-2" style={{ borderColor: 'var(--border)' }}><button type="button" className="text-xs text-muted-foreground hover:text-foreground" disabled={uploadingProject !== null} aria-label={`Choose cover image for ${project.name}`} onClick={() => { coverProjectRef.current = project.workbenchId; fileRef.current?.click() }}><Camera className="mr-1 inline h-3.5 w-3.5" /> {uploadingProject === project.workbenchId ? 'Uploading…' : 'Cover'}</button><span className="ml-auto text-xs text-muted-foreground">{project.effectiveTagIds.length} tags</span></div>
        <div className="divide-y" style={{ borderColor: 'var(--border)' }}>{project.worktrees.map(worktree => { const total = worktree.totalTasks ?? 0; const completed = worktree.completedTasks ?? 0; const percent = total > 0 ? Math.min(100, Math.round(completed / total * 100)) : 0; const label = worktree.availability === 'unavailable' ? 'Task progress unavailable' : total === 0 ? 'No tasks' : `${completed} of ${total} tasks complete`; return <button key={worktree.worktreeId} type="button" className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-accent/30" onClick={() => onSelectWorktree(project.workbenchId, worktree.worktreeId)}><span role="img" aria-label={label} className="grid h-7 w-7 shrink-0 place-items-center rounded-full" style={{ background: `conic-gradient(var(--chart-2) ${percent}%, var(--surface-muted) 0)` }}><span className="grid h-5 w-5 place-items-center rounded-full bg-card text-[9px]">{total > 0 ? `${percent}%` : '—'}</span></span><GitBranch className="h-3.5 w-3.5 shrink-0 text-muted-foreground" /><span className="min-w-0 flex-1"><span className="block truncate text-xs font-medium">{worktree.name}</span><span className="block text-[11px] text-muted-foreground">{worktree.availability === 'unavailable' ? 'Unavailable' : total === 0 ? 'No tasks' : `${completed} of ${total} tasks`} · {worktree.dirtySourceFiles ?? '—'} source changes · {worktree.sessionCount ?? '—'} sessions</span></span></button> })}</div>
      </article>})}</div>}
      {coverError && <p role="alert" className="text-xs text-destructive">{coverError}</p>}
      {coverNotice && <p role="status" aria-live="polite" className="text-xs text-muted-foreground">{coverNotice}</p>}
      <input ref={fileRef} type="file" accept="image/png,image/jpeg,image/gif,image/webp" className="hidden" onChange={async event => { const file = event.target.files?.[0]; const id = coverProjectRef.current; event.target.value = ''; if (!file || !id || uploadingProject) return; setCoverError(null); setCoverNotice('Uploading cover…'); setUploadingProject(id); try { const result = await api.uploadWorkbenchCover(id, file); const assetId = result.coverAssetId; if (assetId) setUploadedCovers(current => ({ ...current, [id]: assetId })); setCoverNotice('Cover uploaded successfully.'); } catch (uploadError) { setCoverNotice(null); setCoverError(uploadError instanceof Error ? uploadError.message : 'Cover upload failed.') } finally { setUploadingProject(null) } }} />
    </div>
  </div>
}
