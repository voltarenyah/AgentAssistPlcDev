import type * as api from '@/api/client'

export type SessionLabel = {
  project: string | null
  worktree: string | null
}

export const normalizeProjectPath = (path: string) =>
  path.trim().replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()

const joinPath = (...parts: string[]) => normalizeProjectPath(parts.filter(Boolean).join('\\'))

const isPathWithin = (path: string, root: string) =>
  path === root || path.startsWith(`${root}\\`)

const projectNameFromPath = (path: string, fallback: string) => {
  const lastSegment = path.split(/[\\/]/).filter(Boolean).pop() ?? ''
  const projectFile = lastSegment.match(/^(.*)\.ap\d+$/i)
  if (projectFile?.[1]) return projectFile[1]
  return /^tia$/i.test(lastSegment) ? fallback : lastSegment || fallback
}

const matchesWorktree = (workbench: api.Workbench, worktree: api.WorkbenchRegistration, path: string) => {
  if (!worktree.relativePath) return false

  const relativePath = normalizeProjectPath(worktree.relativePath)
  const roots = [
    joinPath(workbench.rootPath, relativePath),
    joinPath(workbench.rootPath, 'worktrees', relativePath),
  ]
  return roots.some(root => isPathWithin(path, root))
}

/** Map a TIA session to the visible "Project / worktree" label. */
export const sessionLabelFor = (workbenches: api.Workbench[], session: api.SessionInfo): SessionLabel | null => {
  const path = session.projectPath
  if (!path) return null

  const normalized = normalizeProjectPath(path)
  for (const workbench of workbenches) {
    for (const worktree of workbench.worktrees) {
      if (matchesWorktree(workbench, worktree, normalized)) {
        return {
          project: projectNameFromPath(path, workbench.name),
          worktree: worktree.name || worktree.branch,
        }
      }
    }
    if (workbench.sourceProjectPath && normalizeProjectPath(workbench.sourceProjectPath) === normalized) {
      return {
        project: projectNameFromPath(path, workbench.name),
        worktree: `${workbench.name} (source)`,
      }
    }
  }

  return {
    project: projectNameFromPath(path, ''),
    worktree: null,
  }
}

export const formatSessionLabel = (label: SessionLabel | null): string => {
  if (!label || !label.project) return 'No project open'
  return label.worktree ? `${label.project} / ${label.worktree}` : label.project
}
