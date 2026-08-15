import { Banknote, Loader2, RefreshCw } from 'lucide-react'
import type { DeepSeekBalance } from '@/api/client'

export type DeepSeekBalanceRefreshState = 'idle' | 'refreshing' | 'success' | 'error'

type DeepSeekBalanceStatusProps = {
  balance: DeepSeekBalance | null
  state: DeepSeekBalanceRefreshState
  onRefresh: () => void
}

export default function DeepSeekBalanceStatus({ balance, state, onRefresh }: DeepSeekBalanceStatusProps) {
  const refreshMessage = state === 'refreshing'
    ? 'Refreshing…'
    : state === 'error'
      ? 'Refresh failed'
      : ''

  return (
    <span
      className="status-pill"
      data-api-balance
      title={balance?.fetchedAt ? `Fetched ${new Date(balance.fetchedAt).toLocaleString()} · refresh in Settings → Assistant` : 'DeepSeek account balance'}
    >
      <Banknote className="h-3 w-3 text-chart-4" />
      {balance?.balances.map(item => `${item.currency === 'USD' ? '$' : `${item.currency} `}${item.totalBalance}`).join(' · ') ?? '—'}
      <span
        className={`text-[8px] ${state === 'error' ? 'text-red-400' : state === 'refreshing' ? 'text-chart-2' : 'text-muted-foreground'}`}
        data-balance-refresh-status
        aria-live="polite"
      >
        {refreshMessage}
      </span>
      <button
        className="icon-button h-4 w-4"
        aria-label="Refresh DeepSeek balance"
        aria-busy={state === 'refreshing'}
        disabled={state === 'refreshing'}
        title={state === 'refreshing' ? 'Refreshing DeepSeek balance' : 'Refresh DeepSeek balance'}
        onClick={onRefresh}
      >
        {state === 'refreshing'
          ? <Loader2 className="h-3 w-3 animate-spin" />
          : <RefreshCw className="h-3 w-3" />}
      </button>
    </span>
  )
}
