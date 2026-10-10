import type * as api from '@/api/client'
import type { VersionControlSourceEntry } from './VersionControlChanges'

/**
 * One source object of the worktree's uncommitted set, or null for a status entry that is not a PLC
 * source object (the SVN staging area, a non-XML file, a path outside `devices/<plc>/source`).
 *
 * It lives here rather than inside the panel because two surfaces need the same answer: the changes
 * page renders these entries, and the shell counts them for the rail's badge (ADR-0015).
 */
export function sourceEntry(entry: api.VcStatusEntry, branch: string): VersionControlSourceEntry | null {
  const parts = entry.filePath.replace(/\\/g, '/').split('/')
  const state = branch.toLowerCase() === 'master' ? 'Unauthorized' : entry.state === 'Added' || entry.state === 'Untracked' ? 'Added' : entry.state === 'Deleted' ? 'Deleted' : 'Modified'
  if (parts[0] === 'hardware' && parts.length >= 2 && parts[1] !== 'staging') {
    const file = parts.at(-1) ?? entry.filePath
    return {
      filePath: entry.filePath,
      deviceId: 'project',
      plcName: 'Hardware',
      category: 'Hardware',
      objectName: file,
      state,
      authorizedOnMaster: true,
    }
  }
  if (parts.length < 5 || parts[0] !== 'devices' || parts[2] !== 'source' || !entry.filePath.toLowerCase().endsWith('.xml')) return null
  const category = parts[3] === 'Blocks' ? 'Block' : parts[3] === 'DB' ? 'DB' : parts[3] === 'UDT' ? 'Udt' : parts[3] === 'Tags' ? 'Tags' : parts[3]
  const file = parts.at(-1) ?? entry.filePath
  return {
    filePath: entry.filePath,
    deviceId: parts[1],
    plcName: parts[1],
    category,
    objectName: file.replace(/\.xml$/i, ''),
    state,
    authorizedOnMaster: branch.toLowerCase() !== 'master',
  }
}

/** The uncommitted source objects a status read holds — what the changes page lists and the rail badges. */
export function countUncommittedSourceObjects(status: api.VcStatusResult | null, branch: string): number {
  return (status?.entries ?? [])
    .map(entry => sourceEntry(entry, branch))
    .filter((entry): entry is VersionControlSourceEntry => entry !== null)
    .length
}
