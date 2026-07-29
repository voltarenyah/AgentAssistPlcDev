import * as React from 'react'
import { HexColorPicker } from 'react-colorful'

import { cn } from '@/lib/utils'
import { Button } from './button'
import { Input } from './input'
import { Label } from './label'
import { Popover, PopoverContent, PopoverTrigger } from './popover'

type ColorPickerProps = {
  value: string
  onChange: (value: string) => void
  label?: string
  className?: string
  defaultOpen?: boolean
  selected?: boolean
  triggerLabel?: string
  showHexInTrigger?: boolean
}

const FULL_HEX_COLOR_PATTERN = /^#[0-9a-fA-F]{6}$/

function normalizeHex(value: string): string | null {
  const trimmed = value.trim()
  const withHash = trimmed.startsWith('#') ? trimmed : `#${trimmed}`
  if (FULL_HEX_COLOR_PATTERN.test(withHash)) {
    return withHash.toLowerCase()
  }
  return null
}

export function ColorPicker({
  value,
  onChange,
  label,
  className,
  defaultOpen,
  selected,
  triggerLabel,
  showHexInTrigger
}: ColorPickerProps): React.JSX.Element {
  const inputId = React.useId()
  const [draft, setDraft] = React.useState(value)
  const [isEditing, setIsEditing] = React.useState(false)

  const displayValue = isEditing ? draft : value
  const normalized = normalizeHex(displayValue)
  const hasInvalidDraft = displayValue.trim().length > 0 && !normalized

  const handlePickerChange = (nextColor: string): void => {
    setDraft(nextColor)
    setIsEditing(true)
    onChange(nextColor)
  }

  const handleInputChange = (nextDraft: string): void => {
    setDraft(nextDraft)
    setIsEditing(true)
    const normalized = normalizeHex(nextDraft)
    if (normalized) {
      onChange(normalized)
    }
  }

  const handleBlur = (): void => {
    const normalized = normalizeHex(draft)
    if (normalized) {
      setDraft(normalized)
      onChange(normalized)
    } else {
      setDraft(value)
    }
    setIsEditing(false)
  }

  return (
    <Popover defaultOpen={defaultOpen}>
      <PopoverTrigger asChild>
        <Button
          type="button"
          variant="outline"
          size="sm"
          className={cn(
            'h-8 gap-2 px-2.5',
            selected ? 'ring-2 ring-foreground ring-offset-2 ring-offset-background' : null,
            className
          )}
          aria-label={label}
          aria-pressed={selected}
        >
          <span
            aria-hidden="true"
            className="size-4 rounded-[4px] border border-border/70"
            style={{ backgroundColor: value }}
          />
          {triggerLabel ? <span className="text-xs">{triggerLabel}</span> : null}
          {showHexInTrigger ?? !triggerLabel ? (
            <span className="font-mono text-xs uppercase">{value}</span>
          ) : null}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-64 p-3">
        <div className="space-y-3">
          <HexColorPicker
            color={value}
            onChange={handlePickerChange}
            className="[&_.react-colorful__hue]:rounded-b-md [&_.react-colorful__interactive:focus_.react-colorful__pointer]:ring-[3px] [&_.react-colorful__interactive:focus_.react-colorful__pointer]:ring-ring/50 [&_.react-colorful__pointer]:border-popover"
            style={{ width: '100%', height: 180 }}
          />
          <div className="flex items-center justify-between gap-3">
            <Label htmlFor={inputId}>Hex</Label>
            <span className="font-mono text-xs uppercase text-muted-foreground">{value}</span>
          </div>
          <Input
            id={inputId}
            value={displayValue}
            onFocus={() => setIsEditing(true)}
            onChange={(event) => handleInputChange(event.target.value)}
            onBlur={handleBlur}
            placeholder={value}
            aria-invalid={hasInvalidDraft}
            className="font-mono text-xs uppercase"
          />
          {hasInvalidDraft ? (
            <p className="text-xs text-destructive">Invalid hex color.</p>
          ) : null}
        </div>
      </PopoverContent>
    </Popover>
  )
}
