// Bridge between the Studio header and the Automation Workbench desktop shell.
// The shell hides the native Windows title bar, so Studio renders the drag
// area and window buttons itself and drives the window through WebView2
// messages (see MainWindow.ApplyWindowCommand). Outside the desktop shell
// (plain browser, tests) every helper here is an inert no-op.
export type DesktopWindowCommand =
  | 'minimize'
  | 'toggle-maximize'
  | 'close'
  | 'begin-drag'
  | 'begin-resize'
  | 'get-state'

export type DesktopWindowState = 'normal' | 'maximized'

export type DesktopResizeDirection =
  | 'left'
  | 'right'
  | 'top'
  | 'bottom'
  | 'top-left'
  | 'top-right'
  | 'bottom-left'
  | 'bottom-right'

type WebView2MessageEvent = { data: unknown }

type WebView2Api = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: WebView2MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: WebView2MessageEvent) => void): void
}

const webview = (): WebView2Api | null => {
  if (typeof window === 'undefined') return null
  const candidate = (window as unknown as { chrome?: { webview?: WebView2Api } }).chrome?.webview
  return candidate ?? null
}

export const isDesktopShell = () => webview() !== null

export function sendWindowCommand(
  command: DesktopWindowCommand,
  direction?: DesktopResizeDirection,
): void {
  webview()?.postMessage(direction
    ? { type: 'window-control', command, direction }
    : { type: 'window-control', command })
}

export function subscribeWindowState(
  callback: (state: DesktopWindowState) => void,
): () => void {
  const api = webview()
  if (!api) return () => {}
  const listener = (event: WebView2MessageEvent) => {
    const data = event.data as { type?: unknown; state?: unknown } | null
    if (data?.type === 'window-state'
      && (data.state === 'normal' || data.state === 'maximized')) {
      callback(data.state)
    }
  }
  api.addEventListener('message', listener)
  return () => api.removeEventListener('message', listener)
}

// Dragging the window only makes sense from non-interactive header space;
// buttons and links inside the header keep their normal click behavior.
export const isWindowDragTarget = (target: EventTarget | null): boolean =>
  target instanceof HTMLElement
  && target.closest('button, a, input, select, textarea, [role="button"]') === null

// The shell hides the native frame, so the viewport edges double as the
// resize border: near an edge the resize cursor appears, and pressing there
// asks the shell to start a native modal resize loop. Maximized windows do
// not resize. No-op outside the desktop shell.
export const WindowResizeZonePixels = 6

const resizeCursors: Record<DesktopResizeDirection, string> = {
  left: 'ew-resize',
  right: 'ew-resize',
  top: 'ns-resize',
  bottom: 'ns-resize',
  'top-left': 'nwse-resize',
  'bottom-right': 'nwse-resize',
  'top-right': 'nesw-resize',
  'bottom-left': 'nesw-resize',
}

export function installWindowResizeHandles(): () => void {
  if (!isDesktopShell() || typeof document === 'undefined') return () => {}

  let windowState: DesktopWindowState = 'normal'
  const unsubscribe = subscribeWindowState(state => { windowState = state })
  sendWindowCommand('get-state')

  const directionFor = (x: number, y: number): DesktopResizeDirection | null => {
    const zone = WindowResizeZonePixels
    const left = x < zone
    const right = x >= window.innerWidth - zone
    const top = y < zone
    const bottom = y >= window.innerHeight - zone
    if (top && left) return 'top-left'
    if (top && right) return 'top-right'
    if (bottom && left) return 'bottom-left'
    if (bottom && right) return 'bottom-right'
    if (left) return 'left'
    if (right) return 'right'
    if (top) return 'top'
    if (bottom) return 'bottom'
    return null
  }

  const onPointerMove = (event: PointerEvent) => {
    const direction = windowState === 'normal' && event.buttons === 0
      ? directionFor(event.clientX, event.clientY)
      : null
    document.body.style.cursor = direction ? resizeCursors[direction] : ''
  }
  const onPointerDown = (event: PointerEvent) => {
    if (windowState !== 'normal' || event.button !== 0) return
    const direction = directionFor(event.clientX, event.clientY)
    if (!direction) return
    event.preventDefault()
    sendWindowCommand('begin-resize', direction)
  }

  document.addEventListener('pointermove', onPointerMove, true)
  document.addEventListener('pointerdown', onPointerDown, true)
  return () => {
    document.removeEventListener('pointermove', onPointerMove, true)
    document.removeEventListener('pointerdown', onPointerDown, true)
    document.body.style.cursor = ''
    unsubscribe()
  }
}
