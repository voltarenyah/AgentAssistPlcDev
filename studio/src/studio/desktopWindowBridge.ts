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
  | 'get-state'

export type DesktopWindowState = 'normal' | 'maximized'

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

export function sendWindowCommand(command: DesktopWindowCommand): void {
  webview()?.postMessage({ type: 'window-control', command })
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
