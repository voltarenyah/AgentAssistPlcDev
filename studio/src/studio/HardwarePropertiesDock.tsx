import { CircuitBoard } from 'lucide-react'
import type { HardwareConfigurationNode, HardwareConfigurationTag } from '@/api/client'
import { tagsForHardwareNode } from './hardwareAddressing'

type Props = {
  node: HardwareConfigurationNode | null
  tags: HardwareConfigurationTag[]
  hidden: boolean
}

function PropertyRows({ rows }: { rows: [string, string][] }) {
  return (
    <div className="space-y-2">
      {rows.map(([label, value]) => (
        <div key={label}>
          <div className="text-[10px] uppercase tracking-[0.12em] text-muted-foreground">{label}</div>
          <div className="mt-0.5 break-words text-[11px] leading-4">{value || '—'}</div>
        </div>
      ))}
    </div>
  )
}

export default function HardwarePropertiesDock({ node, tags, hidden }: Props) {
  const boundTags = node ? tagsForHardwareNode(node, tags) : []
  return (
    <div className={`flex h-full min-h-0 w-full flex-col ${hidden ? 'hidden' : ''}`}>
      {!node ? (
        <div className="p-5 text-[11px] leading-4 text-muted-foreground">Select a device or module to inspect its TIA properties.</div>
      ) : (
        <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-4">
          <div className="mb-4 flex items-start gap-2">
            <div className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-chart-2/10">
              <CircuitBoard className="h-4 w-4 text-chart-2" />
            </div>
            <div className="min-w-0">
              <div className="truncate text-[12px] font-semibold">{node.name}</div>
              <div className="mt-1 font-mono text-[10px] text-muted-foreground">{node.kind}</div>
            </div>
          </div>
          <div className="space-y-4">
            <section>
              <div className="mb-2 text-[12px] font-semibold">Identity</div>
              <PropertyRows rows={[
                ['Name', node.name],
                ['ID', node.id],
                ['Type identifier', node.typeIdentifier ?? ''],
              ]} />
            </section>
            {node.ioRanges.length > 0 && (
              <section className="border-t pt-4" style={{ borderColor: 'var(--border)' }}>
                <div className="mb-2 text-[12px] font-semibold">I/O ranges</div>
                <div className="space-y-2">
                  {node.ioRanges.map(range => (
                    <div key={`${range.ioType}-${range.startAddress}-${range.lengthBits}`} className="rounded-lg border bg-muted/10 p-2.5" style={{ borderColor: 'var(--border)' }}>
                      <div className="flex items-center justify-between gap-3">
                        <span className={`text-[11px] font-semibold ${range.ioType.toLowerCase() === 'output' ? 'text-amber-600 dark:text-amber-400' : 'text-sky-600 dark:text-sky-400'}`}>{range.ioType}</span>
                        <span className="font-mono text-[11px] font-semibold">{range.addressRange}</span>
                      </div>
                      <div className="mt-1.5 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[10px]">
                        <span className="uppercase tracking-[0.12em] text-muted-foreground">StartAddress</span><span className="font-mono text-[11px]">{range.startAddress}</span>
                        <span className="uppercase tracking-[0.12em] text-muted-foreground">Length</span><span className="font-mono text-[11px]">{range.lengthBits} bit · {Math.ceil(range.lengthBits / 8)} bytes</span>
                        <span className="uppercase tracking-[0.12em] text-muted-foreground">EndAddress</span><span className="font-mono text-[11px]">{range.endAddress}</span>
                      </div>
                    </div>
                  ))}
                </div>
              </section>
            )}
            {boundTags.length > 0 && (
              <section className="border-t pt-4" style={{ borderColor: 'var(--border)' }}>
                <div className="mb-2 text-[12px] font-semibold">Bound tags ({boundTags.length})</div>
                <div className="space-y-2">
                  {boundTags.map(tag => (
                    <div key={tag.id} className="rounded-lg border bg-muted/10 p-2.5" style={{ borderColor: 'var(--border)' }}>
                      <div className="flex items-center justify-between gap-2">
                        <span className="min-w-0 truncate text-[11px] font-medium">{tag.name}</span>
                        <span className="font-mono text-[10px] text-muted-foreground">{tag.ioType}</span>
                      </div>
                      <div className="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[10px]">
                        <span className="uppercase tracking-[0.12em] text-muted-foreground">LogicalAddress</span><span className="font-mono text-[11px]">{tag.logicalAddress}</span>
                        <span className="uppercase tracking-[0.12em] text-muted-foreground">DataType</span><span className="font-mono text-[11px]">{tag.dataType}</span>
                        {tag.ownerPath && <><span className="uppercase tracking-[0.12em] text-muted-foreground">Owner</span><span className="truncate font-mono text-[11px]">{tag.ownerPath}</span></>}
                      </div>
                    </div>
                  ))}
                </div>
              </section>
            )}
            {node.properties.length > 0 && (
              <section className="border-t pt-4" style={{ borderColor: 'var(--border)' }}>
                <div className="mb-2 text-[12px] font-semibold">AML attributes</div>
                <PropertyRows rows={node.properties.map(property => [property.name, property.value])} />
              </section>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
