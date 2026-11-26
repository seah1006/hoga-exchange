import { useEffect, useRef, useState } from 'react'
import type { BookSnapshot, Candle, Trade, User } from './api/contract'
import type { MarketConnection, MemberSource } from './api/marketSource'
import { CandleChart } from './CandleChart'
import { OrderBook, TradeList, ViBanner } from './MarketPanels'
import { Admin } from './DemoPanels'
import { Experiments } from './Experiments'
import { MemberOrders } from './MemberOrders'
import { AuthDialog, Privacy, RankingPanel, authError, roundNotice } from './Members'
import type { AuthMode } from './Members'
import { formatPrice, formatTime } from './format'

const connectionLabels: Record<MarketConnection, string> = {
  connecting: '시세 서버 연결 중', syncing: '시세 동기화 중', connected: '시세 서버 연결됨', reconnecting: '시세 서버 재연결 중', disconnected: '시세 서버 연결 끊김',
}

export function App({ source, speed }: { source: MemberSource; speed?: number }) {
  const server = source.subscribeConnection !== undefined
  const [connection, setConnection] = useState<MarketConnection>('connecting')
  const [book, setBook] = useState<BookSnapshot>()
  const [candles, setCandles] = useState<Candle[]>([])
  const [trades, setTrades] = useState<(Trade & { receipt: number })[]>([])
  const nextTradeReceipt = useRef(0)
  const [resetCount, setResetCount] = useState(0)
  const [price, setPrice] = useState('')
  const [view, setView] = useState<'market' | 'comparison' | 'ranking' | 'privacy'>('market')
  const [user, setUser] = useState<User | null>()
  const [sessionError, setSessionError] = useState('')
  const [sessionRetry, retrySession] = useState(0)
  const [auth, setAuth] = useState<AuthMode>()
  const [logoutBusy, setLogoutBusy] = useState(false)
  const profileMenu = useRef<HTMLDetailsElement>(null)
  const sessionVersion = useRef(0)
  useEffect(() => {
    let live = true
    const version = sessionVersion.current
    const isCurrent = () => live && version === sessionVersion.current
    void source.refreshCsrf().catch(() => { /* 다음 변경 요청에서 재시도한다. */ })
    void source.getMe().then((profile) => { if (isCurrent()) { setUser(profile); setSessionError('') } }, (error) => { if (isCurrent()) { setUser(null); setSessionError(authError(error)) } })
    return () => { live = false }
  }, [source, sessionRetry])
  function changed(next: User | null) {
    sessionVersion.current++
    if (profileMenu.current) profileMenu.current.open = false
    setUser(next); setSessionError(''); setPrice('')
  }
  async function logout() {
    if (logoutBusy) return
    setLogoutBusy(true); setSessionError('')
    try { await source.logout(); changed(null) } catch (error) { setSessionError(authError(error)) }
    finally { setLogoutBusy(false) }
  }
  const requestLogin = () => setAuth('login')

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
      if (message.type === 'marketReset') {
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
    <header><div><span className="eyebrow">시장 구조 실습 · {server ? '서버 데이터' : '목 데이터'}</span><h1>호가 거래소</h1></div>
      <nav className="row" aria-label="화면 선택"><button aria-pressed={view === 'market'} onClick={() => setView('market')}>시장 화면</button><button aria-pressed={view === 'comparison'} onClick={() => setView('comparison')}>실험 화면</button><button aria-pressed={view === 'ranking'} onClick={() => setView('ranking')}>랭킹</button></nav>
      <div className="row header-member">{server ? <span className="badge" role="status">{connectionLabels[connection]}</span> : <span className="badge">{speed}배속</span>}
      {user ? <details className="member-menu" ref={profileMenu}><summary>{user.nickname}</summary><div className="panel member-menu-items">
        <button onClick={() => { setView('market'); if (profileMenu.current) profileMenu.current.open = false; requestAnimationFrame(() => document.querySelector('[aria-label="내 계좌"]')?.scrollIntoView?.({ block: 'nearest' })) }}>내 계좌</button>
        <button disabled={logoutBusy} onClick={() => void logout()}>{logoutBusy ? '처리 중' : '로그아웃'}</button><button disabled={logoutBusy} onClick={() => setAuth('delete')}>탈퇴</button>
      </div></details> : user === undefined ? <span role="status">로그인 확인 중</span> : <button onClick={requestLogin}>로그인/가입</button>}</div>
    </header>
    {sessionError && <p role="alert" className="error">{sessionError} <button onClick={() => retrySession((n) => n + 1)}>로그인 상태 다시 확인</button></p>}
    {user?.isAdmin && <Admin server={server} source={source} bookSeq={book?.seq} onReturn={() => setView('market')} />}
    {auth && <AuthDialog initial={auth} source={source} close={() => setAuth(undefined)} changed={changed} />}
    {resetCount > 0 && <p key={resetCount} role="status">시장이 초기화되었습니다</p>}
    <div hidden={view !== 'market'}>
    {server && book && connection !== 'connected' && connection !== 'syncing' && <p className="muted">연결이 복구될 때까지 마지막으로 받은 시세를 표시합니다.</p>}
    {book ? <>
      <section className="summary panel" aria-label="시세 요약"><div><span>HOGA · 현재가</span><strong>{formatPrice(book.lastPrice)}</strong></div><div><span>기준가</span><b>{formatPrice(book.basePrice)}</b></div><div><span>상한가</span><b className="buy">{formatPrice(book.upperLimit)}</b></div><div><span>하한가</span><b className="sell">{formatPrice(book.lowerLimit)}</b></div><div><span>{book.phase === 'Continuous' ? '접속매매' : '단일가매매'}</span><b>{formatTime(book.logicalTimeMs)}</b></div></section>
      <ViBanner book={book} server={server} />
      <div className="market"><OrderBook book={book} onPrice={(value) => setPrice(String(value))} /><div className="stack"><section className="panel"><h2>1분봉 차트</h2><CandleChart candles={candles} /></section><TradeList trades={trades} /></div>{user ? <MemberOrders key={`${user.id}/${resetCount}`} book={book} price={price} onPrice={setPrice} source={source} online={!server || connection === 'connected'} showAccount /> : <section className="panel order-entry"><h2>주문창</h2><p className="muted round-help">{roundNotice}</p>{user === undefined ? <p role="status">로그인 확인 중</p> : <><p>로그인하면 주문할 수 있습니다</p><button onClick={requestLogin}>로그인</button></>}</section>}</div>
    </> : <p>시세 불러오는 중</p>}
    </div>
    {view === 'comparison' && <Experiments source={source} mock={!server} />}
    {view === 'ranking' && <RankingPanel key={user?.id ?? 'guest'} source={source} reset={resetCount} />}
    {view === 'privacy' && <Privacy mock={!server} />}
    <footer><a href="#privacy" onClick={(event) => { event.preventDefault(); setView('privacy') }}>개인정보 안내</a> · 교육용 모의투자 · TradingView Lightweight Charts™ · Copyright (с) 2025 TradingView, Inc. <a href="https://www.tradingview.com/">TradingView</a> · <a href={`${import.meta.env.BASE_URL}licenses/lightweight-charts.LICENSE.txt`}>Apache-2.0 라이선스</a></footer>
  </main>
}


