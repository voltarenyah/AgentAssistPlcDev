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

// Entries worth listing in the confirmation dialog. An Unchanged entry only
// earns a row on a genuine fingerprint mismatch — when fingerprints are simply
// unavailable (tag tables have none), equal hashes already prove it unchanged.
export const comparedEntries = (entries: ReconciliationEntry[]) =>
  entries.filter(entry =>
    kindName(entry.kind) !== 'Unchanged' || entry.fingerprintsMatch === false)

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
