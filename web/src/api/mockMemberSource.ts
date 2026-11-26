import type { Account, AccountOrder, AccountTrade, Login, MarketMessage, OrderRequest, OrderResponse, Profile, Ranking, Registration } from './contract'
import type { MemberSource } from './marketSource'
import { MockMarketSource } from './mockMarketSource'
import { MarketApiError } from './serverMarketSource'

const cashStart = 10_000_000, sharesStart = 1_000, valuationStart = 20_000_000
const fail = (status: number, code: string): never => { throw new MarketApiError(status, code, '요청을 완료하지 못했습니다.') }
const open = (order: AccountOrder) => ['Resting', 'WaitingAuction', 'PartiallyFilled'].includes(order.status)
// 휘발성 목 전용 비교값. 운영 인증에 사용하지 않는다.
const verifier = (input: string) => Array.from(input).reduce((sum, char) => Math.imul(sum ^ char.codePointAt(0)!, 16777619), 2166136261)
const ledger = () => ({ cash: cashStart, shares: sharesStart, orders: [] as AccountOrder[], trades: [] as AccountTrade[], first: 0 })
const userDto = ({ id, nickname, isAdmin }: Profile) => ({ id, nickname, isAdmin })
type Member = { profile: Profile; verifier: number; ledger: ReturnType<typeof ledger> }
type Entry = Ranking['entries'][number] & { id: string }

export class MockMemberSource extends MockMarketSource implements MemberSource {
  private members: Member[] = []
  private user?: Member
  private nextUser = 0
  private nextOrder = 0
  private last = 10_000
  private time = 0
  private auction = false
  private round = '2026-11-26'
  private updatedAt = '2026-11-25T20:00:00Z'
  private archives = new Map<string, { entries: Entry[]; updatedAt: string }>([['2026-11-25', { entries: [{ id: 'example', nickname: '목 참가자', rank: 1, returnBp: 27, valuation: 20_055_000 }], updatedAt: '2026-11-25T20:00:00Z' }]])
  private sinks = new Set<(message: MarketMessage) => void>()
  private stop?: () => void
  private attempts = new Map<string, { count: number; until: number }>()

  override subscribe(handler: (message: MarketMessage) => void) {
    this.sinks.add(handler)
    if (!this.stop) this.stop = super.subscribe((message) => {
      if (message.type === 'bookSnapshot') {
        const { lastPrice, logicalTimeMs, phase } = message.data
        const auction = phase === 'CallAuction', changed = lastPrice !== this.last || auction !== this.auction
        this.last = lastPrice; this.time = logicalTimeMs; this.auction = auction
        if (changed) {
          for (const member of this.members) for (const order of member.ledger.orders) if (open(order)) this.match(member, order)
          this.accountEvent()
        }
      }
      this.publish(message)
    })
    else { const replay = super.subscribe(handler); replay() }
    if (this.user) handler({ type: 'accountUpdate', data: this.account(this.user) })
    return () => {
      this.sinks.delete(handler)
      if (!this.sinks.size) { this.stop?.(); this.stop = undefined }
    }
  }
  override async getStateHash() {
    return { ...await super.getStateHash(), ledgerHash: this.mockHash(this.members.map(({ profile, ledger }) => [profile.id, ledger])) }
  }
  private publish(message: MarketMessage) { for (const handler of this.sinks) handler(message) }
  private accountEvent() { if (this.user) this.publish({ type: 'accountUpdate', data: this.account(this.user) }) }
  private requireUser() { return this.user ?? fail(401, 'Unauthorized') }
  private account(member: Member): Account {
    const { cash, shares, orders } = member.ledger
    let cashOnHold = 0, sharesOnHold = 0
    for (const order of orders) if (open(order)) {
      if (order.side === 'Buy') cashOnHold += order.price * order.remainingQuantity
      else sharesOnHold += order.remainingQuantity
    }
    const valuation = cash + shares * this.last, pnl = valuation - valuationStart
    return { round: this.round, cash: cash - cashOnHold, cashOnHold, shares: shares - sharesOnHold, sharesOnHold,
      initialValuation: valuationStart, lastPrice: this.last, valuation, pnl, returnBp: Math.trunc(pnl * 10_000 / valuationStart) }
  }
  private match(member: Member, order: AccountOrder) {
    if (this.auction || (order.side === 'Buy' ? order.price < this.last : order.price > this.last)) {
      order.status = this.auction ? 'WaitingAuction' : 'Resting'; return
    }
    const { ledger } = member, quantity = order.remainingQuantity, price = this.last, sign = order.side === 'Buy' ? 1 : -1
    ledger.cash -= sign * price * quantity; ledger.shares += sign * quantity
    Object.assign(order, { remainingQuantity: 0, filledQuantity: quantity, status: 'Filled' })
    ledger.trades.unshift({ orderId: order.orderId, side: order.side, price, quantity, logicalTimeMs: this.time })
  }
  async refreshCsrf() { return 'mock' }
  async getMe() { return this.user ? { ...this.user.profile } : null }
  async register({ email, password, nickname }: Registration) {
    email = email.trim().toLowerCase(); nickname = nickname.trim()
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) fail(400, 'InvalidEmail')
    if (password.length < 10) fail(400, 'WeakPassword')
    if (nickname.length < 2 || nickname.length > 16) fail(400, 'InvalidNickname')
    if (this.members.some((member) => member.profile.email === email)) fail(409, 'EmailTaken')
    if (this.members.some((member) => member.profile.nickname === nickname)) fail(409, 'NicknameTaken')
    const profile = { email, nickname, id: `u-${++this.nextUser}`, isAdmin: false }
    this.user = { profile, verifier: verifier(password), ledger: ledger() }
    this.members.push(this.user); this.accountEvent()
    return userDto(profile)
  }
  async login({ email, password }: Login) {
    email = email.trim().toLowerCase()
    const now = Date.now(), attempt = this.attempts.get(email)
    if (attempt && attempt.until > now) fail(429, 'LockedOut')
    const member = this.members.find(({ profile }) => profile.email === email)
    if (!member || member.verifier !== verifier(password)) {
      const count = (attempt?.until ? 0 : attempt?.count ?? 0) + 1
      this.attempts.set(email, { count, until: count >= 5 ? now + 300_000 : 0 })
      return fail(count >= 5 ? 429 : 401, count >= 5 ? 'LockedOut' : 'InvalidCredentials')
    }
    this.attempts.delete(email); this.user = member; this.accountEvent()
    return userDto(member.profile)
  }
  async logout() { this.user = undefined }
  async deleteMe(password: string) {
    const member = this.requireUser()
    if (member.verifier !== verifier(password)) fail(401, 'InvalidCredentials')
    this.members = this.members.filter((other) => other !== member)
    for (const { entries } of this.archives.values()) for (const entry of entries) if (entry.id === member.profile.id) entry.nickname = '탈퇴한 사용자'
    this.user = undefined
  }
  async getAccount() { return this.account(this.requireUser()) }
  async getAccountOrders() { return this.requireUser().ledger.orders.slice(0, 200).map((order) => ({ ...order })) }
  async getAccountTrades() { return this.requireUser().ledger.trades.slice(0, 200).map((trade) => ({ ...trade })) }
  override async placeOrder(order: OrderRequest): Promise<OrderResponse> {
    const member = this.requireUser(), account = this.account(member)
    const reject = (reason: string): OrderResponse => ({ orderId: null, accepted: false, ruleId: null, reason })
    if (order.side === 'Buy' && order.price * order.quantity > account.cash) return reject('InsufficientCash')
    if (order.side === 'Sell' && order.quantity > account.shares) return reject('InsufficientShares')
    const response = await super.placeOrder(order)
    if (response.accepted) {
      const next: AccountOrder = { ...order, orderId: response.orderId, filledQuantity: 0, remainingQuantity: order.quantity, status: 'Resting', createdAtMs: this.time }
      member.ledger.orders.unshift(next); member.ledger.first ||= ++this.nextOrder
      this.match(member, next); this.accountEvent()
    }
    return response
  }
  override async cancelOrder(id: string) {
    const order = this.requireUser().ledger.orders.find(({ orderId }) => orderId === id) ?? fail(404, 'NotFound')
    if (!open(order)) return { orderId: id, cancelled: false, reason: 'OrderNotOpen' }
    const response = await super.cancelOrder(id)
    if (response.cancelled) { order.status = 'Cancelled'; order.remainingQuantity = 0; this.accountEvent() }
    return response
  }
  override async getExplanation(id: string) {
    const order = this.requireUser().ledger.orders.find(({ orderId }) => orderId === id)
    if (!order) return null
    const result = await super.getExplanation(id)
    if (!result) return null
    return { ...result, status: order.status, remainingQuantity: order.remainingQuantity, ...(order.status === 'Filled' ? { queueAhead: null, ruleId: null, message: '체결이 완료되었습니다.' } : {}) }
  }
  private standings() {
    return this.members.filter(({ ledger }) => ledger.first).map((member) => ({ ...this.account(member), ...userDto(member.profile), first: member.ledger.first }))
      .sort((left, right) => right.returnBp - left.returnBp || left.first - right.first)
      .map(({ id, nickname, returnBp, valuation }, index) => ({ id, nickname, returnBp, valuation, rank: index + 1 }))
  }
  async getRanking(round = this.round): Promise<Ranking | null> {
    const past = this.archives.get(round), entries = round === this.round ? this.standings() : past?.entries
    if (!entries) return null
    const entry = entries.find(({ id }) => id === this.user?.profile.id)
    const me = entry ? { rank: entry.rank, valuation: entry.valuation, returnBp: entry.returnBp } : null
    return { round, updatedAtUtc: past?.updatedAt ?? this.updatedAt, entries: entries.slice(0, 50).map(({ rank, nickname, valuation, returnBp }) => ({ rank, nickname, valuation, returnBp })), me }
  }
  async getRankingRounds() { return [...this.archives.keys()].reverse().slice(0, 30) }
  resetRound(round: string, atUtc: string) {
    this.archives.set(this.round, { entries: this.standings(), updatedAt: atUtc })
    this.round = round; this.updatedAt = atUtc
    for (const member of this.members) member.ledger = ledger()
    this.publish({ type: 'marketReset', data: { symbol: 'HOGA', atUtc, reason: 'Daily' } })
    const replay = super.subscribe((message) => this.publish(message)); replay()
    this.accountEvent()
  }
}
