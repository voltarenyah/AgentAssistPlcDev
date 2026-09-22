import { useState } from 'react'
import { AlertCircle, CheckCircle2, Download, Loader2, UploadCloud } from 'lucide-react'
import { toast } from 'sonner'
import * as api from '@/api/client'
import type { DiffLine, SourceObjectComparison } from '@/api/client'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { showErrorToast } from '@/components/ui/toast'

type Props = {
  workbenchId: string
  worktreeId: string
  deviceId: string
  comparison: SourceObjectComparison
  onClose: () => void
  /** Accept (TIA → local) succeeded: the caller reloads the device snapshot and closes. */
  onAccepted: () => void
}

const errorMessage = (error: unknown) => error instanceof Error ? error.message : String(error)

const lineClass = (kind: DiffLine['kind']) => {
  switch (kind) {
    case 'added': return 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300'
    case 'removed': return 'bg-red-500/10 text-red-700 dark:text-red-300'
    default: return 'text-muted-foreground'
  }
}

const linePrefix = (kind: DiffLine['kind']) =>
  kind === 'added' ? '+' : kind === 'removed' ? '-' : ' '

export default function PlcSourceCompareDialog({
  workbenchId,
  worktreeId,
  deviceId,
  comparison,
  onClose,
  onAccepted,
}: Props) {
  const [action, setAction] = useState<'accept' | 'push' | null>(null)
  const [failure, setFailure] = useState<string | null>(null)

  const acceptTiaVersion = async () => {
    setAction('accept')
    setFailure(null)
    try {
      const result = await api.acceptTiaSourceObject(workbenchId, worktreeId, deviceId, comparison.comparisonId)
      if (!result.success) {
        setFailure(result.message ?? 'The TIA version could not be applied locally.')
        return
      }
      toast.success(`Local file updated with the TIA version of ${comparison.name}.`)
      onAccepted()
    } catch (error) {
      showErrorToast(errorMessage(error))
    } finally {
      setAction(null)
    }
  }

  const pushLocalToTia = async () => {
    setAction('push')
    setFailure(null)
    try {
      const result = await api.pushSourceObjectToTia(workbenchId, worktreeId, deviceId, comparison.comparisonId)
      if (!result.success) {
        // Typical cause: the object is open in a TIA editor — close it there and retry.
        setFailure(result.message ?? 'The local version could not be imported into TIA.')
        return
      }
      toast.success(`Local version of ${comparison.name} imported into TIA.`)
      onClose()
    } catch (error) {
      showErrorToast(errorMessage(error))
    } finally {
      setAction(null)
    }
  }

  return (
    <Dialog open onOpenChange={open => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[85vh] max-w-4xl flex-col">
        <DialogHeader>
          <DialogTitle className="text-sm">
            Compare with TIA · {comparison.category} {comparison.name}
          </DialogTitle>
          <DialogDescription className="font-mono text-[10px]">
            {comparison.relativePath}
          </DialogDescription>
        </DialogHeader>
        {comparison.same ? (
          <div className="flex items-center gap-2 rounded-lg border bg-emerald-500/5 p-4 text-[11px] text-emerald-700 dark:text-emerald-300" style={{ borderColor: 'var(--border)' }}>
            <CheckCircle2 className="h-4 w-4 shrink-0" />
            No differences — the local source and the TIA version match.
          </div>
        ) : (
          <div className="min-h-0 flex-1 overflow-auto rounded-lg border font-mono text-[10px] leading-relaxed" style={{ borderColor: 'var(--border)' }}>
            {comparison.diffLines.map((line, index) => (
              <div key={index} className={`flex whitespace-pre ${lineClass(line.kind)}`}>
                <span className="w-5 shrink-0 select-none text-center">{linePrefix(line.kind)}</span>
                <span className="break-all">{line.text}</span>
              </div>
            ))}
          </div>
        )}
        {failure && (
          <div className="flex items-start gap-2 rounded-lg border border-red-500/40 bg-red-500/5 p-3 text-[10px] text-red-600 dark:text-red-400">
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span className="break-all">{failure}</span>
          </div>
        )}
        <DialogFooter className="gap-2">
          {!comparison.same && (
            <>
              <button
                type="button"
                className="secondary-button"
                disabled={Boolean(action)}
                onClick={() => void acceptTiaVersion()}
              >
                {action === 'accept' ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Download className="h-3.5 w-3.5" />}
                Use TIA version
              </button>
              <button
                type="button"
                className="secondary-button"
                disabled={Boolean(action)}
                onClick={() => void pushLocalToTia()}
              >
                {action === 'push' ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <UploadCloud className="h-3.5 w-3.5" />}
                Push local to TIA
              </button>
            </>
          )}
          <button type="button" className="primary-button" disabled={Boolean(action)} onClick={onClose}>
            Close
          </button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
