// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import WindowControls from './WindowControls'
import { installWindowResizeHandles, isWindowDragTarget, sendWindowCommand } from './desktopWindowBridge'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

type WebView2Mock = {
  postMessage: ReturnType<typeof vi.fn>
  addEventListener: ReturnType<typeof vi.fn>
  removeEventListener: ReturnType<typeof vi.fn>
}

const installWebView2 = (): WebView2Mock => {
  const mock: WebView2Mock = {
    postMessage: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }
  ;(window as unknown as { chrome?: { webview: WebView2Mock } }).chrome = { webview: mock }
  return mock
}

const removeWebView2 = () => {
  delete (window as unknown as { chrome?: unknown }).chrome
}

describe('WindowControls', () => {
  afterEach(() => {
    removeWebView2()
    document.body.innerHTML = ''
  })

  it('renders nothing in a plain browser', () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)

    act(() => root.render(<WindowControls />))

    expect(host.innerHTML).toBe('')
    act(() => root.unmount())
  })

  it('renders minimize, maximize, and close buttons inside the desktop shell', () => {
    const webview = installWebView2()
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)

    act(() => root.render(<WindowControls />))

    const labels = [...host.querySelectorAll('button')].map(button => button.getAttribute('aria-label'))
    expect(labels).toEqual(['Minimize window', 'Maximize window', 'Close window'])
    // The component asks the shell for the current state so the maximize /
    // restore icon is correct after startup.
    expect(webview.postMessage).toHaveBeenCalledWith({ type: 'window-control', command: 'get-state' })
    act(() => root.unmount())
  })

  it('sends window commands to the shell when the buttons are clicked', () => {
    const webview = installWebView2()
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    act(() => root.render(<WindowControls />))

    const [minimize, maximize, close] = [...host.querySelectorAll('button')]
    act(() => {
      minimize.dispatchEvent(new window.MouseEvent('click', { bubbles: true }))
      maximize.dispatchEvent(new window.MouseEvent('click', { bubbles: true }))
      close.dispatchEvent(new window.MouseEvent('click', { bubbles: true }))
    })

    expect(webview.postMessage).toHaveBeenCalledWith({ type: 'window-control', command: 'minimize' })
    expect(webview.postMessage).toHaveBeenCalledWith({ type: 'window-control', command: 'toggle-maximize' })
    expect(webview.postMessage).toHaveBeenCalledWith({ type: 'window-control', command: 'close' })
    act(() => root.unmount())
  })

  it('ignores commands when no desktop shell is present', () => {
    expect(() => sendWindowCommand('minimize')).not.toThrow()
  })
})

describe('isWindowDragTarget', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('allows dragging from empty header space but not from controls', () => {
    const header = document.createElement('header')
    const spacer = document.createElement('div')
    const button = document.createElement('button')
    const icon = document.createElement('span')
    button.appendChild(icon)
    header.appendChild(spacer)
    header.appendChild(button)
    document.body.appendChild(header)

    expect(isWindowDragTarget(header)).toBe(true)
    expect(isWindowDragTarget(spacer)).toBe(true)
    expect(isWindowDragTarget(button)).toBe(false)
    expect(isWindowDragTarget(icon)).toBe(false)
    expect(isWindowDragTarget(null)).toBe(false)
  })
})

describe('installWindowResizeHandles', () => {
  let uninstall: (() => void) | null = null

  afterEach(() => {
    uninstall?.()
    uninstall = null
    removeWebView2()
    document.body.style.cursor = ''
  })

  const pointer = (type: string, x: number, y: number, options: { button?: number, buttons?: number } = {}) =>
    document.dispatchEvent(new window.MouseEvent(type, {
      bubbles: true,
      clientX: x,
      clientY: y,
      button: options.button ?? 0,
      buttons: options.buttons ?? 0,
    }))

  it('shows a resize cursor near the viewport edges and resets it in the middle', () => {
    installWebView2()
    uninstall = installWindowResizeHandles()

    pointer('pointermove', 2, 2)
    expect(document.body.style.cursor).toBe('nwse-resize')

    pointer('pointermove', window.innerWidth - 2, 300)
    expect(document.body.style.cursor).toBe('ew-resize')

    pointer('pointermove', 400, 300)
    expect(document.body.style.cursor).toBe('')
  })

  it('asks the shell to start a native resize when pressing near an edge', () => {
    const webview = installWebView2()
    uninstall = installWindowResizeHandles()

    pointer('pointerdown', 2, window.innerHeight - 2, { button: 0 })
    expect(webview.postMessage).toHaveBeenCalledWith({
      type: 'window-control',
      command: 'begin-resize',
      direction: 'bottom-left',
    })

    webview.postMessage.mockClear()
    pointer('pointerdown', 400, 300, { button: 0 })
    expect(webview.postMessage).not.toHaveBeenCalled()
  })

  it('does nothing outside the desktop shell', () => {
    uninstall = installWindowResizeHandles()

    pointer('pointermove', 2, 2)
    pointer('pointerdown', 2, 2, { button: 0 })
    expect(document.body.style.cursor).toBe('')
  })
})
