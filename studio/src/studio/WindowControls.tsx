import { useEffect, useState } from 'react'
import { Copy, Minus, Square, X } from 'lucide-react'
import {
  isDesktopShell,
  sendWindowCommand,
  subscribeWindowState,
  type DesktopWindowState,
} from './desktopWindowBridge'

// Window caption buttons for the desktop shell, which hides the native
// Windows title bar. Rendered only inside the WebView2 host; returns null in
// a plain browser where the native chrome is still present.
export default function WindowControls() {
  const desktop = isDesktopShell()
  const [windowState, setWindowState] = useState<DesktopWindowState>('normal')

  useEffect(() => {
    if (!desktop) return undefined
    sendWindowCommand('get-state')
    return subscribeWindowState(setWindowState)
  }, [desktop])

  if (!desktop) return null

  const maximized = windowState === 'maximized'
  return (
    <div className="flex items-center gap-1" data-window-controls>
      <button
        className="icon-button"
        aria-label="Minimize window"
        title="Minimize"
        onClick={() => sendWindowCommand('minimize')}
      >
        <Minus className="h-3.5 w-3.5" />
      </button>
      <button
        className="icon-button"
        aria-label={maximized ? 'Restore window' : 'Maximize window'}
        title={maximized ? 'Restore' : 'Maximize'}
        onClick={() => sendWindowCommand('toggle-maximize')}
      >
        {maximized ? <Copy className="h-3.5 w-3.5" /> : <Square className="h-3 w-3" />}
      </button>
      <button
        className="icon-button hover:bg-red-600 hover:text-white"
        aria-label="Close window"
        title="Close"
        onClick={() => sendWindowCommand('close')}
      >
        <X className="h-3.5 w-3.5" />
      </button>
    </div>
  )
}
