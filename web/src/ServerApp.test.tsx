import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { App } from './App'
import type { BookSnapshot } from './api/contract'
import fixture from './api/fixtures/contract-v02.json'
import { FakeHub } from './api/fixtures/fakeHub'
import { ServerMarketSource } from './api/serverMarketSource'

vi.mock('./CandleChart', () => ({ CandleChart: () => null }))
afterEach(() => { cleanup(); sessionStorage.clear() })

function setup() {
  const hub = new FakeHub()
  const fetcher = vi.fn<typeof fetch>()
  const source = new ServerMarketSource({ fetcher, createHub: () => hub })
  const book: BookSnapshot = { ...fixture.bookSnapshot, symbol: 'HOGA', phase: 'Continuous' }
  const showBook = (snapshot: BookSnapshot) => act(() => { hub.emit('bookSnapshot', JSON.stringify(snapshot)) })
  render(<App source={source} />)
  return { fetcher, book, showBook, reconnect: () => act(() => { hub.reconnecting?.() }) }
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
