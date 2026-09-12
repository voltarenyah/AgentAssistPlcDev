// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, expect, it, vi } from 'vitest'
import SourceObjectInspectorDialog from './SourceObjectInspectorDialog'

const inspectSourceObject = vi.hoisted(() => vi.fn())
vi.mock('@/api/client', () => ({ inspectSourceObject }))
globalThis.IS_REACT_ACT_ENVIRONMENT = true

const inspection = (path: string, name: string, index: number) => ({
  category: 'Blocks', name, relativePath: path, interfaces: [], tables: [],
  networks: [{ index, compileUnitId: `${name}-${index}`, title: null, comment: null, language: 'LAD', structuredText: null, ladder: { elements: [{ id: 'part', kind: 'Contact', label: 'MotorRun', negatedPins: [], referencedObject: 'MotorRun' }], wires: [{ endpoints: [] }] } }],
})

afterEach(() => { document.body.innerHTML = ''; inspectSourceObject.mockReset() })

it('renders exact network cards from multiple source files in a temporary usage workspace', async () => {
  inspectSourceObject.mockImplementation((_wb: string, _wt: string, _device: string, path: string) => Promise.resolve(path.startsWith('Blocks/A') ? inspection(path, 'A', 1) : inspection(path, 'B', 2)))
  const host = document.createElement('div'); document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => {
    root.render(<SourceObjectInspectorDialog workbenchId="wb" worktreeId="wt" deviceId="dev" usage={[
      { block: 'A', blockKind: 'FC', networkIndex: 1, networkTitle: null, sourceFile: 'Blocks/A.xml', access: 'read', networkId: 'a' },
      { block: 'B', blockKind: 'FC', networkIndex: 2, networkTitle: null, sourceFile: 'Blocks/B.xml', access: 'write', networkId: 'b' },
    ]} referenceTargets={{ MotorRun: 'Tags/Signals.xml' }} onOpenReference={vi.fn()} onShowUsage={vi.fn()} onClose={vi.fn()} />)
    await Promise.resolve(); await Promise.resolve()
  })
  expect(document.body.textContent).toContain('A · Network 1')
  expect(document.body.textContent).toContain('B · Network 2')
  expect(document.body.textContent).toContain('LAD topology · 1 connection')
})
