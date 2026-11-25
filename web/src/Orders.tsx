import { useEffect, useRef, useState } from 'react'
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

type MyOrder = OrderRequest & { id: string; cancelled: boolean }
export function Orders({ source, book, price, onPrice }: { source: MarketSource; book: BookSnapshot; price: string; onPrice: (price: string) => void }) {
  const [side, setSide] = useState<Side>('Buy')
  const [quantity, setQuantity] = useState('100')
  const [orders, setOrders] = useState<MyOrder[]>([])
  const [selected, setSelected] = useState<MyOrder | null>(null)
  const [detail, setDetail] = useState<Explanation | null>(null)
  const [detailError, setDetailError] = useState('')
  const [notice, setNotice] = useState<{ text: string; rule?: string | null; error?: boolean } | null>(null)
  const refresh = useRef<() => void>(undefined)
  const selectedId = selected?.id
  useEffect(() => {
    if (!selectedId) return
    setDetail(null); setDetailError('')
    let active = true, loading = false, pending = false
    // 스냅샷이 응답보다 빨라도 성공한 응답을 버리지 않고, 동시에 하나만 요청한다.
    async function load() {
      if (!active || !selectedId) return
      if (loading) { pending = true; return }
      loading = true
      try {
        const value = await source.getExplanation(selectedId)
        if (active) { setDetail(value); setDetailError(value ? '' : '주문을 찾을 수 없습니다.') }
      } catch (cause) {
        if (active) setDetailError(marketErrorMessage(cause))
      } finally {
        loading = false
        if (pending) { pending = false; void load() }
      }
    }
    refresh.current = () => { void load() }
    return () => { active = false; refresh.current = undefined }
  }, [source, selectedId])
  useEffect(() => { refresh.current?.() }, [source, selected, book])

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const request = { side, price: Number(price || book.lastPrice), quantity: Number(quantity) }
    try {
      const result = await source.placeOrder(request)
      if (!result.accepted) {
        setNotice({ text: `주문 거부: ${reasons[result.reason] ?? result.reason}`, rule: result.ruleId, error: true }); return
      }
      const next = { ...request, id: result.orderId, cancelled: false }
      setOrders((old) => [next, ...old]); setSelected(next); setNotice({ text: '주문이 접수되었습니다.' })
    } catch (cause) { setNotice({ text: marketErrorMessage(cause), error: true }) }
  }
  async function cancel(order: MyOrder) {
    try {
      const result = await source.cancelOrder(order.id)
      if (!result.cancelled) { setNotice({ text: reasons[result.reason ?? ''] ?? '취소할 수 없는 주문입니다.', error: true }); return }
      const next = { ...order, cancelled: true }
      setOrders((old) => old.map((item) => item.id === next.id ? next : item)); setSelected(next)
      setNotice({ text: '주문을 취소했습니다.' })
    } catch (cause) { setNotice({ text: marketErrorMessage(cause), error: true }) }
  }
  return <div className="stack"><section className="panel"><h2>주문창</h2>
    <form onSubmit={(event) => void submit(event)}>
      <div className="row" role="group" aria-label="주문 방향">
        {(['Buy', 'Sell'] as const).map((value) => <button type="button" className={value === 'Buy' ? 'buy' : 'sell'} key={value} aria-pressed={side === value} onClick={() => setSide(value)}>{sideText(value)}</button>)}
      </div>
      <label>가격(원)<input type="number" step="any" value={price} placeholder={String(book.lastPrice)} onChange={(event) => onPrice(event.target.value)} /></label>
      <label>수량(주)<input type="number" step="any" value={quantity} onChange={(event) => setQuantity(event.target.value)} /></label>
      <button type="submit" className="primary">{sideText(side)} 주문</button>
      {notice && <p className={notice.error ? 'error' : 'success'} role={notice.error ? 'alert' : 'status'}>{notice.text} {notice.rule && <span className="badge">{notice.rule}</span>}</p>}
    </form>
  </section><section className="panel"><h2>내 주문</h2>
    {!orders.length && <p className="muted">접수된 주문이 없습니다.</p>}
    <ul className="orders">{orders.map((order) => <li key={order.id}>
      <button type="button" aria-pressed={selected?.id === order.id} onClick={() => setSelected(order)}>{sideText(order.side)} {formatPrice(order.price)} · {formatQuantity(order.quantity)}{order.cancelled ? ' (취소)' : ''}</button>
      {!order.cancelled && <button type="button" aria-label={`${order.id} 취소`} onClick={() => void cancel(order)}>취소</button>}
    </li>)}</ul>
  </section><section className="panel"><h2>주문 설명</h2>
    {detailError && <p className="error" role="alert">{detailError}</p>}
    {detail && detail.orderId === selected?.id ? <>
      <p>{detail.message}</p>
      {detail.queueAhead && <p>대기 순번 <strong>{detail.queueAhead.orders + 1}번째</strong></p>}
      {detail.ruleId && <span className="badge">{detail.ruleId}</span>}
    </> : !detailError && <p className="muted">{selected ? '주문 설명 불러오는 중' : '내 주문을 선택하면 설명을 표시합니다.'}</p>}
  </section></div>
}
