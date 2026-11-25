import { HubConnectionBuilder } from '@microsoft/signalr'
import type { CancelResponse, Experiment, Explanation, MarketMessage, Messages, OrderRequest, OrderResponse, Scenario, StateHash } from './contract'
import type { MarketConnection, MarketSource } from './marketSource'
import { readAdminKey } from './adminKey'

// SignalR 자동 재연결은 첫 접속 실패나 재시도 소진 이후의 접속을 다루지 않는다.
const restartDelayMs = 5_000
const events = ['bookSnapshot', 'trade', 'candle', 'viEvent'] as const

export interface MarketHub {
  on<K extends keyof Messages>(name: K, handler: (data: Messages[K]) => void): void
  onreconnecting(handler: () => void): void
  onreconnected(handler: () => void): void
  onclose(handler: () => void): void
  start(): Promise<void>
  stop(): Promise<void>
}

const createHub = (url: string): MarketHub => new HubConnectionBuilder().withUrl(url).withAutomaticReconnect().build()

type ServerOptions = { base?: string; fetcher?: typeof fetch; createHub?: (url: string) => MarketHub }
type HubSession = { hub: MarketHub; retry?: ReturnType<typeof setTimeout> }

export class MarketApiError extends Error {
  constructor(readonly status: number, readonly code: string, message: string) {
    super(message)
    this.name = 'MarketApiError'
  }
}

export function marketErrorMessage(error: unknown) {
  return error instanceof MarketApiError ? error.message : '요청을 완료하지 못했습니다.'
}

export class ServerMarketSource implements MarketSource {
  private readonly base: string
  private readonly fetcher: typeof fetch
  private readonly makeHub: (url: string) => MarketHub
  private readonly listeners = new Set<(message: MarketMessage) => void>()
  private readonly connectionListeners = new Set<(status: MarketConnection) => void>()
  private status: MarketConnection = 'connecting'
  private session?: HubSession

  constructor({ base = '', fetcher = (input, init) => fetch(input, init), createHub: makeHub = createHub }: ServerOptions = {}) {
    this.base = base.trim().replace(/\/+$/, '')
    this.fetcher = fetcher
    this.makeHub = makeHub
  }

  subscribeConnection(handler: (status: MarketConnection) => void) {
    this.connectionListeners.add(handler)
    handler(this.status)
    return () => { this.connectionListeners.delete(handler) }
  }

  private setStatus(status: MarketConnection) {
    if (this.status === status) return
    this.status = status
    for (const handler of this.connectionListeners) handler(status)
  }

  subscribe(handler: (message: MarketMessage) => void) {
    this.listeners.add(handler)
    if (!this.session) this.connect()
    return () => {
      this.listeners.delete(handler)
      if (this.listeners.size || !this.session) return
      const { hub, retry } = this.session
      this.session = undefined
      clearTimeout(retry)
      this.setStatus('disconnected')
      void hub.stop().catch(() => { /* 해제 후에는 상태를 갱신하지 않는다. */ })
    }
  }

  private connect() {
    const hub = this.makeHub(`${this.base}/hubs/market`)
    const session: HubSession = { hub }
    this.session = session
    const isCurrent = () => this.session === session
    const retry = () => {
      if (!isCurrent() || session.retry !== undefined) return
      this.setStatus('disconnected')
      session.retry = setTimeout(() => {
        session.retry = undefined
        if (isCurrent()) start()
      }, restartDelayMs)
    }
    const beginSync = () => {
      if (isCurrent() && (this.status === 'connecting' || this.status === 'reconnecting')) this.setStatus('syncing')
    }
    const start = () => {
      this.setStatus('connecting')
      void hub.start().then(beginSync, retry)
    }
    for (const name of events) {
      hub.on(name, (data) => {
        if (!isCurrent()) return
        // 이력은 start/onreconnected 알림보다 먼저 도착할 수 있다.
        beginSync()
        const message = { type: name, data } as MarketMessage
        for (const handler of this.listeners) handler(message)
        if (isCurrent() && name === 'bookSnapshot') this.setStatus('connected')
      })
    }
    hub.onreconnecting(() => { if (isCurrent()) this.setStatus('reconnecting') })
    hub.onreconnected(beginSync)
    hub.onclose(retry)
    start()
  }

  private async request<T>(path: string, { method = 'GET', body, admin = false }: { method?: string; body?: unknown; admin?: boolean } = {}): Promise<T> {
    const headers = new Headers({ Accept: 'application/json' })
    if (body !== undefined) headers.set('Content-Type', 'application/json')
    const key = admin ? readAdminKey() : ''
    if (key) headers.set('X-Admin-Key', key)
    let response: Response
    try {
      response = await this.fetcher(`${this.base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
    } catch {
      throw new MarketApiError(0, 'NetworkError', '서버에 연결할 수 없습니다.')
    }
    if (!response.ok) {
      const data: unknown = await response.json().catch(() => null)
      const code = data && typeof data === 'object' && 'error' in data && typeof data.error === 'string' ? data.error : 'HttpError'
      const message = data && typeof data === 'object' && 'message' in data && typeof data.message === 'string' && data.message.trim()
        ? data.message : `서버 요청에 실패했습니다 (HTTP ${response.status}).`
      throw new MarketApiError(response.status, code, message)
    }
    try {
      return await response.json() as T
    } catch {
      throw new MarketApiError(response.status, 'InvalidResponse', '서버 응답을 읽을 수 없습니다.')
    }
  }

  private async lookup<T>(path: string): Promise<T | null> {
    try {
      return await this.request<T>(path)
    } catch (error) {
      if (error instanceof MarketApiError && error.status === 404) return null
      throw error
    }
  }

  placeOrder(order: OrderRequest) { return this.request<OrderResponse>('/api/orders', { method: 'POST', body: order }) }
  cancelOrder(orderId: string) { return this.request<CancelResponse>(`/api/orders/${encodeURIComponent(orderId)}`, { method: 'DELETE' }) }
  getExplanation(orderId: string) { return this.lookup<Explanation>(`/api/orders/${encodeURIComponent(orderId)}/explanation`) }
  getExperiment(id: string) { return this.lookup<Experiment>(`/api/experiments/${encodeURIComponent(id)}`) }
  runScenario(name: Scenario) { return this.request<{ ok: boolean }>('/api/admin/scenario', { method: 'POST', body: { name }, admin: true }) }
  crash() { return this.request<{ ok: boolean }>('/api/admin/crash', { method: 'POST', admin: true }) }
  getStateHash() { return this.request<StateHash>('/api/state/hash') }
}
