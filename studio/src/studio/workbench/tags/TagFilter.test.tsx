// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TagNode } from '@/api/client'
import TagFilter from './TagFilter'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const nodes: TagNode[] = [
  { tagId: 'machine', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
  { tagId: 'press', parentTagId: 'machine', name: 'Press', normalizedName: 'press' },
  { tagId: 'state', parentTagId: null, name: 'State', normalizedName: 'state' },
]

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const setInputValue = async (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setter.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

afterEach(() => { document.body.innerHTML = '' })

describe('TagFilter', () => {
  it('selects a searched tag through the command keyboard interaction', async () => {
    const onSelectedTagIdsChange = vi.fn()
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={onSelectedTagIdsChange} />,
    )

    await act(async () => (document.body.querySelector('button[aria-label="Filter tags"]') as HTMLButtonElement).click())
    const input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    await setInputValue(input, 'Machine/Press')
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })

    expect(onSelectedTagIdsChange).toHaveBeenCalledWith(['press'])
    await act(async () => root.unmount())
  })

  it('browses the filter taxonomy as a collapsed hierarchy', async () => {
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={() => {}} />,
    )

    await act(async () => (document.body.querySelector('button[aria-label="Filter tags"]') as HTMLButtonElement).click())

    const tree = document.body.querySelector('[role="tree"]') as HTMLElement
    expect(tree).not.toBeNull()
    expect(tree.textContent).toContain('Machine')
    expect(tree.textContent).not.toContain('Machine/Press')
    await act(async () => (document.body.querySelector('button[aria-label="Expand Machine"]') as HTMLButtonElement).click())
    expect(document.body.querySelector('button[aria-label="Select Machine/Press"]')).not.toBeNull()

    await act(async () => root.unmount())
  })

  it('refreshes the taxonomy when the filter is opened', async () => {
    const onOpen = vi.fn()
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={() => {}} onOpen={onOpen} />,
    )

    await act(async () => (document.body.querySelector('button[aria-label="Filter tags"]') as HTMLButtonElement).click())

    expect(onOpen).toHaveBeenCalledTimes(1)
    await act(async () => root.unmount())
  })

  it('renders full-path chips and clears the final filter with its labelled remove control', async () => {
    const onSelectedTagIdsChange = vi.fn()
    const { host, root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={['press']} onSelectedTagIdsChange={onSelectedTagIdsChange} />,
    )

    expect(host.textContent).toContain('Machine/Press')
    const remove = host.querySelector<HTMLButtonElement>('button[aria-label="Remove tag Machine/Press"]')
    expect(remove).not.toBeNull()
    await act(async () => remove!.click())

    expect(onSelectedTagIdsChange).toHaveBeenCalledWith([])
    await act(async () => root.unmount())
  })
})
