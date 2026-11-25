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
