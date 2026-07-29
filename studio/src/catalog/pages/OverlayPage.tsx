import { Button } from '@/components/ui/button'
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from '@/components/ui/tooltip'
import {
  HoverCard,
  HoverCardContent,
  HoverCardTrigger,
} from '@/components/ui/hover-card'
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from '@/components/ui/popover'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuTrigger,
} from '@/components/ui/context-menu'

export default function OverlayPage() {
  return (
    <TooltipProvider>
      <div className="space-y-12">
        <div>
          <h2 className="mb-4 text-lg font-semibold">Tooltip</h2>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="outline">Hover me</Button>
            </TooltipTrigger>
            <TooltipContent>This is a tooltip</TooltipContent>
          </Tooltip>
        </div>

        <div>
          <h2 className="mb-4 text-lg font-semibold">Hover Card</h2>
          <HoverCard>
            <HoverCardTrigger asChild>
              <Button variant="outline">Hover for details</Button>
            </HoverCardTrigger>
            <HoverCardContent className="w-64">
              <div className="flex flex-col gap-2">
                <h3 className="font-semibold">Orca UI</h3>
                <p className="text-sm text-muted-foreground">
                  shadcn UI components extracted from the Orca design system.
                </p>
              </div>
            </HoverCardContent>
          </HoverCard>
        </div>

        <div>
          <h2 className="mb-4 text-lg font-semibold">Popover</h2>
          <Popover>
            <PopoverTrigger asChild>
              <Button variant="outline">Open popover</Button>
            </PopoverTrigger>
            <PopoverContent className="w-64">
              <div className="flex flex-col gap-2">
                <h3 className="font-semibold">Popover Content</h3>
                <p className="text-sm text-muted-foreground">
                  This is a popover with some content inside.
                </p>
              </div>
            </PopoverContent>
          </Popover>
        </div>

        <div>
          <h2 className="mb-4 text-lg font-semibold">Dropdown Menu</h2>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="outline">Open menu</Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent>
              <DropdownMenuLabel>My Account</DropdownMenuLabel>
              <DropdownMenuSeparator />
              <DropdownMenuItem>Profile</DropdownMenuItem>
              <DropdownMenuItem>Billing</DropdownMenuItem>
              <DropdownMenuItem>Team</DropdownMenuItem>
              <DropdownMenuItem>Subscription</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>

        <div>
          <h2 className="mb-4 text-lg font-semibold">Context Menu</h2>
          <ContextMenu>
            <ContextMenuTrigger asChild>
              <div className="flex h-32 w-64 items-center justify-center rounded-md border border-dashed text-sm text-muted-foreground">
                Right-click here
              </div>
            </ContextMenuTrigger>
            <ContextMenuContent>
              <ContextMenuItem>Cut</ContextMenuItem>
              <ContextMenuItem>Copy</ContextMenuItem>
              <ContextMenuItem>Paste</ContextMenuItem>
              <ContextMenu>...</ContextMenu>
            </ContextMenuContent>
          </ContextMenu>
        </div>
      </div>
    </TooltipProvider>
  )
}
