import { useEffect, useState } from 'react'
import type { Experiment, Scenario, StateHash } from './api/contract'
import type { MarketSource } from './api/marketSource'
import { formatPrice, formatTime } from './format'

export function Comparison({ source }: { source: MarketSource }) {
  const [data, setData] = useState<Experiment | null>()
  useEffect(() => {
    let active = true
    void source.getExperiment('exp-1').then((value) => { if (active) setData(value) })
    return () => { active = false }
  }, [source])
  if (!data) return <section className="panel"><h2>VI 비교 실험</h2><p>{data === null ? '실험을 찾을 수 없습니다.' : '실험 불러오는 중'}</p></section>
  const on = data.runs.find((run) => run.viEnabled), off = data.runs.find((run) => !run.viEnabled)
  if (!on?.series.length || !off?.series.length) return <p>비교 시세가 없습니다.</p>
  const end = Math.max(1, on.series.at(-1)!.timeMs, off.series.at(-1)!.timeMs)
  const min = Math.floor(Math.min(on.minPrice, off.minPrice) / 500) * 500
  const max = Math.max(min + 500, Math.ceil(Math.max(on.maxPrice, off.maxPrice) / 500) * 500)
  const x = (time: number) => 102 + time / end * 850
  const y = (price: number) => 320 - (price - min) / (max - min) * 290
  const intervals = on.viEvents.flatMap((event, i) => event.type === 'Triggered' ? [{ start: event.atMs, end: on.viEvents.slice(i + 1).find((e) => e.type === 'Released')?.atMs ?? end }] : [])
  return <section className="panel comparison"><h2>VI 비교 실험</h2><p className="muted">같은 시드 {data.seed} · 팻 핑거 · VI만 켜고 끈 결과</p>
    <div className="compare-summary"><div><span>VI 켜짐 최저가</span><b className="with-vi">{formatPrice(on.minPrice)}</b></div><div><span>VI 꺼짐 최저가</span><b className="without-vi">{formatPrice(off.minPrice)}</b></div><div><span>최저가 차이</span><strong>{formatPrice(Math.abs(on.minPrice - off.minPrice))}</strong></div></div>
    <p className="graph-key"><b className="with-vi">━ VI 켜짐</b><b className="without-vi">┄ VI 꺼짐</b><span>노란 음영: 단일가매매 구간</span></p>
    <svg viewBox="0 0 1000 365" role="img" aria-label="같은 시드의 VI 켜짐과 꺼짐 가격 비교 그래프">
      {intervals.map((i) => <rect key={i.start} x={x(i.start)} y="30" width={x(i.end) - x(i.start)} height="290" fill="#f4c371" opacity=".18" />)}
      {[0, 1, 2, 3, 4].map((i) => Math.round((min + (max - min) * i / 4) / 10) * 10).map((p) => <g key={p}><line x1="102" x2="952" y1={y(p)} y2={y(p)} stroke="#3a5375"/><text x="94" y={y(p) + 5} textAnchor="end">{formatPrice(p)}</text></g>)}
      {[0, .2, .4, .6, .8, 1].map((fraction) => <text key={fraction} x={x(end * fraction)} y="346" textAnchor="middle">{formatTime(end * fraction)}</text>)}
      {[on, off].map((run) => <polyline key={String(run.viEnabled)} points={run.series.map((p) => `${x(p.timeMs)},${y(p.price)}`).join(' ')} fill="none" stroke={run.viEnabled ? '#87eac6' : '#ff9ca7'} strokeWidth="3.5" strokeDasharray={run.viEnabled ? undefined : '7 4'} />)}
    </svg>
  </section>
}

const names: Record<Scenario, string> = { Calm: '평온', FatFinger: '팻 핑거', PanicSell: '패닉 매도' }
export function Admin({ source, onReturn }: { source: MarketSource; onReturn: () => void }) {
  const [note, setNote] = useState('')
  const [hash, setHash] = useState<StateHash>()
  const [beforeHash, setBeforeHash] = useState<string>()
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
  return <section className="panel admin"><h2>관리자 패널</h2><div className="row">
    {(Object.keys(names) as Scenario[]).map((name) => <button key={name} onClick={() => void run(name)}>{names[name]}</button>)}
    <button onClick={() => void restore()}>서버 강제 종료</button>
    <button onClick={() => { setBeforeHash(undefined); void source.getStateHash().then(setHash) }}>상태 해시 조회</button>
  </div><div aria-live="polite">
    {note && <p>{note}</p>}
    {hash && <p>상태 해시 <code>{hash.stateHash}</code> · 순번 {hash.seq} {hash.restoredFromJournal && <strong className="success">재생으로 복구됨</strong>}
      {beforeHash && <span> · 복구 전 <code>{beforeHash}</code> / {beforeHash === hash.stateHash ? '일치' : '불일치'}</span>}</p>}
  </div></section>
}
