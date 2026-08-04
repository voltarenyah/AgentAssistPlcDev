import type {
  HardwareConfigurationIoRange,
  HardwareConfigurationNode,
  HardwareConfigurationTag,
} from '@/api/client'

type ParsedLogicalAddress = {
  byte: number
  bit: number
}

export function tagsForHardwareNode(
  node: HardwareConfigurationNode,
  tags: HardwareConfigurationTag[],
): HardwareConfigurationTag[] {
  if (node.ioRanges.length === 0 || tags.length === 0) return []
  return tags.filter(tag => node.ioRanges.some(range =>
    range.ioType.toLowerCase() === tag.ioType.toLowerCase()
    && addressInRange(parseLogicalAddress(tag.logicalAddress), range),
  ))
}

export function parseLogicalAddress(value: string): ParsedLogicalAddress | null {
  const normalized = value.trim().replace(/^%/u, '')
  const match = normalized.match(/^(?:I|Q|M|W|B|D)?(\d+)(?:\.(\d+))?$/iu)
  if (!match) return null
  const byte = Number(match[1])
  const bit = match[2] ? Number(match[2]) : 0
  return Number.isFinite(byte) && Number.isFinite(bit) ? { byte, bit } : null
}

function addressInRange(address: ParsedLogicalAddress | null, range: HardwareConfigurationIoRange): boolean {
  return address !== null && address.byte >= range.startAddress && address.byte <= range.endAddress
}
