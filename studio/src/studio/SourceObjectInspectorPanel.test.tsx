// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, expect, it, vi } from 'vitest'
import SourceObjectInspectorPanel from './SourceObjectInspectorPanel'

const inspectSourceObject = vi.hoisted(() => vi.fn())
vi.mock('@/api/client', () => ({ inspectSourceObject }))
globalThis.IS_REACT_ACT_ENVIRONMENT = true

const inspection = (path: string, name: string, index: number) => ({
  category: 'Blocks', name, relativePath: path, interfaces: [], tables: [],
  networks: [{ index, compileUnitId: `${name}-${index}`, title: null, comment: null, language: 'LAD', structuredText: null, ladder: { elements: [
    { id: '26', kind: 'Contact', label: 'CylinderGoForwardPos', negatedPins: [], referencedObject: 'CylinderGoForwardPos', pins: [{ name: 'in', label: null, referencedObject: null, negated: false }, { name: 'operand', label: 'CylinderGoForwardPos', referencedObject: 'CylinderGoForwardPos', negated: false }, { name: 'out', label: null, referencedObject: null, negated: false }] },
    { id: '28', kind: 'TON', label: 'IEC_CylForwardMovement', negatedPins: [], referencedObject: 'IEC_CylForwardMovement', pins: [{ name: 'IN', label: null, referencedObject: null, negated: false }, { name: 'PT', label: 'CylinderMovementSimulate', referencedObject: 'CylinderMovementSimulate', negated: false }, { name: 'Q', label: null, referencedObject: null, negated: false }] },
    { id: '30', kind: 'Contact', label: 'CylinderGoBackwardPos', negatedPins: [], referencedObject: 'CylinderGoBackwardPos', pins: [{ name: 'in', label: null, referencedObject: null, negated: false }, { name: 'out', label: null, referencedObject: null, negated: false }] },
    { id: '31', kind: 'Sr', label: 'io_Cylinder@ForwardPos', negatedPins: [], referencedObject: 'io_Cylinder@ForwardPos', pins: [{ name: 's', label: null, referencedObject: null, negated: false }, { name: 'r1', label: null, referencedObject: null, negated: false }, { name: 'Q', label: null, referencedObject: null, negated: false }] },
  ], wires: [
    { endpoints: [{ kind: 'Powerrail', elementId: null, pin: null }, { kind: 'NameCon', elementId: '26', pin: 'in' }, { kind: 'NameCon', elementId: '30', pin: 'in' }] },
    { endpoints: [{ kind: 'NameCon', elementId: '26', pin: 'out' }, { kind: 'NameCon', elementId: '28', pin: 'IN' }] },
    { endpoints: [{ kind: 'NameCon', elementId: '28', pin: 'Q' }, { kind: 'NameCon', elementId: '31', pin: 's' }] },
    { endpoints: [{ kind: 'NameCon', elementId: '30', pin: 'out' }, { kind: 'NameCon', elementId: '31', pin: 'r1' }] },
  ] } }],
})

afterEach(() => { document.body.innerHTML = ''; inspectSourceObject.mockReset() })

it('renders exact network cards in the dockable source inspector panel', async () => {
  inspectSourceObject.mockImplementation((_wb: string, _wt: string, _device: string, path: string) => Promise.resolve(path.startsWith('Blocks/A') ? inspection(path, 'A', 1) : inspection(path, 'B', 2)))
  const host = document.createElement('div'); document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => {
    root.render(<SourceObjectInspectorPanel workbenchId="wb" worktreeId="wt" deviceId="dev" target={{ kind: 'usage', usage: [
      { block: 'A', blockKind: 'FC', networkIndex: 1, networkTitle: null, sourceFile: 'Blocks/A.xml', access: 'read', networkId: 'a' },
      { block: 'B', blockKind: 'FC', networkIndex: 2, networkTitle: null, sourceFile: 'Blocks/B.xml', access: 'write', networkId: 'b' },
    ] }} referenceTargets={{ MotorRun: 'Tags/Signals.xml' }} onInspectObject={vi.fn()} onInspectUsage={vi.fn()} />)
    await Promise.resolve(); await Promise.resolve()
  })
  expect(host.textContent).toContain('Read/write network workspace')
  expect(host.textContent).toContain('A · Network 1')
  expect(host.textContent).toContain('B · Network 2')
  expect(host.querySelectorAll('[data-lad-element-id]').length).toBe(8)
  expect(host.querySelectorAll('[data-lad-wire]').length).toBeGreaterThanOrEqual(6)
  const forwardContact = host.querySelector('[data-lad-element-id="26"]')!
  const resetContact = host.querySelector('[data-lad-element-id="30"]')!
  expect(resetContact.getAttribute('data-lad-x')).toBe(forwardContact.getAttribute('data-lad-x'))
  expect(Number(resetContact.getAttribute('data-lad-y'))).toBeGreaterThan(Number(forwardContact.getAttribute('data-lad-y')))
  expect(host.textContent).toContain('CylinderMovementSimulate')
})
