// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TagNode } from '@/api/client'
import TagPicker from './TagPicker'

const nodes: TagNode[] = [
  { tagId: 'a', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
  { tagId: 'b', parentTagId: 'a', name: 'Press', normalizedName: 'press' },
]

afterEach(() => { document.body.innerHTML = '' })

describe('TagPicker', () => {
  it('shows one create action for a valid absent slash path', async () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<TagPicker nodes={nodes} onAssign={() => {}} onCreate={() => ({ tagId: 'c', parentTagId: 'b', name: 'Hydraulic', normalizedName: 'hydraulic' })} />))
    await act(async () => (host.querySelector('button[aria-label="Add tag"]') as HTMLButtonElement).click())
    const input = document.body.querySelector('input[aria-label="Search tags"]') as HTMLInputElement
    await act(async () => {
      const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
      setValue?.call(input, 'Machine/Press/Hydraulic')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    expect(document.body.querySelectorAll('[cmdk-item]').length).toBeGreaterThan(0)
    expect(Array.from(document.body.querySelectorAll('[cmdk-item]')).filter(item => item.textContent?.includes('Create')).length).toBe(1)
    await act(async () => root.unmount())
  })

  it('assigns an existing node by keyboard command selection and keeps assignments on error', async () => {
    const onAssign = vi.fn(async () => { throw new Error('offline') })
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<TagPicker nodes={nodes} currentTagIds={['a']} onAssign={onAssign} />))
    await act(async () => (host.querySelector('button[aria-label="Add tag"]') as HTMLButtonElement).click())
    const item = Array.from(document.body.querySelectorAll<HTMLElement>('[cmdk-item]')).find(element => element.textContent?.includes('Machine/Press'))
    expect(item).toBeTruthy()
    await act(async () => item?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })))
    expect(onAssign).toHaveBeenCalledWith('b')
    expect(document.body.textContent).toContain('Machine/Press')
    await act(async () => root.unmount())
  })
})
