// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SandboxDeniedDialog from './SandboxDeniedDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

// notion-kit dialogs portal their content, so assertions read from document.body
// rather than the render host.
const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('SandboxDeniedDialog', () => {
  it('states the denial and lists every allowed sandbox root', () => {
    render(
      <SandboxDeniedDialog
        message={'D:\\outside\\Line.ap17 is outside the sandbox'}
        roots={['C:\\allowed', 'C:\\also-allowed']}
        onClose={vi.fn()}
      />,
    )

    expect(document.body.textContent).toContain('TIA project outside the sandbox')
    expect(document.body.textContent).toContain('D:\\outside\\Line.ap17 is outside the sandbox')
    expect(document.body.textContent).toContain('C:\\allowed')
    expect(document.body.textContent).toContain('C:\\also-allowed')
  })

  it('says so when no sandbox root could be loaded', () => {
    render(<SandboxDeniedDialog message="denied" roots={[]} onClose={vi.fn()} />)

    expect(document.body.textContent).toContain('Sandbox roots could not be loaded.')
  })

  it('closes from the acknowledge action', () => {
    const onClose = vi.fn()
    const { root } = render(<SandboxDeniedDialog message="denied" roots={[]} onClose={onClose} />)

    const acknowledge = [...document.body.querySelectorAll('button')]
      .find(button => button.textContent?.includes('Understood'))
    expect(acknowledge).toBeTruthy()
    act(() => acknowledge!.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onClose).toHaveBeenCalledTimes(1)
    act(() => root.unmount())
  })

  it('exposes exactly one close control', () => {
    render(<SandboxDeniedDialog message="denied" roots={[]} onClose={vi.fn()} />)

    // notion-kit's DialogContent renders a built-in close unless `hideClose` is set, so
    // without it this dialog would present two overlapping close controls.
    const closeControls = Array.from(document.body.querySelectorAll('button')).filter(button =>
      /close/i.test(button.getAttribute('aria-label') ?? '') || /close/i.test(button.textContent ?? ''))
    expect(closeControls).toHaveLength(1)
    expect(document.body.querySelector('button[aria-label="Close sandbox warning"]')).not.toBeNull()
  })
})
