// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import { ThemeToggle } from './ThemeToggle'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

describe('ThemeToggle', () => {
  afterEach(() => {
    document.documentElement.classList.remove('dark')
    document.body.innerHTML = ''
  })

  it('starts the app in dark mode by default', () => {
    document.documentElement.classList.remove('dark')
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)

    act(() => root.render(<ThemeToggle />))

    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(host.querySelector('button')?.getAttribute('aria-label')).toBe('Switch to light theme')

    act(() => root.unmount())
  })
})
