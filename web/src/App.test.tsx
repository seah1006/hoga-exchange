import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { App } from './App'
import { MockMemberSource } from './api/mockMemberSource'

vi.mock('./CandleChart', () => ({ CandleChart: () => null }))
afterEach(cleanup)

test('시장 화면에서 vi 객체가 들어오면 배너가 나타나고 해제되면 사라진다', async () => {
  const source = new MockMemberSource(42, 1)
  await source.register({ email: 'member@example.invalid', nickname: '테스트 회원', password: crypto.randomUUID() })
  const profile = (await source.getMe())!
  vi.spyOn(source, 'getMe').mockResolvedValue({ ...profile, isAdmin: true })
  render(<App source={source} speed={1} />)
  await screen.findByRole('heading', { name: '관리자 패널' })
  await screen.findByText('주문 가능 현금')
  const banner = () => screen.queryByRole('status', { name: '변동성완화장치 진행 중' })
  expect(banner()).toBeNull()
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: '팻 핑거' }))
    for (let i = 0; i < 3; i++) source.advance()
  })
  expect(banner()?.textContent).toContain('동적 VI · 단일가매매 진행 중')
  expect(banner()?.textContent).toContain('R-VI-01')
  expect(banner()?.textContent).toContain('120초 남음 (최대 30초 연장)')
  expect(banner()?.textContent).toMatch(/예상 체결 [\d,]+원 \/ [\d,]+주 목 데이터 예시값/)
  act(() => { for (let i = 0; i < 12; i++) source.advance() })
  expect(banner()).toBeNull()
})

test('주문 거부, 재생 복구, 실험 화면의 시연 흐름을 렌더한다', async () => {
  const source = new MockMemberSource(42, 1)
  await source.register({ email: 'member@example.invalid', nickname: '테스트 회원', password: crypto.randomUUID() })
  const profile = (await source.getMe())!
  vi.spyOn(source, 'getMe').mockResolvedValue({ ...profile, isAdmin: true })
  render(<App source={source} speed={1} />)
  await screen.findByRole('heading', { name: '관리자 패널' })
  await screen.findByText('주문 가능 현금')
  expect(screen.getByRole('heading', { name: '1분봉 차트' })).toBeDefined()
  fireEvent.change(screen.getByLabelText('가격(원)'), { target: { value: '12930' } })
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect((await screen.findByRole('alert')).textContent).toContain('R-LIMIT-04')
  fireEvent.click(screen.getByRole('button', { name: '서버 강제 종료' }))
  expect(await screen.findByText('재생으로 복구됨')).toBeDefined()
  expect(screen.getByText(/\/ 일치/)).toBeDefined()
  act(() => source.advance())
  expect(screen.queryByText('재생으로 복구됨')).toBeNull()
  expect(screen.queryByText(/\/ 일치/)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: '실험 화면' }))
  expect(await screen.findByRole('heading', { name: 'VI 비교 실험' })).toBeDefined()
  const graph = await screen.findByRole('img', { name: '같은 시드의 VI 켜짐과 꺼짐 가격 비교 그래프' })
  expect(graph.querySelector('rect.auction-window')).not.toBeNull()
  expect(screen.getByText('노란 음영: 단일가매매 구간').querySelector('.auction-chip')).not.toBeNull()
  expect(screen.queryByRole('heading', { name: '1분봉 차트' })).toBeNull()
})

test('주문 거부 시 화면의 호가창과 내 주문이 바뀌지 않는다', async () => {
  const source = new MockMemberSource(42, 1)
  await source.register({ email: 'member@example.invalid', nickname: '테스트 회원', password: crypto.randomUUID() })
  const profile = (await source.getMe())!
  vi.spyOn(source, 'getMe').mockResolvedValue({ ...profile, isAdmin: true })
  render(<App source={source} speed={1} />)
  await screen.findByRole('heading', { name: '관리자 패널' })
  await screen.findByText('주문 가능 현금')
  fireEvent.change(screen.getByLabelText('가격(원)'), { target: { value: '9990' } })
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  const existing = await screen.findByRole('button', { name: '매수 9,990원 · 100주' })
  const orders = screen.getByRole('heading', { name: '내 주문' }).closest('section')!
  const book = screen.getByRole('heading', { name: '호가창 10단계' }).closest('section')!
  const beforeBook = book.innerHTML
  const beforeOrders = orders.innerHTML

  fireEvent.change(screen.getByLabelText('가격(원)'), { target: { value: '6950' } })
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect((await screen.findByRole('alert')).textContent).toContain('하한가보다 낮습니다.')
  expect(book.innerHTML).toBe(beforeBook)
  expect(orders.innerHTML).toBe(beforeOrders)
  expect(within(orders).getByRole('button', { name: '매수 9,990원 · 100주' })).toBe(existing)
})
