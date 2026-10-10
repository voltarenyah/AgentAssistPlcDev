import type { LucideIcon } from 'lucide-react'

type Props = {
  icon: LucideIcon
  /** What the page cannot show, in a few words. */
  title: string
  /** What the user has to select for the page to have something to say. */
  description: string
  /** The scope the page is describing right now, when there is one. */
  scope?: string | null
}

/**
 * The one empty state every right-dock page uses.
 *
 * The rail is on every surface (ADR-0015), so an empty page is a normal state rather than an error:
 * it has to say what the page is for, what to select, and which scope it is looking at — a bare muted
 * line centred in a 266 x 600 panel reads as a broken page instead.
 */
export default function RightDockEmptyState({ icon: Icon, title, description, scope = null }: Props) {
  return (
    <div className="grid h-full place-items-center px-5 py-6">
      <div className="flex max-w-[240px] flex-col items-center text-center">
        <span className="grid h-9 w-9 place-items-center rounded-full border bg-background text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
          <Icon className="h-4 w-4" />
        </span>
        <h3 className="mt-3 text-[13px] font-semibold">{title}</h3>
        <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">{description}</p>
        {scope && (
          <span className="mt-3 max-w-full truncate rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
            {scope}
          </span>
        )}
      </div>
    </div>
  )
}
