import { AlertCircle, CheckCircle2, Loader2, X } from 'lucide-react'
import type { OperationStatus } from '@/api/client'

type Props = {
  status: OperationStatus | null
  fallback?: string
  onDismiss?: () => void
}

export default function OperationStatusLine({ status, fallback, onDismiss }: Props) {
  const state = status?.state ?? 'running'
  const message = status?.state === 'failed' && status.errorMessage
    ? `${status.message}: ${status.errorMessage}`
    : status?.message ?? fallback

  if (!message) return null

  const Icon = state === 'succeeded' ? CheckCircle2
    : state === 'failed' ? AlertCircle
      : Loader2
  const color = state === 'succeeded' ? 'text-emerald-500'
    : state === 'failed' ? 'text-red-500'
      : 'text-muted-foreground'

  return (
    <span className={`flex min-w-0 items-center gap-1.5 text-[9px] ${color}`}>
      <Icon className={`h-3 w-3 shrink-0 ${state === 'running' ? 'animate-spin' : ''}`} />
      <span className="truncate">{message}</span>
      {state === 'failed' && onDismiss && (
        <button className="icon-button h-5 w-5 shrink-0" onClick={onDismiss} title="Dismiss operation status">
          <X className="h-3 w-3" />
        </button>
      )}
    </span>
  )
}
