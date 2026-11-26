import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { App } from './App'
import { maxBuy } from './MemberOrders'
import { AccountPanel, AuthDialog, formatReturn } from './Members'
import members from './api/fixtures/contract-v05.json'
import { MockMemberSource } from './api/mockMemberSource'
import { MarketApiError } from './api/serverMarketSource'

vi.mock('./CandleChart', () => ({ CandleChart: () => <div aria-label="1분봉 캔들 차트" /> }))
afterEach(cleanup)

async function setup(width = 1440) {
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width })
  const source = new MockMemberSource(42, 0.01)
  render(<App source={source} />)
  await screen.findByText('로그인하면 주문할 수 있습니다')
  return source
}
const click = (name: string) => fireEvent.click(screen.getByRole('button', { name }))
const set = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } })
const email = 'member@example.invalid', nickname = '목 참가자'

async function register(password: string) {
  click('로그인/가입'); click('가입')
  set('이메일', email); set('비밀번호', password); set('닉네임', nickname)
  click('가입하기')
  await screen.findByText('주문 가능 현금')
}

test('비회원은 시세와 실험을 보되 주문과 관리자 패널은 숨긴다', async () => {
  await setup()
  expect(screen.getByRole('heading', { name: '호가창 10단계' })).toBeDefined()
  expect(screen.queryByRole('button', { name: '매수 주문' })).toBeNull()
  expect(screen.queryByRole('heading', { name: '관리자 패널' })).toBeNull()
  click('실험 화면')
  expect(await screen.findByRole('heading', { name: 'VI 비교 실험' })).toBeDefined()
  fireEvent.click(screen.getByRole('link', { name: '개인정보 안내' }))
  expect(screen.getByText(/실제 돈이나 주식을 거래하지 않습니다/)).toBeDefined()
  expect(screen.getByText(/비밀번호 해시는 계정 확인에/)).toBeDefined()
  for (const text of ['로그인 기록은 30일간 보관합니다', '계정과 로그인 기록이 삭제됩니다', '서버 백업에 최대 7일간 남을 수 있습니다', '지난 랭킹 기록은 ‘탈퇴한 사용자’로 표시됩니다']) {
    expect(screen.getByText((content) => content.includes(text))).toBeDefined()
  }
})

test('가입·로그인 폼이 입력 형식을 확인하고 계약 오류를 한국어로 표시한다', async () => {
  const source = await setup()
  await source.register({ email, password: crypto.randomUUID(), nickname })
  await source.logout()
  click('로그인/가입')
  click('로그인하기')
  expect(screen.getByRole('alert').textContent).toBe('이메일 주소를 확인하세요.')
  set('이메일', email)
  set('비밀번호', crypto.randomUUID())
  click('로그인하기')
  expect(await screen.findByText('이메일 또는 비밀번호가 올바르지 않습니다. 여러 번 실패하면 잠시 로그인할 수 없습니다.')).toBeDefined()
  expect(Boolean((screen.getByLabelText('비밀번호') as HTMLInputElement).value)).toBe(false)
  click('가입'); set('이메일', email); click('가입하기')
  expect(screen.getByRole('alert').textContent).toContain('10자 이상')
  set('비밀번호', crypto.randomUUID()); click('표시')
  expect((screen.getByLabelText('비밀번호') as HTMLInputElement).type).toBe('text')
  click('숨기기')
  expect((screen.getByLabelText('비밀번호') as HTMLInputElement).type).toBe('password')
  click('가입하기')
  expect(screen.getByRole('alert').textContent).toContain('2~16자')
  set('닉네임', nickname); click('가입하기')
  expect(await screen.findByText('이미 가입된 이메일입니다.')).toBeDefined()
})

test.each([1440, 390])('목 화면 %ipx DOM: 가입, 주문, 체결, 랭킹과 지난 라운드', async (width) => {
  const source = await setup(width)
  await register(crypto.randomUUID())
  expect(screen.queryByRole('heading', { name: '관리자 패널' })).toBeNull()
  const account = within(screen.getByRole('region', { name: '내 계좌' }))
  expect(account.getByText('10,000,000원')).toBeDefined()
  expect(screen.getByText('매수 가능 1,000주 · 매도 가능 1,000주')).toBeDefined()
  set('수량(주)', '1001')
  expect(screen.getByRole('alert').textContent).toContain('매수 가능 현금이 부족합니다')
  click('매도')
  expect(screen.getByRole('alert').textContent).toContain('매도 가능 수량이 부족합니다')
  click('매수'); set('수량(주)', '100'); click('매수 주문')
  expect(await screen.findByText('체결 완료')).toBeDefined()
  expect(account.getByText('9,000,000원')).toBeDefined()
  expect(account.getByText('1,100주')).toBeDefined()
  expect(screen.getByRole('table', { name: '내 체결 내역' })).toBeDefined()
  click('랭킹')
  expect(await screen.findByText(/내 순위 1위/)).toBeDefined()
  expect(screen.getByText('(나)').closest('tr')?.classList.contains('ranking-me')).toBe(true)
  expect(screen.getByText(/갱신.*한국시간/)).toBeDefined()
  expect(screen.queryByText(email)).toBeNull()
  act(() => source.resetRound('2026-11-27', '2026-11-26T20:00:00Z'))
  expect(await screen.findByText('아직 순위가 없습니다.')).toBeDefined()
  set('조회할 라운드', '2026-11-26')
  expect(await screen.findByText(/내 순위 1위/)).toBeDefined()
  click('시장 화면')
  expect(await screen.findByText('접수된 주문이 없습니다.')).toBeDefined()
})

test('로그아웃 후 다시 로그인하고 비밀번호 재확인으로 탈퇴한다', async () => {
  const source = await setup()
  const password = crypto.randomUUID()
  await register(password)
  fireEvent.click(screen.getByText(nickname)); click('로그아웃')
  await screen.findByText('로그인하면 주문할 수 있습니다')
  click('로그인/가입'); set('이메일', email); set('비밀번호', password); click('로그인하기')
  await screen.findByText('주문 가능 현금')
  fireEvent.click(screen.getByText(nickname)); click('탈퇴')
  expect(screen.getByText(/되돌릴 수 없습니다/)).toBeDefined()
  set('비밀번호', crypto.randomUUID()); click('탈퇴하기')
  expect(await screen.findByText('비밀번호를 확인하세요.')).toBeDefined()
  set('비밀번호', password); click('탈퇴하기')
  expect(await screen.findByText('로그인하면 주문할 수 있습니다')).toBeDefined()
  expect(await source.getMe()).toBeNull()
})

test('랭킹 조회 실패를 표시하고 다시 시도할 수 있다', async () => {
  const source = await setup()
  vi.spyOn(source, 'getRanking').mockRejectedValueOnce(new MarketApiError(503, 'Unavailable', '잠시 뒤 다시 시도하세요.'))
  click('랭킹')
  expect((await screen.findByRole('alert')).textContent).toContain('잠시 뒤 다시 시도하세요.')
  click('다시 시도')
  expect(await screen.findByText('아직 순위가 없습니다.')).toBeDefined()
})

test('매수 수량은 몫을 내림하고 bp를 소수점 둘째 자리까지 표시한다', () => {
  expect(maxBuy(9_000_000, 10_050)).toBe(895)
  expect(maxBuy(10_000, 0)).toBe(0)
  expect(maxBuy(10_000, 10.5)).toBe(0)
  expect(formatReturn(27)).toBe('0.27%')
  expect(formatReturn(-10)).toBe('-0.10%')
})

test('최신 시세가 없거나 유효하지 않으면 서버 평가값을 사용한다', () => {
  const props = { account: members.account, error: '', retry: () => {} }
  const view = render(<AccountPanel {...props} />)
  expect(screen.getByText('20,555,000원')).toBeDefined()
  expect(screen.getByText('+555,000원')).toBeDefined()
  expect(screen.getByText('+2.77%')).toBeDefined()
  view.rerender(<AccountPanel {...props} lastPrice={0} />)
  expect(screen.getByText('20,555,000원')).toBeDefined()
})

test.each(['login', 'delete'] as const)('%s 화면의 429는 응답 코드 문자열과 무관하게 같은 안내를 표시한다', async (mode) => {
  const source = new MockMemberSource(42, 0.01)
  const failure = new MarketApiError(429, 'HttpError', 'HTTP 429')
  if (mode === 'login') vi.spyOn(source, 'login').mockRejectedValueOnce(failure)
  else vi.spyOn(source, 'deleteMe').mockRejectedValueOnce(failure)
  const changed = vi.fn()
  render(<AuthDialog initial={mode} source={source} changed={changed} close={() => {}} />)
  if (mode === 'login') set('이메일', email)
  set('비밀번호', crypto.randomUUID())
  click(mode === 'login' ? '로그인하기' : '탈퇴하기')
  expect((await screen.findByRole('alert')).textContent).toBe('시도가 너무 많습니다. 잠시 후 다시 시도하세요.')
  expect(changed).not.toHaveBeenCalled()
})


test('로그아웃 전 시작한 세션 조회가 늦게 끝나도 계좌를 다시 열지 않는다', async () => {
  const source = await setup()
  await register(crypto.randomUUID())
  vi.spyOn(source, 'logout').mockRejectedValueOnce(new MarketApiError(503, 'Unavailable', '잠시 뒤 다시 시도하세요.'))
  let answer: (profile: typeof members.profile) => void = () => {}
  const pending = new Promise<typeof members.profile>((resolve) => { answer = resolve })
  const profileRequest = vi.spyOn(source, 'getMe').mockReturnValueOnce(pending)

  fireEvent.click(screen.getByText(nickname)); click('로그아웃')
  await screen.findByRole('alert')
  click('로그인 상태 다시 확인')
  expect(profileRequest).toHaveBeenCalledOnce()
  click('로그아웃')
  await screen.findByText('로그인하면 주문할 수 있습니다')
  await act(async () => { answer(members.profile) })
  expect(screen.getByText('로그인하면 주문할 수 있습니다')).toBeDefined()
  expect(screen.queryByRole('region', { name: '내 계좌' })).toBeNull()
})
