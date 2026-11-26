import { expect, test, vi } from 'vitest'
import market from './fixtures/contract-v02.json'
import { ServerMarketSource } from './serverMarketSource'
import { FakeHub } from './fixtures/fakeHub'
import fixture from './fixtures/contract-v05.json'

test.each([true, false])('잘못된 CSRF 토큰은 다시 발급하고 변경 요청을 1회만 재전송한다 (성공: %s)', async (recover) => {
  let issued = 0
  const sent: { method?: string; token: string | null; credentials?: RequestCredentials }[] = []
  const fetcher: typeof fetch = async (input, init) => {
    if (input === '/api/auth/csrf') return Response.json({ token: `fixture-${++issued}` })
    sent.push({ method: init?.method, token: new Headers(init?.headers).get('X-CSRF-TOKEN'), credentials: init?.credentials })
    if (recover && sent.length === 2) return new Response(null, { status: 204 })
    return Response.json({ error: 'BadCsrfToken' }, { status: 400 })
  }
  const source = new ServerMarketSource({ fetcher })
  await source.refreshCsrf()
  const request = source.deleteMe(crypto.randomUUID())
  if (recover) await expect(request).resolves.toBeUndefined()
  else await expect(request).rejects.toMatchObject({ status: 400, code: 'BadCsrfToken' })
  expect(sent).toEqual([
    { method: 'DELETE', token: 'fixture-1', credentials: 'include' },
    { method: 'DELETE', token: 'fixture-2', credentials: 'include' },
  ])
  expect(issued).toBe(recover ? 3 : 2)
})

test('로그인 후 토큰 재발급과 사용자 권한으로 허브 재접속을 수행한다', async () => {
  const hubs: FakeHub[] = []
  const requests: Array<[string, string | undefined]> = []
  const fetcher: typeof fetch = async (input, init) => {
    requests.push([String(input), init?.method])
    if (input === '/api/auth/csrf') return Response.json({ token: 'fixture' })
    if (input === '/api/auth/login') return Response.json({ id: fixture.profile.id, nickname: fixture.profile.nickname, isAdmin: false })
    throw new Error('Unexpected route')
  }
  const source = new ServerMarketSource({ fetcher, createHub: () => { const hub = new FakeHub(); hubs.push(hub); return hub } })
  const subscribed = source.subscribe(() => {})
  await source.refreshCsrf()
  expect((await source.login({ email: fixture.profile.email, password: crypto.randomUUID() })).id).toBe(fixture.profile.id)
  expect(requests).toEqual([['/api/auth/csrf', 'GET'], ['/api/auth/login', 'POST'], ['/api/auth/csrf', 'GET']])
  expect(hubs).toHaveLength(2)
  expect(hubs[0].stop).toHaveBeenCalledOnce()
  expect(hubs[1].start).toHaveBeenCalledOnce()
  subscribed()
})

test('인증 응답의 임의 메시지는 화면용 오류로 전달하지 않는다', async () => {
  const marker = crypto.randomUUID()
  const fetcher: typeof fetch = async (input) => input === '/api/auth/csrf'
    ? Response.json({ token: 'fixture' })
    : Response.json({ error: 'Unexpected', message: marker }, { status: 400 })
  const source = new ServerMarketSource({ fetcher })
  const safe = await source.login({ email: fixture.profile.email, password: marker }).then(() => false, (error: unknown) => error instanceof Error && error.message === '서버 요청에 실패했습니다 (HTTP 400).')
  expect(safe).toBe(true)
})

test.each(['logout', 'deleteMe'] as const)('%s 성공 뒤 이전 연결과 익명 연결의 계좌 이벤트를 무시한다', async (action) => {
  const hubs: FakeHub[] = []
  const fetcher: typeof fetch = async (input) => {
    if (input === '/api/auth/csrf') return Response.json({ token: 'fixture' })
    if (input === '/api/auth/login') return Response.json(fixture.profile)
    return new Response(null, { status: 204 })
  }
  const source = new ServerMarketSource({ fetcher, createHub: () => { const hub = new FakeHub(); hubs.push(hub); return hub } })
  const listener = vi.fn()
  const unsubscribe = source.subscribe(listener)
  hubs[0].stop.mockImplementationOnce(() => new Promise(() => {}))
  const account = JSON.stringify(fixture.account)
  hubs[0].emit('accountUpdate', account)
  expect(listener).toHaveBeenCalledWith({ type: 'accountUpdate', data: fixture.account })
  listener.mockClear()
  if (action === 'logout') await source.logout()
  else await source.deleteMe(crypto.randomUUID())
  hubs[0].emit('accountUpdate', account)
  hubs[1].emit('accountUpdate', account)
  expect(listener).not.toHaveBeenCalled()
  hubs[1].emit('bookSnapshot', JSON.stringify(market.bookSnapshot))
  expect(listener).toHaveBeenCalledWith({ type: 'bookSnapshot', data: market.bookSnapshot })
  await source.login({ email: fixture.profile.email, password: crypto.randomUUID() })
  hubs[2].emit('accountUpdate', account)
  expect(listener).toHaveBeenLastCalledWith({ type: 'accountUpdate', data: fixture.account })
  unsubscribe()
})
