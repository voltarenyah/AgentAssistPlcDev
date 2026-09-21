// @vitest-environment happy-dom
import React from 'react'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import NotionKitPage from './NotionKitPage'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

afterEach(() => {
  document.body.innerHTML = ''
})

describe('NotionKitPage', () => {
  it('renders live Notion Kit primitives and their accessible states', () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    act(() => root.render(<NotionKitPage />))

    expect(host.querySelector('h2')?.textContent).toBe('Notion Kit components')
    expect(host.querySelector('[data-notion-kit-preview]')).not.toBeNull()

    const toggle = host.querySelector<HTMLElement>('[role="switch"][aria-label="Show page cover"]')!
    expect(toggle.getAttribute('aria-checked')).toBe('false')

    act(() => root.unmount())
  })
})
