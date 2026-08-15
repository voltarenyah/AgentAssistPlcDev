import { Moon, Sun } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { setThemePreference, useThemePreference } from '@/studio/theme'

export function ThemeToggle() {
  const theme = useThemePreference()
  const dark = theme === 'dark'

  return (
    <Button
      variant="ghost"
      size="icon"
      onClick={() => setThemePreference(dark ? 'light' : 'dark')}
      aria-label={dark ? 'Switch to light theme' : 'Switch to dark theme'}
    >
      {dark ? <Sun className="size-4" /> : <Moon className="size-4" />}
    </Button>
  )
}
