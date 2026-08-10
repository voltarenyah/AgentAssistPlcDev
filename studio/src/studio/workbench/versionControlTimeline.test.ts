import { describe, expect, it } from 'vitest'
import type { VersionControlTimelineResult } from '@/api/client'
import { buildTimelineColumns } from './versionControlTimeline'

const result: VersionControlTimelineResult = {
  gitCommits: [
    { sha: 'new', author: 'A', message: 'new', timestamp: '2026-08-10T10:00:00Z', files: [], tiaChecksum: 'new-checksum', svnRevision: 183 },
    { sha: 'middle', author: 'A', message: 'middle', timestamp: '2026-08-10T09:00:00Z', files: [], tiaChecksum: null, svnRevision: null },
    { sha: 'old', author: 'A', message: 'old', timestamp: '2026-08-10T08:00:00Z', files: [], tiaChecksum: 'old-checksum', svnRevision: 182 },
  ],
  svnRevisions: [
    { revision: 183, author: 'A', message: 'svn 183', timestamp: '2026-08-10T10:00:00Z', tiaChecksum: 'new-checksum', gitCommitSha: 'new' },
    { revision: 182, author: 'A', message: 'svn 182', timestamp: '2026-08-10T08:00:00Z', tiaChecksum: 'old-checksum', gitCommitSha: 'old' },
  ],
  hasMore: false,
}

describe('buildTimelineColumns', () => {
  it('aligns SVN revisions under their linked Git commits in chronological order', () => {
    const columns = buildTimelineColumns(result)

    expect(columns.map(column => column.git.sha)).toEqual(['old', 'middle', 'new'])
    expect(columns[0].svn?.revision).toBe(182)
    expect(columns[1].svn).toBeNull()
    expect(columns[2].svn?.revision).toBe(183)
  })
})
