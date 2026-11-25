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
function currentBook(market: MockMarketSource) {
  let snapshot: MarketMessage | undefined
  const stop = market.subscribe((message) => { if (message.type === 'bookSnapshot') snapshot = message })
  stop()
  return snapshot
}

test.each([
  { price: 12930, quantity: 10, reason: 'AboveUpperLimit', ruleId: 'R-LIMIT-04' },
  { price: 6950, quantity: 10, reason: 'BelowLowerLimit', ruleId: 'R-LIMIT-04' },
  { price: 10005, quantity: 10, reason: 'InvalidTick', ruleId: 'R-TICK-02' },
  { price: 9990, quantity: 0, reason: 'InvalidOrder', ruleId: null },
  { price: 9990, quantity: -1, reason: 'InvalidOrder', ruleId: null },
  { price: 9990, quantity: 1.5, reason: 'InvalidOrder', ruleId: null },
])('$reason: $price원 / $quantity주 거부 시 호가와 기존 주문은 그대로다', async ({ price, quantity, reason, ruleId }) => {
  const market = new MockMarketSource()
  const accepted = await market.placeOrder({ side: 'Buy', price: 9990, quantity: 10 })
  expect(accepted.accepted).toBe(true)
  const beforeBook = currentBook(market)
  const beforeOrder = await market.getExplanation(accepted.orderId!)
  const beforeHash = await market.getStateHash()

  expect(await market.placeOrder({ side: 'Buy', price, quantity })).toEqual({ orderId: null, accepted: false, reason, ruleId })
  expect(currentBook(market)).toEqual(beforeBook)
  expect(await market.getExplanation(accepted.orderId!)).toEqual(beforeOrder)
  expect(await market.getStateHash()).toEqual(beforeHash)
})

test('이미 취소된 주문의 재취소는 호가와 주문 상태를 바꾸지 않는다', async () => {
  const market = new MockMarketSource()
  const accepted = await market.placeOrder({ side: 'Buy', price: 9990, quantity: 10 })
  const id = accepted.orderId!
  expect(await market.cancelOrder(id)).toEqual({ orderId: id, cancelled: true, reason: null })
  const beforeBook = currentBook(market)
  const beforeOrder = await market.getExplanation(id)
  const beforeHash = await market.getStateHash()
  expect(beforeOrder).toMatchObject({ status: 'Cancelled', remainingQuantity: 0 })

  expect(await market.cancelOrder(id)).toEqual({ orderId: id, cancelled: false, reason: 'OrderNotOpen' })
  expect(currentBook(market)).toEqual(beforeBook)
  expect(await market.getExplanation(id)).toEqual(beforeOrder)
  expect(await market.getStateHash()).toEqual(beforeHash)
})

test('주문·시세를 저널에서 복구하고 첫 새 이벤트에서 복구 표시를 해제한다', async () => {
  const market = new MockMarketSource()
  const accepted = await market.placeOrder({ side: 'Buy', price: 9990, quantity: 10 })
  expect(accepted.accepted).toBe(true)
  await market.runScenario('FatFinger')
  for (let i = 0; i < 3; i++) market.advance()
  expect(await market.getExplanation(accepted.orderId!)).toMatchObject({ status: 'WaitingAuction' })
  const before = await market.getStateHash()
  expect(await market.crash()).toEqual({ ok: true })
  const restored = { ...before, restoredFromJournal: true }
  expect(await market.getStateHash()).toEqual(restored)
  expect(await market.placeOrder({ side: 'Buy', price: 9990, quantity: 0 })).toMatchObject({ accepted: false })
  expect(await market.getStateHash()).toEqual(restored)
  expect(await market.cancelOrder(accepted.orderId!)).toMatchObject({ cancelled: true })
  expect(await market.getStateHash()).toMatchObject({ restoredFromJournal: false })
  await market.crash()
  expect(await market.getStateHash()).toMatchObject({ restoredFromJournal: true })
  market.advance()
  expect(await market.getStateHash()).toMatchObject({ restoredFromJournal: false })
})

test('목 demo는 시장 시드와 무관하게 시연 시드 16의 실제 ID로 응답한다', async () => {
  const demo = await new MockMarketSource(42).getExperiment('demo')
  expect(demo).toMatchObject({ id: 'exp-FatFinger-16', seed: 16, scenario: 'FatFinger' })
  expect(demo).toEqual(await new MockMarketSource(43).getExperiment('exp-FatFinger-16'))
  expect(demo?.runs.map(({ viEnabled }) => viEnabled)).toEqual([true, false])
})
