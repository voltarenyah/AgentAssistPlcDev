import { Input } from '@/components/ui/input'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Slider } from '@/components/ui/slider'
import { Toggle } from '@/components/ui/toggle'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { ColorPicker } from '@/components/ui/color-picker'
import { Bold, Italic, Underline } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ButtonGroup } from '@/components/ui/button-group'

export default function FormPage() {
  return (
    <div className="space-y-12">
      <div>
        <h2 className="mb-4 text-lg font-semibold">Input</h2>
        <div className="flex flex-wrap items-end gap-4">
          <div className="grid gap-1.5">
            <Label htmlFor="input-default">Default</Label>
            <Input id="input-default" placeholder="Placeholder" className="w-64" />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="input-disabled">Disabled</Label>
            <Input id="input-disabled" disabled placeholder="Disabled" className="w-64" />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="input-file">File</Label>
            <Input id="input-file" type="file" className="w-64" />
          </div>
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Checkbox</h2>
        <div className="flex items-center gap-6">
          <div className="flex items-center gap-2">
            <Checkbox id="check-1" />
            <Label htmlFor="check-1">Unchecked</Label>
          </div>
          <div className="flex items-center gap-2">
            <Checkbox id="check-2" defaultChecked />
            <Label htmlFor="check-2">Checked</Label>
          </div>
          <div className="flex items-center gap-2">
            <Checkbox id="check-3" disabled />
            <Label htmlFor="check-3">Disabled</Label>
          </div>
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Select</h2>
        <Select>
          <SelectTrigger className="w-48">
            <SelectValue placeholder="Select a fruit" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="apple">Apple</SelectItem>
            <SelectItem value="banana">Banana</SelectItem>
            <SelectItem value="orange">Orange</SelectItem>
            <SelectItem value="grape" disabled>
              Grape (out of stock)
            </SelectItem>
          </SelectContent>
        </Select>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Slider</h2>
        <div className="w-64">
          <Slider defaultValue={[50]} max={100} step={1} />
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Toggle</h2>
        <div className="flex flex-wrap items-center gap-3">
          <Toggle aria-label="Toggle bold">
            <Bold className="size-4" />
          </Toggle>
          <Toggle aria-label="Toggle italic">
            <Italic className="size-4" />
          </Toggle>
          <Toggle aria-label="Toggle underline">
            <Underline className="size-4" />
          </Toggle>
        </div>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Toggle Group</h2>
        <ToggleGroup type="multiple">
          <ToggleGroupItem value="bold" aria-label="Toggle bold">
            <Bold className="size-4" />
          </ToggleGroupItem>
          <ToggleGroupItem value="italic" aria-label="Toggle italic">
            <Italic className="size-4" />
          </ToggleGroupItem>
          <ToggleGroupItem value="underline" aria-label="Toggle underline">
            <Underline className="size-4" />
          </ToggleGroupItem>
        </ToggleGroup>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Button Group</h2>
        <ButtonGroup>
          <Button variant="outline">Left</Button>
          <Button variant="outline">Center</Button>
          <Button variant="outline">Right</Button>
        </ButtonGroup>
      </div>

      <div>
        <h2 className="mb-4 text-lg font-semibold">Color Picker</h2>
        <ColorPicker value="#6366f1" onChange={() => {}} />
      </div>
    </div>
  )
}
