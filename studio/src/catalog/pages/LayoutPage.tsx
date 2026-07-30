import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from '@/components/ui/accordion'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ScrollArea } from '@/components/ui/scroll-area'
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from '@/components/ui/collapsible'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'

export default function LayoutPage() {
  return (
    <div className="space-y-12">
      <div>
        <h2 className="mb-4 text-lg font-semibold">Card</h2>
        <Card className="w-80">
          <CardHeader>
            <CardTitle>Card Title</CardTitle>
            <CardDescription>Card Description</CardDescription>
          </CardHeader>
          <CardContent>
            <p>Card content goes here.</p>
          </CardContent>
          <CardFooter>
            <Button>Action</Button>
          </CardFooter>
        </Card>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Separator</h2>
        <div className="flex flex-col gap-3">
          <div className="text-sm">Content above</div>
          <Separator />
          <div className="text-sm">Content below</div>
          <Separator orientation="vertical" className="mx-auto h-8" />
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Accordion</h2>
        <Accordion type="single" collapsible className="w-80">
          <AccordionItem value="item-1">
            <AccordionTrigger>What is Orca UI?</AccordionTrigger>
            <AccordionContent>
              A component catalog extracted from the Orca design system.
            </AccordionContent>
          </AccordionItem>
          <AccordionItem value="item-2">
            <AccordionTrigger>Is it production ready?</AccordionTrigger>
            <AccordionContent>
              These are standard shadcn/ui components with the Orca theme applied.
            </AccordionContent>
          </AccordionItem>
        </Accordion>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Tabs</h2>
        <Tabs defaultValue="tab1" className="w-80">
          <TabsList>
            <TabsTrigger value="tab1">Tab 1</TabsTrigger>
            <TabsTrigger value="tab2">Tab 2</TabsTrigger>
            <TabsTrigger value="tab3">Tab 3</TabsTrigger>
          </TabsList>
          <TabsContent value="tab1" className="text-sm text-muted-foreground">
            Content for tab 1.
          </TabsContent>
          <TabsContent value="tab2" className="text-sm text-muted-foreground">
            Content for tab 2.
          </TabsContent>
          <TabsContent value="tab3" className="text-sm text-muted-foreground">
            Content for tab 3.
          </TabsContent>
        </Tabs>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Scroll Area</h2>
        <ScrollArea className="h-32 w-80 rounded-md border p-4">
          <div className="space-y-2">
            {Array.from({ length: 20 }, (_, i) => (
              <p key={i} className="text-sm">
                Row {i + 1}
              </p>
            ))}
          </div>
        </ScrollArea>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Collapsible</h2>
        <Collapsible className="w-80 space-y-2">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-medium">Show details</h3>
            <CollapsibleTrigger asChild>
              <Button variant="ghost" size="sm">Toggle</Button>
            </CollapsibleTrigger>
          </div>
          <CollapsibleContent className="text-sm text-muted-foreground space-y-1">
            <p>Detail line 1</p>
            <p>Detail line 2</p>
            <p>Detail line 3</p>
          </CollapsibleContent>
        </Collapsible>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Sheet</h2>
        <Sheet>
          <SheetTrigger asChild>
            <Button variant="outline">Open Sheet</Button>
          </SheetTrigger>
          <SheetContent>
            <SheetHeader>
              <SheetTitle>Sheet Title</SheetTitle>
              <SheetDescription>
                This is a sheet with some content.
              </SheetDescription>
            </SheetHeader>
            <div className="py-4 text-sm text-muted-foreground">
              Sheet body content goes here.
            </div>
            <SheetFooter>
              <Button>Save</Button>
            </SheetFooter>
          </SheetContent>
        </Sheet>
      </div>
    </div>
  )
}
