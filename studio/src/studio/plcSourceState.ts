import type { OfflineBlockInfo, SourceObjectInfo } from '@/api/client'

// Pure state helpers for the PLC source browser panel (PlcSourcePanel).

export type SourceTypeFilter = 'all' | 'OB' | 'FB' | 'FC' | 'DB' | 'Tags' | 'UDT'

export const SOURCE_TYPE_FILTERS: { id: SourceTypeFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'OB', label: 'OB' },
  { id: 'FB', label: 'FB' },
  { id: 'FC', label: 'FC' },
  { id: 'DB', label: 'DB' },
  { id: 'Tags', label: 'Tag table' },
  { id: 'UDT', label: 'UDT' },
]

/** Fallback row shape when the export manifest is missing: blocks-only entries. */
export const blocksToSourceObjects = (blocks: OfflineBlockInfo[]): SourceObjectInfo[] =>
  blocks.map(block => ({
    id: block.id,
    name: block.name,
    number: block.number,
    category: block.blockType,
    programmingLanguage: block.programmingLanguage,
    groupPath: block.groupPath,
    relativePath: block.relativePath,
    contentHash: null,
    isKnowHowProtected: null,
    modifiedDate: null,
    status: null,
  }))

/** Items shown by the panel: manifest source objects, or blocks mapped as a fallback. */
export const resolveSourceObjects = (
  sourceObjects: SourceObjectInfo[] | null | undefined,
  blocks: OfflineBlockInfo[] | null | undefined,
): SourceObjectInfo[] => {
  if (sourceObjects && sourceObjects.length > 0) return sourceObjects
  return blocksToSourceObjects(blocks ?? [])
}

export const countSourceObjectsByType = (
  items: SourceObjectInfo[],
): Record<SourceTypeFilter, number> => {
  const counts: Record<SourceTypeFilter, number> = {
    all: items.length,
    OB: 0,
    FB: 0,
    FC: 0,
    DB: 0,
    Tags: 0,
    UDT: 0,
  }
  for (const item of items) {
    if (item.category in counts && item.category !== 'all') {
      counts[item.category as SourceTypeFilter] += 1
    }
  }
  return counts
}

export const filterSourceObjects = (
  items: SourceObjectInfo[],
  typeFilter: SourceTypeFilter,
  query: string,
): SourceObjectInfo[] => {
  const normalized = query.trim().toLowerCase()
  return items.filter(item => {
    if (typeFilter !== 'all' && item.category !== typeFilter) return false
    if (!normalized) return true
    return item.name.toLowerCase().includes(normalized)
      || item.relativePath.toLowerCase().includes(normalized)
      || `${item.category}${item.number ?? ''}`.toLowerCase().includes(normalized)
  })
}

/** Source object handed from the PLC source browser to the AI chat composer. */
export type SourceChatContext = {
  name: string
  category: string
  number: number | null
  relativePath: string
  plcName: string
}

/** Composer prefix so the agent knows the referenced source object without the user naming it. */
export const sourceContextPrefix = (context: SourceChatContext): string => {
  const identity = context.number != null ? `${context.category}${context.number}` : context.category
  return `[PLC source context: ${context.category} "${context.name}" (${identity}), path "${context.relativePath}", PLC "${context.plcName}"]`
}
