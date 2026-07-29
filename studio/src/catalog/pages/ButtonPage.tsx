import { Button } from '@/components/ui/button'

const variants = ['default', 'secondary', 'destructive', 'outline', 'ghost', 'link'] as const
const sizes = ['xs', 'sm', 'default', 'lg', 'icon', 'icon-xs', 'icon-sm', 'icon-lg'] as const

export default function ButtonPage() {
  return (
    <div className="space-y-12">
      <div>
        <h2 className="mb-4 text-lg font-semibold">Variants</h2>
        <div className="flex flex-wrap items-center gap-3">
          {variants.map((v) => (
            <Button key={v} variant={v}>
              {v}
            </Button>
          ))}
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Sizes</h2>
        <div className="flex flex-wrap items-center gap-3">
          {sizes.map((s) => (
            <Button key={s} size={s}>
              {s === 'icon' || s === 'icon-xs' || s === 'icon-sm' || s === 'icon-lg' ? 'A' : s}
            </Button>
          ))}
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">States</h2>
        <div className="flex flex-wrap items-center gap-3">
          <Button disabled>Disabled</Button>
          <Button asChild>
            <a href="#">As Child (link)</a>
          </Button>
        </div>
      </div>
    </div>
  )
}
