export type Side = 'Buy' | 'Sell'
export type Scenario = 'Calm' | 'FatFinger' | 'PanicSell'
export type Phase = 'Continuous' | 'CallAuction'
export type ViKind = 'Dynamic' | 'Static'
export interface Vi {
  kind: ViKind
  referencePrice: number
  triggerPrice: number
  startMs: number
  scheduledEndMs: number
  ruleId: string
}
export interface Level { price: number; quantity: number; orderCount: number }
export interface BookSnapshot {
  symbol: 'HOGA'; seq: number; logicalTimeMs: number; phase: Phase
  basePrice: number; upperLimit: number; lowerLimit: number; lastPrice: number
  asks: Level[]; bids: Level[]; vi: Vi | null
  expectedPrice?: number | null; expectedQuantity?: number | null
}
export interface Trade {
  symbol: 'HOGA'; seq: number; logicalTimeMs: number; price: number; quantity: number; aggressorSide: Side | null
}
export interface Candle { symbol: 'HOGA'; startMs: number; open: number; high: number; low: number; close: number; volume: number }
interface ViEventFields { kind: ViKind; referencePrice: number; triggerPrice: number; atMs: number; ruleId: string }
export type ViEvent = ViEventFields & ({ type: 'Triggered' } | { type: 'Released'; auctionPrice: number })
export interface MarketReset { symbol: 'HOGA'; atUtc: string; reason: 'Daily' | 'Admin' }
export interface Messages { marketReset: MarketReset; bookSnapshot: BookSnapshot; trade: Trade; candle: Candle; viEvent: ViEvent }
export type MarketMessage = { [K in keyof Messages]: { type: K; data: Messages[K] } }[keyof Messages]
export interface OrderRequest { side: Side; price: number; quantity: number }
export type OrderResponse =
  | { orderId: string; accepted: true; reason: null; ruleId: null }
  | { orderId: null; accepted: false; reason: string; ruleId: string | null }
export interface CancelResponse { orderId: string; cancelled: boolean; reason: string | null }
export interface Explanation {
  orderId: string
  status: 'Resting' | 'WaitingAuction' | 'Filled' | 'PartiallyFilled' | 'Rejected' | 'Cancelled'
  side: Side; price: number; remainingQuantity: number
  queueAhead: { orders: number; quantity: number } | null
  reason: string | null; ruleId: string | null; message: string
}
export interface ExperimentRun {
  viEnabled: boolean; series: { timeMs: number; price: number }[]; viEvents: ViEvent[]; minPrice: number; maxPrice: number; minAtMs: number; maxAtMs: number
}
export interface Experiment { id: string; seed: number; scenario: Scenario; runs: ExperimentRun[] }
export interface ExperimentSeed { seed: number; minOn: number; minOff: number; delta: number; viTriggers: number }
export interface ExperimentScenarioSummary {
  scenario: Scenario
  deltaQuantiles: { min: number; p25: number; median: number; p75: number; max: number }
  counts: { viWorse: number; tie: number; viBetter: number }
  runsEndingInVi: { viOn: number; viOff: number }
  seeds: ExperimentSeed[]
}
export interface ExperimentSummary {
  generatedBy: string
  seeds: { from: number; to: number }
  durationMs: number
  demo: { scenario: Scenario; seed: number; rule: string }
  scenarios: ExperimentScenarioSummary[]
  caveat: string
}
export interface StateHash { seq: number; stateHash: string; restoredFromJournal: boolean }
