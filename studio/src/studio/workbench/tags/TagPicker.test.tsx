// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TagNode } from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import TagPicker from './TagPicker'

vi.mock('@/components/ui/toast', () => ({ showErrorToast: vi.fn() }))

const nodes: TagNode[] = [
  { tagId: 'a', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
  { tagId: 'b', parentTagId: 'a', name: 'Press', normalizedName: 'press' },
]

afterEach(() => { document.body.innerHTML = '' })

const getByRole = (role: string, name?: string) => {
  const candidates = Array.from(document.body.getElementsByTagName('*')).filter(element => {
    const elementRole = element.getAttribute('role') ?? (element.tagName === 'BUTTON' ? 'button' : element.tagName === 'INPUT' ? 'combobox' : '')
    if (elementRole !== role) return false
    if (!name) return true
    const accessibleName = element.getAttribute('aria-label') ?? element.textContent?.trim() ?? ''
    return typeof name === 'string' ? accessibleName === name : false
  })
  expect(candidates.length, `role ${role} name ${name}`).toBeGreaterThan(0)
  return candidates[0] as HTMLElement
}

const getAllByRole = (role: string) => Array.from(document.body.getElementsByTagName('*')).filter(element => {
  const elementRole = element.getAttribute('role') ?? (element.tagName === 'BUTTON' ? 'button' : element.tagName === 'INPUT' ? 'combobox' : '')
  return elementRole === role
})

const getByLabel = (label: string) => getByRole('combobox', label) as HTMLInputElement

const type = async (input: HTMLInputElement, value: string) => {
  const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
  await act(async () => {
    setValue?.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

describe('TagPicker', () => {
  it('shows one create action for a valid absent slash path', async () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<TagPicker nodes={nodes} onAssign={() => {}} onCreate={() => ({ tagId: 'c', parentTagId: 'b', name: 'Hydraulic', normalizedName: 'hydraulic' })} />))
    await act(async () => getByRole('button', 'Add tag').click())
    await type(getByLabel('Search tags'), 'Machine/Press/Hydraulic')
    expect(getByRole('option', 'Create tag Machine/Press/Hydraulic')).toBeTruthy()
    await act(async () => root.unmount())
  })

  it('assigns an existing node by keyboard search and keeps controlled assignments on error', async () => {
    const onAssign = vi.fn(async () => { throw new Error('offline') })
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    function Harness() {
      const [currentTagIds] = React.useState(['a'])
      return <TagPicker nodes={nodes} currentTagIds={currentTagIds} onAssign={onAssign} />
    }
    await act(async () => root.render(<Harness />))
    expect(getAllByRole('listitem')).toHaveLength(0)
    await act(async () => getByRole('button', 'Add tag').click())
    const input = getByLabel('Search tags')
    await type(input, 'Machine/Press')
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })
    expect(onAssign).toHaveBeenCalledWith('b')
    expect(getAllByRole('listitem')).toHaveLength(0)
    expect(getAllByRole('listitem').some(item => item.getAttribute('aria-label') === 'Machine/Press')).toBe(false)
    expect(vi.mocked(showErrorToast)).toHaveBeenCalledWith('offline')
    expect(getByLabel('Search tags')).toBeTruthy()
    await act(async () => root.unmount())
  })
})
