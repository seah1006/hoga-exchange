import { afterEach, expect, test, vi } from 'vitest'
import type { MarketMessage } from './contract'
import type { MarketConnection } from './marketSource'
import { ServerMarketSource } from './serverMarketSource'
import { writeAdminKey } from './adminKey'
import fixture from './fixtures/contract-v02.json'
import { FakeHub } from './fixtures/fakeHub'

function setup(base?: string) {
  const hub = new FakeHub()
  const fetcher = vi.fn<typeof fetch>()
  const createHub = vi.fn(() => hub)
  return { hub, fetcher, createHub, source: new ServerMarketSource({ base, fetcher, createHub }) }
}

function respond(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => { sessionStorage.clear(); vi.useRealTimers() })

test('SignalR 계약 JSON 4종을 유형을 유지해 구독자에게 전달한다', () => {
  const { source, hub, createHub } = setup()
  const received: MarketMessage[] = []
  const unsubscribe = source.subscribe((message) => received.push(message))
  const events = ['bookSnapshot', 'trade', 'candle', 'viEvent'] as const
  for (const type of events) hub.emit(type, JSON.stringify(fixture[type]))
  expect(createHub).toHaveBeenCalledWith('/hubs/market')
  expect(received).toEqual(events.map((type) => ({ type, data: fixture[type] })))
  unsubscribe()
  hub.emit('bookSnapshot', JSON.stringify(fixture.bookSnapshot))
  expect(received).toHaveLength(4)
  expect(hub.stop).toHaveBeenCalledOnce()
})

test('재연결 후 첫 최신 bookSnapshot을 받기 전까지 재연결 상태를 유지한다', () => {
  const { source, hub, createHub } = setup('https://example.invalid/')
  const states: MarketConnection[] = []
  const received: MarketMessage[] = []
  const unwatch = source.subscribeConnection((state) => states.push(state))
  const unsubscribe = source.subscribe((message) => received.push(message))
  hub.emit('bookSnapshot', JSON.stringify(fixture.bookSnapshot))
  hub.reconnecting?.()
  expect(states.at(-1)).toBe('reconnecting')
  expect(received).toHaveLength(1)
  const fresh = { ...fixture.bookSnapshot, seq: 1026, logicalTimeMs: 3601000, phase: 'CallAuction', vi: fixture.vi, expectedPrice: 10050, expectedQuantity: 300 }
  hub.emit('bookSnapshot', JSON.stringify(fresh))
  expect(states).toEqual(['connecting', 'connected', 'reconnecting', 'connected'])
  expect(received.at(-1)).toEqual({ type: 'bookSnapshot', data: fresh })
  expect(createHub).toHaveBeenCalledWith('https://example.invalid/hubs/market')
  unsubscribe(); unwatch()
})

test('첫 접속 실패와 자동 재연결 종료 뒤에 재시도하며 해제하면 멈춘다', async () => {
  vi.useFakeTimers()
  const { source, hub } = setup()
  hub.start.mockRejectedValueOnce(new Error('서버 비활성'))
  const states: MarketConnection[] = []
  source.subscribeConnection((state) => states.push(state))
  const unsubscribe = source.subscribe(() => {})
  await Promise.resolve()
  expect(states.at(-1)).toBe('disconnected')
  await vi.runOnlyPendingTimersAsync()
  expect(hub.start).toHaveBeenCalledTimes(2)
  hub.emit('bookSnapshot', JSON.stringify(fixture.bookSnapshot))
  expect(states.at(-1)).toBe('connected')
  hub.reconnecting?.()
  hub.closed?.()
  expect(states.at(-1)).toBe('disconnected')
  await vi.runOnlyPendingTimersAsync()
  expect(hub.start).toHaveBeenCalledTimes(3)
  hub.closed?.()
  unsubscribe()
  await vi.runOnlyPendingTimersAsync()
  expect(hub.start).toHaveBeenCalledTimes(3)
})

test('REST는 base, 메서드, 인코딩한 ID와 주문 JSON을 사용하고 설명을 바꾸지 않는다', async () => {
  const { source, fetcher } = setup('https://example.invalid/')
  fetcher.mockResolvedValueOnce(respond(fixture.accepted)).mockResolvedValueOnce(respond(fixture.cancelled))
    .mockResolvedValueOnce(respond(fixture.explanation)).mockResolvedValueOnce(respond(fixture.experiment)).mockResolvedValueOnce(respond(fixture.stateHash))
  await expect(source.placeOrder({ ...fixture.order, side: 'Buy' })).resolves.toEqual(fixture.accepted)
  await expect(source.cancelOrder('o/123')).resolves.toEqual(fixture.cancelled)
  await expect(source.getExplanation('o-123')).resolves.toEqual(fixture.explanation)
  await expect(source.getExperiment('exp-1')).resolves.toEqual(fixture.experiment)
  await expect(source.getStateHash()).resolves.toEqual(fixture.stateHash)
  expect(fetcher.mock.calls.map(([url, options]) => [url, options?.method])).toEqual([
    ['https://example.invalid/api/orders', 'POST'], ['https://example.invalid/api/orders/o%2F123', 'DELETE'],
    ['https://example.invalid/api/orders/o-123/explanation', 'GET'], ['https://example.invalid/api/experiments/exp-1', 'GET'],
    ['https://example.invalid/api/state/hash', 'GET'],
  ])
  expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual(fixture.order)
})

test('HTTP 200의 accepted:false는 예외가 아니라 주문 응답이다', async () => {
  const { source, fetcher } = setup()
  fetcher.mockResolvedValueOnce(respond(fixture.rejected))
  await expect(source.placeOrder({ side: 'Buy', price: 12930, quantity: 100 })).resolves.toEqual(fixture.rejected)
})

const failures = [
  [400, 'BadRequest', '잘못된 요청입니다.'], [400, 'UnknownScenario', '알 수 없는 시나리오입니다.'],
  [401, 'Unauthorized', '관리자 키를 확인하세요.'], [404, 'NotFound', '찾을 수 없습니다.'],
] as const

test.each(failures)('HTTP %i %s 실패 응답은 상태, 코드, 메시지를 보존한다', async (status, error, message) => {
  const { source, fetcher } = setup()
  fetcher.mockResolvedValueOnce(respond({ error, message }, status))
  const request = status === 404 ? source.cancelOrder('missing') : source.runScenario('Calm')
  await expect(request).rejects.toMatchObject({ status, code: error, message })
})

test('조회 404는 기존 MarketSource 계약대로 null이다', async () => {
  const { source, fetcher } = setup()
  fetcher.mockResolvedValueOnce(respond({}, 404)).mockResolvedValueOnce(respond({}, 404))
  await expect(source.getExplanation('missing')).resolves.toBeNull()
  await expect(source.getExperiment('missing')).resolves.toBeNull()
})

test('세션 입력값은 관리자 POST에만 사용하고 지우면 헤더를 보내지 않는다', async () => {
  const { source, fetcher } = setup()
  const placeholder = '(not a credential)'
  writeAdminKey(placeholder)
  fetcher.mockResolvedValueOnce(respond({ ok: true })).mockResolvedValueOnce(respond({ ok: true }))
    .mockResolvedValueOnce(respond(fixture.stateHash)).mockResolvedValueOnce(respond({}, 401))
  await source.runScenario('FatFinger')
  await source.crash()
  await source.getStateHash()
  writeAdminKey('')
  await expect(source.crash()).rejects.toMatchObject({ status: 401 })
  expect(fetcher.mock.calls.map(([url, options]) => [url, new Headers(options?.headers).get('X-Admin-Key')])).toEqual([
    ['/api/admin/scenario', placeholder], ['/api/admin/crash', placeholder], ['/api/state/hash', null], ['/api/admin/crash', null],
  ])
  expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ name: 'FatFinger' })
})

test('네트워크 장애와 JSON이 아닌 응답을 처리한다', async () => {
  const { source, fetcher } = setup()
  fetcher.mockRejectedValueOnce(new TypeError('Failed to fetch')).mockResolvedValueOnce(new Response('', { status: 502 }))
    .mockResolvedValueOnce(new Response('not json', { status: 200 }))
  await expect(source.getStateHash()).rejects.toMatchObject({ code: 'NetworkError' })
  await expect(source.getStateHash()).rejects.toMatchObject({ status: 502, code: 'HttpError' })
  await expect(source.getStateHash()).rejects.toMatchObject({ code: 'InvalidResponse' })
})
