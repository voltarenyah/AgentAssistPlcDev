import { afterEach, describe, expect, it, vi } from 'vitest'
import { bootstrapAppAssistant } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('app assistant client', () => {
  it('sends an empty bootstrap message for orientation', async () => {
    const fetchMock = vi.fn(async () => new Response('', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await bootstrapAppAssistant()

    const request = fetchMock.mock.calls[0]?.[1] as RequestInit
    expect(JSON.parse(String(request.body))).toEqual({ message: '' })
  })

  it('sends the assistant session id with bootstrap requests', async () => {
    const fetchMock = vi.fn(async () => new Response('', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await bootstrapAppAssistant('session-1')

    const request = fetchMock.mock.calls[0]?.[1] as RequestInit
    expect(JSON.parse(String(request.body))).toEqual({ message: '', sessionId: 'session-1' })
  })
})
