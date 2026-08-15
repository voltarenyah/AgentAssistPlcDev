// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import TiaCloseConfirmationDialog from './TiaCloseConfirmationDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

afterEach(() => { document.body.innerHTML = '' })

describe('TiaCloseConfirmationDialog', () => {
  it('explains why the current TIA instance must close and offers the three choices', () => {
    const { host } = render(
      <TiaCloseConfirmationDialog
        operationLabel="Create workbench project"
        busy={false}
        onSaveAndClose={vi.fn()}
        onCloseWithoutSaving={vi.fn()}
        onCancel={vi.fn()}
      />,
    )

    expect(host.textContent).toContain('Create workbench project requires close current attached TIA instance')
    expect(host.querySelector('button[aria-label="Save and close TIA instance"]')).not.toBeNull()
    expect(host.querySelector('button[aria-label="Close TIA instance without saving"]')).not.toBeNull()
    expect(host.querySelector('button[aria-label="Cancel and close manually"]')).not.toBeNull()
  })

  it('routes each choice to its action', () => {
    const onSaveAndClose = vi.fn()
    const onCloseWithoutSaving = vi.fn()
    const onCancel = vi.fn()
    const { host } = render(
      <TiaCloseConfirmationDialog
        operationLabel="Create workbench project"
        busy={false}
        onSaveAndClose={onSaveAndClose}
        onCloseWithoutSaving={onCloseWithoutSaving}
        onCancel={onCancel}
      />,
    )

    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Save and close TIA instance"]')!.click())
    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Close TIA instance without saving"]')!.click())
    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Cancel and close manually"]')!.click())

    expect(onSaveAndClose).toHaveBeenCalledTimes(1)
    expect(onCloseWithoutSaving).toHaveBeenCalledTimes(1)
    expect(onCancel).toHaveBeenCalledTimes(1)
  })
})
