import { useState } from 'react'
import { ChevronDown } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'

export type TaskBriefModel = {
  goal?: string
  expectedResult?: string
  details?: string
  elementRefs?: string[]
}

type Props = {
  taskTitle: string
  brief: TaskBriefModel
}

export default function TaskBriefDisclosure({ taskTitle, brief }: Props) {
  const [open, setOpen] = useState(false)
  const hasBrief = Boolean(brief.goal || brief.expectedResult || brief.details || brief.elementRefs?.length)

  return (
    <Collapsible open={open} onOpenChange={setOpen} className="border-t">
      <div className="px-3 py-1.5">
        <CollapsibleTrigger asChild>
          <Button type="button" variant="ghost" size="xs" aria-label={`${open ? 'Hide' : 'View'} brief for ${taskTitle}`}>
            {open ? 'Hide brief' : 'View brief'}
            <ChevronDown className={`h-3.5 w-3.5 transition-transform ${open ? 'rotate-180' : ''}`} />
          </Button>
        </CollapsibleTrigger>
      </div>
      <CollapsibleContent className="border-t bg-muted/30 px-4 py-3 text-xs">
        {hasBrief ? <div className="grid gap-3 sm:grid-cols-2">
          {brief.goal && <div className="min-w-0"><div className="font-medium text-muted-foreground">Goal</div><p className="mt-1 whitespace-pre-wrap break-words leading-5">{brief.goal}</p></div>}
          {brief.expectedResult && <div className="min-w-0"><div className="font-medium text-muted-foreground">Expected result</div><p className="mt-1 whitespace-pre-wrap break-words leading-5">{brief.expectedResult}</p></div>}
          {brief.details && <div className="min-w-0 sm:col-span-2"><div className="font-medium text-muted-foreground">Details</div><div className="mt-1 break-words leading-5 [&_p]:my-1 [&_ul]:list-disc [&_ul]:pl-4"><ReactMarkdown remarkPlugins={[remarkGfm]}>{brief.details}</ReactMarkdown></div></div>}
          {brief.elementRefs && brief.elementRefs.length > 0 && <div className="flex flex-wrap gap-1.5 sm:col-span-2">{brief.elementRefs.map(ref => <Badge key={ref} variant="outline" className="font-mono">{ref}</Badge>)}</div>}
        </div> : <p className="text-muted-foreground">No brief recorded for this task.</p>}
      </CollapsibleContent>
    </Collapsible>
  )
}
