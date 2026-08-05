// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import McpToolsHelper from './McpToolsHelper'

vi.mock('@/api/client', () => ({
  getTools: vi.fn(),
}))

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const mocked = api as unknown as {
  getTools: ReturnType<typeof vi.fn>
}

afterEach(() => {
  vi.clearAllMocks()
  document.body.innerHTML = ''
})

describe('McpToolsHelper', () => {
  it('describes direct checked-out PLC source editing and its Git and TIA lifecycle', async () => {
    mocked.getTools.mockResolvedValue([
      {
        name: 'src_validate',
        description: 'Validate a PLC source file.',
        serverName: 'sourceeditor',
        schema: {
          properties: {
            baselineFilePath: { type: 'string' },
          },
        },
        tier: 'read',
      } satisfies api.ToolInfo,
    ])
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)

    await act(async () => root.render(<McpToolsHelper onClose={() => undefined} />))

    const guideText = Array.from(host.querySelectorAll('section'))
      .filter(section => /Before calling|Constraints & safety/.test(section.textContent ?? ''))
      .map(section => section.textContent)
      .join(' ')

    expect(guideText).toContain('one PLC source root')
    expect(guideText).toContain('checked-out XML files directly')
    expect(guideText).toContain('Commit XML changes to Git manually')
    expect(guideText).toContain('TIA Portal does not change until the XML is imported')
    expect(guideText).not.toMatch(/overlay|baseline|exported-source|modified-source/i)

    await act(async () => root.unmount())
  })
})
