import { HubConnectionBuilder } from '@microsoft/signalr'
import type { Account, AccountOrder, AccountTrade, Login, Profile, Ranking, Registration, User, CancelResponse, Experiment, ExperimentSummary, Explanation, MarketMessage, Messages, OrderRequest, OrderResponse, Scenario, StateHash } from './contract'
import type { MarketConnection, MemberSource } from './marketSource'
import { readAdminKey } from './adminKey'

// SignalR 자동 재연결은 첫 접속 실패나 재시도 소진 이후의 접속을 다루지 않는다.
const restartDelayMs = 5_000
const events = ['bookSnapshot', 'trade', 'candle', 'viEvent', 'marketReset', 'accountUpdate'] as const

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

export class ServerMarketSource implements MemberSource {
  private readonly base: string
  private readonly fetcher: typeof fetch
  private readonly makeHub: (url: string) => MarketHub
  private readonly listeners = new Set<(message: MarketMessage) => void>()
  private readonly connectionListeners = new Set<(status: MarketConnection) => void>()
  private status: MarketConnection = 'connecting'
  private session?: HubSession
  private csrf?: Promise<string>

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

  private async request<T>(path: string, { method = 'GET', body, admin = false, token }: { method?: string; body?: unknown; admin?: boolean; token?: string } = {}): Promise<T> {
    const headers = new Headers({ Accept: 'application/json' })
    if (token) headers.set('X-CSRF-TOKEN', token)
    if (body !== undefined) headers.set('Content-Type', 'application/json')
    const key = admin ? readAdminKey() : ''
    if (key) headers.set('X-Admin-Key', key)
    let response: Response
    try {
      response = await this.fetcher(`${this.base}${path}`, { method, headers, credentials: 'include', body: body === undefined ? undefined : JSON.stringify(body) })
    } catch {
      throw new MarketApiError(0, 'NetworkError', '서버에 연결할 수 없습니다.')
    }
    if (!response.ok) {

      const data: unknown = await response.json().catch(() => null)
      const code = data && typeof data === 'object' && 'error' in data && typeof data.error === 'string' ? data.error : response.status === 429 ? 'TooManyRequests' : 'HttpError'
      const message = data && typeof data === 'object' && 'message' in data && typeof data.message === 'string' && data.message.trim()
        ? data.message : `서버 요청에 실패했습니다 (HTTP ${response.status}).`
      throw new MarketApiError(response.status, code, response.status === 429 ? '요청이 너무 잦습니다' : message)
    }
    if (response.status === 204) return undefined as T
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

  refreshCsrf() {
    const pending = this.request<{ token: string }>('/api/auth/csrf').then(({ token }) => {
      if (!token) throw new MarketApiError(0, 'InvalidResponse', '보안 토큰을 받을 수 없습니다.')
      return token
    })
    this.csrf = pending
    void pending.catch(() => { if (this.csrf === pending) this.csrf = undefined })
    return pending
  }

  private async mutate<T>(path: string, options: { method: 'POST' | 'DELETE'; body?: unknown; admin?: boolean }) {
    const pending = this.csrf ?? this.refreshCsrf()
    try {
      return await this.request<T>(path, { ...options, token: await pending })
    } catch (error) {
      if (!(error instanceof MarketApiError) || error.status !== 400 || error.code !== 'BadCsrfToken') throw error
      const renewed = this.csrf !== pending && this.csrf ? this.csrf : this.refreshCsrf()
      return this.request<T>(path, { ...options, token: await renewed })
    }
  }

  private async sessionChanged() {
    this.csrf = undefined
    const old = this.session
    if (old) {
      this.session = undefined
      clearTimeout(old.retry)
      this.setStatus('syncing')
      void old.hub.stop().catch(() => { /* 이전 세션은 무시한다. */ })
      if (this.listeners.size) this.connect()
    }
    // 쿠키 변경이 끝났다. 재발급 실패 시 다음 변경 요청에서 다시 시도한다.
    await this.refreshCsrf().catch(() => undefined)
  }

  async getMe() {
    try { return await this.request<Profile>('/api/auth/me') }
    catch (error) { if (error instanceof MarketApiError && error.status === 401) return null; throw error }
  }
  async login(data: Login) {
    const user = await this.mutate<User>('/api/auth/login', { method: 'POST', body: data })
    await this.sessionChanged()
    return user
  }
  async register(data: Registration) {
    const user = await this.mutate<User>('/api/auth/register', { method: 'POST', body: data })
    await this.sessionChanged()
    return user
  }
  async logout() { await this.mutate<void>('/api/auth/logout', { method: 'POST' }); await this.sessionChanged() }
  async deleteMe(password: string) { await this.mutate<void>('/api/auth/me', { method: 'DELETE', body: { password } }); await this.sessionChanged() }
  getAccount() { return this.request<Account>('/api/account') }
  getAccountOrders() { return this.request<AccountOrder[]>('/api/account/orders') }
  getAccountTrades() { return this.request<AccountTrade[]>('/api/account/trades') }
  getRanking(round?: string) { return this.lookup<Ranking>(round ? `/api/ranking/${encodeURIComponent(round)}` : '/api/ranking') }
  getRankingRounds() { return this.request<string[]>('/api/ranking/rounds') }

  placeOrder(order: OrderRequest) { return this.mutate<OrderResponse>('/api/orders', { method: 'POST', body: order }) }
  cancelOrder(orderId: string) { return this.mutate<CancelResponse>(`/api/orders/${encodeURIComponent(orderId)}`, { method: 'DELETE' }) }
  getExplanation(orderId: string) { return this.lookup<Explanation>(`/api/orders/${encodeURIComponent(orderId)}/explanation`) }
  getExperimentSummary() { return this.request<ExperimentSummary>('/api/experiments/summary') }
  getExperiment(id: string) { return this.lookup<Experiment>(`/api/experiments/${encodeURIComponent(id)}`) }
  runScenario(name: Scenario) { return this.mutate<{ ok: boolean }>('/api/admin/scenario', { method: 'POST', body: { name }, admin: true }) }
  crash() { return this.mutate<{ ok: boolean }>('/api/admin/crash', { method: 'POST', admin: true }) }
  getStateHash() { return this.request<StateHash>('/api/state/hash') }
}

