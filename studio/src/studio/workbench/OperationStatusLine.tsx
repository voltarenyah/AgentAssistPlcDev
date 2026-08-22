import { AlertCircle, CheckCircle2, Loader2, X } from 'lucide-react'
import type { OperationStatus } from '@/api/client'
import { parseExportProgress, type ExportProgress } from '@/studio/operationStatus'

type Props = {
  status: OperationStatus | null
  fallback?: string
  onDismiss?: () => void
}

const ExportProgressRing = ({ progress }: { progress: ExportProgress }) => {
  const percent = Math.min(100, Math.max(0, Math.round((progress.current / progress.total) * 100)))
  const radius = 5
  const circumference = 2 * Math.PI * radius
  const filled = circumference * (Math.min(progress.current, progress.total) / progress.total)
  return (
    <span className="flex h-3 w-3 shrink-0 items-center justify-center" role="progressbar" aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100} title={`${percent}% exported`}>
      <svg viewBox="0 0 12 12" className="h-3 w-3 -rotate-90">
        <circle cx="6" cy="6" r={radius} fill="none" stroke="currentColor" strokeOpacity="0.25" strokeWidth="2" />
        <circle cx="6" cy="6" r={radius} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeDasharray={circumference} strokeDashoffset={circumference - filled} />
      </svg>
    </span>
  )
}

export default function OperationStatusLine({ status, fallback, onDismiss }: Props) {
  const state = status?.state ?? 'running'
  const message = status?.state === 'failed' && status.errorMessage
    ? `${status.message}: ${status.errorMessage}`
    : status?.message ?? fallback
  const exportProgress = state === 'running' ? parseExportProgress(message) : null

  if (!message) return null

  const Icon = state === 'succeeded' ? CheckCircle2
    : state === 'failed' ? AlertCircle
      : Loader2
  const color = state === 'succeeded' ? 'text-emerald-500'
    : state === 'failed' ? 'text-red-500'
      : 'text-muted-foreground'

  return (
    <span className={`flex min-w-0 items-center gap-1.5 text-[9px] ${color}`}>
      {exportProgress
        ? <><ExportProgressRing progress={exportProgress} /><span className="shrink-0">{Math.min(100, Math.round((exportProgress.current / exportProgress.total) * 100))}%</span></>
        : <Icon className={`h-3 w-3 shrink-0 ${state === 'running' ? 'animate-spin' : ''}`} />}
      <span className="truncate">{message}</span>
      {state === 'failed' && onDismiss && (
        <button className="icon-button h-5 w-5 shrink-0" onClick={onDismiss} title="Dismiss operation status">
          <X className="h-3 w-3" />
        </button>
      )}
    </span>
  )
}
