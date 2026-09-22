import { Archive, AlertCircle, FolderOpen, Loader2, X } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import type { Workbench, WorkbenchRegistration } from '@/api/client'

type ArchiveValues = {
  targetDirectory: string
  archiveName: string
  archivationMode: string
}

type Props = {
  workbench: Workbench
  worktree: WorkbenchRegistration
  busy: boolean
  error: string | null
  onClose: () => void
  onBrowseExportDirectory: (currentDirectory: string) => Promise<string | null>
  onArchive: (values: ArchiveValues) => Promise<void>
}

const sanitizeArchiveBaseName = (name: string) =>
  name.trim().replace(/[<>:"/\\|?*]/g, '-').replace(/[. ]+$/g, '') || 'tia-project'

const defaultArchiveName = (projectName: string, now = new Date()) => {
  const pad = (value: number) => String(value).padStart(2, '0')
  const timestamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}${pad(now.getHours())}${pad(now.getMinutes())}`
  return `${sanitizeArchiveBaseName(projectName)}_${timestamp}.zap17`
}

const normalizeRelativePath = (path: string) => path.replaceAll('/', '\\').replace(/^\\+|\\+$/g, '')
const defaultArchiveDirectory = (rootPath: string) => `${rootPath.replace(/[\\/]+$/g, '')}\\archive`

export default function ArchiveProjectDialog({
  workbench,
  worktree,
  busy,
  error,
  onClose,
  onBrowseExportDirectory,
  onArchive,
}: Props) {
  const [targetDirectory, setTargetDirectory] = useState(() => defaultArchiveDirectory(workbench.rootPath))
  const [archiveName, setArchiveName] = useState(() => defaultArchiveName(workbench.name))
  const [archivationMode, setArchivationMode] = useState('compressed')
  const [browsing, setBrowsing] = useState(false)
  const archiveNameHasPath = /[\\/]/.test(archiveName.trim())
  const valid = Boolean(targetDirectory.trim() && archiveName.trim() && !archiveNameHasPath)
  const worktreeDirectory = useMemo(
    () => `${workbench.rootPath}\\${normalizeRelativePath(worktree.relativePath)}`,
    [workbench.rootPath, worktree.relativePath],
  )

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!valid || busy) return
    await onArchive({
      targetDirectory: targetDirectory.trim(),
      archiveName: archiveName.trim(),
      archivationMode,
    })
  }

  const browseExportDirectory = async () => {
    setBrowsing(true)
    try {
      const selectedDirectory = await onBrowseExportDirectory(targetDirectory.trim())
      if (selectedDirectory) setTargetDirectory(selectedDirectory)
    } finally {
      setBrowsing(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <form onSubmit={event => void submit(event)} className="w-full max-w-[560px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-chart-2/10">
            <Archive className="h-4 w-4 text-chart-2" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 className="text-sm font-semibold">Archive TIA project</h2>
            <p className="truncate text-[10px] text-muted-foreground">{workbench.name} / {worktree.name} · {worktree.branch}</p>
          </div>
          <button type="button" className="icon-button" onClick={onClose} disabled={busy} aria-label="Close archive dialog">
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="space-y-4 p-5">
          {busy && (
            <div className="flex items-start gap-2 rounded-lg border border-chart-2/30 bg-chart-2/10 p-3 text-[10px]" aria-live="polite">
              <Loader2 className="mt-0.5 h-3.5 w-3.5 shrink-0 animate-spin text-chart-2" />
              <div>
                <div className="font-medium">Creating project archive…</div>
                <div className="mt-0.5 text-muted-foreground">TIA Portal is writing the archive. Keep the project session open.</div>
              </div>
            </div>
          )}

          <label className="field-label">
            <span>Export directory</span>
            <div className="flex gap-1.5">
              <input
                className="field-input min-w-0 flex-1 font-mono"
                aria-label="Export directory"
                value={targetDirectory}
                onChange={event => setTargetDirectory(event.target.value)}
                placeholder="C:\\Automation\\Exports"
                autoFocus
                readOnly
                disabled={busy || browsing}
              />
              <button
                type="button"
                className="secondary-button shrink-0"
                aria-label="Browse for export directory"
                onClick={() => void browseExportDirectory()}
                disabled={busy || browsing}
              >
                {browsing ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <FolderOpen className="h-3.5 w-3.5" />}
                Browse
              </button>
            </div>
            <span className="text-[9px] text-muted-foreground">Enter an existing folder where TIA Portal can write the archive.</span>
            <button
              type="button"
              className="self-start text-left text-[9px] text-chart-2 hover:underline"
              onClick={() => setTargetDirectory(worktreeDirectory)}
              disabled={busy}
            >
              Use this worktree folder: {worktreeDirectory}
            </button>
          </label>

          <label className="field-label">
            <span>Archive file name</span>
            <input
              className="field-input font-mono"
              aria-label="Archive file name"
              value={archiveName}
              onChange={event => setArchiveName(event.target.value)}
              placeholder="Line7.zap17"
              disabled={busy}
            />
            {archiveNameHasPath && <span className="text-[9px] text-amber-500">Enter a file name only; choose the destination directory separately.</span>}
          </label>

          <label className="field-label">
            <span>Archive mode</span>
            <select className="field-input" aria-label="Archive mode" value={archivationMode} onChange={event => setArchivationMode(event.target.value)} disabled={busy}>
              <option value="compressed">Compressed (recommended)</option>
              <option value="none">Uncompressed</option>
              <option value="discard_restorable_data">Discard restorable data</option>
              <option value="discard_restorable_data_and_compressed">Discard restorable data and compression</option>
            </select>
          </label>

          {error && (
            <div className="flex items-start gap-2 rounded-lg bg-red-500/8 p-3 text-[9px] leading-relaxed text-red-700 dark:text-red-300" role="alert">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              {error}
            </div>
          )}
        </div>

        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button type="button" className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="primary-button" disabled={!valid || busy}>
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Archive project
          </button>
        </div>
      </form>
    </div>
  )
}
