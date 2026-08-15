import { AlertTriangle, Loader2, Save, X, XCircle } from 'lucide-react'

type Props = {
  operationLabel: string
  busy: boolean
  onSaveAndClose: () => void
  onCloseWithoutSaving: () => void
  onCancel: () => void
}

export default function TiaCloseConfirmationDialog({
  operationLabel,
  busy,
  onSaveAndClose,
  onCloseWithoutSaving,
  onCancel,
}: Props) {
  return (
    <div className="fixed inset-0 z-[60] grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[560px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-amber-500/10">
            <AlertTriangle className="h-4 w-4 text-amber-500" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 className="text-sm font-semibold">Close the attached TIA instance?</h2>
            <p className="text-[10px] text-muted-foreground">The current TIA connection must be released first.</p>
          </div>
          <button
            className="icon-button"
            aria-label="Close confirmation dialog"
            title="Close confirmation dialog"
            onClick={onCancel}
            disabled={busy}
          >
            <X className="h-4 w-4" />
          </button>
        </div>
        <div className="space-y-3 p-5 text-[10px]">
          <p className="leading-relaxed text-muted-foreground">
            <span className="font-medium text-foreground">{operationLabel} requires close current attached TIA instance.</span>{' '}
            Please choose how we can proceed.
          </p>
          <div className="rounded-lg border bg-muted/25 p-3 text-[9px] text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
            Save and close preserves the current project before the instance is closed. Close without saving discards unsaved changes. Cancel leaves TIA open so you can close it manually.
          </div>
        </div>
        <div className="flex flex-wrap justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button
            className="secondary-button"
            aria-label="Cancel and close manually"
            onClick={onCancel}
            disabled={busy}
          >
            Cancel
          </button>
          <button
            className="secondary-button text-red-600 dark:text-red-400"
            aria-label="Close TIA instance without saving"
            onClick={onCloseWithoutSaving}
            disabled={busy}
          >
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            <XCircle className="h-3.5 w-3.5" /> Close without save
          </button>
          <button
            className="primary-button"
            aria-label="Save and close TIA instance"
            onClick={onSaveAndClose}
            disabled={busy}
          >
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            <Save className="h-3.5 w-3.5" /> Save and close
          </button>
        </div>
      </div>
    </div>
  )
}
