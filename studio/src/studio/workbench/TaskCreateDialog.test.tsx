// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import TaskCreateDialog from './TaskCreateDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const devices: api.DeviceSummary[] = [{ deviceId: 'device-1', plcName: 'Main PLC' }]

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return { ...actual, createGraphWorktreeTask: vi.fn(async () => ({})) }
})

const mountedRoots: ReturnType<typeof createRoot>[] = []

const renderDialog = async (overrides: Partial<React.ComponentProps<typeof TaskCreateDialog>> = {}) => {
  const onClose = vi.fn()
  const onCreated = vi.fn()
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  mountedRoots.push(root)
  await act(async () => root.render(
    <TaskCreateDialog
      workbenchId="wb1"
      worktreeId="wt1"
      open
      devices={devices}
      onClose={onClose}
      onCreated={onCreated}
      {...overrides}
    />,
  ))
  return { host, root, onClose, onCreated }
}

const dialog = () => document.body.querySelector<HTMLElement>('[data-slot="dialog-content"]')!
const target = () => dialog().querySelector<HTMLSelectElement>('select[aria-label="New task target"]')!

const setInputValue = (input: HTMLInputElement | HTMLTextAreaElement, value: string) => {
  const prototype = input instanceof HTMLTextAreaElement
    ? window.HTMLTextAreaElement.prototype
    : window.HTMLInputElement.prototype
  Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

const fillForm = async (title: string) => {
  await act(async () => setInputValue(dialog().querySelector<HTMLInputElement>('input[aria-label="New task title"]')!, title))
  await act(async () => setInputValue(dialog().querySelector<HTMLInputElement>('input[aria-label="New task goal"]')!, 'Relocate the rack'))
  await act(async () => setInputValue(dialog().querySelector<HTMLTextAreaElement>('textarea[aria-label="New task expected result"]')!, 'Rack layout is committed'))
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  for (const root of mountedRoots.splice(0)) act(() => root.unmount())
  document.body.innerHTML = ''
})

describe('TaskCreateDialog', () => {
  it('preselects the device the create action was invoked from and binds the task to it (AC-005)', async () => {
    const { onClose, onCreated } = await renderDialog({ origin: { kind: 'device', deviceId: 'device-1' } })
    expect(target().value).toBe('device-1')

    await fillForm('Add alarm handling')
    await act(async () => {
      dialog().querySelector<HTMLButtonElement>('button[type="submit"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.createGraphWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', {
      title: 'Add alarm handling', deviceId: 'device-1', type: 'feature', intent: 'Relocate the rack', expectedResult: 'Rack layout is committed',
    })
    expect(onCreated).toHaveBeenCalled()
    expect(onClose).toHaveBeenCalled()
  })

  it('creates a hardware task with no device when the action came from the hardware row (AC-010)', async () => {
    await renderDialog({ origin: { kind: 'hardware' } })
    expect(target().value).toBe('hardware')
    expect(dialog().textContent).toContain('binds no PLC device')

    await fillForm('Move the rack')
    // A hardware target is complete on its own: the missing device must not keep the form disabled.
    expect(dialog().querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(false)
    await act(async () => {
      dialog().querySelector<HTMLButtonElement>('button[type="submit"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.createGraphWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', {
      title: 'Move the rack', targetKind: 'hardware', type: 'feature', intent: 'Relocate the rack', expectedResult: 'Rack layout is committed',
    })
  })

  it('keeps a target the user picks inside the dialog while it stays open', async () => {
    const { root } = await renderDialog({ origin: { kind: 'device', deviceId: 'device-1' } })
    await act(async () => {
      const select = target()
      select.value = 'hardware'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    expect(target().value).toBe('hardware')

    // The origin is a fresh object on every owner render; only a new opening may re-apply it.
    await act(async () => root.render(
      <TaskCreateDialog
        workbenchId="wb1"
        worktreeId="wt1"
        open
        devices={devices}
        origin={{ kind: 'device', deviceId: 'device-1' }}
        onClose={vi.fn()}
        onCreated={vi.fn()}
      />,
    ))
    expect(target().value).toBe('hardware')
  })

  it('closes without creating when the user cancels', async () => {
    const { onClose, onCreated } = await renderDialog({ origin: { kind: 'device', deviceId: 'device-1' } })

    await act(async () => {
      Array.from(dialog().querySelectorAll<HTMLButtonElement>('button'))
        .find(button => button.textContent?.trim() === 'Cancel')!
        .dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(onClose).toHaveBeenCalled()
    expect(onCreated).not.toHaveBeenCalled()
    expect(vi.mocked(api.createGraphWorktreeTask)).not.toHaveBeenCalled()
  })
})
