import type { BookSnapshot, Level, Trade } from './api/contract'
import { formatPrice, formatQuantity, formatTime } from './format'

export function ViBanner({ book, server }: { book: BookSnapshot; server: boolean }) {
  if (!book.vi) return null
  const { kind, referencePrice, scheduledEndMs, ruleId } = book.vi
  const seconds = Math.max(0, Math.ceil((scheduledEndMs - book.logicalTimeMs) / 1000))
  return <aside className="vi" role="status" aria-label="변동성완화장치 진행 중">
    <strong>{kind === 'Dynamic' ? '동적' : '정적'} VI · 단일가매매 진행 중</strong><span className="badge">{ruleId}</span>
    <span>참조가격 <b>{formatPrice(referencePrice)}</b></span>
    <span>약 <b>{seconds}초</b> 남음 (최대 30초 연장){server && ' · 거래소 논리 시간 기준'}</span>
    <span>예상 체결 <b>{book.expectedPrice == null ? '미정' : formatPrice(book.expectedPrice)} / {book.expectedQuantity == null ? '미정' : formatQuantity(book.expectedQuantity)}</b> {!server && <span className="badge">목 데이터 예시값</span>}</span>
  </aside>
}

export function OrderBook({ book, onPrice }: { book: BookSnapshot; onPrice: (price: number) => void }) {
  const max = Math.max(1, ...[...book.asks, ...book.bids].map(({ quantity }) => quantity))
  const row = (level: Level, side: string) => <tr key={level.price} className={side}>
    <td><button type="button" onClick={() => onPrice(level.price)}>{formatPrice(level.price)}</button></td>
    <td className="depth"><span className="depth-bar" style={{ width: `${100 * level.quantity / max}%` }} /><span>{formatQuantity(level.quantity)}</span></td>
  </tr>
  return <section className="panel"><h2>호가창 <small>10단계</small></h2>
    <table><thead><tr><th>호가</th><th>잔량</th></tr></thead><tbody>
      <tr className="section-row"><th colSpan={2}>매도</th></tr>
      {[...book.asks].reverse().map((l) => row(l, 'sell'))}
      <tr className="current"><th colSpan={2}>현재가 {formatPrice(book.lastPrice)}</th></tr>
      <tr className="section-row"><th colSpan={2}>매수</th></tr>
      {book.bids.map((l) => row(l, 'buy'))}
    </tbody></table>
  </section>
}

export function TradeList({ trades }: { trades: Trade[] }) {
  return <section className="panel"><h2 id="recent-trades">최근 체결</h2><div className="trade-scroll" role="region" aria-labelledby="recent-trades" tabIndex={0}>
    <table><thead><tr><th>시각</th><th>가격</th><th>수량</th><th>구분</th></tr></thead><tbody>
      {trades.map((trade) => <tr key={trade.seq}><td>{formatTime(trade.logicalTimeMs)}</td><td>{formatPrice(trade.price)}</td><td>{formatQuantity(trade.quantity)}</td><td className={trade.aggressorSide === 'Buy' ? 'buy' : trade.aggressorSide === 'Sell' ? 'sell' : ''}>{trade.aggressorSide === 'Buy' ? '매수' : trade.aggressorSide === 'Sell' ? '매도' : '단일가'}</td></tr>)}
      {!trades.length && <tr><td colSpan={4}>첫 체결 대기 중</td></tr>}
    </tbody></table></div>
  </section>
}
