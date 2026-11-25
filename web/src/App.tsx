import { useEffect, useRef, useState } from 'react'
import type { BookSnapshot, Candle, Trade } from './api/contract'
import type { MarketConnection, MarketSource } from './api/marketSource'
import { CandleChart } from './CandleChart'
import { OrderBook, TradeList, ViBanner } from './MarketPanels'
import { Admin } from './DemoPanels'
import { Experiments } from './Experiments'
import { Orders } from './Orders'
import { formatPrice, formatTime } from './format'

const connectionLabels: Record<MarketConnection, string> = {
  connecting: '시세 서버 연결 중', syncing: '시세 동기화 중', connected: '시세 서버 연결됨', reconnecting: '시세 서버 재연결 중', disconnected: '시세 서버 연결 끊김',
}

export function App({ source, speed }: { source: MarketSource; speed?: number }) {
  const server = source.subscribeConnection !== undefined
  const [connection, setConnection] = useState<MarketConnection>('connecting')
  const [book, setBook] = useState<BookSnapshot>()
  const [candles, setCandles] = useState<Candle[]>([])
  const [trades, setTrades] = useState<(Trade & { receipt: number })[]>([])
  const nextTradeReceipt = useRef(0)
  const [resetCount, setResetCount] = useState(0)
  const [price, setPrice] = useState('')
  const [view, setView] = useState<'market' | 'comparison'>('market')
  useEffect(() => {
    let lastBookSeq: number | undefined
    let awaitingFirstSnapshot = false
    const clearHistory = () => { setCandles([]); setTrades([]) }
    const clearMarket = () => {
      clearHistory(); setPrice(''); setResetCount((count) => count + 1)
    }
    const unsubscribeConnection = source.subscribeConnection?.((status) => {
      if (status === 'syncing') { clearHistory(); awaitingFirstSnapshot = true }
      setConnection(status)
    })
    const unsubscribe = source.subscribe((message) => {
      if (server && message.type === 'marketReset') {
        lastBookSeq = undefined
        setBook(undefined); clearMarket()
      }
      if (message.type === 'bookSnapshot') {
        if (awaitingFirstSnapshot && lastBookSeq !== undefined && message.data.seq < lastBookSeq) clearMarket()
        awaitingFirstSnapshot = false
        lastBookSeq = message.data.seq
        setBook(message.data)
      }
      if (message.type === 'trade') {
        const trade = { ...message.data, receipt: nextTradeReceipt.current++ }
        // 서버의 연결별 이력과 라이브는 겹치지 않고, 한 명령의 seq에 여러 체결이 있을 수 있다.
        setTrades((old) => [trade, ...(server ? old : old.filter(({ seq }) => seq !== trade.seq))].slice(0, server ? 50 : 6))
      }
      if (message.type === 'candle') setCandles((old) => {
        const next = [...old.filter((bar) => bar.startMs !== message.data.startMs), message.data].sort((a, b) => a.startMs - b.startMs)
        return server ? next : next.slice(-60)
      })
    })
    return () => { unsubscribeConnection?.(); unsubscribe() }
  }, [source, server])
  return <main>
    <header><div><span className="eyebrow">시장 구조 실습 · {server ? '서버 데이터' : '목 데이터'}</span><h1>호가 거래소</h1></div><nav className="row" aria-label="화면 선택"><button aria-pressed={view === 'market'} onClick={() => setView('market')}>시장 화면</button><button aria-pressed={view === 'comparison'} onClick={() => setView('comparison')}>실험 화면</button>{server ? <span className="badge" role="status">{connectionLabels[connection]}</span> : <span className="badge">{speed}배속</span>}</nav></header>
    <Admin server={server} source={source} bookSeq={book?.seq} onReturn={() => setView('market')} />
    {resetCount > 0 && <p key={resetCount} role="status">시장이 초기화되었습니다</p>}
    <div hidden={view !== 'market'}>
    {server && book && connection !== 'connected' && connection !== 'syncing' && <p className="muted">연결이 복구될 때까지 마지막으로 받은 시세를 표시합니다.</p>}
    {book ? <>
      <section className="summary panel" aria-label="시세 요약"><div><span>HOGA · 현재가</span><strong>{formatPrice(book.lastPrice)}</strong></div><div><span>기준가</span><b>{formatPrice(book.basePrice)}</b></div><div><span>상한가</span><b className="buy">{formatPrice(book.upperLimit)}</b></div><div><span>하한가</span><b className="sell">{formatPrice(book.lowerLimit)}</b></div><div><span>{book.phase === 'Continuous' ? '접속매매' : '단일가매매'}</span><b>{formatTime(book.logicalTimeMs)}</b></div></section>
      <ViBanner book={book} server={server} />
      <div className="market"><OrderBook book={book} onPrice={(value) => setPrice(String(value))} /><div className="stack"><section className="panel"><h2>1분봉 차트</h2><CandleChart candles={candles} /></section><TradeList trades={trades} /></div><Orders key={resetCount} book={book} price={price} onPrice={setPrice} source={source} canPoll={!server || connection === 'connected'} /></div>
    </> : <p>시세 불러오는 중</p>}
    </div>
    {view === 'comparison' && <Experiments source={source} mock={!server} />}
    <footer>TradingView Lightweight Charts™ · Copyright (с) 2025 TradingView, Inc. <a href="https://www.tradingview.com/">TradingView</a> · <a href={`${import.meta.env.BASE_URL}licenses/lightweight-charts.LICENSE.txt`}>Apache-2.0 라이선스</a></footer>
  </main>
}
