import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const read = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8')
const mainStudio = read('./MainStudio.tsx')
const worktreeLanding = read('./workbench/WorktreeLandingPage.tsx')
const deviceDock = read('./DevicePropertiesDock.tsx')
const knowledgeDock = read('./KnowledgePropertiesDock.tsx')
const sessionDock = read('./chat/SessionDock.tsx')
const versionControl = read('./version-control/VersionControlPanel.tsx')

describe('MainStudio dock visual structure', () => {
  it('keeps the top separator strips on the left-dock 48px standard', () => {
    expect(mainStudio).toContain('className="flex h-12 shrink-0 items-center gap-1 border-b px-3"')
    expect(worktreeLanding).toContain('className="flex h-12 shrink-0 items-center gap-1 border-b px-3"')
  })

  it('uses the same sidebar surface and top line height for every right-dock panel', () => {
    for (const source of [deviceDock, knowledgeDock, sessionDock]) {
      expect(source).toContain('flex h-full w-full shrink-0 flex-col border-l bg-sidebar')
      expect(source).toContain('flex h-12 items-center gap-2 border-b px-3')
    }
    expect(versionControl).toContain('flex h-full min-h-0 w-full flex-col bg-sidebar')
    expect(versionControl).toContain('flex h-12 shrink-0 items-center justify-center gap-1 border-b px-2 pt-1')
    expect(mainStudio).toContain('dock-shell dock-shell-right min-h-0 shrink-0 bg-sidebar')
  })
})
