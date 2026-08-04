// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StatusBadge from './StatusBadge'
import { showErrorToast } from '@/components/ui/toast'

vi.mock('@/components/ui/toast', () => ({
  showErrorToast: vi.fn(),
}))

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const openDropdown = async (host: HTMLElement) => {
  const trigger = host.querySelector('button[aria-label="Change worktree status"]') as HTMLButtonElement
  expect(trigger, 'status badge trigger').toBeDefined()
  await act(async () => {
    trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, ctrlKey: false }))
    trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

const menuItem = (text: string) => {
  const item = Array.from(document.body.querySelectorAll<HTMLElement>('[role="menuitem"]'))
    .find(element => element.textContent?.trim() === text)
  expect(item, `menu item "${text}"`).toBeDefined()
  return item!
}

afterEach(() => {
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('StatusBadge', () => {
  it('shows the current status label', async () => {
    const { host, root } = await render(<StatusBadge status="ongoing" onChange={() => {}} />)
    expect(host.textContent).toContain('Ongoing')
    expect(host.textContent).not.toContain('Finished')
    await act(async () => root.unmount())
  })

  it('calls onChange when a different status is picked from the dropdown', async () => {
    const onChange = vi.fn(async () => {})
    const { host, root } = await render(<StatusBadge status="ongoing" onChange={onChange} />)

    await openDropdown(host)
    await act(async () => {
      menuItem('Finished').dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(onChange).toHaveBeenCalledWith('finished')
    await act(async () => root.unmount())
  })

  it('does not call onChange when the current status is re-picked', async () => {
    const onChange = vi.fn(async () => {})
    const { host, root } = await render(<StatusBadge status="finished" onChange={onChange} />)

    await openDropdown(host)
    await act(async () => {
      menuItem('Finished').dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(onChange).not.toHaveBeenCalled()
    await act(async () => root.unmount())
  })

  it('surfaces a failed status change through the error toast', async () => {
    const onChange = vi.fn(async () => { throw new Error('backend exploded') })
    const { host, root } = await render(<StatusBadge status="ongoing" onChange={onChange} />)

    await openDropdown(host)
    await act(async () => {
      menuItem('Finished').dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(showErrorToast).toHaveBeenCalled()
    expect(vi.mocked(showErrorToast).mock.calls[0][0]).toContain('backend exploded')
    await act(async () => root.unmount())
  })
})
