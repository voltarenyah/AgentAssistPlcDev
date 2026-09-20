import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./MainStudio.tsx', import.meta.url), 'utf8')
const startup = source.slice(source.indexOf('const loadStartup ='), source.indexOf('useEffect(() => { void loadStartup() }, [loadStartup])'))

describe('all-projects startup contract', () => {
  it('loads landing data without implicitly selecting the first Project', () => {
    expect(startup).toContain('reloadLanding()')
    expect(startup).not.toContain('api.selectWorkbench(first.workbenchId)')
    expect(startup).not.toContain('setSelection({ workbenchId: first.workbenchId')
  })

  it('hosts the landing page with explicit Project and worktree callbacks', () => {
    expect(source).toContain('<AllProjectsLandingPage')
    expect(source).toContain('void selectWorkbench(workbench)')
    expect(source).toContain('void selectWorktree(workbench, worktree)')
  })
})
