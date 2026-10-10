import type { ReactNode } from 'react'
import { GitBranch, PanelRightClose, RefreshCw } from 'lucide-react'

type Props = {
  /** The page's name, shown in its header and used for its controls' accessible names. */
  title: string
  /** What the page describes right now — the selected worktree, or the worktree and its object. */
  scope?: string | null
  /** A short piece of page state worth seeing without opening anything, such as the uncommitted count. */
  chip?: string | null
  onRefresh?: () => void
  /** Collapses the page to the rail; the same gesture as the rail's active item. */
  onCollapse: () => void
  /** Hidden while another page is open. The page stays mounted, so its state survives a switch. */
  hidden: boolean
  children: ReactNode
}

/**
 * One right-dock page: a 44 px header (name, scope, refresh, collapse) over a body that fills the
 * rest. The header is what the four page-owned dock headers used to be, so the pages below it carry
 * content only (ADR-0015).
 */
export default function RightDockPage({ title, scope, chip = null, onRefresh, onCollapse, hidden, children }: Props) {
  return (
    <div
      hidden={hidden}
      className="flex h-full min-h-0 w-full flex-col border-l bg-sidebar"
      style={{ borderColor: 'var(--border)' }}
    >
      <div
        className="flex h-11 shrink-0 items-center gap-2 border-b px-3"
        style={{ borderColor: 'var(--border)' }}
      >
        <h2 className="shrink-0 text-[13px] font-semibold">{title}</h2>
        {scope && (
          <span className="flex min-w-0 items-center gap-1 text-[11px] text-muted-foreground">
            <GitBranch className="h-3 w-3 shrink-0 text-chart-4" />
            <span className="truncate font-mono">{scope}</span>
          </span>
        )}
        {chip && (
          <span className="shrink-0 rounded bg-amber-500/10 px-1.5 py-0.5 font-mono text-[10px] text-amber-500" data-testid="right-dock-page-chip">
            {chip}
          </span>
        )}
        <div className="ml-auto flex shrink-0 items-center gap-0.5">
          {onRefresh && (
            <button
              type="button"
              className="icon-button h-6 w-6"
              aria-label={`Refresh ${title}`}
              title={`Refresh ${title}`}
              onClick={onRefresh}
            >
              <RefreshCw className="h-3 w-3" />
            </button>
          )}
          <button
            type="button"
            className="icon-button h-6 w-6"
            aria-label={`Collapse ${title} to the rail`}
            title="Collapse to the rail — the rail stays"
            onClick={onCollapse}
          >
            <PanelRightClose className="h-3 w-3" />
          </button>
        </div>
      </div>
      <div className="flex min-h-0 flex-1 flex-col">{children}</div>
    </div>
  )
}
