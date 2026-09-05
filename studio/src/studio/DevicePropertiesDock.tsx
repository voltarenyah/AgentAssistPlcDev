import { Boxes } from 'lucide-react'
import * as api from '@/api/client'

type Props = {
  meta: api.DeviceExportMetadata | null
  info: api.DeviceInfo | null
  hidden: boolean
}

function PropertyRows({ rows, mono = false }: { rows: [string, string | null][]; mono?: boolean }) {
  const visible = rows.filter(([, value]) => value)
  if (visible.length === 0) return null
  return (
    <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
      {visible.map(([label, value]) => (
        <div key={label} className="px-2 py-1.5">
          <div className="text-[8px] uppercase tracking-[0.12em] text-muted-foreground">{label}</div>
          <div className={`mt-0.5 break-all text-[10px] leading-relaxed ${mono ? 'font-mono' : ''}`}>{value}</div>
        </div>
      ))}
    </div>
  )
}

export default function DevicePropertiesDock({ meta, info, hidden }: Props) {
  const projectRows: [string, string | null][] = meta
    ? [
        ['Project', meta.projectName],
        ['Author', meta.projectAuthor],
        ['Version', meta.projectVersion],
        ['Copyright', meta.projectCopyright],
        ['Created', meta.projectCreationTime ? new Date(meta.projectCreationTime).toLocaleString() : null],
        ['Last modified', meta.projectLastModified ? new Date(meta.projectLastModified).toLocaleString() : null],
        ['Modified by', meta.projectLastModifiedBy],
      ]
    : []
  const deviceRows: [string, string | null][] = meta
    ? [
        ['PLC name', meta.plcName],
        ['Device name', meta.deviceName],
        ['PLC type', meta.typeIdentifier?.replace(/^OrderNumber:/, '') ?? null],
      ]
    : []
  const fSignatureStates: Record<string, string> = {
    ok: 'OK',
    'no-signature': 'No signature (Safety license required)',
    'read-failed': 'Read failed',
  }
  const safetyRows: [string, string | null][] = meta
    ? [
        ['Failsafe device', meta.isSafetyDevice == null ? null : meta.isSafetyDevice ? 'Yes' : 'No'],
        ['F-signature state', meta.fSignatureReadState ? (fSignatureStates[meta.fSignatureReadState] ?? meta.fSignatureReadState) : null],
        ['F-signature', meta.fSignature],
      ]
    : []
  const pathRows: [string, string | null][] = [
    ['TIA project', info?.sourceProjectPath ?? null],
    ['PLC source', info?.sourceRoot ?? null],
    ['Knowledge DB', info?.knowledgeDbPath ?? null],
  ]

  return (
    <aside
      hidden={hidden}
      className="flex h-full w-full shrink-0 flex-col border-l bg-card"
      style={{ borderColor: 'var(--border)' }}
    >
      <div className="flex h-10 items-center gap-2 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        <Boxes className="h-3.5 w-3.5 text-chart-3" />
        <h2 className="text-[10px] font-semibold">Device properties</h2>
      </div>
      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
        {!meta && !info ? (
          <div className="grid h-full place-items-center px-5 text-center text-[10px] text-muted-foreground">
            <div>
              <Boxes className="mx-auto mb-2 h-5 w-5" />
              Device details appear once the snapshot loads
            </div>
          </div>
        ) : (
          <div className="space-y-2">
            {meta && deviceRows.some(([, value]) => value) && (
              <section className="rounded-md border bg-background" style={{ borderColor: 'var(--border)' }}>
                <div className="border-b px-2 py-1.5" style={{ borderColor: 'var(--border)' }}>
                  <div className="text-[10px] font-medium">Device</div>
                  <div className="mt-0.5 text-[8px] text-muted-foreground">Captured at last export</div>
                </div>
                <PropertyRows rows={deviceRows} />
              </section>
            )}
            {meta && safetyRows.some(([, value]) => value) && (
              <section className="rounded-md border bg-background" style={{ borderColor: 'var(--border)' }} data-testid="device-safety-section">
                <div className="border-b px-2 py-1.5" style={{ borderColor: 'var(--border)' }}>
                  <div className="text-[10px] font-medium">Safety</div>
                  <div className="mt-0.5 text-[8px] text-muted-foreground">Captured at last export</div>
                </div>
                <PropertyRows rows={safetyRows} mono />
              </section>
            )}
            {meta && projectRows.some(([, value]) => value) && (
              <section className="rounded-md border bg-background" style={{ borderColor: 'var(--border)' }}>
                <div className="border-b px-2 py-1.5" style={{ borderColor: 'var(--border)' }}>
                  <div className="text-[10px] font-medium">TIA project</div>
                  <div className="mt-0.5 text-[8px] text-muted-foreground">Captured at last export</div>
                </div>
                <PropertyRows rows={projectRows} />
                {meta.projectComment && (
                  <div className="border-t px-2 py-1.5" style={{ borderColor: 'var(--border)' }}>
                    <div className="text-[8px] uppercase tracking-[0.12em] text-muted-foreground">Project comment</div>
                    <div className="mt-0.5 whitespace-pre-wrap text-[10px] leading-relaxed">{meta.projectComment}</div>
                  </div>
                )}
              </section>
            )}
            {pathRows.some(([, value]) => value) && (
              <section className="rounded-md border bg-background" style={{ borderColor: 'var(--border)' }}>
                <div className="border-b px-2 py-1.5" style={{ borderColor: 'var(--border)' }}>
                  <div className="text-[10px] font-medium">Paths</div>
                </div>
                <PropertyRows rows={pathRows} mono />
              </section>
            )}
          </div>
        )}
      </div>
    </aside>
  )
}
