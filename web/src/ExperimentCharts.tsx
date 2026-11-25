import type { Experiment, ExperimentScenarioSummary, ExperimentSummary } from './api/contract'
import { formatPrice, formatTime } from './format'

export const scenarioNames = { Calm: '평온', FatFinger: '팻 핑거', PanicSell: '패닉 매도' }
const formatDelta = (delta: number) => `${delta > 0 ? '+' : ''}${formatPrice(delta)}`

function SummaryCards({ summary, durationMs }: { summary: ExperimentScenarioSummary; durationMs: number }) {
  const { counts, deltaQuantiles, runsEndingInVi } = summary
  return <div className="experiment-cards">
    <div><span>최저가 차이 중앙값</span><strong>{formatDelta(deltaQuantiles.median)}</strong></div>
    <div><span>VI 켬이 더 높음</span><strong className="with-vi">{counts.viBetter}개</strong></div>
    <div><span>동률</span><strong>{counts.tie}개</strong></div>
    <div><span>VI 켬이 더 나쁨</span><strong className="without-vi">{counts.viWorse}개</strong></div>
    <div><span>{durationMs / 1000}초에 VI 진행 중</span><strong>{runsEndingInVi.viOn + runsEndingInVi.viOff}건</strong>
      <small>켬 {runsEndingInVi.viOn}건 · 끔 {runsEndingInVi.viOff}건</small></div>
  </div>
}

export function Distribution({ summary, demo, selected, range, mock, durationMs, onSelect }: {
  summary: ExperimentScenarioSummary; range: ExperimentSummary['seeds']; demo?: number; selected?: number;
  mock: boolean; durationMs: number; onSelect: (seed: number) => void
}) {
  const { seeds } = summary
  const left = 106, right = 1100, top = 27, bottom = 173
  const spacing = (right - left) / Math.max(1, range.to - range.from + 1)
  const x = (seed: number) => left + (seed - range.from + .5) * spacing
  // 관측된 분포와 0을 모두 포함한다. 축 반올림과 여백은 표시용(임의 지정).
  const unit = Math.max(10, 10 ** Math.floor(Math.log10(Math.max(1, ...seeds.map(({ delta }) => Math.abs(delta))))))
  const min = Math.floor(Math.min(0, ...seeds.map(({ delta }) => delta)) / unit) * unit
  const max = Math.ceil(Math.max(0, ...seeds.map(({ delta }) => delta)) / unit) * unit
  const pad = Math.max(unit, max - min) * .12
  const y = (delta: number) => bottom - (delta - min + pad) / (max - min + 2 * pad) * (bottom - top)
  const ticks = [...new Set([min, 0, max])]
  const xTicks = [...new Set([range.from, ...[25, 50, 75].filter((seed) => seed > range.from && seed < range.to), range.to])]
  return <>
    <SummaryCards summary={summary} durationMs={durationMs} />
    <div className="experiment-heading distribution-heading"><div><h3>시드 {seeds.length}개 분포 {mock && <span className="badge experiment-mock">시드별 값: 목 데이터</span>}</h3>
      <p className="muted">Δ = VI 켬 최저가 − VI 끔 최저가 · 양수이면 VI 켬의 최저가가 더 높았습니다.</p></div>
      <label className="seed-picker">시드 선택 <select value={selected ?? ''} onChange={(event) => { if (event.target.value) onSelect(Number(event.target.value)) }}>
        <option value="" disabled>시드 번호</option>
        {seeds.map(({ seed, delta }) => <option key={seed} value={seed}>{seed}{seed === demo ? ' (시연)' : ''} · {formatDelta(delta)}</option>)}
      </select></label>
    </div>
    <p className="distribution-key"><span>점이나 번호 목록에서 시드를 고르면 아래 상세가 바뀝니다.</span>{demo !== undefined && <b>◇ 시연 시드 {demo}</b>}</p>
    <svg className="seed-distribution" viewBox="0 0 1160 218" role="group" aria-label="시드별 최저가 차이 분포">
      <text x="18" y="22">Δ (원)</text>
      {ticks.map((value) => <g key={value}><line x1={left} x2={right} y1={y(value)} y2={y(value)} className={value === 0 ? 'zero-line' : 'grid-line'} />
        <text x={left - 10} y={y(value) + 5} textAnchor="end">{value.toLocaleString('ko-KR')}</text></g>)}
      {xTicks.map((seed) => <text key={seed} x={x(seed)} y="200" textAnchor="middle">{seed}</text>)}
      <text x="1115" y="200">시드</text>
      {seeds.map(({ seed, delta }) => <g key={seed} role="button" aria-label={`시드 ${seed}${seed === demo ? ' (시연)' : ''}: ${formatDelta(delta)}`}
        aria-pressed={seed === selected} className={`seed-mark ${delta > 0 ? 'positive' : delta < 0 ? 'negative' : 'tie'}`} onClick={() => onSelect(seed)}>
        <title>{`시드 ${seed} · ${formatDelta(delta)}`}</title>
        <rect className="seed-target" x={x(seed) - spacing / 2} y="0" width={spacing} height="182" />
        <circle cx={x(seed)} cy={y(delta)} r="3.3" className="seed-point" />
        {seed === selected && <circle cx={x(seed)} cy={y(delta)} r="6.5" className="seed-selection" />}
        {seed === demo && <rect x={x(seed) - 6} y={y(delta) - 6} width="12" height="12" transform={`rotate(45 ${x(seed)} ${y(delta)})`} className="seed-demo" />}
      </g>)}
    </svg>
  </>
}

export function ComparisonChart({ data, demo }: { data: Experiment; demo: boolean }) {
  const on = data.runs.find((run) => run.viEnabled), off = data.runs.find((run) => !run.viEnabled)
  if (!on?.series.length || !off?.series.length) return <p>비교 시세가 없습니다.</p>
  const end = Math.max(1, on.series.at(-1)!.timeMs, off.series.at(-1)!.timeMs)
  const min = Math.floor(Math.min(on.minPrice, off.minPrice) / 500) * 500
  const max = Math.max(min + 500, Math.ceil(Math.max(on.maxPrice, off.maxPrice) / 500) * 500)
  const x = (time: number) => 112 + time / end * 994
  const y = (price: number) => 302 - (price - min) / (max - min) * 266
  const intervals = on.viEvents.flatMap((event, i) => event.type === 'Triggered' ? [{ start: event.atMs, end: on.viEvents.slice(i + 1).find((e) => e.type === 'Released')?.atMs ?? end }] : [])
  return <>
    <p className="muted">{scenarioNames[data.scenario]} · 시드 {data.seed}{demo ? ' (시연)' : ''} · VI만 켜고 끈 결과</p>
    <div className="compare-summary"><div><span>VI 켜짐 최저가</span><b className="with-vi">{formatPrice(on.minPrice)}</b></div>
      <div><span>VI 꺼짐 최저가</span><b className="without-vi">{formatPrice(off.minPrice)}</b></div>
      <div><span>최저가 차이</span><strong>{formatDelta(on.minPrice - off.minPrice)}</strong></div></div>
    <p className="graph-key"><b className="with-vi">━ VI 켜짐</b><b className="without-vi">┄ VI 꺼짐</b>
      <span>선: 1초 종가 · ● 실제 체결 극값 · ◇ 시작 기준가(0ms)</span><span className="auction-key"><i className="auction-chip" aria-hidden="true" />노란 음영: 단일가매매 구간</span></p>
    <svg viewBox="0 0 1160 345" role="img" aria-label="같은 시드의 VI 켜짐과 꺼짐 가격 비교 그래프">
      {intervals.map((i) => <rect key={i.start} className="auction-window" x={x(i.start)} y="36" width={x(i.end) - x(i.start)} height="266" />)}
      {[0, 1, 2, 3, 4].map((i) => Math.round((min + (max - min) * i / 4) / 10) * 10).map((p) => <g key={p}>
        <line x1="112" x2="1106" y1={y(p)} y2={y(p)} className="grid-line"/><text x="102" y={y(p) + 5} textAnchor="end">{formatPrice(p)}</text></g>)}
      {[0, .2, .4, .6, .8, 1].map((fraction) => <text key={fraction} x={x(end * fraction)} y="330" textAnchor="middle">{formatTime(end * fraction)}</text>)}
      {[on, off].map((run) => <polyline className={run.viEnabled ? 'vi-on' : 'vi-off'} key={String(run.viEnabled)}
        points={run.series.map((p) => `${x(p.timeMs)},${y(p.price)}`).join(' ')} fill="none" strokeWidth="3.5" strokeDasharray={run.viEnabled ? undefined : '7 4'} />)}
      {[on, off].map((run) => <g key={String(run.viEnabled)} className={`extrema ${run.viEnabled ? 'vi-on' : 'vi-off'}`}>
        {([{ time: run.minAtMs, price: run.minPrice, label: '최저' }, { time: run.maxAtMs, price: run.maxPrice, label: '최고' }]).map(({ time, price, label }) => {
          if (!Number.isFinite(time)) return null
          if (time === 0) {
            const px = x(time), py = y(price), radius = run.viEnabled ? 7 : 4.5
            return <path key={label} className="starting-reference" d={`M ${px} ${py - radius} l ${radius} ${radius} l ${-radius} ${radius} l ${-radius} ${-radius} Z`}>
              <title>{`VI ${run.viEnabled ? '켬' : '끔'} ${label}: 시작 기준가 ${formatPrice(price)}`}</title>
            </path>
          }
          return <circle key={label} cx={x(time)} cy={y(price)} r={run.viEnabled ? 7 : 4.5}>
            <title>{`VI ${run.viEnabled ? '켬' : '끔'} ${label}: ${formatPrice(price)} · ${time / 1000}초`}</title>
          </circle>
        })}
      </g>)}
    </svg>
  </>
}
