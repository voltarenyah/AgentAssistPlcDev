import { useMemo, useState } from 'react'
import type { TagNode } from '@/api/client'
import { Button } from '@/components/ui/button'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { showErrorToast } from '@/components/ui/toast'
import TagTree from './TagTree'
import { tagPaths, validTagPath } from './tagPaths'

export type TagPickerProps = {
  nodes: TagNode[]
  currentTagIds?: string[]
  onAssign: (tagId: string) => Promise<void> | void
  onCreate?: (path: string) => Promise<TagNode> | TagNode
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  disabled?: boolean
}

export function TagPicker({ nodes, currentTagIds = [], onAssign, onCreate, loading = false, error, onRetry, disabled = false }: TagPickerProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [expandedIds, setExpandedIds] = useState<string[]>([])
  const paths = useMemo(() => tagPaths(nodes), [nodes])
  const current = new Set(currentTagIds)
  const normalizedQuery = query.trim().toLowerCase()
  const normalizedPath = validTagPath(query)
  const existing = normalizedPath ? nodes.find(node => paths.get(node.tagId)?.toLowerCase() === normalizedPath.toLowerCase()) : undefined
  const canCreate = Boolean(normalizedPath && !existing && onCreate)

  const assign = async (tagId: string) => {
    try {
      await onAssign(tagId)
      setQuery('')
      setOpen(false)
    } catch (caught) {
      showErrorToast(caught instanceof Error ? caught.message : 'Tag could not be assigned')
    }
  }

  const create = async () => {
    if (!normalizedPath || !onCreate) return
    try {
      const node = await onCreate(normalizedPath)
      await onAssign(node.tagId)
      setQuery('')
      setOpen(false)
    } catch (caught) {
      showErrorToast(caught instanceof Error ? caught.message : 'Tag could not be created')
    }
  }

  return (
    <>
      <Button type="button" variant="outline" size="xs" disabled={disabled || loading} onClick={() => setOpen(true)} aria-label={loading ? 'Add tag (loading)' : 'Add tag'}>
        {loading ? 'Loading tags…' : 'Add tag'}
      </Button>
      {error && <div role="alert" className="mt-1 text-xs text-destructive">{error}{onRetry && <Button type="button" variant="link" size="xs" onClick={onRetry}>Retry</Button>}</div>}
      <CommandDialog open={open} onOpenChange={setOpen} title="Add tag" description="Search or browse the tag taxonomy" shouldFilter={false}>
        <CommandInput value={query} onValueChange={setQuery} onInput={event => setQuery(event.currentTarget.value)} placeholder="Search tags or enter a/path" aria-label="Search tags" />
        <CommandList>
          {canCreate && <CommandItem value={normalizedPath!} onSelect={() => void create()} aria-label={`Create tag ${normalizedPath}`}>Create “{normalizedPath}”</CommandItem>}
          {normalizedQuery.length === 0 ? (
            <div className="p-2">
              <TagTree
                nodes={nodes}
                expandedIds={expandedIds}
                onExpandedChange={setExpandedIds}
                onSelect={node => { if (!current.has(node.tagId)) void assign(node.tagId) }}
                selectedTagIds={currentTagIds}
              />
            </div>
          ) : (
            <>
              <CommandEmpty>No matching tags.</CommandEmpty>
              {nodes.filter(node => !current.has(node.tagId) && paths.get(node.tagId)?.toLowerCase().includes(normalizedQuery)).map(node => (
                <CommandItem key={node.tagId} value={paths.get(node.tagId)} onSelect={() => void assign(node.tagId)}>{paths.get(node.tagId)}</CommandItem>
              ))}
            </>
          )}
        </CommandList>
      </CommandDialog>
    </>
  )
}

export default TagPicker
