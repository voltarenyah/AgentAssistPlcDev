import type {
  VersionControlTimelineGitCommit,
  VersionControlTimelineResult,
  VersionControlTimelineSvnRevision,
} from '@/api/client'

export type TimelineColumn = {
  git: VersionControlTimelineGitCommit
  svn: VersionControlTimelineSvnRevision | null
}

export function buildTimelineColumns(result: VersionControlTimelineResult): TimelineColumn[] {
  const svnByGitSha = new Map(result.svnRevisions.map(revision => [revision.gitCommitSha, revision]))
  return [...result.gitCommits]
    .reverse()
    .map(git => ({ git, svn: svnByGitSha.get(git.sha) ?? null }))
}
