import { useRef, type ReactNode } from 'react'
import { Tabs, TabsContent } from '@/components/ui/tabs'
import { RIGHT_DOCK_PAGES, RIGHT_DOCK_RAIL_WIDTH, type RightDockPage } from '@/studio/shellLayout'
import RightDockPageFrame from './RightDockPage'
import RightDockRail from './RightDockRail'
import { RIGHT_DOCK_PAGE_LABELS } from './rightDockPageMeta'

type Props = {
  /** The open page, or null while the page is collapsed to the rail. */
  page: RightDockPage | null
  /** The page the rail keeps marked while collapsed. */
  markedPage: RightDockPage
  /** The open page's width; the rail adds its own fixed width to the column. */
  pageWidth: number
  /** Whether the whole column is shown. The title bar's toggle owns it. */
  columnOpen?: boolean
  /** The selected worktree's uncommitted object count, or null when there is nothing to badge. */
  changesBadge?: number | null
  /** The one-line scope each page's header shows. */
  scopes?: Partial<Record<RightDockPage, string | null>>
  /** A short piece of page state each page's header shows, such as the uncommitted count. */
  chips?: Partial<Record<RightDockPage, string | null>>
  onSelectPage: (page: RightDockPage) => void
  onRefresh?: (page: RightDockPage) => void
  pages: Record<RightDockPage, ReactNode>
}

/**
 * The right dock's column: the rail on the outer edge, and one page open beside it.
 *
 * The column is a vertical tablist, which is what gives the rail its keyboard contract and the pages
 * their `tabpanel` wiring. A page mounts the first time it is opened and stays mounted afterwards, so
 * switching pages neither refetches a page nobody has opened nor remounts the version-control surface
 * (which would re-run a TIA comparison). The page keeps its target width inside a clipping track, so
 * collapsing narrows the column without reflowing the page's content mid-flight (ADR-0015).
 */
export default function RightDock({
  page,
  markedPage,
  pageWidth,
  columnOpen = true,
  changesBadge = null,
  scopes = {},
  chips = {},
  onSelectPage,
  onRefresh,
  pages,
}: Props) {
  // Mutating during render is deliberate: the render that first shows a page is the render that
  // mounts it, and re-rendering for the bookkeeping alone would mount it one paint later.
  const opened = useRef<Set<RightDockPage>>(new Set<RightDockPage>())
  if (page) opened.current.add(page)

  const collapsed = page === null
  const width = columnOpen ? RIGHT_DOCK_RAIL_WIDTH + (collapsed ? 0 : pageWidth) : 0
  return (
    <Tabs
      orientation="vertical"
      value={page ?? markedPage}
      // No onValueChange: the rail's own click handler is the single activation source, because
      // Radix also activates on focus, and a controlled value that both paths wrote would toggle the
      // page twice (open, then straight back to collapsed). Activation stays manual — `↑`/`↓` move
      // between items and Enter/Space activates, which is the contract the UI spec states.
      data-dock="right"
      data-dock-state={columnOpen ? 'open' : 'closed'}
      data-page-state={collapsed ? 'collapsed' : 'open'}
      // A hidden column keeps its element so its width can animate; nothing inside may be reached.
      inert={!columnOpen || undefined}
      className="dock-shell dock-shell-right min-h-0 shrink-0 flex-row items-stretch gap-0 bg-sidebar"
      style={{ width }}
    >
      <div
        className="dock-page-track min-h-0 flex-1"
        inert={collapsed || undefined}
        aria-hidden={collapsed || undefined}
      >
        {RIGHT_DOCK_PAGES.map(candidate => (
          <TabsContent
            key={candidate}
            value={candidate}
            forceMount
            // `forceMount` keeps every page mounted — which is what stops a switch from remounting the
            // version-control surface — but it also leaves every wrapper in the layout. Three 266 px
            // boxes in a 266 px track packed to its right edge put the *open* page outside the clip
            // unless it happens to be the last one, so the wrapper is hidden explicitly. Measured in
            // the running app before this: the open Properties page sat at x=438 in a track at x=970.
            hidden={page !== candidate}
            data-testid={`right-dock-page-${candidate}`}
            className="m-0 h-full min-h-0 flex-none outline-none"
            style={{ width: pageWidth }}
          >
            <RightDockPageFrame
              title={RIGHT_DOCK_PAGE_LABELS[candidate]}
              scope={scopes[candidate] ?? null}
              chip={chips[candidate] ?? null}
              onRefresh={onRefresh ? () => onRefresh(candidate) : undefined}
              onCollapse={() => onSelectPage(candidate)}
              hidden={page !== candidate}
            >
              {opened.current.has(candidate) ? pages[candidate] : null}
            </RightDockPageFrame>
          </TabsContent>
        ))}
      </div>
      <RightDockRail
        openPage={page}
        markedPage={markedPage}
        changesBadge={changesBadge}
        onSelectPage={onSelectPage}
      />
    </Tabs>
  )
}
