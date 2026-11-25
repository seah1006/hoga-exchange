import { expect, test, vi } from 'vitest'
import { EXPERIMENT_ID, MockMarketSource } from './mockMarketSource'
import type { MarketMessage } from './contract'

async function collect(seed: number, scenario: 'Calm' | 'FatFinger' | 'PanicSell' = 'FatFinger') {
  const market = new MockMarketSource(seed)
  const messages: MarketMessage[] = []
  const stop = market.subscribe((message) => messages.push(message))
  await market.runScenario(scenario)
  for (let i = 0; i < 30; i++) market.advance()
  stop()
  return messages
}
test('같은 시드는 같은 메시지 수열을 만든다', async () => {
  expect(await collect(42)).toEqual(await collect(42))
  expect(await collect(43)).not.toEqual(await collect(42))
})
test('R-TICK-02 / R-LIMIT-04: 메시지와 실험 가격은 호가단위를 지키고 상하한가 안이다', async () => {
  const prices: number[] = []
  for (const scenario of ['Calm', 'FatFinger', 'PanicSell'] as const) {
    for (const message of await collect(42, scenario)) {
      if (message.type === 'bookSnapshot') {
        const b = message.data
        prices.push(b.basePrice, b.lowerLimit, b.upperLimit, b.lastPrice, ...[...b.asks, ...b.bids].map((l) => l.price))
        if (b.expectedPrice != null) prices.push(b.expectedPrice)
        if (b.vi) prices.push(b.vi.referencePrice, b.vi.triggerPrice)
      } else if (message.type === 'candle') prices.push(message.data.open, message.data.high, message.data.low, message.data.close)
      else if (message.type === 'trade') prices.push(message.data.price)
      else {
        prices.push(message.data.referencePrice, message.data.triggerPrice)
        if (message.data.type === 'Released') prices.push(message.data.auctionPrice)
      }
    }
  }
  const experiment = await new MockMarketSource().getExperiment(EXPERIMENT_ID)
  for (const run of experiment!.runs) prices.push(run.minPrice, run.maxPrice, ...run.series.map((p) => p.price))
  expect(experiment!.runs[0].minPrice).toBeGreaterThan(experiment!.runs[1].minPrice)
  expect(experiment!.runs[0].series.slice(0, 3)).toEqual(experiment!.runs[1].series.slice(0, 3))
  expect(prices.filter((p) => p < 6960 || p > 12920 || p % 10 !== 0)).toEqual([])
})
test('R-VI-01 / R-VI-04: 기본 10배속에서 발동 12초 뒤 해제한다', async () => {
  vi.useFakeTimers()
  try {
    const market = new MockMarketSource()
    const messages: MarketMessage[] = []
    const stop = market.subscribe((m) => messages.push(m))
    await market.runScenario('FatFinger')
    vi.advanceTimersByTime(3000)
    expect(messages.at(-1)).toMatchObject({ type: 'bookSnapshot', data: { phase: 'CallAuction', vi: { kind: 'Dynamic', startMs: 30_000, scheduledEndMs: 150_000 } } })
    vi.advanceTimersByTime(11999)
    expect(messages.at(-1)).toMatchObject({ data: { phase: 'CallAuction' } })
    vi.advanceTimersByTime(1)
    expect(messages.at(-1)).toMatchObject({ type: 'bookSnapshot', data: { phase: 'Continuous', vi: null } })
    expect(messages).toContainEqual(expect.objectContaining({ type: 'viEvent', data: expect.objectContaining({ type: 'Released', atMs: 150_000 }) }))
    stop()
  } finally { vi.useRealTimers() }
})
test('R-LIMIT-04 주문 거부와 주문·시세 저널 복구', async () => {
  const market = new MockMarketSource()
  expect(await market.placeOrder({ side: 'Buy', price: 12930, quantity: 10 })).toMatchObject({ accepted: false, ruleId: 'R-LIMIT-04' })
  const accepted = await market.placeOrder({ side: 'Buy', price: 9990, quantity: 10 })
  expect(accepted.accepted).toBe(true)
  await market.runScenario('FatFinger')
  for (let i = 0; i < 3; i++) market.advance()
  expect(await market.getExplanation(accepted.orderId!)).toMatchObject({ status: 'WaitingAuction' })
  const before = await market.getStateHash()
  expect(await market.crash()).toEqual({ ok: true })
  expect(await market.getStateHash()).toEqual({ ...before, restoredFromJournal: true })
  expect(await market.cancelOrder(accepted.orderId!)).toMatchObject({ cancelled: true })
})
