import { Moon, Sun } from 'lucide-react'
import { Button } from '@notion-kit/ui/primitives'
import { setThemePreference, useThemePreference } from '@/studio/theme'

export function ThemeToggle() {
  const theme = useThemePreference()
  const dark = theme === 'dark'

  return (
    <Button
      variant="nav-icon"
      onClick={() => setThemePreference(dark ? 'light' : 'dark')}
      aria-label={dark ? 'Switch to light theme' : 'Switch to dark theme'}
    >
      {dark ? <Sun className="size-4" /> : <Moon className="size-4" />}
    </Button>
  )
}
