import type {
  VcSourceEntry,
  VcValidationState,
} from '@/api/client'

export type VersionControlSourceGroup = {
  key: string
  plcName: string
  category: string
  entries: VcSourceEntry[]
}

const categoryOrder: Record<string, number> = {
  Block: 0,
  DB: 1,
  Udt: 2,
  Tags: 3,
}

const compareText = (left: string, right: string): number =>
  left.localeCompare(right, undefined, { sensitivity: 'base' }) || left.localeCompare(right)

export const groupSourceObjects = (entries: readonly VcSourceEntry[]): VersionControlSourceGroup[] => {
  const groups = new Map<string, VersionControlSourceGroup>()

  for (const entry of entries) {
    const key = `${entry.plcName}/${entry.category}`
    const group = groups.get(key)
    if (group) {
      group.entries.push(entry)
    } else {
      groups.set(key, {
        key,
        plcName: entry.plcName,
        category: entry.category,
        entries: [entry],
      })
    }
  }

  return [...groups.values()]
    .map(group => ({
      ...group,
      entries: [...group.entries].sort((left, right) =>
        compareText(left.objectName, right.objectName) || compareText(left.filePath, right.filePath)),
    }))
    .sort((left, right) =>
      compareText(left.plcName, right.plcName)
      || (categoryOrder[left.category] ?? Number.MAX_SAFE_INTEGER) - (categoryOrder[right.category] ?? Number.MAX_SAFE_INTEGER)
      || compareText(left.category, right.category))
}

export const togglePath = (
  selected: ReadonlySet<string>,
  path: string,
  isSelected: boolean,
): Set<string> => {
  const next = new Set(selected)
  if (isSelected) next.add(path)
  else next.delete(path)
  return next
}

export const validationLabel = (state: VcValidationState): string => {
  switch (state) {
    case 'Validated': return 'TIA validated'
    case 'Unlabeled': return 'Full scan required'
    case 'Invalid': return 'Validation evidence invalid'
  }
}

const blockReasons: Record<string, string> = {
  TIA_FEATURE_OVERLAP: 'This object changed in both TIA and the feature.',
  SOURCE_LIFECYCLE_UNSUPPORTED: 'Adding, deleting, or renaming this object is not supported yet.',
  SOURCE_DELETE_UNSUPPORTED: 'Deleting this object through the current import flow is not supported yet.',
  SOURCE_NOT_IMPORTABLE: 'This object cannot be imported.',
}

export const mergeBlockReason = (item: { importable: boolean; reason: string | null }): string | null => {
  if (item.importable) return null
  return item.reason ? blockReasons[item.reason] ?? 'This object cannot be imported.' : 'This object cannot be imported.'
}
