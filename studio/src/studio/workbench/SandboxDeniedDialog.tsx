import { AlertCircle, ShieldCheck, X } from 'lucide-react'

type Props = {
  message: string
  roots: string[]
  onClose: () => void
}

export default function SandboxDeniedDialog({ message, roots, onClose }: Props) {
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/60 p-5 backdrop-blur-[2px]">
      <div className="w-full max-w-[560px] overflow-hidden rounded-xl border bg-card shadow-2xl" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
          <div className="grid h-9 w-9 place-items-center rounded-lg bg-amber-500/10">
            <ShieldCheck className="h-4 w-4 text-amber-500" />
          </div>
          <div className="flex-1">
            <h2 className="text-sm font-semibold">TIA project outside the sandbox</h2>
            <p className="text-[10px] text-muted-foreground">The engineering project path is not under an allowed root.</p>
          </div>
          <button className="icon-button" onClick={onClose}><X className="h-4 w-4" /></button>
        </div>
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
        <div className="flex justify-end gap-2 border-t bg-muted/25 px-5 py-3" style={{ borderColor: 'var(--border)' }}>
          <button className="primary-button" onClick={onClose}>Understood</button>
        </div>
      </div>
    </div>
  )
}
