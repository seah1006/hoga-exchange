import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { Account, AccountOrder, AccountTrade, BookSnapshot, Explanation, Side } from './api/contract'
import type { MemberSource } from './api/marketSource'
import { AccountPanel, authError, roundNotice } from './Members'
import { formatPrice, formatQuantity, formatTime } from './format'

const sideText = (side: Side) => side === 'Buy' ? '매수' : '매도'
const statusLabel: Record<AccountOrder['status'], string> = { Resting: '대기 중', WaitingAuction: '단일가 대기', PartiallyFilled: '부분 체결', Filled: '체결 완료', Cancelled: '취소', Rejected: '거부' }
const reasons: Record<string, string> = {
  AboveUpperLimit: '상한가를 초과했습니다.', BelowLowerLimit: '하한가보다 낮습니다.', InvalidTick: '호가단위에 맞지 않습니다.', OffTick: '호가단위에 맞지 않습니다.',
  InvalidOrder: '가격과 수량을 확인하세요.', InvalidQuantity: '수량은 양의 정수로 입력하세요.', InvalidPrice: '가격은 1원 이상이어야 합니다.',
  DuplicateOrderId: '이미 접수된 주문입니다.', InvalidOrderId: '주문 번호를 확인하세요.', InsufficientCash: '매수 가능 현금이 부족합니다.', InsufficientShares: '매도 가능 수량이 부족합니다.',
}
const open = ({ status, remainingQuantity }: AccountOrder) => remainingQuantity > 0 && ['Resting', 'WaitingAuction', 'PartiallyFilled'].includes(status)
export const maxBuy = (cash: number, price: number) => Number.isSafeInteger(price) && price > 0 ? Math.floor(cash / price) : 0

type Data<T> = { value?: T; error: string }
export function MemberOrders({ source, book, price, onPrice, showAccount, online }: { source: MemberSource; book: BookSnapshot; price: string; onPrice: (value: string) => void; showAccount: boolean; online: boolean }) {
  const [account, setAccount] = useState<Data<Account>>({ error: '' })
  const [orders, setOrders] = useState<Data<AccountOrder[]>>({ error: '' })
  const [trades, setTrades] = useState<Data<AccountTrade[]>>({ error: '' })
  const [side, setSide] = useState<Side>('Buy')
  const [quantity, setQuantity] = useState('100')
  const [notice, setNotice] = useState<{ text: string; error?: boolean; rule?: string | null }>()
  const [busy, setBusy] = useState(false)
  const [selected, setSelected] = useState('')
  const [details, setDetails] = useState<{ id: string; data?: Explanation | null; error?: string }>()
  const refresh = useRef<() => void>(() => {})
  const previousPhase = useRef(book.phase)
  const selectedOrder = orders.value?.find(({ orderId }) => orderId === selected)
  const selectedStatus = selectedOrder?.status, selectedRemaining = selectedOrder?.remainingQuantity
  const [detailRetry, retryDetail] = useState(0)

  useEffect(() => {
    let live = true, accountVersion = 0, running = false, again = false, needAccount = false
    let timer: ReturnType<typeof setTimeout> | undefined
    function load(full: boolean) {
      needAccount ||= full
      if (running) { again = true; return }
      running = true
      const version = accountVersion
      const jobs: Promise<unknown>[] = [
        source.getAccountOrders().then((value) => { if (live) setOrders({ value, error: '' }) }, (error) => { if (live) setOrders((old) => ({ ...old, error: authError(error) })) }),
        source.getAccountTrades().then((value) => { if (live) setTrades({ value, error: '' }) }, (error) => { if (live) setTrades((old) => ({ ...old, error: authError(error) })) }),
      ]
      if (needAccount) jobs.push(source.getAccount().then((value) => { if (live && version === accountVersion) setAccount({ value, error: '' }) }, (error) => { if (live && version === accountVersion) setAccount((old) => ({ ...old, error: authError(error) })) }))
      needAccount = false
      void Promise.all(jobs).finally(() => { running = false; if (live && again) { again = false; load(false) } })
    }
    refresh.current = () => load(true)
    const unsubscribe = source.subscribe((message) => {
      if (!live || message.type !== 'accountUpdate') return
      accountVersion++
      setAccount({ value: message.data, error: '' })
      timer ??= setTimeout(() => { timer = undefined; load(false) }, 200)
    })
    load(true)
    return () => { live = false; clearTimeout(timer); unsubscribe(); refresh.current = () => {} }
  }, [source, online])

  // Entering or leaving an auction changes resting order statuses even without a balance event.
  useEffect(() => {
    if (previousPhase.current !== book.phase) refresh.current()
    previousPhase.current = book.phase
  }, [book.phase])

  useEffect(() => {
    if (!selected) return
    let live = true
    void source.getExplanation(selected).then((data) => { if (live) setDetails({ id: selected, data }) }, (error) => { if (live) setDetails({ id: selected, error: authError(error) }) })
    return () => { live = false }
  }, [source, selected, selectedStatus, selectedRemaining, detailRetry])

  const currentPrice = Number(price || book.lastPrice)
  const max = account.value ? side === 'Buy' ? maxBuy(account.value.cash, currentPrice) : account.value.shares : undefined
  const exceeds = max !== undefined && currentPrice > 0 && Number.isInteger(Number(quantity)) && Number(quantity) <= 1_000_000 && Number(quantity) > max
  const balanceWarning = side === 'Buy' ? reasons.InsufficientCash : reasons.InsufficientShares
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (busy || !online || !account.value || account.error) return
    const count = Number(quantity)
    if (!Number.isSafeInteger(currentPrice) || currentPrice <= 0) return setNotice({ text: '가격은 1원 이상의 정수로 입력하세요.', error: true })
    if (!Number.isInteger(count) || count < 1 || count > 1_000_000) return setNotice({ text: '수량은 1~1,000,000 사이의 정수로 입력하세요.', error: true })
    if (exceeds) return
    setBusy(true); setNotice(undefined)
    try {
      const result = await source.placeOrder({ side, price: currentPrice, quantity: count })
      if (!result.accepted) setNotice({ text: `주문 거부: ${reasons[result.reason] ?? '주문 조건을 확인하세요.'}`, error: true, rule: result.ruleId })
      else { setSelected(result.orderId); setNotice({ text: '주문이 접수되었습니다.' }); refresh.current() }
    } catch (error) { setNotice({ text: authError(error), error: true }) }
    finally { setBusy(false) }
  }
  async function cancel(orderId: string) {
    if (busy || !online) return
    setBusy(true); setNotice(undefined)
    try {
      const result = await source.cancelOrder(orderId)
      setNotice(result.cancelled ? { text: '주문을 취소했습니다.' } : { text: '취소할 수 없는 주문입니다.', error: true })
      if (result.cancelled) { setSelected(orderId); retryDetail((n) => n + 1) }
    } catch (error) { setNotice({ text: authError(error), error: true }) }
    finally { refresh.current(); setBusy(false) }
  }
  const detail = details?.id === selected ? details : undefined
  return <div className="stack">
    {showAccount && <AccountPanel account={account.value} lastPrice={book.lastPrice} error={account.error} retry={() => refresh.current()} />}
    <section className="panel order-entry"><h2>주문창</h2><p className="muted round-help">{roundNotice}</p>
      {!online && <p className="muted">시세 연결 후 주문할 수 있습니다.</p>}
      {!showAccount && account.error && <p role="alert" className="error">{account.error} <button onClick={() => refresh.current()}>다시 시도</button></p>}
      <form noValidate onSubmit={(event) => void submit(event)}>
        <div className="row" role="group" aria-label="주문 방향">{(['Buy', 'Sell'] as const).map((item) => <button type="button" key={item} className={item === 'Buy' ? 'buy' : 'sell'} aria-pressed={side === item} onClick={() => setSide(item)}>{sideText(item)}</button>)}</div>
        <label>가격(원)<input type="number" step="any" inputMode="numeric" value={price} placeholder={String(book.lastPrice)} onChange={(event) => onPrice(event.target.value)} /></label>
        <label>수량(주)<input type="number" min={1} max={1_000_000} step={1} inputMode="numeric" value={quantity} onChange={(event) => setQuantity(event.target.value)} /></label>
        <p className="muted">{account.value ? `매수 가능 ${formatQuantity(maxBuy(account.value.cash, currentPrice))} · 매도 가능 ${formatQuantity(account.value.shares)}` : !account.error && '주문 가능 수량 불러오는 중'}</p>
        {exceeds && <p className="error" role="alert">{balanceWarning}</p>}
        <button className="primary" type="submit" disabled={busy || !online || !account.value || !!account.error || exceeds}>{busy ? '처리 중' : `${sideText(side)} 주문`}</button>
      </form>
      {notice && <p className={notice.error ? 'error' : 'success'} role={notice.error ? 'alert' : 'status'}>{notice.text} {notice.rule && <span className="badge">{notice.rule}</span>}</p>}
    </section>
    <section className="panel"><h2>내 주문</h2>
      {orders.error && <p role="alert" className="error">{orders.error} <button onClick={() => refresh.current()}>다시 시도</button></p>}
      {!orders.value && !orders.error && <p role="status">주문 불러오는 중</p>}
      {orders.value?.length === 0 && <p className="muted">접수된 주문이 없습니다.</p>}
      <ul className="orders">{orders.value?.map((order) => <li key={order.orderId}>
        <button type="button" aria-pressed={order.orderId === selected} onClick={() => { setSelected(order.orderId); retryDetail((n) => n + 1) }}>{sideText(order.side)} {formatPrice(order.price)} · {formatQuantity(order.quantity)}</button>
        <span className="badge">{statusLabel[order.status] ?? '상태 확인 불가'}</span>
        {open(order) && <button type="button" disabled={busy || !online} aria-label={`${order.orderId} 취소`} onClick={() => void cancel(order.orderId)}>취소</button>}
      </li>)}</ul>
    </section>
    <section className="panel"><h2>내 체결</h2>
      {trades.error && <p role="alert" className="error">{trades.error} <button onClick={() => refresh.current()}>다시 시도</button></p>}
      {!trades.value && !trades.error && <p role="status">체결 불러오는 중</p>}
      {trades.value?.length === 0 && <p className="muted">체결 내역이 없습니다.</p>}
      {!!trades.value?.length && <div className="trade-scroll"><table aria-label="내 체결 내역"><thead><tr><th>시각</th><th>구분</th><th>가격</th><th>수량</th></tr></thead><tbody>{trades.value.map((trade, index) => <tr key={`${trade.orderId}-${index}`}><td>{formatTime(trade.logicalTimeMs)}</td><td>{sideText(trade.side)}</td><td>{formatPrice(trade.price)}</td><td>{formatQuantity(trade.quantity)}</td></tr>)}</tbody></table></div>}
    </section>
    <section className="panel"><h2>주문 설명</h2>
      {detail?.error && <p className="error" role="alert">{detail.error} <button onClick={() => retryDetail((n) => n + 1)}>다시 시도</button></p>}
      {detail?.data ? <><p>{detail.data.message}</p>{detail.data.queueAhead && <p>대기 순번 <strong>{detail.data.queueAhead.orders + 1}번째</strong></p>}{detail.data.ruleId && <span className="badge">{detail.data.ruleId}</span>}</> :
        !detail?.error && <p className="muted">{detail?.data === null ? '주문 설명을 찾을 수 없습니다.' : selected ? '주문 설명 불러오는 중' : '내 주문을 선택하면 설명을 표시합니다.'}</p>}
    </section>
  </div>
}
