import {
  Command,
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
} from '@/components/ui/command'
import { Button } from '@/components/ui/button'
import { Calculator, Calendar, CreditCard, Settings, Smile, User } from 'lucide-react'
import { useState } from 'react'

function CommandDemo() {
  return (
    <Command className="w-80 rounded-lg border shadow-md">
      <CommandInput placeholder="Type a command..." />
      <CommandList>
        <CommandEmpty>No results found.</CommandEmpty>
        <CommandGroup heading="Suggestions">
          <CommandItem>
            <Calendar className="mr-2 size-4" />
            <span>Calendar</span>
          </CommandItem>
          <CommandItem>
            <Smile className="mr-2 size-4" />
            <span>Search Emoji</span>
          </CommandItem>
          <CommandItem>
            <Calculator className="mr-2 size-4" />
            <span>Calculator</span>
          </CommandItem>
        </CommandGroup>
        <CommandSeparator />
        <CommandGroup heading="Settings">
          <CommandItem>
            <User className="mr-2 size-4" />
            <span>Profile</span>
          </CommandItem>
          <CommandItem>
            <CreditCard className="mr-2 size-4" />
            <span>Billing</span>
          </CommandItem>
          <CommandItem>
            <Settings className="mr-2 size-4" />
            <span>Settings</span>
          </CommandItem>
        </CommandGroup>
      </CommandList>
    </Command>
  )
}

export default function CommandPage() {
  const [dialogOpen, setDialogOpen] = useState(false)

  return (
    <div className="space-y-12">
      <div>
        <h2 className="mb-4 text-lg font-semibold">Command Palette</h2>
        <CommandDemo />
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Command Dialog</h2>
        <Button variant="outline" onClick={() => setDialogOpen(true)}>
          Open Command Palette
        </Button>
        <CommandDialog open={dialogOpen} onOpenChange={setDialogOpen}>
          <CommandInput placeholder="Type a command..." />
          <CommandList>
            <CommandEmpty>No results found.</CommandEmpty>
            <CommandGroup heading="Suggestions">
              <CommandItem onSelect={() => setDialogOpen(false)}>
                <Calendar className="mr-2 size-4" />
                <span>Calendar</span>
              </CommandItem>
              <CommandItem onSelect={() => setDialogOpen(false)}>
                <User className="mr-2 size-4" />
                <span>Profile</span>
              </CommandItem>
            </CommandGroup>
          </CommandList>
        </CommandDialog>
      </div>
    </div>
  )
}
