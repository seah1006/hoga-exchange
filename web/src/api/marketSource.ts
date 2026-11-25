import type { CancelResponse, Experiment, ExperimentSummary, Explanation, MarketMessage, OrderRequest, OrderResponse, Scenario, StateHash } from './contract'

export type MarketConnection = 'connecting' | 'syncing' | 'connected' | 'reconnecting' | 'disconnected'

export interface MarketSource {
  subscribeConnection?(handler: (status: MarketConnection) => void): () => void
  subscribe(handler: (message: MarketMessage) => void): () => void
  placeOrder(order: OrderRequest): Promise<OrderResponse>
  cancelOrder(orderId: string): Promise<CancelResponse>
  getExplanation(orderId: string): Promise<Explanation | null>
  getExperimentSummary(): Promise<ExperimentSummary>
  getExperiment(id: string): Promise<Experiment | null>
  runScenario(name: Scenario): Promise<{ ok: boolean }>
  crash(): Promise<{ ok: boolean }>
  getStateHash(): Promise<StateHash>
}
