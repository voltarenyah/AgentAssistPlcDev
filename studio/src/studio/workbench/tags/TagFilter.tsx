import { useMemo, useState } from 'react'
import type { TagNode } from '@/api/client'
import { ListFilter } from 'lucide-react'
import {
  Autocomplete,
  AutocompleteContent,
  AutocompleteEmpty,
  AutocompleteInput,
  AutocompleteInputGroup,
  AutocompleteItem,
  AutocompleteList,
  Button,
} from '@notion-kit/ui/primitives'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
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

/** Tags that can still be added, optionally narrowed by a query. Previously this rule was written
 *  out twice - once for the inline list and once for the taxonomy dialog - with the two copies
 *  differing only in how they handled an empty query. Exported because notion-kit's autocomplete
 *  owns its own item collection, so the rule will need to be fed to it as data, and because a
 *  direct assertion on the rule is stronger than asserting through cmdk's rendered list. */
export const filterableTags = (
  nodes: TagNode[],
  paths: Map<string, string>,
  selectedTagIds: string[],
  query: string,
) => {
  const selected = new Set(selectedTagIds)
  const normalized = query.trim().toLowerCase()
  return nodes.filter(node => !selected.has(node.tagId)
    && (normalized.length === 0 || (paths.get(node.tagId) ?? node.name).toLowerCase().includes(normalized)))
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
  const [dialogQuery, setDialogQuery] = useState('')
  const [expandedIds, setExpandedIds] = useState<string[]>([])
  const paths = useMemo(() => tagPaths(nodes), [nodes])
  const selected = new Set(selectedTagIds)
  const selectedNodes = nodes.filter(node => selected.has(node.tagId))
  // No query argument: notion-kit's Autocomplete owns the filtering, so this is its item source -
  // every unselected tag - rather than a pre-filtered list.
  const selectableNodes = filterableTags(nodes, paths, selectedTagIds, '')

  const select = (tagId: string) => {
    onSelectedTagIdsChange([...selectedTagIds, tagId])
    setOpen(false)
  }

  const remove = (tagId: string) => {
    onSelectedTagIdsChange(selectedTagIds.filter(selectedTagId => selectedTagId !== tagId))
  }

  return (
    <div className="border-b px-2 py-2" style={{ borderColor: 'var(--border)' }}>
      <div className="flex items-stretch gap-1">
        {/* Selection is handled by each item's own click rather than the root's onValueChange.
            That callback fires while typing, because Base UI's autocomplete highlights a match as
            the query narrows - so using it for selection would add a tag the moment the typed
            prefix matched one, which cmdk never did. The item click keeps the original semantics:
            a tag is added only when it is explicitly chosen. */}
        <Autocomplete
          items={selectableNodes.map(node => {
            const path = paths.get(node.tagId) ?? node.name
            return { value: path, label: path }
          })}
          disabled={loading}
        >
          <AutocompleteInputGroup className="min-w-0 flex-1">
            <AutocompleteInput
              placeholder={loading ? 'Loading tags…' : 'Filter tags'}
              aria-label="Search filter tags"
              disabled={loading}
              className="h-8!"
            />
          </AutocompleteInputGroup>
          <AutocompleteContent>
            <AutocompleteEmpty>No matching tags.</AutocompleteEmpty>
            <AutocompleteList>
              {selectableNodes.map(node => {
                const path = paths.get(node.tagId) ?? node.name
                return <AutocompleteItem key={node.tagId} value={path} aria-label={`Filter by ${path}`} onClick={() => select(node.tagId)}>{path}</AutocompleteItem>
              })}
            </AutocompleteList>
          </AutocompleteContent>
        </Autocomplete>
        <Button
          type="button"
          variant="primary"
          size="sm"
          className="w-8! shrink-0 justify-center px-0!"
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
      {selectedNodes.length > 0 && (
        <div role="list" aria-label="Active tag filters" className="mt-2 flex flex-wrap items-center gap-1">
          {selectedNodes.map(node => (
            <TagChip key={node.tagId} node={node} nodes={nodes} removable onRemove={remove} />
          ))}
        </div>
      )}
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
              {filterableTags(nodes, paths, selectedTagIds, dialogQuery).map(node => {
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
