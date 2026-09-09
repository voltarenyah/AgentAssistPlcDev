import { Check, ChevronDown, ChevronRight } from 'lucide-react'
import type { TagNode } from '@/api/client'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { tagPaths } from './tagPaths'

export type TagTreeProps = {
  nodes: TagNode[]
  expandedIds?: Iterable<string>
  onExpandedChange?: (expandedIds: string[]) => void
  onSelect?: (node: TagNode) => void
  selectedTagIds?: Iterable<string>
  className?: string
}

export function TagTree({ nodes, expandedIds = [], onExpandedChange, onSelect, selectedTagIds = [], className }: TagTreeProps) {
  const expanded = new Set(expandedIds)
  const selected = new Set(selectedTagIds)
  const children = new Map<string | null, TagNode[]>()
  for (const node of nodes) {
    const group = children.get(node.parentTagId) ?? []
    group.push(node)
    children.set(node.parentTagId, group)
  }
  const paths = tagPaths(nodes)
  const nodesById = new Map(nodes.map(node => [node.tagId, node]))
  const selectedAncestorIds = new Set<string>()
  for (const selectedTagId of selected) {
    let parentTagId = nodesById.get(selectedTagId)?.parentTagId
    while (parentTagId) {
      selectedAncestorIds.add(parentTagId)
      parentTagId = nodesById.get(parentTagId)?.parentTagId
    }
  }

  const toggle = (tagId: string) => {
    const next = new Set(expanded)
    if (next.has(tagId)) next.delete(tagId)
    else next.add(tagId)
    onExpandedChange?.([...next])
  }

  const render = (parentTagId: string | null, depth: number): React.ReactNode => (
    <div role={depth === 0 ? 'group' : undefined}>
      {(children.get(parentTagId) ?? []).map(node => {
        const childNodes = children.get(node.tagId) ?? []
        const hasChildren = childNodes.length > 0
        const isExpanded = expanded.has(node.tagId) || selectedAncestorIds.has(node.tagId)
        const isSelected = selected.has(node.tagId)
        return (
          <div key={node.tagId} role="treeitem" aria-level={depth + 1} aria-expanded={hasChildren ? isExpanded : undefined} aria-selected={isSelected}>
            <div className={cn('flex items-center gap-1 rounded-md px-1 py-0.5', isSelected && 'bg-accent text-accent-foreground')} style={{ paddingLeft: `${depth * 16 + 4}px` }}>
              {hasChildren ? (
                <Button type="button" variant="ghost" size="icon-xs" aria-label={`${isExpanded ? 'Collapse' : 'Expand'} ${paths.get(node.tagId) ?? node.name}`} onClick={() => toggle(node.tagId)}>
                  {isExpanded ? <ChevronDown aria-hidden="true" /> : <ChevronRight aria-hidden="true" />}
                </Button>
              ) : <span className="size-6" aria-hidden="true" />}
              <button type="button" className="min-w-0 flex-1 truncate rounded px-1 py-1 text-left text-sm hover:bg-accent" onClick={() => onSelect?.(node)} aria-label={`Select ${paths.get(node.tagId) ?? node.name}`}>
                {paths.get(node.tagId) ?? node.name}
              </button>
              {isSelected && <Check className="size-4 shrink-0" aria-hidden="true" />}
            </div>
            {hasChildren && isExpanded && render(node.tagId, depth + 1)}
          </div>
        )
      })}
    </div>
  )

  return <div role="tree" className={cn('space-y-0.5', className)}>{render(null, 0)}</div>
}

export default TagTree
