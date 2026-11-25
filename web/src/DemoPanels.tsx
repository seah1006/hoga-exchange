import { useState } from 'react'
import type { Scenario, StateHash } from './api/contract'
import type { MarketSource } from './api/marketSource'
import { readAdminKey, writeAdminKey } from './api/adminKey'
import { marketErrorMessage } from './api/serverMarketSource'

const names: Record<Scenario, string> = { Calm: '평온', FatFinger: '팻 핑거', PanicSell: '패닉 매도' }
function AdminKeyField() {
  const [key, setKey] = useState(readAdminKey)
  return <label>관리자 키 <input type="password" autoComplete="off" value={key} onChange={(event) => {
    setKey(event.target.value)
    writeAdminKey(event.target.value)
  }} /></label>
}

export function Admin({ source, server, bookSeq, onReturn }: { source: MarketSource; server: boolean; bookSeq?: number; onReturn: () => void }) {
  const [note, setNote] = useState('')
  const [error, setError] = useState('')
  const [hash, setHash] = useState<StateHash>()
  const [beforeHash, setBeforeHash] = useState<string>()
  const justRestored = hash?.restoredFromJournal && hash.seq === bookSeq
  async function run(name: Scenario) {
    const result = await source.runScenario(name)
    setNote(result.ok ? `${names[name]} 시나리오를 시작했습니다.` : '단일가매매가 끝난 뒤 시나리오를 시작하세요.')
    if (result.ok) onReturn()
  }
  async function restore() {
    const before = await source.getStateHash()
    const result = await source.crash()
    const after = await source.getStateHash()
    setBeforeHash(before.stateHash); setHash(after)
    setNote(result.ok ? '' : '복구에 실패했습니다.')
  }
  async function perform(action: () => Promise<void>) {
    setNote(''); setError('')
    try { await action() } catch (cause) { setError(marketErrorMessage(cause)) }
  }
  return <section className="panel admin"><h2>관리자 패널</h2><div className="row">
    {server && <AdminKeyField />}
    {(Object.keys(names) as Scenario[]).map((name) => <button key={name} onClick={() => void perform(() => run(name))}>{names[name]}</button>)}
    <button onClick={() => void perform(restore)}>서버 강제 종료</button>
    <button onClick={() => void perform(async () => { setBeforeHash(undefined); setHash(await source.getStateHash()) })}>상태 해시 조회</button>
  </div><div aria-live="polite">
    {note && <p>{note}</p>}
    {error && <p className="error" role="alert">{error}</p>}
    {hash && <p>상태 해시 <code>{hash.stateHash}</code> · 순번 {hash.seq} {justRestored && <strong className="success">재생으로 복구됨</strong>}
      {justRestored && beforeHash && <span> · 복구 전 <code>{beforeHash}</code> / {beforeHash === hash.stateHash ? '일치' : '불일치'}</span>}</p>}
  </div></section>
}
