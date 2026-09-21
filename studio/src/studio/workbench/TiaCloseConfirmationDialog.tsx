import { AlertTriangle, Loader2, Save, X, XCircle } from 'lucide-react'
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@notion-kit/ui/primitives'

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
    <Dialog open onOpenChange={open => { if (!open && !busy) onCancel() }}>
      {/*
        notion-kit's Dialog exposes no showCloseButton / onEscapeKeyDown /
        onPointerDownOutside props; the busy guard in onOpenChange already covers
        Escape, overlay clicks and the close control.
      */}
      <DialogContent hideClose className="max-w-[560px] gap-0 overflow-hidden p-0">
        <DialogHeader className="flex-row items-center gap-3 border-b border-border px-5 py-4 text-left">
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-amber-500/10">
            <AlertTriangle className="h-4 w-4 text-amber-500" />
          </div>
          <div className="min-w-0 flex-1">
            <DialogTitle className="text-sm">Close the attached TIA instance?</DialogTitle>
            <DialogDescription className="text-[10px]">The current TIA connection must be released first.</DialogDescription>
          </div>
          <Button
            variant="close"
            aria-label="Close confirmation dialog"
            title="Close confirmation dialog"
            onClick={onCancel}
            disabled={busy}
          ><X /></Button>
        </DialogHeader>
        <div className="space-y-3 p-5 text-[10px]">
          <p className="leading-relaxed text-muted-foreground">
            <span className="font-medium text-foreground">{operationLabel} requires close current attached TIA instance.</span>{' '}
            Please choose how we can proceed.
          </p>
          <div className="rounded-lg border bg-surface-muted/25 p-3 text-[9px] text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
            Save and close preserves the current project before the instance is closed. Close without saving discards unsaved changes. Cancel leaves TIA open so you can close it manually.
          </div>
        </div>
        <DialogFooter className="flex-row flex-wrap justify-end gap-2 border-t border-border bg-surface-muted/25 px-5 py-3">
          <Button
            variant="primary"
            size="xs"
            aria-label="Cancel and close manually"
            onClick={onCancel}
            disabled={busy}
          >Cancel</Button>
          {/* red-fill keeps the destructive weight the old `destructive` variant carried. */}
          <Button
            variant="red-fill"
            size="xs"
            aria-label="Close TIA instance without saving"
            onClick={onCloseWithoutSaving}
            disabled={busy}
          >
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            <XCircle className="h-3.5 w-3.5" /> Close without save
          </Button>
          <Button
            variant="blue"
            size="xs"
            aria-label="Save and close TIA instance"
            onClick={onSaveAndClose}
            disabled={busy}
          >
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            <Save className="h-3.5 w-3.5" /> Save and close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
