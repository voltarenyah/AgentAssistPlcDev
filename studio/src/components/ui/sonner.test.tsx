// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { toast } from 'sonner'
import { Toaster } from './sonner'
import { showErrorToast } from './toast'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

describe('Toaster', () => {
  afterEach(() => {
    toast.dismiss()
    document.body.innerHTML = ''
  })

  it('marks long error messages for bounded, readable presentation', async () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)

    act(() => root.render(<Toaster />))
    act(() => toast.error('A detailed backend failure '.repeat(120)))
    await act(async () => {
      await new Promise(resolve => setTimeout(resolve, 20))
    })

    const errorToast = document.body.querySelector<HTMLElement>('[data-sonner-toast][data-type="error"]')

    expect(errorToast).not.toBeNull()
    expect(errorToast?.className).toContain('app-toast')
    expect(errorToast?.querySelector<HTMLElement>('[data-content]')?.className).toContain('app-toast-content')

    root.unmount()
  })

  it('copies the complete error message from the toast action', async () => {
    const writeText = vi.fn(async (_message: string) => {})
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    })

    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    const message = 'PLC export failed: the source file could not be written.'

    act(() => root.render(<Toaster />))
    act(() => showErrorToast(message))
    await act(async () => {
      await new Promise(resolve => setTimeout(resolve, 20))
    })

    const copyButton = document.body.querySelector<HTMLButtonElement>('[data-sonner-toast][data-type="error"] [data-button]')
    expect(copyButton?.textContent).toBe('Copy error')

    act(() => copyButton?.click())
    await act(async () => {})

    expect(writeText).toHaveBeenCalledWith(message)

    root.unmount()
  })
})
