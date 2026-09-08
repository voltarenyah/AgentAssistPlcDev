import { useMemo, useState } from 'react'
import type { TagNode } from '@/api/client'
import { Button } from '@/components/ui/button'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import TagChip from './TagChip'
import { tagPaths } from './tagPaths'

export type TagFilterProps = {
  nodes: TagNode[]
  selectedTagIds: string[]
  onSelectedTagIdsChange: (tagIds: string[]) => void
  loading?: boolean
  error?: string | null
  onRetry?: () => void
}

export function TagFilter({
  nodes,
  selectedTagIds,
  onSelectedTagIdsChange,
  loading = false,
  error,
  onRetry,
}: TagFilterProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const paths = useMemo(() => tagPaths(nodes), [nodes])
  const selected = new Set(selectedTagIds)
  const normalizedQuery = query.trim().toLowerCase()
  const selectableNodes = nodes.filter(node => !selected.has(node.tagId)
    && (normalizedQuery.length === 0 || (paths.get(node.tagId) ?? node.name).toLowerCase().includes(normalizedQuery)))

  const select = (tagId: string) => {
    onSelectedTagIdsChange([...selectedTagIds, tagId])
    setQuery('')
    setOpen(false)
  }

  const remove = (tagId: string) => {
    onSelectedTagIdsChange(selectedTagIds.filter(selectedTagId => selectedTagId !== tagId))
  }

  return (
    <div className="border-b px-2 py-2" style={{ borderColor: 'var(--border)' }}>
      <div role="list" aria-label="Active tag filters" className="flex flex-wrap items-center gap-1">
        {nodes.filter(node => selected.has(node.tagId)).map(node => (
          <TagChip key={node.tagId} node={node} nodes={nodes} removable onRemove={remove} />
        ))}
        <Button
          type="button"
          variant="outline"
          size="xs"
          disabled={loading}
          aria-label={loading ? 'Filter tags (loading)' : 'Filter tags'}
          onClick={() => setOpen(true)}
        >
          {loading ? 'Loading tags…' : 'Filter tags'}
        </Button>
      </div>
      {error && (
        <div role="alert" className="mt-1 text-xs text-destructive">
          {error}{onRetry && <Button type="button" variant="link" size="xs" onClick={onRetry}>Retry</Button>}
        </div>
      )}
      <CommandDialog open={open} onOpenChange={setOpen} title="Filter projects" description="Search tags to filter projects and worktrees" shouldFilter={false}>
        <CommandInput
          value={query}
          onValueChange={setQuery}
          onInput={event => setQuery(event.currentTarget.value)}
          placeholder="Search tags"
          aria-label="Search filter tags"
        />
        <CommandList>
          <CommandEmpty>No matching tags.</CommandEmpty>
          {selectableNodes.map(node => {
            const path = paths.get(node.tagId) ?? node.name
            return <CommandItem key={node.tagId} value={path} onSelect={() => select(node.tagId)} aria-label={`Filter by ${path}`}>{path}</CommandItem>
          })}
        </CommandList>
      </CommandDialog>
    </div>
  )
}

export default TagFilter
