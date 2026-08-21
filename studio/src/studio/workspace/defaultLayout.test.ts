import { describe, expect, it } from 'vitest'
import { buildDefaultWorkspaceLayout, DEFAULT_WORKSPACE_TABSET_ID } from './defaultLayout'
import { DEFAULT_WORKSPACE_VIEW_KINDS, workspaceViewInstanceId } from './workspaceTypes'

describe('buildDefaultWorkspaceLayout', () => {
  it('builds a single tabset with one tab per workspace view', () => {
    const json = buildDefaultWorkspaceLayout()

    expect(json.layout.type).toBe('row')
    expect(json.layout.children).toHaveLength(1)

    const tabset = json.layout.children[0]
    expect(tabset.type).toBe('tabset')
    expect(tabset.id).toBe(DEFAULT_WORKSPACE_TABSET_ID)
    expect(tabset.children).toHaveLength(4)
  })

  it('uses stable instanceIds as tab ids and the kind as the component', () => {
    const json = buildDefaultWorkspaceLayout()
    const tabs = json.layout.children[0].children ?? []

    expect(tabs.map(tab => tab.id)).toEqual(DEFAULT_WORKSPACE_VIEW_KINDS.map(workspaceViewInstanceId))
    expect(tabs.map(tab => tab.component)).toEqual([...DEFAULT_WORKSPACE_VIEW_KINDS])
    expect(tabs.map(tab => tab.name)).toEqual([
      'Device overview',
      'AI chat',
      'PLC source',
      'Knowledge',
    ])
  })

  it('keeps views fixed (tabs cannot be closed)', () => {
    const json = buildDefaultWorkspaceLayout()
    expect(json.global?.tabEnableClose).toBe(false)
  })
})
