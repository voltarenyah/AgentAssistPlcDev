import { TooltipProvider } from '@/components/ui/tooltip'
import ErrorBoundary from '@/components/ErrorBoundary'
import MainStudio from '@/studio/MainStudio'
import { Toaster } from '@/components/ui/sonner'
import LangGraphFlowPage, { isLangGraphFlowDevRoute } from '@/dev/LangGraphFlowPage'

export default function App() {
  if (isLangGraphFlowDevRoute(window.location.href, import.meta.env.DEV)) {
    return <LangGraphFlowPage />
  }
  return (
    <ErrorBoundary>
      <TooltipProvider>
        <MainStudio />
        <Toaster />
      </TooltipProvider>
    </ErrorBoundary>
  )
}
