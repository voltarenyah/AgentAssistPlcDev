// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TagNode } from '@/api/client'
import TagFilter, { filterableTags } from './TagFilter'
import { tagPaths } from './tagPaths'

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
  it('renders the compact search input and taxonomy control as separate peer elements', async () => {
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={() => {}} />,
    )

    const input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    // Deliberately not input.closest('[cmdk-root]'): that attribute belongs to the library the
    // inline filter currently uses and cannot survive its move to notion-kit's Autocomplete. What
    // the test is actually claiming is that the search field and the taxonomy button sit in the
    // same row, so it asserts exactly that, without naming any library's wrapper.
    const taxonomyButton = document.body.querySelector('button[aria-label="Open tag taxonomy"]') as HTMLButtonElement

    expect(input).not.toBeNull()
    expect(taxonomyButton).not.toBeNull()
    expect((taxonomyButton.parentElement as HTMLElement).contains(input)).toBe(true)
    // Assert the accessible contract rather than the Studio Button's data-size, which no migrated
    // button carries: an icon-only button must have a name and render an icon, nothing else.
    expect(taxonomyButton.getAttribute('aria-label')).toBe('Open tag taxonomy')
    expect(taxonomyButton.querySelector('svg')).not.toBeNull()
    await act(async () => root.unmount())
  })

  it('offers full-path suggestions and selects a searched tag through keyboard interaction', async () => {
    const onSelectedTagIdsChange = vi.fn()
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={onSelectedTagIdsChange} />,
    )

    const input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    await setInputValue(input, 'Machine/Press')
    expect(document.body.textContent).toContain('Machine/Press')
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })

    expect(onSelectedTagIdsChange).toHaveBeenCalledWith(['press'])
    expect(input.value).toBe('')
    // The same outcome asserted against the extracted rule, so the matching half of this behaviour
    // already has a home that does not depend on which component renders the list. When the inline
    // filter moves to notion-kit's Autocomplete, cmdk's keyboard path here is replaced by a browser
    // check - the tag filter is reachable on the project landing page - and this rule assertion
    // stays as it is.
    const paths = tagPaths(nodes)
    expect(filterableTags(nodes, paths, [], 'Machine/Press').map(node => node.tagId)).toEqual(['press'])
    expect(filterableTags(nodes, paths, ['press'], 'Machine/Press')).toEqual([])
    expect(filterableTags(nodes, paths, [], '').map(node => node.tagId)).toEqual(['machine', 'press', 'state'])
    await act(async () => root.unmount())
  })

  it('selects an inline suggestion by pointer', async () => {
    const onSelectedTagIdsChange = vi.fn()
    const { root } = await render(<TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={onSelectedTagIdsChange} />)
    const input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    await setInputValue(input, 'press')
    await act(async () => (document.body.querySelector('[aria-label="Filter by Machine/Press"]') as HTMLElement).click())
    expect(onSelectedTagIdsChange).toHaveBeenCalledWith(['press'])
    await act(async () => root.unmount())
  })

  it('browses the filter taxonomy as a collapsed hierarchy', async () => {
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={() => {}} />,
    )

    await act(async () => (document.body.querySelector('button[aria-label="Open tag taxonomy"]') as HTMLButtonElement).click())

    const tree = document.body.querySelector('[role="tree"]') as HTMLElement
    expect(tree).not.toBeNull()
    expect(tree.textContent).toContain('Machine')
    expect(tree.textContent).not.toContain('Machine/Press')
    await act(async () => (document.body.querySelector('button[aria-label="Expand Machine"]') as HTMLButtonElement).click())
    expect(document.body.querySelector('button[aria-label="Select Machine/Press"]')).not.toBeNull()

    await act(async () => root.unmount())
  })

  it('marks active filters in the focused taxonomy', async () => {
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={['machine']} onSelectedTagIdsChange={() => {}} />,
    )

    await act(async () => (document.body.querySelector('button[aria-label="Open tag taxonomy"]') as HTMLButtonElement).click())

    const active = document.body.querySelector('[role="treeitem"][aria-selected="true"]') as HTMLElement
    expect(active).not.toBeNull()
    expect(active.textContent).toContain('Machine')
    await act(async () => root.unmount())
  })

  it('refreshes the taxonomy when the filter is opened', async () => {
    const onOpen = vi.fn()
    const { root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={() => {}} onOpen={onOpen} />,
    )

    await act(async () => (document.body.querySelector('button[aria-label="Open tag taxonomy"]') as HTMLButtonElement).click())

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

  it('does not reserve chip spacing when no tags are selected', async () => {
    const { host, root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={[]} onSelectedTagIdsChange={() => {}} />,
    )

    expect(host.querySelector('[aria-label="Active tag filters"]')).toBeNull()
    await act(async () => root.unmount())
  })

  it('places active tag chips below the search controls', async () => {
    const { host, root } = await render(
      <TagFilter nodes={nodes} selectedTagIds={['press']} onSelectedTagIdsChange={() => {}} />,
    )

    const searchRow = host.querySelector('input[aria-label="Search filter tags"]')!.closest('.flex.items-stretch') as HTMLElement
    const activeFilters = host.querySelector('[aria-label="Active tag filters"]') as HTMLElement
    expect(searchRow).not.toBeNull()
    expect(activeFilters).not.toBeNull()
    expect(searchRow.compareDocumentPosition(activeFilters) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    await act(async () => root.unmount())
  })
})
