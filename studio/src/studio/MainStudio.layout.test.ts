import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const read = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8')
const mainStudio = read('./MainStudio.tsx')
const worktreeLanding = read('./workbench/WorktreeLandingPage.tsx')
const rightDock = read('./workspace/RightDock.tsx')
const rightDockPage = read('./workspace/RightDockPage.tsx')
const deviceDock = read('./DevicePropertiesDock.tsx')
const hardwareDock = read('./HardwarePropertiesDock.tsx')
const knowledgeDock = read('./KnowledgePropertiesDock.tsx')
const versionControl = read('./version-control/VersionControlPanel.tsx')

describe('MainStudio dock visual structure', () => {
  it('keeps the top separator strips on the left-dock 48px standard', () => {
    expect(mainStudio).toContain('className="flex h-12 shrink-0 items-center gap-1 border-b px-3"')
    expect(worktreeLanding).toContain('className="flex h-12 shrink-0 items-center gap-1 border-b px-3"')
  })

  it('keeps one right-dock surface and one page-header height for every page', () => {
    // The right dock's chrome lives in the rail column and its page frame now: a page's own panel is
    // content only, so no dock panel may bring a surface or a header of its own (ADR-0015).
    expect(rightDock).toContain('dock-shell dock-shell-right min-h-0 shrink-0 flex-row items-stretch gap-0 bg-sidebar')
    expect(rightDockPage).toContain('flex h-full min-h-0 w-full flex-col border-l bg-sidebar')
    expect(rightDockPage).toContain('flex h-11 shrink-0 items-center gap-2 border-b px-3')
    for (const source of [deviceDock, hardwareDock, knowledgeDock, versionControl]) {
      expect(source).not.toContain('bg-sidebar')
      expect(source).not.toContain('border-l ')
    }
  })
})
