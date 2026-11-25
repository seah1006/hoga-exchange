import { useEffect, useState } from 'react'
import type { BookSnapshot, Candle, Trade } from './api/contract'
import type { MarketSource } from './api/marketSource'
import { CandleChart } from './CandleChart'
import { OrderBook, TradeList, ViBanner } from './MarketPanels'
import { Admin, Comparison } from './DemoPanels'
import { Orders } from './Orders'
import { formatPrice, formatTime } from './format'

export function App({ source, speed }: { source: MarketSource; speed: number }) {
  const [book, setBook] = useState<BookSnapshot>()
  const [candles, setCandles] = useState<Candle[]>([])
  const [trades, setTrades] = useState<Trade[]>([])
  const [price, setPrice] = useState('')
  const [view, setView] = useState<'market' | 'comparison'>('market')
  useEffect(() => source.subscribe((message) => {
    if (message.type === 'bookSnapshot') setBook(message.data)
    if (message.type === 'trade') setTrades((old) => [message.data, ...old.slice(0, 5)])
    if (message.type === 'candle') setCandles((old) => [...old.filter((bar) => bar.startMs !== message.data.startMs).slice(-59), message.data])
  }), [source])
  return <main>
    <header><div><span className="eyebrow">시장 구조 실습 · 목 데이터</span><h1>호가 거래소</h1></div><nav className="row" aria-label="화면 선택"><button aria-pressed={view === 'market'} onClick={() => setView('market')}>시장 화면</button><button aria-pressed={view === 'comparison'} onClick={() => setView('comparison')}>비교 화면</button><span className="badge">{speed}배속</span></nav></header>
    <Admin source={source} bookSeq={book?.seq} onReturn={() => setView('market')} />
    <div hidden={view !== 'market'}>
    {book ? <>
      <section className="summary panel" aria-label="시세 요약"><div><span>HOGA · 현재가</span><strong>{formatPrice(book.lastPrice)}</strong></div><div><span>기준가</span><b>{formatPrice(book.basePrice)}</b></div><div><span>상한가</span><b className="buy">{formatPrice(book.upperLimit)}</b></div><div><span>하한가</span><b className="sell">{formatPrice(book.lowerLimit)}</b></div><div><span>{book.phase === 'Continuous' ? '접속매매' : '단일가매매'}</span><b>{formatTime(book.logicalTimeMs)}</b></div></section>
      <ViBanner book={book} />
      <div className="market"><OrderBook book={book} onPrice={(value) => setPrice(String(value))} /><div className="stack"><section className="panel"><h2>1분봉 차트</h2><CandleChart candles={candles} /></section><TradeList trades={trades} /></div><Orders book={book} price={price} onPrice={setPrice} source={source} /></div>
    </> : <p>시세 불러오는 중</p>}
    </div>
    {view === 'comparison' && <Comparison source={source} />}
    <footer>TradingView Lightweight Charts™ · Copyright (с) 2025 TradingView, Inc. <a href="https://www.tradingview.com/">TradingView</a> · <a href={`${import.meta.env.BASE_URL}licenses/lightweight-charts.LICENSE.txt`}>Apache-2.0 라이선스</a></footer>
  </main>
}
