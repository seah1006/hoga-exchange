import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { Experiments } from './Experiments'
import { MockMarketSource } from './api/mockMarketSource'
import { ServerMarketSource } from './api/serverMarketSource'

const graphName = '같은 시드의 VI 켜짐과 꺼짐 가격 비교 그래프'
afterEach(cleanup)

test('목 실험: 시나리오별 집계와 100개 시드, 시연 강조 및 원문 한계를 표시한다', async () => {
  const source = new MockMarketSource()
  const summary = await source.getExperimentSummary()
  render(<Experiments source={source} mock />)
  const distribution = await screen.findByRole('group', { name: '시드별 최저가 차이 분포' })
  expect(within(distribution).getAllByRole('button')).toHaveLength(100)
  const demo = within(distribution).getByRole('button', { name: '시드 16 (시연): +2,470원' })
  expect(demo.getAttribute('aria-pressed')).toBe('true')
  expect(demo.querySelector('.seed-demo')).not.toBeNull()
  expect(distribution.querySelector('.zero-line')).not.toBeNull()
  expect(screen.getByText('+2,465원')).toBeDefined()
  expect(screen.getByText('시드별 값: 목 데이터')).toBeDefined()
  expect(screen.getByText(summary.caveat)).toBeDefined()
  const tab = screen.getByRole('tab', { name: '팻 핑거' })
  fireEvent.keyDown(tab, { key: 'ArrowRight' })
  const panic = screen.getByRole('tab', { name: '패닉 매도' })
  expect(document.activeElement).toBe(panic)
  expect(panic.getAttribute('aria-selected')).toBe('true')
  expect(screen.getByText('71개')).toBeDefined()
  expect(screen.getByText('29개')).toBeDefined()
  expect(screen.getByText('켬 16건 · 끔 0건')).toBeDefined()
  expect(screen.queryByRole('img', { name: graphName })).toBeNull()
  fireEvent.click(screen.getByRole('tab', { name: '평온' }))
  expect(screen.getByText('100개')).toBeDefined()
  expect(screen.getByText('켬 1건 · 끔 0건')).toBeDefined()
})

test('계약 v0.4 경로로 demo와 선택한 시드를 조회하며 상세 404를 처리한다', async () => {
  const mock = new MockMarketSource()
  const fetcher = vi.fn<typeof fetch>(async (url) => {
    const id = String(url).split('/').at(-1)!
    if (id === 'summary') return Response.json(await mock.getExperimentSummary())
    if (id === 'exp-Calm-5') return Response.json({ error: 'NotFound' }, { status: 404 })
    return Response.json(await mock.getExperiment(id))
  })
  render(<Experiments source={new ServerMarketSource({ fetcher })} mock={false} />)
  expect(await screen.findByRole('img', { name: graphName })).toBeDefined()
  expect(fetcher.mock.calls.map(([url]) => url).sort()).toEqual(['/api/experiments/demo', '/api/experiments/summary'])
  fireEvent.click(screen.getByRole('button', { name: /^시드 42:/ }))
  expect(await screen.findByText(/팻 핑거 · 시드 42 ·/)).toBeDefined()
  expect(fetcher.mock.calls.at(-1)![0]).toBe('/api/experiments/exp-FatFinger-42')
  fireEvent.click(screen.getByRole('tab', { name: '평온' }))
  fireEvent.change(screen.getByRole('combobox', { name: '시드 선택' }), { target: { value: '5' } })
  expect(fetcher.mock.calls.at(-1)![0]).toBe('/api/experiments/exp-Calm-5')
  expect((await screen.findByRole('alert')).textContent).toBe('실험을 찾을 수 없습니다.')
})

test('비교 그래프의 최저·최고 표식은 1초 종가가 아닌 계약의 ms와 가격을 사용한다', async () => {
  const source = new MockMarketSource()
  const demo = (await source.getExperiment('demo'))!
  const off = demo.runs.find((run) => !run.viEnabled)!
  vi.spyOn(source, 'getExperiment').mockResolvedValue({ ...demo, runs: [demo.runs[0], { ...off, minAtMs: 60_275, maxAtMs: 19_125 }] })
  render(<Experiments source={source} mock />)
  const graph = await screen.findByRole('img', { name: graphName })
  const [min, max] = graph.querySelectorAll('.vi-off.extrema circle')
  expect(Number(min.getAttribute('cx'))).toBeCloseTo(112 + 60_275 / 240_000 * 994)
  expect(Number(max.getAttribute('cx'))).toBeCloseTo(112 + 19_125 / 240_000 * 994)
  expect(Number(min.getAttribute('cy'))).toBeCloseTo(302 - (7020 - 7000) / 3500 * 266)
  expect(min.textContent).toContain('7,020원 · 60.275초')
  expect(off.series.every(({ price }) => price > off.minPrice)).toBe(true)
  expect(screen.getByText('선: 1초 종가, 점: 실제 최저·최고 체결')).toBeDefined()
})

test('요약 503은 demo를 가리지 않으며 재시도하면 분포가 표시된다', async () => {
  const mock = new MockMarketSource()
  let attempts = 0
  const fetcher = vi.fn<typeof fetch>(async (url) => {
    if (String(url).endsWith('/demo')) return Response.json(await mock.getExperiment('demo'))
    return ++attempts === 1 ? new Response(null, { status: 503 }) : Response.json(await mock.getExperimentSummary())
  })
  render(<Experiments source={new ServerMarketSource({ fetcher })} mock={false} />)
  expect((await screen.findByRole('alert')).textContent).toContain('503')
  expect(await screen.findByRole('img', { name: graphName })).toBeDefined()
  fireEvent.click(screen.getByRole('button', { name: '요약 다시 불러오기' }))
  expect(await screen.findByRole('heading', { name: '시드 100개 분포' })).toBeDefined()
  expect(screen.queryByRole('alert')).toBeNull()
  expect(fetcher.mock.calls.filter(([url]) => String(url).endsWith('/demo'))).toHaveLength(1)
})

test('시드를 빠르게 바꿔도 늦게 도착한 실험은 선택 결과를 덮지 않는다', async () => {
  const source = new MockMarketSource()
  const delayed = await source.getExperiment('exp-FatFinger-7')
  const fallback = source.getExperiment.bind(source)
  let finish = () => {}
  vi.spyOn(source, 'getExperiment').mockImplementation((id) => id === 'exp-FatFinger-7'
    ? new Promise((resolve) => { finish = () => resolve(delayed) }) : fallback(id))
  render(<Experiments source={source} mock />)
  fireEvent.click(await screen.findByRole('button', { name: /^시드 7:/ }))
  fireEvent.click(screen.getByRole('button', { name: /^시드 8:/ }))
  expect(await screen.findByText(/시드 8 · VI만/)).toBeDefined()
  await act(async () => finish())
  expect(screen.queryByText(/시드 7 · VI만/)).toBeNull()
  expect(screen.getByText(/시드 8 · VI만/)).toBeDefined()
})
