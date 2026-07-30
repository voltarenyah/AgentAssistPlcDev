import { Badge } from '@/components/ui/badge'

const variants = ['default', 'secondary', 'destructive', 'outline'] as const

export default function BadgePage() {
  return (
    <div className="space-y-12">
      <div>
        <h2 className="mb-4 text-lg font-semibold">Variants</h2>
        <div className="flex flex-wrap items-center gap-3">
          {variants.map((v) => (
            <Badge key={v} variant={v}>
              {v}
            </Badge>
          ))}
        </div>
      </div>
    </div>
  )
}
