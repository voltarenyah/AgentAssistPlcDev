import { TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { RIGHT_DOCK_PAGES, type RightDockPage } from '@/studio/shellLayout'
import { RIGHT_DOCK_PAGE_ICONS, RIGHT_DOCK_PAGE_LABELS } from './rightDockPageMeta'

type Props = {
  /** The page the rail has open, or null while the page is collapsed to the rail. */
  openPage: RightDockPage | null
  /** The selected worktree's uncommitted object count, or null when there is nothing to badge. */
  changesBadge?: number | null
  /** Opens the clicked page, or collapses it when it is already the open one. */
  onSelectPage: (page: RightDockPage) => void
}

/**
 * The dock's pages as an icon rail: 44 px on the column's outer edge, one item per page, a vertical
 * tablist so `↑`/`↓` and `aria-selected` come from the primitive. Clicking the open page's own item
 * collapses the page; the click handler therefore owns the gesture and the value change only covers
 * moving between pages (ADR-0015).
 */
export default function RightDockRail({ openPage, changesBadge = null, onSelectPage }: Props) {
  return (
    <TooltipProvider>
      <TabsList
        variant="line"
        aria-label="Right dock pages"
        data-dock-content="right-rail"
        className="h-full w-11 flex-none flex-col items-center gap-1 rounded-none border-l bg-sidebar p-0 pt-2"
        style={{ borderColor: 'var(--border)' }}
      >
        {RIGHT_DOCK_PAGES.map(page => {
          const Icon = RIGHT_DOCK_PAGE_ICONS[page]
          const label = RIGHT_DOCK_PAGE_LABELS[page]
          const open = openPage === page
          const badge = page === 'changes' ? changesBadge : null
          return (
            <Tooltip key={page}>
              <TooltipTrigger asChild>
                <TabsTrigger
                  value={page}
                  aria-label={label}
                  data-testid={`right-dock-rail-${page}`}
                  data-open={open}
                  onClick={() => onSelectPage(page)}
                  className={cn(
                    'relative h-[30px] w-[30px] flex-none rounded-md border-0 p-0',
                    'text-muted-foreground hover:bg-accent/50 hover:text-foreground',
                    'data-[state=active]:bg-accent data-[state=active]:text-foreground',
                    'dark:data-[state=active]:bg-accent dark:data-[state=active]:text-foreground',
                    'group-data-[variant=line]/tabs-list:data-[state=active]:bg-accent',
                    'dark:group-data-[variant=line]/tabs-list:data-[state=active]:bg-accent',
                    'after:hidden',
                  )}
                >
                  <Icon className="h-4 w-4" />
                  {badge !== null && badge > 0 && (
                    <span
                      data-testid="right-dock-rail-changes-badge"
                      className="absolute -right-0.5 -bottom-0.5 grid h-[15px] min-w-[15px] place-items-center rounded-full bg-chart-3 px-1 font-mono text-[9px] font-semibold text-white ring-2 ring-sidebar"
                    >
                      {badge > 99 ? '99+' : badge}
                    </span>
                  )}
                </TabsTrigger>
              </TooltipTrigger>
              <TooltipContent side="left">{open ? `Hide ${label}` : label}</TooltipContent>
            </Tooltip>
          )
        })}
      </TabsList>
    </TooltipProvider>
  )
}
