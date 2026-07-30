import { Component, type ReactNode, type ErrorInfo } from 'react'

type Props = { children: ReactNode }
type State = { error: Error | null }

export default class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props)
    this.state = { error: null }
  }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[ErrorBoundary]', error, info.componentStack)
  }

  render() {
    if (this.state.error) {
      return (
        <div className="flex h-screen flex-col items-center justify-center gap-4 bg-background p-8">
          <span className="text-lg" style={{ color: 'var(--destructive)' }}>⚠</span>
          <h1 className="text-sm font-semibold" style={{ color: 'var(--foreground)' }}>
            Something went wrong
          </h1>
          <pre className="max-w-xl rounded-md border p-3 text-[10px] leading-relaxed overflow-auto" style={{ background: 'var(--card)', color: 'var(--muted-foreground)', borderColor: 'var(--border)' }}>
            {this.state.error.message}
          </pre>
          <button
            onClick={() => { this.setState({ error: null }); window.location.reload() }}
            className="rounded-md px-4 py-2 text-xs font-medium"
            style={{ background: 'var(--primary)', color: 'var(--primary-foreground)' }}
          >
            Reload
          </button>
        </div>
      )
    }

    return this.props.children
  }
}
