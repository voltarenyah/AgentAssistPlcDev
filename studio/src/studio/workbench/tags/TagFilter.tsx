import { useMemo, useState } from 'react'
import type { TagNode } from '@/api/client'
import { ListFilter } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Command, CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import TagChip from './TagChip'
import TagTree from './TagTree'
import { tagPaths } from './tagPaths'

export type TagFilterProps = {
  nodes: TagNode[]
  selectedTagIds: string[]
  onSelectedTagIdsChange: (tagIds: string[]) => void
  onOpen?: () => void
  loading?: boolean
  error?: string | null
  onRetry?: () => void
}

export function TagFilter({
  nodes,
  selectedTagIds,
  onSelectedTagIdsChange,
  onOpen,
  loading = false,
  error,
  onRetry,
}: TagFilterProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [dialogQuery, setDialogQuery] = useState('')
  const [expandedIds, setExpandedIds] = useState<string[]>([])
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
      </div>
      <div className="mt-1 flex items-stretch gap-1">
        <Command shouldFilter={false} className="relative h-auto w-auto min-w-0 flex-1 overflow-visible bg-transparent">
          <CommandInput
            value={query}
            onValueChange={setQuery}
            onInput={event => setQuery(event.currentTarget.value)}
            placeholder={loading ? 'Loading tags…' : 'Filter tags'}
            aria-label="Search filter tags"
            disabled={loading}
            className="h-8 py-0"
            wrapperClassName="h-8 rounded-md border border-input bg-transparent px-2 py-0"
          />
          {normalizedQuery.length > 0 && (
            <CommandList className="absolute top-full z-10 mt-1 max-h-48 w-full rounded-md border border-border bg-popover p-1 shadow-md">
              <CommandEmpty>No matching tags.</CommandEmpty>
              {selectableNodes.map(node => {
                const path = paths.get(node.tagId) ?? node.name
                return <CommandItem key={node.tagId} value={path} onSelect={() => select(node.tagId)} aria-label={`Filter by ${path}`}>{path}</CommandItem>
              })}
            </CommandList>
          )}
        </Command>
        <Button
          type="button"
          variant="outline"
          size="icon-sm"
          className="shrink-0"
          disabled={loading}
          aria-label={loading ? 'Open tag taxonomy (loading)' : 'Open tag taxonomy'}
          onClick={() => {
            onOpen?.()
            setDialogQuery('')
            setOpen(true)
          }}
        >
          <ListFilter aria-hidden="true" />
        </Button>
      </div>
      {error && (
        <div role="alert" className="mt-1 text-xs text-destructive">
          {error}{onRetry && <Button type="button" variant="link" size="xs" onClick={onRetry}>Retry</Button>}
        </div>
      )}
      <CommandDialog open={open} onOpenChange={setOpen} title="Filter projects" description="Search tags to filter projects and worktrees" shouldFilter={false}>
        <CommandInput
          value={dialogQuery}
          onValueChange={setDialogQuery}
          onInput={event => setDialogQuery(event.currentTarget.value)}
          placeholder="Search tags"
          aria-label="Search taxonomy tags"
        />
        <CommandList>
          {dialogQuery.trim().toLowerCase().length === 0 ? (
            <div className="p-2">
              <TagTree
                nodes={nodes}
                expandedIds={expandedIds}
                onExpandedChange={setExpandedIds}
                onSelect={node => { if (!selected.has(node.tagId)) select(node.tagId) }}
                selectedTagIds={selectedTagIds}
              />
            </div>
          ) : (
            <>
              <CommandEmpty>No matching tags.</CommandEmpty>
              {nodes.filter(node => !selected.has(node.tagId)
                && (paths.get(node.tagId) ?? node.name).toLowerCase().includes(dialogQuery.trim().toLowerCase())).map(node => {
                const path = paths.get(node.tagId) ?? node.name
                return <CommandItem key={node.tagId} value={path} onSelect={() => select(node.tagId)} aria-label={`Filter by ${path}`}>{path}</CommandItem>
              })}
            </>
          )}
        </CommandList>
      </CommandDialog>
    </div>
  )
}

export default TagFilter
