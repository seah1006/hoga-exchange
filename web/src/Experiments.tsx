import { useEffect, useId, useState } from 'react'
import type { Experiment, ExperimentSummary, Scenario } from './api/contract'
import type { MarketSource } from './api/marketSource'
import { MarketApiError, marketErrorMessage } from './api/serverMarketSource'
import { ComparisonChart, Distribution, scenarioNames } from './ExperimentCharts'
import './Experiments.css'

const scenarios = ['Calm', 'FatFinger', 'PanicSell'] as const

type Query<T> = { value: T } | { error: unknown }
function useSummary(source: MarketSource, attempt: number) {
  const [result, setResult] = useState<{ source: MarketSource; attempt: number; state: Query<ExperimentSummary> }>()
  useEffect(() => {
    let active = true
    void source.getExperimentSummary().then(
      (value) => { if (active) setResult({ source, attempt, state: { value } }) },
      (error: unknown) => { if (active) setResult({ source, attempt, state: { error } }) },
    )
    return () => { active = false }
  }, [source, attempt])
  return result?.source === source && result.attempt === attempt ? result.state : undefined
}

export function Experiments({ source, mock }: { source: MarketSource; mock: boolean }) {
  const [scenario, setScenario] = useState<Scenario>('FatFinger')
  // 다른 시나리오에서는 명시적으로 시드를 선택할 때까지 기다린다.
  const [requested, setRequested] = useState<string | null>('demo')
  const [attempt, setAttempt] = useState(0)
  const query = useSummary(source, attempt)
  const tabId = useId()
  const summary = query && 'value' in query ? query.value : undefined
  const current = summary?.scenarios.find((item) => item.scenario === scenario)
  const demo = summary?.demo.scenario === scenario ? summary.demo.seed : undefined
  const selected = requested === 'demo' ? demo : current?.seeds.find(({ seed }) => requested === `exp-${scenario}-${seed}`)?.seed
  function choose(name: Scenario) {
    if (name === scenario) return
    setScenario(name)
    setRequested(name === (summary?.demo.scenario ?? 'FatFinger') ? 'demo' : null)
  }
  return <div className="experiment-view">
    <section className="panel experiment-overview" aria-labelledby={`${tabId}-title`}>
      <div className="experiment-heading"><div><h2 id={`${tabId}-title`}>VI 실험</h2><p className="muted">같은 시드에서 VI만 켜고 끈 뒤, 두 실험의 최저가를 비교합니다.</p></div>
        {mock && <span className="badge experiment-mock">목 모드 · 집계는 실험 보고서 수치</span>}
      </div>
      <div className="experiment-tabs" role="tablist" aria-label="실험 시나리오">
        {scenarios.map((name, index) => <button type="button" key={name} id={`${tabId}-${name}`} role="tab" aria-selected={scenario === name}
          aria-controls={`${tabId}-panel`} tabIndex={scenario === name ? 0 : -1} onClick={() => choose(name)} onKeyDown={(event) => {
            const next = event.key === 'ArrowLeft' ? (index + 2) % 3 : event.key === 'ArrowRight' ? (index + 1) % 3
              : event.key === 'Home' ? 0 : event.key === 'End' ? 2 : undefined
            if (next === undefined) return
            event.preventDefault()
            const target = scenarios[next]
            choose(target)
            document.getElementById(`${tabId}-${target}`)?.focus()
          }}>{scenarioNames[name]}</button>)}
      </div>
      <div id={`${tabId}-panel`} role="tabpanel" aria-labelledby={`${tabId}-${scenario}`}>
        {!query && <p role="status">실험 요약을 불러오는 중입니다.</p>}
        {query && 'error' in query && <div className="experiment-notice">
          <p className="error" role="alert">{query.error instanceof MarketApiError && query.error.status === 503
            ? '실험 요약을 아직 사용할 수 없습니다 (503).' : `실험 요약: ${marketErrorMessage(query.error)}`}</p>
          <button type="button" onClick={() => setAttempt((value) => value + 1)}>요약 다시 불러오기</button>
        </div>}
        {summary && (current ? <Distribution summary={current} range={summary.seeds} demo={demo} selected={selected} mock={mock}
          durationMs={summary.durationMs} onSelect={(seed) => setRequested(`exp-${scenario}-${seed}`)} /> : <p>이 시나리오의 분포 데이터가 없습니다.</p>)}
      </div>
    </section>
    <ExperimentDetail key={requested ?? scenario} source={source} id={requested} mock={mock} />
    {summary && <aside className="panel experiment-caveat"><h2>해석의 한계</h2><p>{summary.caveat}</p></aside>}
  </div>
}

function ExperimentDetail({ source, id, mock }: { source: MarketSource; id: string | null; mock: boolean }) {
  const [result, setResult] = useState<{ source: MarketSource; state: Query<Experiment | null> }>()
  useEffect(() => {
    if (!id) return
    let active = true
    void source.getExperiment(id).then(
      (value) => { if (active) setResult({ source, state: { value } }) },
      (error: unknown) => { if (active) setResult({ source, state: { error } }) },
    )
    return () => { active = false }
  }, [source, id])
  const query = result?.source === source ? result.state : undefined
  return <section className="panel comparison" aria-label="시드별 VI 비교">
    <div className="experiment-heading"><h2>VI 비교 실험</h2>{mock && <span className="badge experiment-mock">개별 실험: 목 데이터</span>}</div>
    {!id ? <p>이 시나리오의 시드를 선택하면 VI 비교를 볼 수 있습니다.</p>
      : !query ? <p role="status">실험을 불러오는 중입니다.</p>
      : 'error' in query ? <p className="error" role="alert">{marketErrorMessage(query.error)}</p>
      : query.value === null ? <p className="error" role="alert">실험을 찾을 수 없습니다.</p>
      : <ComparisonChart data={query.value} demo={id === 'demo'} />}
  </section>
}
