import { TooltipProvider } from '@/components/ui/tooltip'
import ErrorBoundary from '@/components/ErrorBoundary'
import MainStudio from '@/studio/MainStudio'
import { Toaster } from '@/components/ui/sonner'

export default function App() {
  return (
    <ErrorBoundary>
      <TooltipProvider>
        <MainStudio />
        <Toaster />
      </TooltipProvider>
    </ErrorBoundary>
  )
}
