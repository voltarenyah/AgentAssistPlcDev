import { X } from 'lucide-react'
import type { TagNode } from '@/api/client'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { tagPaths } from './tagPaths'

export type TagChipProps = {
  node: TagNode
  nodes?: TagNode[]
  removable?: boolean
  inherited?: boolean
  onRemove?: (tagId: string) => void
  className?: string
}

export function TagChip({ node, nodes = [node], removable = false, inherited = false, onRemove, className }: TagChipProps) {
  const path = tagPaths(nodes).get(node.tagId) ?? node.name
  const canRemove = removable && !inherited && Boolean(onRemove)

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-full border bg-muted/40 px-2 py-1 text-xs text-foreground',
        inherited ? 'border-dashed border-muted-foreground/40' : 'border-border',
        className,
      )}
      data-tag-id={node.tagId}
    >
      <span>{path}</span>
      {inherited && <span className="text-[10px] text-muted-foreground">Inherited from Project</span>}
      {canRemove && (
        <Button
          type="button"
          variant="ghost"
          size="icon-xs"
          aria-label={`Remove tag ${path}`}
          onClick={() => onRemove?.(node.tagId)}
        >
          <X aria-hidden="true" />
        </Button>
      )}
    </span>
  )
}

export default TagChip
