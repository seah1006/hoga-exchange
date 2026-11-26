import type { Account, AccountOrder, AccountTrade, Login, Profile, Ranking, Registration, User, CancelResponse, Experiment, ExperimentSummary, Explanation, MarketMessage, OrderRequest, OrderResponse, Scenario, StateHash } from './contract'

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

export interface MemberSource extends MarketSource {
  refreshCsrf(): Promise<string>
  getMe(): Promise<Profile | null>
  register(data: Registration): Promise<User>
  login(data: Login): Promise<User>
  logout(): Promise<void>
  deleteMe(password: string): Promise<void>
  getAccount(): Promise<Account>
  getAccountOrders(): Promise<AccountOrder[]>
  getAccountTrades(): Promise<AccountTrade[]>
  getRanking(round?: string): Promise<Ranking | null>
  getRankingRounds(): Promise<string[]>
}
