import { vi } from 'vitest'
import type { Messages } from '../contract'
import type { MarketHub } from '../serverMarketSource'

export class FakeHub implements MarketHub {
  private handlers = new Map<keyof Messages, (data: unknown) => void>()
  reconnecting?: () => void
  reconnected?: () => void
  closed?: () => void
  on<K extends keyof Messages>(name: K, handler: (data: Messages[K]) => void) {
    this.handlers.set(name, (data) => handler(data as Messages[K]))
  }
  onreconnecting(handler: () => void) { this.reconnecting = handler }
  onreconnected(handler: () => void) { this.reconnected = handler }
  onclose(handler: () => void) { this.closed = handler }
  start = vi.fn<() => Promise<void>>().mockResolvedValue(undefined)
  stop = vi.fn<() => Promise<void>>().mockResolvedValue(undefined)
  emit(name: keyof Messages, json: string) { this.handlers.get(name)?.(JSON.parse(json)) }
}
