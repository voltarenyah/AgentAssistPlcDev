import { useState } from 'react'
import { Check, ChevronDown, Loader2 } from 'lucide-react'
import type { WorktreeStatus } from '@/api/client'
import { WorkbenchApiError } from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

const worktreeStatusLabel = (status: WorktreeStatus) =>
  status === 'finished' ? 'Finished' : 'Ongoing'

const statusDisplayError = (error: unknown) => {
  if (error instanceof WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

type Props = {
  status: WorktreeStatus
  onChange: (status: WorktreeStatus) => Promise<void> | void
  disabled?: boolean
}

export default function StatusBadge({ status, onChange, disabled }: Props) {
  const [busy, setBusy] = useState(false)

  const select = (next: WorktreeStatus) => {
    if (next === status || busy) return
    setBusy(true)
    void Promise.resolve()
      .then(() => onChange(next))
      .catch(error => showErrorToast(`Status could not be updated: ${statusDisplayError(error)}`))
      .finally(() => setBusy(false))
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild disabled={disabled || busy}>
        <button
          type="button"
          aria-label="Change worktree status"
          className={`inline-flex w-fit items-center gap-1 rounded-full border px-2 py-0.5 text-[9px] font-medium uppercase tracking-[0.1em] transition-colors ${
            status === 'finished'
              ? 'border-border bg-muted text-muted-foreground hover:bg-accent'
              : 'border-emerald-500/30 bg-emerald-500/10 text-emerald-600 hover:bg-emerald-500/20 dark:text-emerald-400'
          }`}
          onClick={event => event.stopPropagation()}
        >
          {busy
            ? <Loader2 className="h-2.5 w-2.5 animate-spin" />
            : <span className={`h-1.5 w-1.5 rounded-full ${status === 'finished' ? 'bg-muted-foreground' : 'bg-emerald-500'}`} />}
          {worktreeStatusLabel(status)}
          <ChevronDown className="h-2.5 w-2.5" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent onClick={event => event.stopPropagation()}>
        {(['ongoing', 'finished'] as const).map(option => (
          <DropdownMenuItem key={option} onSelect={() => select(option)}>
            <Check className={`h-3.5 w-3.5 ${option === status ? 'opacity-100' : 'opacity-0'}`} />
            {worktreeStatusLabel(option)}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
