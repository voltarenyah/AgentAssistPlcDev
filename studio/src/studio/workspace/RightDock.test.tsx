// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import RightDock from './RightDock'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const click = async (element: Element | null) => {
  await act(async () => {
    element?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

const pages = {
  properties: <div data-testid="properties-content">properties body</div>,
  changes: <div data-testid="changes-content">changes body</div>,
  history: <div data-testid="history-content">history body</div>,
}

const setup = async (overrides: Partial<React.ComponentProps<typeof RightDock>> = {}) => {
  const onSelectPage = vi.fn()
  const props: React.ComponentProps<typeof RightDock> = {
    page: 'changes',
    markedPage: 'changes',
    pageWidth: 266,
    onSelectPage,
    pages,
    ...overrides,
  }
  const { host, root } = await render(<RightDock {...props} />)
  return { host, root, onSelectPage }
}

const column = (host: HTMLElement) => host.querySelector<HTMLElement>('[data-dock="right"]')
const rail = (host: HTMLElement, page: 'properties' | 'changes' | 'history') =>
  host.querySelector<HTMLButtonElement>(`[data-testid="right-dock-rail-${page}"]`)
const track = (host: HTMLElement) => host.querySelector<HTMLElement>('.dock-page-track')

afterEach(() => {
  document.body.innerHTML = ''
})

describe('RightDock', () => {
  it('presents the three pages in rail order with accessible names', async () => {
    const { host } = await setup()

    const tabs = [...host.querySelectorAll('[role="tab"]')]
    expect(tabs.map(tab => tab.getAttribute('aria-label'))).toEqual(['Properties', 'Changes', 'History'])
    expect(host.querySelector('[role="tablist"]')?.getAttribute('aria-orientation')).toBe('vertical')
  })

  it('shows only the open page among the pages that have been opened', async () => {
    const { host, root } = await setup({ page: 'changes' })
    const show = async (next: 'properties' | 'changes' | 'history') => {
      await act(async () => root.render(
        <RightDock page={next} markedPage={next} pageWidth={266} onSelectPage={() => {}} pages={pages} />,
      ))
    }
    await show('properties')
    await show('history')

    for (const name of ['properties', 'changes', 'history'] as const) {
      const content = host.querySelector(`[data-testid="${name}-content"]`)
      expect(content, name).not.toBeNull()
      // A hidden page is still mounted, which is what keeps its state across a switch.
      expect(content?.closest('[hidden]') === null, name).toBe(name === 'history')
    }
  })

  it('mounts a page the first time it is opened and keeps it after a switch', async () => {
    const { host, root } = await setup({ page: 'changes' })
    expect(host.querySelector('[data-testid="history-content"]')).toBeNull()

    await act(async () => root.render(
      <RightDock page="history" markedPage="history" pageWidth={266} onSelectPage={() => {}} pages={pages} />,
    ))
    expect(host.querySelector('[data-testid="history-content"]')).not.toBeNull()
    expect(host.querySelector('[data-testid="changes-content"]')).not.toBeNull()
  })

  it('is a zero-width, inert column while the whole column is hidden', async () => {
    const { host } = await setup({ columnOpen: false })

    expect(column(host)?.getAttribute('data-dock-state')).toBe('closed')
    expect(column(host)?.style.width).toBe('0px')
    expect(column(host)?.hasAttribute('inert')).toBe(true)
  })

  it('reports the clicked page, including the open one so the shell can collapse it', async () => {
    const { host, onSelectPage } = await setup()

    await click(rail(host, 'history'))
    expect(onSelectPage).toHaveBeenLastCalledWith('history')

    await click(rail(host, 'changes'))
    expect(onSelectPage).toHaveBeenLastCalledWith('changes')
  })

  it('collapses the page from its header control', async () => {
    const { host, onSelectPage } = await setup()

    await click(host.querySelector('[aria-label="Collapse Changes"]'))
    expect(onSelectPage).toHaveBeenLastCalledWith('changes')
  })

  it('offers the open page refresh and keeps the page it was asked for', async () => {
    const onRefresh = vi.fn()
    const { host } = await setup({ onRefresh })

    await click(host.querySelector('[aria-label="Refresh History"]'))
    expect(onRefresh).toHaveBeenLastCalledWith('history')
  })

  it('marks the page the rail keeps while collapsed and inert-renders nothing else', async () => {
    const { host } = await setup({ page: null, markedPage: 'history' })

    expect(column(host)?.getAttribute('data-page-state')).toBe('collapsed')
    expect(column(host)?.style.width).toBe('44px')
    expect(rail(host, 'history')?.getAttribute('aria-selected')).toBe('true')
    expect(track(host)?.hasAttribute('inert')).toBe(true)
    // Nothing is on screen while collapsed, but every page is still mounted behind the rail.
    expect(host.querySelector('[data-testid="history-content"]')?.closest('[hidden]')).not.toBeNull()
  })

  it('is the rail plus the page when the page is open', async () => {
    const { host } = await setup({ page: 'changes', pageWidth: 300 })

    expect(column(host)?.getAttribute('data-dock-state')).toBe('open')
    expect(column(host)?.style.width).toBe('344px')
    expect(track(host)?.hasAttribute('inert')).toBe(false)
  })

  it('badges the changes page with a count, and not with zero', async () => {
    const withBadge = await setup({ changesBadge: 3 })
    expect(withBadge.host.querySelector('[data-testid="right-dock-rail-changes-badge"]')?.textContent).toBe('3')

    const clean = await setup({ changesBadge: 0 })
    expect(clean.host.querySelector('[data-testid="right-dock-rail-changes-badge"]')).toBeNull()
  })

  it('shows the page scope it was given', async () => {
    const { host } = await setup({ scopes: { changes: 'feature/pei-fix' } })

    expect(host.textContent).toContain('feature/pei-fix')
  })
})
