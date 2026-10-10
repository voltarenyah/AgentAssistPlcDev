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

  it('overrides the vertical-tab skin so the item is a centred 30 px square', async () => {
    // The `Tabs` primitive ships `group-data-[orientation=vertical]/tabs:w-full` and `…:justify-start`
    // for labelled vertical tabs. Those selectors beat a plain `w-[30px]` / `justify-center`, so a rail
    // item that does not override the same variant stretches across the whole rail and leaves its icon
    // flush against the inner edge (measured in the running app: item 43 px wide, icon left gap 0,
    // right gap 27). happy-dom has no layout, so the contract is asserted on the class list.
    const { host } = await setup()

    for (const name of ['properties', 'changes', 'history'] as const) {
      const className = rail(host, name)?.className ?? ''
      expect(className, name).toContain('group-data-[orientation=vertical]/tabs:w-[30px]')
      expect(className, name).toContain('group-data-[orientation=vertical]/tabs:justify-center')
    }
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
      // A hidden page is still mounted, which is what keeps its state across a switch. The wrapper is
      // hidden rather than left in the layout: three page boxes in one track push the open page out of
      // the clip unless it is the last of them (see RightDock's comment).
      const wrapper = host.querySelector<HTMLElement>(`[data-testid="right-dock-page-${name}"]`)
      expect(content?.closest('[hidden]') === null, name).toBe(name === 'history')
      expect(wrapper?.hasAttribute('hidden'), name).toBe(name !== 'history')
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

    await click(host.querySelector('[aria-label="Collapse Changes to the rail"]'))
    expect(onSelectPage).toHaveBeenLastCalledWith('changes')
  })

  it('offers a labelled expand handle only while the page is collapsed', async () => {
    const { host, onSelectPage, root } = await setup({ page: 'changes' })
    expect(host.querySelector('[data-testid="right-dock-rail-expand"]')).toBeNull()

    await act(async () => root.render(
      <RightDock page={null} markedPage="history" pageWidth={266} onSelectPage={onSelectPage} pages={pages} />,
    ))
    const expand = host.querySelector<HTMLButtonElement>('[data-testid="right-dock-rail-expand"]')
    expect(expand?.getAttribute('aria-label')).toBe('Expand History')

    // The handle opens the page the rail kept marked, not some other one.
    await click(expand)
    expect(onSelectPage).toHaveBeenLastCalledWith('history')
  })

  it('shows the header chip it was given, and none when there is nothing to say', async () => {
    const withChip = await setup({ chips: { changes: '3 uncommitted' } })
    expect(withChip.host.querySelector('[data-testid="right-dock-page-chip"]')?.textContent).toBe('3 uncommitted')

    const without = await setup({ chips: { changes: null } })
    expect(without.host.querySelector('[data-testid="right-dock-page-chip"]')).toBeNull()
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
