import { lazy, Suspense, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { ThemeToggle } from './ThemeToggle'
import { Toaster } from '@/components/ui/sonner'

type PageEntry = {
  id: string
  label: string
  component: React.LazyExoticComponent<() => ReactNode>
}

const pages: PageEntry[] = [
  { id: 'buttons', label: 'Buttons', component: lazy(() => import('./pages/ButtonPage')) },
  { id: 'badges', label: 'Badges', component: lazy(() => import('./pages/BadgePage')) },
  { id: 'dialog', label: 'Dialog', component: lazy(() => import('./pages/DialogPage')) },
  { id: 'forms', label: 'Forms', component: lazy(() => import('./pages/FormPage')) },
  { id: 'overlays', label: 'Overlays', component: lazy(() => import('./pages/OverlayPage')) },
  { id: 'layout', label: 'Layout', component: lazy(() => import('./pages/LayoutPage')) },
  { id: 'command', label: 'Command', component: lazy(() => import('./pages/CommandPage')) },
  { id: 'toasts', label: 'Toasts', component: lazy(() => import('./pages/ToastPage')) },
]

function PageLoader() {
  return (
    <div className="flex items-center justify-center py-20 text-sm text-muted-foreground">
      Loading...
    </div>
  )
}

export default function Catalog() {
  const [active, setActive] = useState('buttons')

  const ActivePage = pages.find((p) => p.id === active)!

  return (
    <div className="flex h-full flex-col overflow-hidden">
      {/* Header */}
      <header className="flex h-11 items-center justify-between border-b px-4 shrink-0">
        <h1 className="text-sm font-semibold">Orca UI — Component Catalog</h1>
        <ThemeToggle />
      </header>

      <div className="flex flex-1 overflow-hidden">
        {/* Sidebar navigation */}
        <nav className="w-44 shrink-0 border-r overflow-y-auto p-2">
          {pages.map((p) => (
            <button
              key={p.id}
              onClick={() => setActive(p.id)}
              className={cn(
                'w-full rounded px-3 py-1.5 text-left text-sm transition-colors',
                active === p.id
                  ? 'bg-accent text-accent-foreground font-medium'
                  : 'text-muted-foreground hover:text-foreground hover:bg-accent/50'
              )}
            >
              {p.label}
            </button>
          ))}
        </nav>

        {/* Content area */}
        <main className="flex-1 overflow-y-auto p-6">
          <Suspense fallback={<PageLoader />}>
            <ActivePage.component />
          </Suspense>
        </main>
      </div>

      <Toaster />
    </div>
  )
}
