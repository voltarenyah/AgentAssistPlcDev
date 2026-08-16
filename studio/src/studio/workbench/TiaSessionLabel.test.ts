import { describe, expect, it } from 'vitest'
import type * as api from '@/api/client'
import { formatSessionLabel, sessionLabelFor } from './TiaSessionLabel'

const workbench = (workbenchId: string, name: string, rootPath: string, relativePath = 'master'): api.Workbench => ({
  schemaVersion: '1.0',
  workbenchId,
  name,
  createdAt: '2026-08-16T00:00:00Z',
  rootPath,
  repositoryPath: `${rootPath}/repository.git`,
  engineeringProjectId: null,
  sourceProjectPath: null,
  worktrees: [{ worktreeId: `${workbenchId}-master`, name: 'master', branch: 'master', relativePath }],
})

const session = (projectPath: string): api.SessionInfo => ({
  id: 17,
  mode: 'WithUserInterface',
  projectPath,
  portalPath: null,
})

describe('sessionLabelFor', () => {
  it('uses the matching workbench when multiple projects have a master worktree', () => {
    const label = sessionLabelFor(
      [
        workbench('wb-a', 'Line A', 'C:/projects/line-a'),
        workbench('wb-b', 'Line B', 'C:/projects/line-b'),
      ],
      session('C:/projects/line-b/worktrees/master/tia'),
    )

    expect(label).toEqual({ project: 'Line B', worktree: 'master' })
    expect(formatSessionLabel(label)).toBe('Line B / master')
  })

  it('keeps the project name when TIA reports the project file instead of the tia directory', () => {
    const label = sessionLabelFor(
      [workbench('wb-a', 'Workbench A', 'C:/projects/line-a', 'worktrees/master')],
      session('C:/projects/line-a/worktrees/master/tia/ActualProject.ap17'),
    )

    expect(label).toEqual({ project: 'ActualProject', worktree: 'master' })
  })
})
