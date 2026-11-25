import type { ExperimentRun, ExperimentScenarioSummary, ExperimentSeed, ExperimentSummary, Scenario, ViEvent } from './contract'

const durationMs = 240_000
const demo = { scenario: 'FatFinger', seed: 16, rule: 'FatFinger Δ가 중앙값에 가장 가까운 시드, 동률이면 작은 시드' } as const

// SUMMARY.md의 집계만 실측값이다. 시드 배치·개별 최저가·VI 패턴은 화면 검증용(임의 지정).
const fatDeltas = [1500, ...Array.from({ length: 23 }, (_, i) => 1700 + i * 20), 2270,
  ...Array.from({ length: 24 }, (_, i) => 2280 + i * 7), 2460, 2470,
  ...Array.from({ length: 23 }, (_, i) => 2480 + i * 7), 2650,
  ...Array.from({ length: 24 }, (_, i) => 2660 + i * 10), 2990]
const seedRank = (seed: number) => seed * 37 % 100
const demoRank = seedRank(demo.seed)
function fatDelta(seed: number) {
  const rank = seedRank(seed)
  return fatDeltas[rank === demoRank ? 50 : rank === 50 ? demoRank : rank]
}
const panicDelta = (seed: number) => {
  const rank = seedRank(seed) // 동률과 양수를 섞어서 시드 순서에 의미가 없음을 표현
  return rank < 71 ? 0 : rank < 74 ? 20 * (rank - 70) : rank < 94 ? 130 + 16 * (rank - 74) : 600 + 140 * (rank - 94)
}
function mockSeed(scenario: Scenario, seed: number): ExperimentSeed {
  const index = (seed - 1) % 100 + 1
  const delta = scenario === 'FatFinger' ? fatDelta(index) : scenario === 'PanicSell' ? panicDelta(index) : 0
  const minOff = scenario === 'FatFinger' ? (index === demo.seed ? 7020 : 7000) : scenario === 'PanicSell' ? 8300 + index % 13 * 10 : 9800 + index % 7 * 10
  const viTriggers = scenario === 'FatFinger' ? (index <= 6 ? 2 : 1)
    : scenario === 'PanicSell' ? Number(delta > 0 || seedRank(index) < 8) : Number(index === 1)
  return { seed, minOn: minOff + delta, minOff, delta, viTriggers }
}
const aggregates: Omit<ExperimentScenarioSummary, 'seeds'>[] = [
  { scenario: 'Calm', deltaQuantiles: { min: 0, p25: 0, median: 0, p75: 0, max: 0 }, counts: { viWorse: 0, tie: 100, viBetter: 0 }, runsEndingInVi: { viOn: 1, viOff: 0 } },
  { scenario: 'FatFinger', deltaQuantiles: { min: 1500, p25: 2270, median: 2465, p75: 2650, max: 2990 }, counts: { viWorse: 0, tie: 0, viBetter: 100 }, runsEndingInVi: { viOn: 6, viOff: 0 } },
  { scenario: 'PanicSell', deltaQuantiles: { min: 0, p25: 0, median: 0, p75: 130, max: 1300 }, counts: { viWorse: 0, tie: 71, viBetter: 29 }, runsEndingInVi: { viOn: 16, viOff: 0 } },
]

export function mockExperimentSummary(): ExperimentSummary {
  return {
    generatedBy: 'dotnet run --project src/Hoga.Experiments -- --batch --out docs/experiments',
    seeds: { from: 1, to: 100 }, durationMs, demo: { ...demo },
    scenarios: aggregates.map(({ scenario, deltaQuantiles, counts, runsEndingInVi }) => ({
      scenario, deltaQuantiles: { ...deltaQuantiles }, counts: { ...counts }, runsEndingInVi: { ...runsEndingInVi },
      seeds: Array.from({ length: 100 }, (_, i) => mockSeed(scenario, i + 1)),
    })),
    caveat: '이 결과는 임의 지정한 봇·호가 깊이·충격 크기·관측 구간에 한정된다. VI의 실제 시장 효과나 다른 입력에서의 우위를 입증하지 않는다. 최저가 차이에는 VI 중 체결을 기다리는 시간이 포함되므로 거래량·체결 지연과도 구분해서 읽어야 한다.',
  }
}

export function mockExperimentRuns(scenario: Scenario, seed: number): ExperimentRun[] {
  const { minOn, minOff, viTriggers } = mockSeed(scenario, seed)
  return [true, false].map((viEnabled) => {
    const minPrice = viEnabled ? minOn : minOff
    const maxPrice = 10_100
    const viEvents: ViEvent[] = []
    const index = (seed - 1) % 100 + 1
    const shock = scenario === 'PanicSell' ? 90_000 : 60_000
    const openAtEnd = scenario === 'Calm' || scenario === 'PanicSell' && seedRank(index) >= 84
    const startPrice = 10_000
    const sampledMin = scenario === 'FatFinger' && !viEnabled ? 9_180 : Math.min(startPrice, minPrice + 20)
    for (let i = 0; viEnabled && i < viTriggers; i++) {
      const fields = { kind: 'Dynamic', referencePrice: 10_000, triggerPrice: minPrice, ruleId: 'R-VI-01' } as const
      const atMs = openAtEnd && i === viTriggers - 1 ? 220_000 : shock + i * 125_000
      viEvents.push({ ...fields, type: 'Triggered', atMs })
      if (atMs + 120_000 <= durationMs) viEvents.push({ ...fields, type: 'Released', atMs: atMs + 120_000, auctionPrice: sampledMin })
    }
    // 일부 극값은 1초 종가 사이에서 발생하도록 구성한다(임의 지정).
    const released = viEvents.find((event) => event.type === 'Released')
    const minAtMs = (released?.atMs ?? shock) + 200
    const maxAtMs = 19_300
    let lastPrice = startPrice, nextEvent = 0
    const series = Array.from({ length: durationMs / 1000 + 1 }, (_, i) => {
      const timeMs = i * 1000
      const trough = minAtMs / 1000
      const impact = i < shock / 1000 ? 0 : Math.max(0, 1 - Math.abs(i - trough) / 45)
      const noise = (i * 11 + seed * 3) % 7 * 10 - 30
      const price = i === 0 ? startPrice : Math.max(sampledMin, Math.round((startPrice - (startPrice - sampledMin) * impact + noise) / 10) * 10)
      while (nextEvent < viEvents.length && viEvents[nextEvent].atMs <= timeMs) nextEvent++
      const event = viEvents[nextEvent - 1]
      if (event?.type !== 'Triggered') lastPrice = event?.atMs === timeMs && event.type === 'Released' ? event.auctionPrice : price
      return { timeMs, price: lastPrice }
    })
    return { viEnabled, series, viEvents, minPrice, maxPrice, minAtMs, maxAtMs }
  })
}
