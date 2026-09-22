import { Network } from 'lucide-react'
import { useMemo } from 'react'
import type { HardwareNetworkNode, HardwareNetworkView } from '@/api/client'

type Props = {
  view: HardwareNetworkView | null
}

const UNLINKED = '— Unlinked'

function EmptyNetwork({ message }: { message: string }) {
  return (
    <div className="grid h-full min-h-[520px] place-items-center p-8">
      <div className="max-w-md text-center">
        <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-2xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
          <Network className="h-7 w-7 text-chart-2" />
        </div>
        <h2 className="text-base font-semibold">Network list</h2>
        <p className="mt-2 text-[10px] leading-relaxed text-muted-foreground">{message}</p>
      </div>
    </div>
  )
}

const compareAddresses = (a: string, b: string) => {
  const octetsA = a.split('.').map(part => Number.parseInt(part, 10))
  const octetsB = b.split('.').map(part => Number.parseInt(part, 10))
  for (let index = 0; index < 4; index += 1) {
    const left = octetsA[index]
    const right = octetsB[index]
    if (Number.isNaN(left) || Number.isNaN(right)) return a.localeCompare(b)
    if (left !== right) return left - right
  }
  return 0
}

export default function HardwareNetworkView({ view }: Props) {
  const groups = useMemo(() => {
    const bySubnet = new Map<string, HardwareNetworkNode[]>()
    for (const node of view?.nodes ?? []) {
      const key = node.subnetName ?? UNLINKED
      const list = bySubnet.get(key) ?? []
      list.push(node)
      bySubnet.set(key, list)
    }
    return [...bySubnet.entries()]
      .map(([subnet, nodes]) => ({
        subnet,
        nodes: [...nodes].sort((a, b) => compareAddresses(a.address, b.address)),
      }))
      .sort((a, b) => {
        if (a.subnet === UNLINKED) return 1
        if (b.subnet === UNLINKED) return -1
        return a.subnet.localeCompare(b.subnet)
      })
  }, [view])

  if (!view || view.state !== 'available') {
    return <EmptyNetwork message={view?.message ?? 'Loading hardware network list...'} />
  }

  return (
    <div className="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
      <header className="flex shrink-0 items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
        <div className="grid h-10 w-10 place-items-center rounded-xl bg-chart-2/10">
          <Network className="h-5 w-5 text-chart-2" />
        </div>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <h1 className="text-sm font-semibold">Network list</h1>
            <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-[8px] font-medium uppercase tracking-[0.12em] text-emerald-600 dark:text-emerald-400">AML loaded</span>
          </div>
          <p className="mt-1 text-[9px] text-muted-foreground">Every addressed network node, grouped by subnet.</p>
        </div>
        <div className="ml-auto flex items-center gap-3 text-[9px] text-muted-foreground">
          <span>{view.nodes.length} node{view.nodes.length === 1 ? '' : 's'}</span>
          <span>{groups.filter(group => group.subnet !== UNLINKED).length} subnet{groups.filter(group => group.subnet !== UNLINKED).length === 1 ? '' : 's'}</span>
          {view.exportedAt && <span>{new Date(view.exportedAt).toLocaleString()}</span>}
        </div>
      </header>

      <div className="scrollbar-sleek min-h-0 flex-1 overflow-auto">
        {view.nodes.length === 0 ? (
          <div className="p-8 text-center text-[10px] text-muted-foreground">No addressed network nodes found in the project AML.</div>
        ) : groups.map(group => (
          <section key={group.subnet} className="border-b" style={{ borderColor: 'var(--border)' }}>
            <div className="flex items-center gap-2 border-b bg-muted/30 px-5 py-2" style={{ borderColor: 'var(--border)' }}>
              <Network className="h-3.5 w-3.5 text-chart-2" />
              <span className="text-[10px] font-semibold">{group.subnet}</span>
              <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">{group.nodes.length}</span>
            </div>
            <table className="w-full text-[10px]">
              <thead className="sticky top-0 bg-card">
                <tr className="border-b text-left text-[9px] uppercase tracking-wide text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
                  <th className="px-4 py-2 font-medium">IP address</th>
                  <th className="px-4 py-2 font-medium">Device name</th>
                  <th className="px-4 py-2 font-medium">PROFINET name</th>
                  <th className="px-4 py-2 font-medium">Interface</th>
                  <th className="px-4 py-2 font-medium">Subnet mask</th>
                  <th className="px-4 py-2 font-medium">Device path</th>
                </tr>
              </thead>
              <tbody>
                {group.nodes.map(node => (
                  <tr key={node.id} className="border-b hover:bg-accent/40" style={{ borderColor: 'var(--border)' }}>
                    <td className="px-4 py-1.5 font-mono font-medium">{node.address}</td>
                    <td className="px-4 py-1.5">{node.deviceName}</td>
                    <td className="px-4 py-1.5 font-mono text-muted-foreground">{node.profinetDeviceName ?? '—'}</td>
                    <td className="px-4 py-1.5">{node.interfaceLabel ?? '—'}</td>
                    <td className="px-4 py-1.5 font-mono text-muted-foreground">{node.subnetMask ?? '—'}</td>
                    <td className="px-4 py-1.5 font-mono text-[9px] text-muted-foreground">{node.devicePath}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>
        ))}
      </div>
    </div>
  )
}
