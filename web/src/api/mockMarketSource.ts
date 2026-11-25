import type { BookSnapshot, Candle, CancelResponse, ExperimentRun, Explanation, Level, MarketMessage, OrderRequest, OrderResponse, Scenario, Vi, ViEvent } from './contract'
import type { MarketSource } from './marketSource'
import { mockExperimentRuns, mockExperimentSummary } from './mockExperiments'
import { formatQuantity } from '../format'

const BASE = 9940, UPPER = 12920, LOWER = 6960, TICK = 10, STEP = 10_000
export const DEFAULT_SPEED = 10
export const EXPERIMENT_ID = 'exp-1'
const priceOnGrid = (price: number) => Math.min(UPPER, Math.max(LOWER, Math.round(price / TICK) * TICK))
const initial = (seed: number) => ({ random: seed >>> 0, seq: 0, time: 0, last: 10000, name: 'Calm' as Scenario, step: 0, vi: null as Vi | null, candle: null as Candle | null, nextOrder: 0, orders: {} as Record<string, OrderRequest & { cancelled: boolean }> })
type Action = { type: 'step' } | { type: 'scenario'; name: Scenario } | { type: 'order'; order: OrderRequest } | { type: 'cancel'; id: string }

export class MockMarketSource implements MarketSource {
  private state
  private journal: Action[] = []
  private listeners = new Set<(message: MarketMessage) => void>()
  private timer?: ReturnType<typeof setInterval>
  private replaying = false
  private restored = false

  constructor(private readonly seed = 42, private readonly speed = DEFAULT_SPEED, private readonly viEnabled = true) {
    this.state = initial(seed)
    if (!Number.isFinite(speed) || speed <= 0) throw new RangeError('시간 배속은 양수여야 합니다.')
  }

  subscribe(handler: (message: MarketMessage) => void) {
    this.listeners.add(handler)
    handler({ type: 'bookSnapshot', data: this.book() })
    if (this.state.candle) handler({ type: 'candle', data: { ...this.state.candle } })
    this.timer ??= setInterval(() => this.advance(), STEP / this.speed)
    return () => {
      this.listeners.delete(handler)
      if (!this.listeners.size) { clearInterval(this.timer); this.timer = undefined }
    }
  }
  private emit(message: MarketMessage) {
    if (!this.replaying) for (const handler of this.listeners) handler(message)
  }
  private book(): BookSnapshot {
    const { last, time, seq, vi, random } = this.state
    const levels = (side: number): Level[] => Array.from({ length: 10 }, (_, i) => ({
      price: last + side * (i + 1) * TICK,
      quantity: 100 + ((random >>> 5) + i * 139 + side * 31) % 2200,
      orderCount: 1 + ((random >>> 10) + i * 3) % 9,
    })).filter(({ price }) => price >= LOWER && price <= UPPER)
    return { symbol: 'HOGA', logicalTimeMs: time, seq, basePrice: BASE, upperLimit: UPPER, lowerLimit: LOWER, lastPrice: last,
      phase: vi ? 'CallAuction' : 'Continuous', vi: vi && { ...vi }, asks: levels(1), bids: levels(-1),
      expectedPrice: vi ? priceOnGrid(vi.triggerPrice + 100) : null, expectedQuantity: vi ? 1800 : null }
  }
  private snapshot() {
    this.state.seq++
    this.emit({ type: 'bookSnapshot', data: this.book() })
  }
  private trade(price: number, quantity: number, auction = false) {
    const state = this.state
    this.emit({ type: 'trade', data: { symbol: 'HOGA', seq: ++state.seq, logicalTimeMs: state.time, price, quantity,
      aggressorSide: auction ? null : price >= state.last ? 'Buy' : 'Sell' } })
    state.last = price
    const startMs = Math.floor(state.time / 60_000) * 60_000
    const bar = state.candle
    state.candle = bar?.startMs === startMs
      ? { ...bar, high: Math.max(bar.high, price), low: Math.min(bar.low, price), close: price, volume: bar.volume + quantity }
      : { symbol: 'HOGA', startMs, open: price, high: price, low: price, close: price, volume: quantity }
    this.emit({ type: 'candle', data: { ...state.candle } })
  }
  private viEvent(vi: Vi, atMs: number, auctionPrice?: number) {
    const { kind, referencePrice, triggerPrice, ruleId } = vi
    const common = { kind, referencePrice, triggerPrice, ruleId, atMs }
    this.emit({ type: 'viEvent', data: auctionPrice === undefined ? { ...common, type: 'Triggered' } : { ...common, type: 'Released', auctionPrice } })
  }
  private frame() {
    const state = this.state
    state.time += STEP
    state.step++
    state.random = (Math.imul(state.random, 1664525) + 1013904223) >>> 0
    const noise = (state.random % 13 - 6) * TICK
    const quantity = 40 + state.random % 960
    const shock = priceOnGrid(state.last - 700 + noise)
    if (state.vi) {
      if (state.time >= state.vi.scheduledEndMs) {
        const vi = state.vi, price = priceOnGrid(vi.triggerPrice + 100)
        state.vi = null
        this.trade(price, 1800, true)
        this.viEvent(vi, state.time, price)
      }
    } else if (state.name === 'FatFinger' && state.step === 3 && this.viEnabled) {
      const vi: Vi = { kind: 'Dynamic', referencePrice: state.last, triggerPrice: shock,
        startMs: state.time, scheduledEndMs: state.time + 120_000, ruleId: 'R-VI-01' }
      state.vi = vi
      this.viEvent(vi, state.time)
    } else {
      let target = 10000 + noise
      if (state.name === 'FatFinger' && state.step >= 3 && state.step <= 15) {
        target = state.step === 3 ? shock : state.last - 240 + noise
      } else if (state.name === 'PanicSell') {
        target = 10000 - Math.min(state.step * 100, 1400) + noise
      } else if (state.name === 'FatFinger' && state.step > 15) {
        target = Math.min(10000, state.last + 140) + noise
      }
      this.trade(priceOnGrid(target), quantity)
    }
    this.snapshot()
  }
  private apply(action: Action) {
    if (action.type === 'step') this.frame()
    else if (action.type === 'scenario') { this.state.name = action.name; this.state.step = 0; this.snapshot() }
    else if (action.type === 'order') {
      this.state.orders[`o-${++this.state.nextOrder}`] = { ...action.order, cancelled: false }
      this.snapshot()
    } else {
      this.state.orders[action.id].cancelled = true
      this.snapshot()
    }
  }
  private record(action: Action) {
    this.restored = false
    this.journal.push(action)
    this.apply(action)
  }
  advance() { this.record({ type: 'step' }) }
  async runScenario(name: Scenario) {
    if (this.state.vi) return { ok: false }
    this.record({ type: 'scenario', name }); return { ok: true }
  }
  async placeOrder(order: OrderRequest): Promise<OrderResponse> {
    const reject = (reason: string, ruleId: string | null = null): OrderResponse => ({ orderId: null, accepted: false, reason, ruleId })
    if (!Number.isSafeInteger(order.price) || !Number.isSafeInteger(order.quantity) || order.quantity <= 0) return reject('InvalidOrder')
    if (order.price > UPPER) return reject('AboveUpperLimit', 'R-LIMIT-04')
    if (order.price < LOWER) return reject('BelowLowerLimit', 'R-LIMIT-04')
    if (order.price % TICK) return reject('InvalidTick', 'R-TICK-02')
    this.record({ type: 'order', order: { ...order } })
    return { orderId: `o-${this.state.nextOrder}`, accepted: true, reason: null, ruleId: null }
  }
  async cancelOrder(orderId: string): Promise<CancelResponse> {
    const order = Object.hasOwn(this.state.orders, orderId) ? this.state.orders[orderId] : null
    if (!order || order.cancelled) return { orderId, cancelled: false, reason: 'OrderNotOpen' }
    this.record({ type: 'cancel', id: orderId })
    return { orderId, cancelled: true, reason: null }
  }
  async getExplanation(orderId: string): Promise<Explanation | null> {
    if (!Object.hasOwn(this.state.orders, orderId)) return null
    const { side, price, cancelled, quantity } = this.state.orders[orderId]
    const vi = !cancelled && this.state.vi
    const level = this.book()[side === 'Buy' ? 'bids' : 'asks'].find((level) => level.price === price)
    const ahead = Object.entries(this.state.orders).filter(([id, order]) => Number(id.slice(2)) < Number(orderId.slice(2)) && order.side === side && order.price === price && !order.cancelled)
    const queueAhead = { orders: (level?.orderCount ?? 0) + ahead.length, quantity: (level?.quantity ?? 0) + ahead.reduce((sum, [, order]) => sum + order.quantity, 0) }
    return { orderId, side, price, remainingQuantity: cancelled ? 0 : quantity,
      status: cancelled ? 'Cancelled' : vi ? 'WaitingAuction' : 'Resting', queueAhead: cancelled ? null : queueAhead,
      reason: null, ruleId: vi ? 'R-VI-04' : null, message: cancelled ? '취소된 주문입니다.' :
        `${vi ? '단일가 대기 중. ' : ''}같은 가격 대기열 ${queueAhead.orders + 1}번째, 앞에 ${formatQuantity(queueAhead.quantity)}가 있습니다.` }
  }
  async getExperimentSummary() { return mockExperimentSummary() }
  async getExperiment(id: string) {
    if (id === 'demo') id = 'exp-FatFinger-16'
    if (id !== EXPERIMENT_ID) {
      const match = /^exp-(Calm|FatFinger|PanicSell)-([1-9][0-9]{0,3})$/.exec(id)
      if (!match || Number(match[2]) > 1000) return null
      const scenario = match[1] as Scenario, seed = Number(match[2])
      return { id, seed, scenario, runs: mockExperimentRuns(scenario, seed) }
    }
    // 기존 목 전용 exp-1과 그 시세를 유지한다.
    const seed = this.seed
    const runs = [true, false].map((viEnabled): ExperimentRun => {
      const market = new MockMarketSource(seed, this.speed, viEnabled)
      const viEvents: ViEvent[] = []
      const series: ExperimentRun['series'] = []
      const trades = [{ timeMs: 0, price: 10_000 }]
      market.listeners.add((message) => {
        if (message.type === 'viEvent') viEvents.push(message.data)
        if (message.type === 'trade') trades.push({ timeMs: message.data.logicalTimeMs, price: message.data.price })
        if (message.type === 'bookSnapshot') series.push({ timeMs: message.data.logicalTimeMs, price: message.data.lastPrice })
      })
      market.apply({ type: 'scenario', name: 'FatFinger' })
      for (let i = 0; i < 35; i++) market.frame()
      const min = trades.reduce((best, trade) => trade.price < best.price ? trade : best)
      const max = trades.reduce((best, trade) => trade.price > best.price ? trade : best)
      return { viEnabled, viEvents, series, minPrice: min.price, maxPrice: max.price, minAtMs: min.timeMs, maxAtMs: max.timeMs }
    })
    return { id, seed, scenario: 'FatFinger' as const, runs }
  }
  async crash() {
    this.replaying = true
    try {
      this.state = initial(this.seed)
      for (const action of this.journal) this.apply(action)
      this.restored = true
    } finally { this.replaying = false }
    this.emit({ type: 'bookSnapshot', data: this.book() })
    return { ok: true }
  }
  async getStateHash() {
    let hash = 2166136261
    for (const character of JSON.stringify(this.state)) hash = Math.imul(hash ^ character.charCodeAt(0), 16777619)
    return { seq: this.state.seq, stateHash: (hash >>> 0).toString(16).padStart(8, '0'), restoredFromJournal: this.restored }
  }
}
