# REVIEW-6A: TASK-008S 서버 + TASK-008W 화면 (1단계, 규칙 기준)

- 범위: `git diff 6e4320c..725ce3d` (25개 파일, +867 / -98)
- 리뷰어: Claude Sonnet 5.5 (읽기 전용, 코드 수정 없음)
- 개수: P0 0 / P1 4 / P2 9 (확인 필요 표시는 항목 안에 따로 적음)
- 실행: `dotnet test HogaExchange.slnx` 1회차에서 Server 테스트 1건 실패(`JournaledBotsPreserveHashAcrossCrash`, 아래 P1-4). 단독 3회 재실행은 모두 통과. 나머지 Engine 103 / Journal 34 통과. web 테스트는 돌리지 않았다.
- 기준 문서: REVIEW_GUIDE.md, CONTRACT v0.3, REVIEW-5, TASK-008S/008W-PROGRESS

## 요청 항목별 결론 (먼저 요약)
| 항목 | 결론 |
|---|---|
| 봇 주문이 저널을 거쳐 재생되는가 | 예. `RunBots`와 `CancelBot`은 모두 `Process`(저널 Append, 엔진, 투영 순)를 거친다. 재생은 저널 명령만 쓰므로 해시가 같다. `Crash`는 해시 비교를 직접 한다. |
| 봇 난수·시각이 시스템 시계에 의존하는가 | 아니오. 난수는 `SplitMix64(seed)`, 시각은 100ms 논리 격자 `_nextBotTick`과 `Math.Max(tick, 투영 시각)`. 시계는 "몇 틱을 실행하느냐"에만 영향을 주고 그 결과는 저널에 남는다. 엔진 규칙 위반 없음. |
| 실제 재시작 뒤 봇 | 해시는 같지만 봇의 난수 스트림, 적정가치, 시나리오는 처음부터 다시 시작한다(P2-1, 진행 기록에 명시됨). |
| 접속 이력이 새 연결에만 가는가 | 예. `Subscribe`가 `hub.Clients.Client(id)`로 그 연결에만 보내고 그 뒤 그룹에 합류한다. 같은 루프에서 처리되어 라이브 이벤트에 추월당하지 않는다. 테스트 `HistoryIsSentOnlyToNewClientInCandleTradeSnapshotOrder` 있음. |
| 읽기 예외 처리가 저널 기록 뒤 예외를 삼키는가 | 아니오. `readOnly: true`는 `Read`와 `Subscribe`에서만 켠다. `Place`/`Cancel`/`SelectScenario`/`Crash`/`BotBatch`는 기존대로 예외를 다시 던져 루프를 멈춘다. 단 플래그가 호출자 선언이라 오용 여지가 있다(P2-2). |
| 보관 상한 뒤 저널 탐색 | 루프는 막지 않는다(`Task.Run`, 루프 밖). 비용은 크다(P1-2). |
| 관리자 인증 | `crash`와 `scenario` 모두 같은 `IsAdmin`(해시 후 FixedTimeEquals, 키 미설정 401). 본문 읽기 전에 인증한다. 통과. |
| 실험 ID 파싱·캐시 | 파싱은 엄격하다(`exp-{Calm|FatFinger|PanicSell}-{1..1000}`, `NumberStyles.None`, 정규형 비교). 캐시 8개 FIFO. 하지만 미스 계산이 루프 안에서 돈다(P1-1). |
| 계약 v0.3 일치 | 접속 직후 순서(candle 오름차순 → trade 오래된 것부터 최대 50 → bookSnapshot), `UnknownScenario`, 401, 404, `demo` 별칭의 실제 id 모두 일치. 계약 문서 19행은 아직 "TASK-008에서 구현, 그 전까지는 생략"으로 남아 있다(P2-9). |

## P0
없음. (비밀값은 키를 런타임 생성/환경 변수로만 다루며 diff에 없음. 엔진 규칙·정수 가격 변경 없음. 봇 가격은 `FloorToTick`과 상하한 clamp를 거침.)

## P1

[P1] src/Hoga.Server/ExperimentCache.cs:15, Program.cs `/api/experiments/{id}` - 캐시 미스 계산이 시장 처리 루프 안에서 실행된다
  근거: `market.Read(() => market.Experiments.Get(id))`는 `Invoke(readOnly)`로 단일 처리 루프에서 돈다. `Get`은 미스마다 `ExperimentRunner.RunExperiment`(VI 켬/끔 시뮬레이션 2회)를 동기 실행한다. 인증이 없고 유효 ID가 3종 x 1000개라 서로 다른 ID를 순환 요청하면(캐시 8개 FIFO) 계속 미스가 나서 루프가 계산에 점유된다. 그동안 주문, 봇 배치, 해시 요청이 밀려 5초 타임아웃(504)이 난다. 한 번 계산에 걸리는 시간은 직접 재지 못했다(확인 필요, 진행 기록은 300쌍 배치만 언급).
  제안: 계산을 `Task.Run`으로 루프 밖에서 하고(캐시는 `lock` 또는 `ConcurrentDictionary<key, Lazy<Task>>`), 동시 계산 수를 1로 제한한다. 또는 `demo`만 서버 계산하고 나머지는 시작 시 미리 계산한 3~몇 개로 제한. 캐시 FIFO 퇴출과 미스 경로 테스트도 없다(P2-8).

[P1] src/Hoga.Server/MarketLoop.cs:80-95 `ExplainOrder`, MarketProjection.cs `MayHaveExpired` - 만료 후보 설명 조회가 요청마다 저널 앞에서 끝까지 순차 파싱한다
  근거: `_lastEvictedSequence` 이하의 `o-{n}`은 투영에 없으면 모두 "후보"가 되어 `JournalReader.Read(_path).Commands`를 처음부터 seq까지 줄 단위 파싱(JSON + 체크섬)한다. 결과를 캐시하지 않는다. 진행 기록 기준 저널이 6분에 약 30MB(10배속 하루 약 7GB)라 오래된 id일수록 수백 MB 읽기가 된다. 게다가 실제 주문이 아닌 순번(`o-5`가 취소 명령 순번 등)도 후보라 전체 탐색 후 404가 된다. 루프는 막지 않지만 `SemaphoreSlim(1)` 대기열이 무제한이고 `WaitAsync(ct)`에 타임아웃이 없다. 인증 없는 요청이 쌓이면 스레드풀·디스크를 오래 점유하고, 쓰기와 같은 디스크를 쓴다. 정상 화면 폴링은 종료 상태에서 멈추므로 평소에는 영향이 작다(`web` 404를 null로 받는지는 `lookup` 구현 미확인, 확인 필요).
  제안: (1) 대기열 상한과 대기 타임아웃(예: 대기 1~2건 초과 시 즉시 404/429). (2) 결과 캐시(만료 확정 id 소형 LRU). (3) 취소·시각 명령 순번 같은 비주문 id를 거르는 방법이 없으면 만료 확정 후보를 더 줄이는 인덱스(주문 id가 `o-{seq}`이므로 seq 줄 번호로 바로 이동하는 오프셋 인덱스, 진행 기록의 제안과 동일). 인덱스 전까지는 (1)(2)만으로 충분.

[P1] src/Hoga.Server/LiveBots.cs `Observe` / MarketLoop.cs:195-204 `Restore(rebuildBots)` - 실제 재시작 때 봇 상태 재구성 경로에 테스트가 없다
  근거: 새 분기 `Restore(rebuildBots: true)`는 `_owners`, `LiveOrders`, `Inventory`를 저널 재생으로 만든다. 기존 테스트는 `Crash`(봇 상태 유지, `rebuildBots` false)와 projection 재생만 확인한다. 재시작 뒤 첫 `Requote`가 이전 활성 봇 주문을 정확히 취소하는지, 재고가 같은지 검증하는 테스트를 찾지 못했다(확인 필요: `DistinguishesExpiredOrderFromAnOldNonOrderSequenceAfterReplay`가 봇 켠 재시작을 하는지는 코드를 끝까지 보지 않았다). 재구성이 틀리면 저널에 봇의 불필요한 거부 취소가 쌓인다(P2-3).
  제안: 봇을 켠 서버를 돌리고 재시작(같은 저널로 새 `MarketFactory`)한 뒤 `Observe`가 만든 `LiveOrders`가 엔진의 호가창 봇 주문과 같은지, 다음 틱의 취소가 모두 `OrderCancelled`인지 테스트.

[P1] tests/Hoga.Server.Tests/MarketTests.cs:358, 372 `JournaledBotsPreserveHashAcrossCrash` - 전체 실행 1회차에서 실패(타임아웃 5초), 단독 3회는 통과
  근거: 첫 `WaitUntil`(MarketMaker 주문이 저널에 나타날 때까지)이 5초 안에 끝나지 않았다. 가능한 원인(추정, 확인 필요): `app.Clock.AddMilliseconds(1_000)`를 `ExecuteAsync`가 `_timeOrigin`을 잡기 전에 호출하면 가짜 시계의 경과가 0으로 시작해 봇 틱이 영원히 안 온다. 다른 테스트와 병렬로 돌 때 호스트 기동이 늦어지면 발생한다. 이 경우 "결정적 해시" 핵심 테스트가 CI에서 간헐 실패한다. 측정 서버가 같은 PC에서 돌고 있어 부하가 영향을 줬을 수도 있다.
  제안: 클라이언트 요청 하나(`/api/state/hash`)를 먼저 보내 루프가 시작된 것을 확인한 뒤 시계를 올린다. 대기 한도는 늘리고 실패 시 저널 줄 수를 메시지에 남긴다.

## P2

[P2-1] LiveBots.cs / MarketLoop.cs:155 - 실제 재시작 뒤 봇이 연속되지 않는다
  근거: `_fair = basePrice`, 난수 스트림 처음부터, `_previousClose`, `_downStreak`, 시나리오가 초기화된다. 시장 가격이 기준가에서 멀어졌다면 재시작 직후 봇이 기준가 주변을 새로 호가해 급변이 생길 수 있다(영향 크기는 확인 필요). 진행 기록에 명시된 한계이며 해시 결정성은 깨지지 않는다.
  제안: 재시작 때 `_fair`를 엔진 마지막 가격 또는 `DynamicReferencePrice`로 시작. 시나리오 선택을 저널 명령으로 남기는 것은 TASK-003B 이후 검토.

[P2-2] MarketLoop.cs:59-60, 297-308 - `readOnly`가 호출자 선언이라 읽기가 아닌 작업에 붙이면 저널 기록 뒤 예외를 삼킬 수 있다
  근거: 지금은 `Read`, `Subscribe`만 켠다(안전). `Read<T>(Func<T>)`가 임의 `Func`를 받으므로 나중에 상태를 바꾸는 작업이 들어가도 막을 장치가 없다.
  제안: `Read`에 "엔진·저널을 바꾸지 않는다" 주석과, 읽기 중 `_seq`가 변하면 예외로 정지하는 방어(`_seq` 비교) 한 줄.

[P2-3] LiveBots.cs `Observe` - 거부된 `CancelOrder`는 `LiveOrders`에서 지우지 않는다
  근거: `OrderRejected`는 `command is PlaceLimitOrder`일 때만 `Forget`. 봇 취소가 `UnknownOrder`로 거부되면(예: P1-3의 재구성 불일치, VI 단일가 중 취소 가능 여부) id가 남아 매 requote마다 거부 취소를 저널에 반복 기록한다. 현재 경로에서 실제로 발생하는지는 확인하지 못했다(확인 필요).
  제안: 취소 명령의 `UnknownOrder` 거부 시에도 `Forget`.

[P2-4] MarketLoop.cs:224-240 `RunBots` - 속도 20배 초과에서 봇 틱이 따라가지 못한다
  근거: 한 번에 최대 20틱(=논리 2초)이고 배치는 타이머 틱(실시간 100ms)당 1개다. `Speed`는 최대 1000까지 허용하므로 20배 초과면 `_nextBotTick`이 계속 뒤처진다. 뒤처진 틱은 `Math.Max(tick, 투영 시각)`로 시각이 보정되어 정확성은 유지된다.
  제안: `Speed` 검증 상한을 낮추거나 문서에 "봇 켜면 20배 이하"를 적는다.

[P2-5] Program.cs `/api/admin/scenario` - 봇이 꺼져 있어도 `{ ok: true }`를 반환한다
  근거: 진행 기록에 의도로 적혀 있으나 화면은 성공으로 오해한다. 관리자 키 무차별 시도 제한이 없는 점도 REVIEW-5 P2-11 그대로.
  제안: 계약에 "봇 꺼짐이면 ok지만 효과 없음"을 적거나 `bots: false` 필드를 응답에 추가(계약 변경 필요 → 메인 결정).

[P2-6] MarketLoop.cs:102-117 `Subscribe` - 최대 약 1,490건(1분봉 하루치 + 체결 50)을 루프 안에서 순차 전송
  근거: 한 전송이 느린 클라이언트는 최대 1초(`CancelAfter`) 동안 루프를 붙잡는다. 재연결이 몰리면 누적된다. REVIEW-5의 "SignalR 전송 대기" P2의 연장이다.
  제안: 이력을 한 번에 묶어 보내는 계약 변경은 다음 계약 버전에서 검토. 지금은 기록만.

[P2-7] web/src/Orders.tsx:47-62, 95 - 조회 오류가 반복되면 같은 주문을 2초마다 계속 조회한다. 수량 한도 1,000,000이 서버 설정과 중복
  근거: `catch`는 `status`를 바꾸지 않아 `!status`인 주문이 폴링 대상에 남는다(한 번에 1건, 2초라 부하는 작다). `MAX_QUANTITY`는 `Market:MaxOrderQuantity` 기본값을 복사한 상수라 서버 설정을 바꾸면 화면과 어긋난다. 가격은 여전히 `Number(price)`로 NaN/소수를 검증 없이 보낸다(서버가 400).
  제안: 연속 오류 n회 뒤 `Unavailable` 처리. 한도는 서버 응답의 400 메시지를 우선 표시.

[P2-8] 테스트 빈 곳 - `ExperimentCache` FIFO 퇴출(9번째 ID), 봇 꺼짐 + 시나리오 요청, 만료 조회 동시성/타임아웃, 기존 `exp-1` 404 테스트(MarketTests.cs:128)가 그대로 남아 있음
  근거: 새 분기마다 테스트가 있어야 한다는 가이드 기준. 파싱 범위(0, 1001, `+1`, 소문자)는 테스트가 있다.
  제안: 위 3건 추가. `exp-1` 테스트는 의도(형식 오류 404)임을 이름에 드러낸다.

[P2-9] docs/api/CONTRACT.md:19 - 구현 후에도 "TASK-008에서 구현. 그 전까지는 생략됨" 문구가 남아 있고, 만료 설명 404 메시지("기록 보관 기간이 지났습니다")와 만료 보관 상한은 계약에 없다
  근거: diff에 CONTRACT.md 변경이 없다. 화면은 404를 그대로 `null`로 처리하므로 동작에는 문제가 없다.
  제안: v0.3 주석 갱신과 "종료 주문 설명은 최근 4,096건(임의 지정)만 유지, 이후 404 + 보관 기간 메시지" 한 줄 추가(계약 변경이므로 메인 에이전트가 처리).

## 화면 폴링·경쟁 상태 판단
- 한 번에 1건, 2초 주기, 미종료·미확인 주문만 순환한다. 열린 주문이 N개면 주문당 간격이 2N초라 서버 부하는 작고 상한이 있다. 탭 수만큼 늘어나는 점만 주의.
- 취소 직후 늦게 도착한 `Resting` 응답은 `cancelled` ref와 `status` 갱신 조건으로 무시된다. `inFlight` 집합으로 같은 id 중복 요청도 막는다. 경쟁 상태 결함은 찾지 못했다.
- 재접속 초기화는 `beginSync`가 첫 이력 메시지 또는 `onreconnected` 중 먼저 오는 쪽에서 `syncing`으로 바꾸며 목록을 비운다. 이력이 알림보다 먼저 와도 순서가 맞다(테스트 있음). 확인한 한계는 없음.
