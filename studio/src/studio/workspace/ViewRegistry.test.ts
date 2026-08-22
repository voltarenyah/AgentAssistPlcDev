import { describe, expect, it } from 'vitest'
import { workspaceViewRegistry } from './ViewRegistry'
import { DEFAULT_WORKSPACE_VIEW_KINDS } from './workspaceTypes'

describe('workspaceViewRegistry', () => {
  it('registers every workspace view kind', () => {
    expect(Object.keys(workspaceViewRegistry).sort()).toEqual([...DEFAULT_WORKSPACE_VIEW_KINDS].sort())
    for (const kind of DEFAULT_WORKSPACE_VIEW_KINDS) {
      expect(workspaceViewRegistry[kind].render).toBeTypeOf('function')
    }
  })

  it('keeps the current tab titles', () => {
    expect(DEFAULT_WORKSPACE_VIEW_KINDS.map(kind => workspaceViewRegistry[kind].title)).toEqual([
      'Device overview',
      'AI chat',
      'PLC source',
      'Knowledge',
    ])
  })
})
