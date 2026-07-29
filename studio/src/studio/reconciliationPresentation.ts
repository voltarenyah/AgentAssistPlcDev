import type { ReconciliationEntry } from '@/api/client'

export type ComparisonState = 'new' | 'missing' | 'same' | 'different' | 'unverifiable'

const kindName = (kind: ReconciliationEntry['kind']) => {
  if (kind === 0 || kind === 'Added') return 'Added'
  if (kind === 1 || kind === 'Changed') return 'Changed'
  if (kind === 2 || kind === 'Removed') return 'Removed'
  return 'Unchanged'
}

export const actionableEntries = (entries: ReconciliationEntry[]) =>
  entries.filter(entry => kindName(entry.kind) !== 'Unchanged')

export const toggleApprovedPath = (
  current: ReadonlySet<string>,
  relativePath: string,
  checked: boolean,
) => {
  const next = new Set(current)
  if (checked) next.add(relativePath)
  else next.delete(relativePath)
  return next
}

export const comparisonState = (entry: ReconciliationEntry): ComparisonState => {
  const kind = kindName(entry.kind)
  if (kind === 'Added') return 'new'
  if (kind === 'Removed') return 'missing'
  if (kind === 'Changed') return 'different'
  if (entry.fingerprintsMatch === false) return 'different'
  if (entry.fingerprintsMatch === true) return 'same'
  if (entry.baselineHash !== null && entry.stagingHash !== null) {
    return entry.baselineHash === entry.stagingHash ? 'same' : 'different'
  }
  return 'unverifiable'
}
