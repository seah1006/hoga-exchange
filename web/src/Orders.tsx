import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { BookSnapshot, Explanation, OrderRequest, Side } from './api/contract'
import type { MarketSource } from './api/marketSource'
import { formatPrice, formatQuantity } from './format'

const reasons: Record<string, string> = { AboveUpperLimit: '상한가를 초과했습니다.', BelowLowerLimit: '하한가보다 낮습니다.', InvalidTick: '10원 호가단위에 맞지 않습니다.', InvalidOrder: '가격과 수량을 올바른 정수로 입력하세요.' }
const sideText = (side: Side) => side === 'Buy' ? '매수' : '매도'

type MyOrder = OrderRequest & { id: string; cancelled: boolean }
export function Orders({ source, book, price, onPrice }: { source: MarketSource; book: BookSnapshot; price: string; onPrice: (price: string) => void }) {
  const [side, setSide] = useState<Side>('Buy')
  const [quantity, setQuantity] = useState('100')
  const [orders, setOrders] = useState<MyOrder[]>([])
  const [selected, setSelected] = useState<MyOrder | null>(null)
  const [detail, setDetail] = useState<Explanation | null>(null)
  const [notice, setNotice] = useState<{ text: string; rule?: string | null; error?: boolean } | null>(null)
  useEffect(() => {
    if (!selected) return
    let active = true
    void source.getExplanation(selected.id).then((value) => { if (active) setDetail(value) })
    return () => { active = false }
  }, [source, selected, book.seq])

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const request = { side, price: Number(price || book.lastPrice), quantity: Number(quantity) }
    const result = await source.placeOrder(request)
    if (!result.accepted) {
      setNotice({ text: `주문 거부: ${reasons[result.reason] ?? result.reason}`, rule: result.ruleId, error: true }); return
    }
    const next = { ...request, id: result.orderId, cancelled: false }
    setOrders((old) => [next, ...old]); setSelected(next); setNotice({ text: '주문이 접수되었습니다.' })
  }
  async function cancel(order: MyOrder) {
    const result = await source.cancelOrder(order.id)
    if (!result.cancelled) { setNotice({ text: '취소할 수 없는 주문입니다.', error: true }); return }
    const next = { ...order, cancelled: true }
    setOrders((old) => old.map((item) => item.id === next.id ? next : item)); setSelected(next)
    setNotice({ text: '주문을 취소했습니다.' })
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
    {detail && detail.orderId === selected?.id ? <>
      <p>{detail.message}</p>
      {detail.queueAhead && <p>대기 순번 <strong>{detail.queueAhead.orders + 1}번째</strong></p>}
      {detail.ruleId && <span className="badge">{detail.ruleId}</span>}
    </> : <p className="muted">내 주문을 선택하면 설명을 표시합니다.</p>}
  </section></div>
}
