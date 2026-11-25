import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { App } from './App'
import { MockMarketSource } from './api/mockMarketSource'

vi.mock('./CandleChart', () => ({ CandleChart: () => null }))
afterEach(cleanup)

test('시장 화면에서 vi 객체가 들어오면 배너가 나타나고 해제되면 사라진다', async () => {
  const source = new MockMarketSource()
  render(<App source={source} speed={10} />)
  const banner = () => screen.queryByRole('status', { name: '변동성완화장치 진행 중' })
  expect(banner()).toBeNull()
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: '팻 핑거' }))
    for (let i = 0; i < 3; i++) source.advance()
  })
  expect(banner()?.textContent).toContain('동적 VI · 단일가매매 진행 중')
  expect(banner()?.textContent).toContain('R-VI-01')
  expect(banner()?.textContent).toContain('120초 남음 (최대 30초 연장)')
  act(() => { for (let i = 0; i < 12; i++) source.advance() })
  expect(banner()).toBeNull()
})

test('주문 거부, 재생 복구, 비교 화면의 시연 흐름을 렌더한다', async () => {
  const source = new MockMarketSource(42, 1)
  render(<App source={source} speed={1} />)
  expect(screen.getByRole('heading', { name: '1분봉 차트' })).toBeDefined()
  fireEvent.change(screen.getByLabelText('가격(원)'), { target: { value: '12930' } })
  fireEvent.click(screen.getByRole('button', { name: '매수 주문' }))
  expect((await screen.findByRole('alert')).textContent).toContain('R-LIMIT-04')
  fireEvent.click(screen.getByRole('button', { name: '서버 강제 종료' }))
  expect(await screen.findByText('재생으로 복구됨')).toBeDefined()
  expect(screen.getByText(/\/ 일치/)).toBeDefined()
  fireEvent.click(screen.getByRole('button', { name: '비교 화면' }))
  expect(await screen.findByRole('heading', { name: 'VI 비교 실험' })).toBeDefined()
  expect(screen.getByRole('img', { name: '같은 시드의 VI 켜짐과 꺼짐 가격 비교 그래프' })).toBeDefined()
  expect(screen.queryByRole('heading', { name: '1분봉 차트' })).toBeNull()
})
