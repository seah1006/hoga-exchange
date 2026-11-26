import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { Account, Ranking, User } from './api/contract'
import type { MemberSource } from './api/marketSource'
import { MarketApiError, marketErrorMessage } from './api/serverMarketSource'
import { formatPrice, formatQuantity } from './format'

export const roundNotice = '매일 05:00 새 라운드 · 시작 자산 현금 1,000만 원 + 1,000주'
export const formatReturn = (bp: number) => `${(bp / 100).toLocaleString('ko-KR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}%`
const authMessages: Record<string, string> = {
  InvalidEmail: '이메일 주소를 확인하세요.', WeakPassword: '비밀번호를 10자 이상 입력하세요.', InvalidNickname: '닉네임은 2~16자로 입력하세요.',
  EmailTaken: '이미 가입된 이메일입니다.', NicknameTaken: '이미 사용 중인 닉네임입니다.', InvalidCredentials: '이메일 또는 비밀번호를 확인하세요.',
  LockedOut: '로그인이 잠겼습니다. 5분 뒤 다시 시도하세요.', Unauthorized: '다시 로그인하세요.', BadCsrfToken: '보안 확인에 실패했습니다. 다시 시도하세요.',
}
export const authError = (error: unknown) => error instanceof MarketApiError ? authMessages[error.code] ?? marketErrorMessage(error) : marketErrorMessage(error)
export type AuthMode = 'login' | 'register' | 'delete'

export function AuthDialog({ initial, source, close, changed }: { initial: AuthMode; source: MemberSource; close: () => void; changed: (user: User | null) => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [mode, setMode] = useState(initial)
  const [visible, show] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    const node = dialog.current
    const trigger = document.activeElement
    if (node?.showModal) node.showModal()
    else node?.setAttribute('open', '')
    return () => { node?.close?.(); if (trigger instanceof HTMLElement && trigger.isConnected) trigger.focus() }
  }, [])
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (busy) return
    const form = event.currentTarget, data = new FormData(form)
    const password = String(data.get('password') ?? ''), email = String(data.get('email') ?? '').trim(), nickname = String(data.get('nickname') ?? '').trim()
    if (mode !== 'delete' && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) return setError(authMessages.InvalidEmail)
    if (mode === 'register' && password.length < 10) return setError(authMessages.WeakPassword)
    if (mode === 'register' && (nickname.length < 2 || nickname.length > 16)) return setError(authMessages.InvalidNickname)
    if (!password) return setError('비밀번호를 입력하세요.')
    setBusy(true); setError(''); show(false)
    const field = form.elements.namedItem('password') as HTMLInputElement
    field.value = ''
    try {
      if (mode === 'delete') { await source.deleteMe(password); changed(null) }
      else changed(mode === 'register' ? await source.register({ email, password, nickname }) : await source.login({ email, password }))
      close()
    } catch (cause) {
      setError(mode === 'delete' && cause instanceof MarketApiError && cause.code === 'InvalidCredentials' ? '비밀번호를 확인하세요.' : authError(cause))
      setBusy(false); field.focus()
    }
  }
  const title = mode === 'login' ? '로그인' : mode === 'register' ? '가입' : '회원 탈퇴'
  return <dialog className="panel member-dialog" ref={dialog} aria-labelledby="auth-title" onCancel={(event) => { event.preventDefault(); if (!busy) close() }}>
    <div className="row dialog-heading"><h2 id="auth-title">{title}</h2><button type="button" onClick={close} disabled={busy}>닫기</button></div>
    {mode === 'delete' ? <p className="error">탈퇴하면 이번 라운드 주문이 취소되고 계정이 삭제됩니다. 되돌릴 수 없습니다.</p> :
      <div className="row">{(['login', 'register'] as const).map((tab) => <button key={tab} disabled={busy} aria-pressed={mode === tab} onClick={() => { setMode(tab); show(false); setError('') }}>{tab === 'login' ? '로그인' : '가입'}</button>)}</div>}
    <form key={mode} noValidate onSubmit={(event) => void submit(event)}>
      {mode !== 'delete' && <label>이메일<input name="email" type="email" autoComplete="username" required disabled={busy} /></label>}
      <div className="password-row"><label>비밀번호<input name="password" type={visible ? 'text' : 'password'} autoComplete={mode === 'register' ? 'new-password' : 'current-password'} aria-describedby={mode === 'register' ? 'password-hint' : undefined} required disabled={busy} /></label><button type="button" disabled={busy} onClick={() => show(!visible)}>{visible ? '숨기기' : '표시'}</button></div>
      {mode === 'register' && <><small id="password-hint">비밀번호 10자 이상</small><label>닉네임<input name="nickname" minLength={2} maxLength={16} required disabled={busy} /></label></>}
      {error && <p role="alert" className="error">{error}</p>}
      <button className="primary" type="submit" disabled={busy}>{busy ? '처리 중' : mode === 'delete' ? '탈퇴하기' : `${title}하기`}</button>
    </form>
  </dialog>
}

export function AccountPanel({ account, error, retry }: { account?: Account; error: string; retry: () => void }) {
  return <section className="panel" aria-label="내 계좌"><h2>내 계좌</h2>
    {error && <p className="error" role="alert">{error} <button onClick={retry}>다시 시도</button></p>}
    {!account && !error && <p role="status">계좌 불러오는 중</p>}
    {account && <><p className="muted">라운드 {account.round}</p><dl className="account-values">
      <dt>주문 가능 현금</dt><dd>{formatPrice(account.cash)}</dd><dt>묶인 현금</dt><dd>{formatPrice(account.cashOnHold)}</dd>
      <dt>매도 가능 주식</dt><dd>{formatQuantity(account.shares)}</dd><dt>묶인 주식</dt><dd>{formatQuantity(account.sharesOnHold)}</dd>
      <dt>평가액</dt><dd>{formatPrice(account.valuation)}</dd><dt>평가 손익</dt><dd>{account.pnl > 0 ? '+' : ''}{formatPrice(account.pnl)}</dd>
      <dt>수익률</dt><dd>{account.returnBp > 0 ? '+' : ''}{formatReturn(account.returnBp)}</dd>
    </dl></>}
  </section>
}

const rankingTime = new Intl.DateTimeFormat('ko-KR', { timeZone: 'Asia/Seoul', dateStyle: 'short', timeStyle: 'medium' })
export function RankingPanel({ source, reset }: { source: MemberSource; reset: number }) {
  const [round, setRound] = useState('')
  const [retries, retry] = useState(0)
  const [rounds, setRounds] = useState<string[]>([])
  const [roundError, setRoundError] = useState('')
  const [state, setState] = useState<{ key: string; data?: Ranking | null; error?: string }>({ key: '' })
  const requestKey = `${round}/${reset}/${retries}`
  useEffect(() => {
    let live = true
    void source.getRankingRounds().then((list) => { if (live) { setRounds(list); setRoundError('') } }, (error) => { if (live) setRoundError(authError(error)) })
    return () => { live = false }
  }, [source, reset, retries])
  useEffect(() => {
    let live = true, pending = false
    async function load() {
      if (pending) return
      pending = true
      try { const data = await source.getRanking(round || undefined); if (live) setState({ key: requestKey, data }) }
      catch (error) { if (live) setState({ key: requestKey, error: authError(error) }) }
      finally { pending = false }
    }
    void load()
    const timer = round ? undefined : setInterval(() => void load(), 10_000)
    return () => { live = false; clearInterval(timer) }
  }, [source, round, requestKey])
  const active = state.key === requestKey ? state : null
  const data = active?.data
  return <section className="panel ranking"><h2>라운드 랭킹</h2><p className="muted">{roundNotice}</p>
    <label>조회할 라운드<select value={round} onChange={(event) => setRound(event.target.value)}><option value="">현재 라운드</option>{rounds.map((item) => <option key={item}>{item}</option>)}</select></label>
    {roundError && <p role="alert" className="error">지난 라운드 조회: {roundError} <button onClick={() => retry((n) => n + 1)}>다시 시도</button></p>}
    {!active && <p role="status">랭킹 불러오는 중</p>}
    {active?.error && <p role="alert" className="error">{active.error} <button onClick={() => retry((n) => n + 1)}>다시 시도</button></p>}
    {data === null && <p>해당 라운드가 없습니다.</p>}
    {data && <><p className="muted">{data.round} · 갱신 {rankingTime.format(new Date(data.updatedAtUtc))} (한국시간)</p>
      {data.me && <p className="ranking-me">내 순위 {data.me.rank}위 · {formatReturn(data.me.returnBp)} · {formatPrice(data.me.valuation)}</p>}
      {!data.entries.length ? <p>아직 순위가 없습니다.</p> : <table><caption>상위 50명</caption><thead><tr><th scope="col">순위</th><th scope="col">닉네임</th><th scope="col">수익률</th><th scope="col">평가액</th></tr></thead><tbody>{data.entries.slice(0, 50).map((entry) => <tr key={entry.rank} className={entry.rank === data.me?.rank ? 'ranking-me' : undefined}><td>{entry.rank}</td><td>{entry.nickname}{entry.rank === data.me?.rank && <small> (나)</small>}</td><td>{formatReturn(entry.returnBp)}</td><td>{formatPrice(entry.valuation)}</td></tr>)}</tbody></table>}
    </>}
  </section>
}

export function Privacy({ mock }: { mock: boolean }) {
  return <section className="panel privacy"><h2>개인정보 안내</h2>
    <p>교육용 모의투자 서비스입니다. 실제 돈이나 주식을 거래하지 않습니다.</p>
    <h3>수집 항목과 목적</h3><p>이메일과 비밀번호 해시는 계정 확인에, 닉네임은 랭킹 표시에, 로그인 기록은 보안 관리에 사용합니다. 랭킹에 이메일은 공개하지 않습니다.</p>
    <h3>보관과 탈퇴</h3><p>닉네임 메뉴의 ‘탈퇴’에서 비밀번호를 다시 입력하면 계정이 삭제됩니다. 지난 랭킹에는 ‘탈퇴한 사용자’로 표시됩니다. 로그인 기록의 구체적인 보관 기간은 아직 정해지지 않았습니다.</p>
    {mock && <p className="muted">목 모드에서는 새로고침하면 입력 정보가 사라집니다. 실제 비밀번호를 사용하지 마세요.</p>}
  </section>
}
