import { AlertCircle, ShieldCheck, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'

type Props = {
  message: string
  roots: string[]
  onClose: () => void
}

export default function SandboxDeniedDialog({ message, roots, onClose }: Props) {
  return (
    <Dialog open onOpenChange={open => { if (!open) onClose() }}>
      <DialogContent showCloseButton={false} className="max-w-[560px] gap-0 overflow-hidden p-0">
        <DialogHeader className="flex-row items-center gap-3 border-b px-5 py-4 text-left" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-amber-500/10">
            <ShieldCheck className="h-4 w-4 text-amber-500" />
          </div>
          <div className="flex-1">
            <DialogTitle className="text-sm">TIA project outside the sandbox</DialogTitle>
            <DialogDescription className="text-[10px]">The engineering project path is not under an allowed root.</DialogDescription>
          </div>
          <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Close sandbox warning"><X /></Button>
        </DialogHeader>
        <div className="space-y-3 p-5">
          <div className="flex items-start gap-2 rounded-lg bg-amber-500/8 p-3 text-[9px] leading-relaxed text-amber-700 dark:text-amber-300">
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span className="break-all">{message}</span>
          </div>
          <p className="text-[10px] leading-relaxed text-muted-foreground">
            Move the TIA project under one of the allowed sandbox roots, then create the workbench again:
          </p>
          <div className="space-y-1 rounded-lg border bg-muted/40 p-3" style={{ borderColor: 'var(--border)' }}>
            {roots.length === 0 ? (
              <div className="text-[9px] text-muted-foreground">Sandbox roots could not be loaded.</div>
            ) : roots.map(root => (
              <div key={root} className="break-all font-mono text-[10px]">{root}</div>
            ))}
          </div>
        </div>
        <DialogFooter className="flex-row justify-end border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <Button size="xs" onClick={onClose}>Understood</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
