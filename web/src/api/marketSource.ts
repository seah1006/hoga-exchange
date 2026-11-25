import type { CancelResponse, Experiment, Explanation, MarketMessage, OrderRequest, OrderResponse, Scenario, StateHash } from './contract'

export interface MarketSource {
  subscribe(handler: (message: MarketMessage) => void): () => void
  placeOrder(order: OrderRequest): Promise<OrderResponse>
  cancelOrder(orderId: string): Promise<CancelResponse>
  getExplanation(orderId: string): Promise<Explanation | null>
  getExperiment(id: string): Promise<Experiment | null>
  runScenario(name: Scenario): Promise<{ ok: boolean }>
  crash(): Promise<{ ok: boolean }>
  getStateHash(): Promise<StateHash>
}
