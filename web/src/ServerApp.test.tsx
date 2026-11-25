import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { App } from './App'
import type { BookSnapshot, Candle, Messages } from './api/contract'
import fixture from './api/fixtures/contract-v02.json'
import { FakeHub } from './api/fixtures/fakeHub'
import { ServerMarketSource } from './api/serverMarketSource'

vi.mock('./CandleChart', () => ({ CandleChart: ({ candles }: { candles: Candle[] }) => <output aria-label="봉 시작 시각">{candles.map(({ startMs }) => startMs).join(',')}</output> }))
afterEach(() => { cleanup(); sessionStorage.clear(); vi.useRealTimers() })

function setup() {
  const hub = new FakeHub()
  const fetcher = vi.fn<typeof fetch>()
  const source = new ServerMarketSource({ fetcher, createHub: () => hub })
  const book: BookSnapshot = { ...fixture.bookSnapshot, symbol: 'HOGA', phase: 'Continuous' }
  const emit = <K extends keyof Messages,>(name: K, data: Messages[K]) => act(() => { hub.emit(name, JSON.stringify(data)) })
  const showBook = (snapshot: BookSnapshot) => emit('bookSnapshot', snapshot)
  render(<App source={source} />)
  return { fetcher, book, emit, showBook, reconnect: () => act(() => { hub.reconnecting?.() }), reconnected: () => act(() => { hub.reconnected?.() }) }
}

test('서버 모드 VI 값과 논리 시간 표기를 재연결 뒤 최신 스냅샷으로 갱신한다', () => {
  const { book, showBook, reconnect } = setup()
  expect(screen.getByText('시세 서버 연결 중')).toBeDefined()
  showBook(book)
  expect(screen.getByText('시세 서버 연결됨')).toBeDefined()
  reconnect()
  expect(screen.getByText('시세 서버 재연결 중')).toBeDefined()
  expect(screen.getByText(/마지막으로 받은 시세/)).toBeDefined()
  showBook({ ...book, seq: book.seq + 1, logicalTimeMs: 3610000, phase: 'CallAuction',
    vi: { ...fixture.vi, kind: 'Dynamic' }, expectedPrice: 10600, expectedQuantity: 1200 })
  const banner = screen.getByRole('status', { name: '변동성완화장치 진행 중' })
  expect(banner.textContent).toContain('110초 남음 (최대 30초 연장) · 거래소 논리 시간 기준')
  expect(banner.textContent).toContain('10,600원 / 1,200주')
  expect(banner.textContent).not.toContain('목 데이터 예시값')
  expect(screen.getByText('시세 서버 연결됨')).toBeDefined()
  expect(screen.queryByText(/마지막으로 받은 시세/)).toBeNull()
})

test('주문 설명은 서버 문장, 대기 순번, 규칙 ID를 표시한다', async () => {
  const { fetcher, book, showBook } = setup()
  fetcher.mockResolvedValueOnce(Response.json(fixture.accepted)).mockResolvedValueOnce(Response.json({ ...fixture.explanation, ruleId: 'R-VI-01' }))
  showBook(book)
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect(await screen.findByText(fixture.explanation.message)).toBeDefined()
  expect(screen.getByText('8번째')).toBeDefined()
  expect(screen.getByText('R-VI-01')).toBeDefined()
})

test('관리자 패널은 요청 실패를 표시하고 키 입력을 세션에만 저장한다', async () => {
  const { fetcher } = setup()
  const placeholder = '(not a credential)'
  fetcher.mockResolvedValueOnce(Response.json({ error: 'Unauthorized', message: '관리자 키가 필요합니다.' }, { status: 401 }))
  const input = screen.getByLabelText('관리자 키') as HTMLInputElement
  expect(input.type).toBe('password')
  fireEvent.change(input, { target: { value: placeholder } })
  expect(sessionStorage.getItem('hoga.adminKey')).toBe(placeholder)
  expect(localStorage.getItem('hoga.adminKey')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: '팻 핑거' }))
  expect((await screen.findByRole('alert')).textContent).toBe('관리자 키가 필요합니다.')
})

test('이력 전송과 연결 완료 알림 순서에 관계없이 재연결 때 목록이 비워진다', async () => {
  const { book, emit, showBook, reconnect, reconnected } = setup()
  await act(async () => {})
  expect(screen.getByText('시세 동기화 중')).toBeDefined()
  showBook(book)
  emit('candle', { ...fixture.candle, symbol: 'HOGA', startMs: 60_000 })
  const trade = { ...fixture.trade, symbol: 'HOGA', seq: 10, quantity: 75, aggressorSide: 'Buy' } as const
  emit('trade', trade)
  const trades = within(screen.getByRole('region', { name: '최근 체결' }))
  const candles = screen.getByLabelText('봉 시작 시각')
  expect(candles.textContent).toBe('60000')
  expect(trades.getByText('75주')).toBeDefined()
  reconnect()
  emit('candle', { ...fixture.candle, symbol: 'HOGA', startMs: 120_000 })
  expect(screen.getByText('시세 동기화 중')).toBeDefined()
  expect(candles.textContent).toBe('120000')
  expect(trades.getByText('첫 체결 대기 중')).toBeDefined()
  emit('trade', trade)
  emit('trade', { ...trade, seq: 11, quantity: 80 })
  reconnected()
  expect(trades.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[2].textContent)).toEqual(['80주', '75주'])
  expect(screen.queryByText('시세 서버 연결됨')).toBeNull()
  showBook({ ...book, seq: book.seq + 1 })
  expect(screen.getByText('시세 서버 연결됨')).toBeDefined()
  expect(candles.textContent).toBe('120000')
  reconnect()
  reconnected()
  expect(screen.getByText('시세 동기화 중')).toBeDefined()
  expect(candles.textContent).toBe('')
  expect(trades.getByText('첫 체결 대기 중')).toBeDefined()
  showBook({ ...book, seq: book.seq + 2 })
  expect(screen.getByText('시세 서버 연결됨')).toBeDefined()
})

test('시장 초기화는 체결·봉·내 주문을 비우고 안내를 표시한다', async () => {
  const { book, emit, fetcher, showBook } = setup()
  showBook(book)
  emit('candle', { ...fixture.candle, symbol: 'HOGA', startMs: 60_000 })
  emit('trade', { ...fixture.trade, symbol: 'HOGA', quantity: 75, aggressorSide: 'Buy' })
  fetcher.mockResolvedValueOnce(Response.json(fixture.accepted)).mockResolvedValueOnce(Response.json(fixture.explanation))
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect(await screen.findByRole('button', { name: 'o-123 취소' })).toBeDefined()
  expect(screen.getByLabelText('봉 시작 시각').textContent).toBe('60000')
  expect(within(screen.getByRole('region', { name: '최근 체결' })).getByText('75주')).toBeDefined()
  act(() => {
    emit('marketReset', { symbol: 'HOGA', atUtc: '2026-11-25T20:00:00Z', reason: 'Daily' })
    showBook({ ...book, seq: 0, logicalTimeMs: 0, asks: [], bids: [] })
  })
  expect(screen.getByText('시장이 초기화되었습니다')).toBeDefined()
  expect(screen.getByLabelText('봉 시작 시각').textContent).toBe('')
  expect(within(screen.getByRole('region', { name: '최근 체결' })).getByText('첫 체결 대기 중')).toBeDefined()
  expect(screen.queryByRole('button', { name: 'o-123 취소' })).toBeNull()
})

test('서버 이력과 라이브에서 동일 seq의 체결을 개별 행으로 표시한다', () => {
  const { book, emit, showBook } = setup()
  const trade = { ...fixture.trade, symbol: 'HOGA', seq: book.seq - 1, quantity: 75, aggressorSide: 'Buy' } as const
  emit('trade', trade)
  emit('trade', { ...trade, quantity: 80 })
  showBook(book)
  const trades = within(screen.getByRole('region', { name: '최근 체결' }))
  const quantities = () => trades.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[2].textContent)
  expect(quantities()).toEqual(['80주', '75주'])
  const liveTrade = { ...trade, seq: book.seq + 1, quantity: 120 }
  emit('trade', liveTrade)
  emit('trade', liveTrade)
  expect(quantities()).toEqual(['120주', '120주', '80주', '75주'])
})

test('서버 비교 화면은 demo 별칭으로 조회한다', async () => {
  const { fetcher } = setup()
  fetcher.mockResolvedValueOnce(Response.json({}, { status: 404 }))
  fireEvent.click(screen.getByRole('button', { name: '비교 화면' }))
  expect(await screen.findByText('실험을 찾을 수 없습니다.')).toBeDefined()
  expect(fetcher.mock.calls[0][0]).toBe('/api/experiments/demo')
})

test.each([['Filled', '체결 완료'], ['Cancelled', '취소'], ['Rejected', '거부']])('%s 상태는 취소 버튼을 표시하지 않는다', async (status, label) => {
  const { fetcher, book, showBook } = setup()
  showBook(book)
  fetcher.mockResolvedValueOnce(Response.json(fixture.accepted))
    .mockResolvedValueOnce(Response.json({ ...fixture.explanation, status, remainingQuantity: 0 }))
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect(await screen.findByText(label)).toBeDefined()
  expect(screen.queryByRole('button', { name: 'o-123 취소' })).toBeNull()
})

test('미종료 주문을 2초에 한 건씩 조회하여 비선택 주문 체결을 반영한다', async () => {
  vi.useFakeTimers()
  const { fetcher, book, showBook, reconnect, reconnected } = setup()
  const response = (orderId: string, status: string) => Response.json({ ...fixture.explanation, orderId, status })
  showBook(book)
  const orderButton = screen.getByRole('button', { name: '매수 주문' })
  const click = async () => { await act(async () => { fireEvent.click(orderButton) }) }
  const wait = async (ms: number) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms) }) }
  fetcher.mockResolvedValueOnce(Response.json(fixture.accepted)).mockResolvedValueOnce(response('o-123', 'Resting'))
    .mockResolvedValueOnce(Response.json({ ...fixture.accepted, orderId: 'o-124' })).mockResolvedValueOnce(response('o-124', 'Resting'))
  await click()
  await click()
  expect(screen.getByRole('button', { name: 'o-123 취소' })).toBeDefined()
  expect(screen.getByRole('button', { name: 'o-124 취소' })).toBeDefined()
  await wait(1999)
  expect(fetcher).toHaveBeenCalledTimes(4)
  fetcher.mockResolvedValueOnce(response('o-123', 'Filled')).mockResolvedValueOnce(response('o-124', 'PartiallyFilled'))
  await wait(1)
  expect(fetcher).toHaveBeenCalledTimes(5)
  expect(screen.getByText('체결 완료')).toBeDefined()
  expect(screen.queryByRole('button', { name: 'o-123 취소' })).toBeNull()
  reconnect()
  reconnected()
  await wait(4000)
  expect(fetcher).toHaveBeenCalledTimes(5)
  showBook({ ...book, seq: book.seq + 1 })
  await wait(2000)
  expect(fetcher.mock.calls[5][0]).toBe('/api/orders/o-124/explanation')
  expect(screen.getByText('부분 체결')).toBeDefined()
  expect(screen.getByRole('button', { name: 'o-124 취소' })).toBeDefined()
})

test.each(['', '0', '-1', '1.5', '1000001', '9007199254740993'])('수량 %j은 서버에 전송하지 않는다', (quantity) => {
  const { fetcher, book, showBook } = setup()
  showBook(book)
  fireEvent.change(screen.getByLabelText('수량(주)'), { target: { value: quantity } })
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect(screen.getByRole('alert').textContent).toContain('1~1,000,000')
  expect(fetcher).not.toHaveBeenCalled()
})

test.each([1, 1_000_000])('수량 경계 %s를 서버에 전달한다', async (quantity) => {
  const { fetcher, book, showBook } = setup()
  showBook(book)
  fetcher.mockResolvedValueOnce(Response.json(fixture.accepted)).mockResolvedValueOnce(Response.json(fixture.explanation))
  fireEvent.change(screen.getByLabelText('수량(주)'), { target: { value: String(quantity) } })
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect(await screen.findByText(fixture.explanation.message)).toBeDefined()
  expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body)).quantity).toBe(quantity)
})
