import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { BookSnapshot, Explanation, OrderRequest, Side } from './api/contract'
import type { MarketSource } from './api/marketSource'
import { formatPrice, formatQuantity } from './format'
import { marketErrorMessage } from './api/serverMarketSource'

const reasons: Record<string, string> = {
  AboveUpperLimit: '상한가를 초과했습니다.', BelowLowerLimit: '하한가보다 낮습니다.',
  InvalidTick: '10원 호가단위에 맞지 않습니다.', InvalidOrder: '가격과 수량을 올바른 정수로 입력하세요.',
  OffTick: '호가단위에 맞지 않습니다.', InvalidQuantity: '수량은 양의 정수로 입력하세요.', InvalidPrice: '가격은 1원 이상이어야 합니다.',
  InvalidOrderId: '주문 ID가 비어 있습니다.', DuplicateOrderId: '이미 사용한 주문 ID입니다.', UnknownOrder: '취소할 수 없는 주문입니다.',
}
const sideText = (side: Side) => side === 'Buy' ? '매수' : '매도'

// CONTRACT v0.3: Market:MaxOrderQuantity 기본값. KRX 규칙이 아닌 서버 입력 한도.
const MIN_QUANTITY = 1, MAX_QUANTITY = 1_000_000
const POLL_MS = 2_000 // 임의 지정: 화면당 자동 조회 1건씩
const statuses = {
  Resting: '대기 중', WaitingAuction: '단일가 대기', PartiallyFilled: '부분 체결',
  Filled: '체결 완료', Cancelled: '취소', Rejected: '거부', Unavailable: '상태 확인 불가',
}
type OrderStatus = keyof typeof statuses
type MyOrder = OrderRequest & { id: string; status?: OrderStatus }
type OrderDetail = { value?: Explanation; error?: string; fallback?: string }
const isOpen = (status: OrderStatus | undefined) =>
  status === 'Resting' || status === 'WaitingAuction' || status === 'PartiallyFilled'

export function Orders({ source, book, price, onPrice, canPoll }: { source: MarketSource; book: BookSnapshot; price: string; onPrice: (price: string) => void; canPoll: boolean }) {
  const [side, setSide] = useState<Side>('Buy')
  const [quantity, setQuantity] = useState('100')
  const [orders, setOrders] = useState<MyOrder[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [details, setDetails] = useState<Record<string, OrderDetail>>({})
  const [notice, setNotice] = useState<{ text: string; rule?: string | null; error?: boolean } | null>(null)
  const inFlight = useRef(new Set<string>())
  const cancelled = useRef(new Set<string>())
  const pollIndex = useRef(0)
  const pollIds = useRef<string[]>([])
  const detail = selectedId ? details[selectedId]?.value : undefined
  const detailError = selectedId ? details[selectedId]?.error : undefined
  const fallback = selectedId ? details[selectedId]?.fallback : undefined

  const refresh = useCallback(async (id: string) => {
    if (inFlight.current.has(id)) return
    inFlight.current.add(id)
    try {
      const value = await source.getExplanation(id)
      // 취소 응답 전에 시작된 조회가 늦게 도착해도 성공한 취소를 되돌리지 않는다.
      if (cancelled.current.has(id) && value?.status !== 'Cancelled') return
      const status = value?.status ?? 'Unavailable'
      setOrders((old) => old.map((order) => order.id === id && (!order.status || order.status === 'Unavailable' || isOpen(order.status))
        ? { ...order, status } : order))
      setDetails((old) => ({ ...old, [id]: value ? { value } : { error: '주문 설명을 찾을 수 없습니다.' } }))
    } catch (cause) {
      if (!cancelled.current.has(id)) setDetails((old) => ({ ...old, [id]: { ...old[id], error: marketErrorMessage(cause) } }))
    } finally {
      inFlight.current.delete(id)
    }
  }, [source])

  // 스냅샷 빈도와 무관하게 미확인·미종료 내 주문만 오래된 순서대로 순환한다.
  const hasPendingOrders = orders.some(({ status }) => !status || isOpen(status))
  useEffect(() => {
    pollIds.current = orders.filter(({ status }) => !status || isOpen(status)).map(({ id }) => id).reverse()
  }, [orders])
  useEffect(() => {
    if (!canPoll || !hasPendingOrders) return
    const timer = setInterval(() => {
      const ids = pollIds.current
      if (inFlight.current.size || !ids.length) return
      const id = ids[pollIndex.current++ % ids.length]
      void refresh(id)
    }, POLL_MS)
    return () => clearInterval(timer)
  }, [canPoll, hasPendingOrders, refresh])

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const request = { side, price: Number(price || book.lastPrice), quantity: Number(quantity) }
    if (!Number.isInteger(request.quantity) || request.quantity < MIN_QUANTITY || request.quantity > MAX_QUANTITY) {
      setNotice({ text: `수량은 ${MIN_QUANTITY}~${MAX_QUANTITY.toLocaleString('ko-KR')} 사이의 정수로 입력하세요.`, error: true }); return
    }
    try {
      const result = await source.placeOrder(request)
      if (!result.accepted) {
        setNotice({ text: `주문 거부: ${reasons[result.reason] ?? result.reason}`, rule: result.ruleId, error: true }); return
      }
      const next = { ...request, id: result.orderId }
      setOrders((old) => [next, ...old]); setSelectedId(next.id); setNotice({ text: '주문이 접수되었습니다.' })
      void refresh(next.id)
    } catch (cause) { setNotice({ text: marketErrorMessage(cause), error: true }) }
  }
  async function cancel(order: MyOrder) {
    try {
      const result = await source.cancelOrder(order.id)
      if (!result.cancelled) {
        setNotice({ text: reasons[result.reason ?? ''] ?? '취소할 수 없는 주문입니다.', error: true })
        void refresh(order.id)
        return
      }
      cancelled.current.add(order.id)
      setOrders((old) => old.map((item) => item.id === order.id ? { ...item, status: 'Cancelled' } : item))
      setDetails((old) => ({ ...old, [order.id]: { fallback: '취소된 주문입니다.' } }))
      setSelectedId(order.id)
      void refresh(order.id)
      setNotice({ text: '주문을 취소했습니다.' })
    } catch (cause) { setNotice({ text: marketErrorMessage(cause), error: true }) }
  }
  return <div className="stack"><section className="panel"><h2>주문창</h2>
    <form noValidate onSubmit={(event) => void submit(event)}>
      <div className="row" role="group" aria-label="주문 방향">
        {(['Buy', 'Sell'] as const).map((value) => <button type="button" className={value === 'Buy' ? 'buy' : 'sell'} key={value} aria-pressed={side === value} onClick={() => setSide(value)}>{sideText(value)}</button>)}
      </div>
      <label>가격(원)<input type="number" step="any" value={price} placeholder={String(book.lastPrice)} onChange={(event) => onPrice(event.target.value)} /></label>
      <label>수량(주)<input type="number" min={MIN_QUANTITY} max={MAX_QUANTITY} step={1} required value={quantity} onChange={(event) => setQuantity(event.target.value)} /></label>
      <button type="submit" className="primary">{sideText(side)} 주문</button>
      {notice && <p className={notice.error ? 'error' : 'success'} role={notice.error ? 'alert' : 'status'}>{notice.text} {notice.rule && <span className="badge">{notice.rule}</span>}</p>}
    </form>
  </section><section className="panel"><h2>내 주문</h2>
    {!orders.length && <p className="muted">접수된 주문이 없습니다.</p>}
    <ul className="orders">{orders.map((order) => <li key={order.id}>
      <button type="button" aria-pressed={selectedId === order.id} onClick={() => {
        setSelectedId(order.id)
        if (!details[order.id]?.value || isOpen(order.status)) void refresh(order.id)
      }}>{sideText(order.side)} {formatPrice(order.price)} · {formatQuantity(order.quantity)}</button>
      <span className="badge">{order.status ? statuses[order.status] : '상태 확인 중'}</span>
      {isOpen(order.status) && <button type="button" aria-label={`${order.id} 취소`} onClick={() => void cancel(order)}>취소</button>}
    </li>)}</ul>
  </section><section className="panel"><h2>주문 설명</h2>
    {detailError && <p className="error" role="alert">{detailError}</p>}
    {detail && detail.orderId === selectedId ? <>
      <p>{detail.message}</p>
      {detail.queueAhead && <p>대기 순번 <strong>{detail.queueAhead.orders + 1}번째</strong></p>}
      {detail.ruleId && <span className="badge">{detail.ruleId}</span>}
    </> : fallback ? <p>{fallback}</p> : !detailError && <p className="muted">{selectedId ? '주문 설명 불러오는 중' : '내 주문을 선택하면 설명을 표시합니다.'}</p>}
  </section></div>
}
