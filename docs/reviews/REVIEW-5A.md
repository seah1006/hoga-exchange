# REVIEW-5A: TASK-005 / TASK-004 / TASK-006B 병합 1단계(규칙 기반) 리뷰

- 범위: `git diff 19744aa..bb3b945` (병합 3건 `d1dc927`, `db8ff47`, `bb3b945`, 43파일 +2411). src/Hoga.Bots, src/Hoga.Experiments, src/Hoga.Server, src/Hoga.Journal 변경분, web/ 변경분, 해당 테스트와 docs/experiments.
- 리뷰어: Claude Sonnet 5.5
- 기준: REVIEW_GUIDE.md, AGENTS.md, TASK-004/005/006B 및 각 PROGRESS, CONTRACT v0.2, ADR 0001~0004, REVIEW-4A(형식·이월 항목)
- 등급별 개수: **P0 1건 / P1 5건 / P2 9건**
- 검증: `dotnet test HogaExchange.slnx` 전부 통과(Engine 103, Journal 34, Server 10, Bots 8). `dotnet run -c Release --project src/Hoga.Experiments -- --batch --out <임시 폴더>`로 SUMMARY.md와 demo-fatfinger-16.json 재생성: JSON은 바이트 동일, SUMMARY.md는 줄바꿈(작업 트리 CRLF 대 생성 LF)만 다르고 내용 동일. 임시 프로브 프로젝트(저장소 밖)로 엔진 수량 합산 오버플로를 확인. 저장소 파일은 이 문서 외 변경 없음. web 테스트는 돌리지 않음(이번 범위의 web은 계약 필드 대조만 함).

## 확인한 것 (문제 없음)
- 결정성(봇): 난수는 SplitMix64뿐이고 `(시드, 봇 이름)`을 SHA-256으로 파생해 봇별·적정가치 스트림이 분리됨. 참조 벡터 테스트와 "봇 추가가 기존 스트림을 옮기지 않음" 테스트가 있음. 시스템 시계·전역 난수·double 없음. `Dictionary`는 조회 전용, 집계는 `SortedDictionary`. `NextBelow`의 편향 제거 임계값 식이 표준과 일치. 엔진 단일가 난수에는 실험 시드를 주입. 같은 시드 두 번 실행 시 JSON 해시 일치를 테스트가 검증하고 배치 재실행으로도 확인.
- 실험 정직성: 시연 시드 선정 규칙 커밋(`f13f9b7`)이 구현(`06be57e`)과 결과(`ee68a23`)보다 앞섬. 시드 1~100 전수, 시나리오 3개 모두 보고, "VI 켬이 더 나쁨 0건"도 그대로 적음. SUMMARY 표와 시연 JSON은 위 재생성으로 코드와 일치. 한계 문구(임의 지정, 관측 구간, 240초 시점 VI 진행 중 run 수)가 있음.
- 서버 단일 처리 루프: 엔진·저널·투영을 만지는 경로(Place, Cancel, Explain, Hash, Subscribe, Crash, 틱)가 모두 `Invoke` 채널을 거쳐 `ExecuteAsync`에서만 실행됨. 루프 밖에서 엔진을 직접 만지는 경로는 찾지 못함.
- 서버가 엔진에 넣는 논리 시각: `TimeProvider`의 단조 경과 시간으로 만들고 명령에 실려 저널에 기록되므로 재생은 시계와 무관. `AdvanceTime`은 VI 중에만 100ms마다 저널에 기록. 시작 시 마지막 기록 시각에서 이어 감. 명령 간 시각이 거꾸로 가는 경로는 못 찾음.
- 저널 이어쓰기: 기존 envelope·골든·헤더 형식 변경 없음(Serialization 변경은 `advance-time/v1` 변환기 추가뿐). 테스트 삭제·약화 없음(Journal 테스트는 +2, 기존 32 유지). 끝 조각 제거는 LF 없는 꼬리만, 완료된 줄 손상은 예외로 중단하고 파일 불변을 테스트함. 9000바이트 꼬리(4096 블록 여러 개)도 테스트됨.
- 관리자 키: 키 미설정 또는 빈 값이면 401(항상 거부), 헤더가 정확히 1개여야 하고, SHA-256 해시끼리 `FixedTimeEquals`로 비교(길이 노출 없음). 로그(`Crash simulation`)에 키 없음. 코드·appsettings·.env.example·테스트에 고정 키 없음(테스트는 난수 생성). `.env`와 `data/journal/` ignore 확인.
- 엔진 무변경(`src/Hoga.Engine` diff 없음), CONTRACT 무변경.

## P0

[P0] src/Hoga.Server/Program.cs:33-44, src/Hoga.Server/MarketLoop.cs:41-48,180-196,231 - 수량 상한 검증이 없어 인증 없는 요청 2건으로 서버가 죽고 재시작 뒤에도 같은 문제가 남는다
  근거: `POST /api/orders`는 `quantity`가 양의 `long`이면 그대로 엔진에 넣는다(상한 없음). 엔진은 `GetBook`에서 같은 가격 잔량을 `checked`로 합산한다(MatchingEngine.Queries.cs:27). 프로브로 확인: 같은 가격에 `long.MaxValue/2+10`주 매수 2건은 둘 다 `OrderAccepted`로 받아들여지지만 이후 `GetBook(10)`이 `OverflowException`을 던진다(`ComputeStateHash`와 이후 주문 처리는 정상). 서버는 `Process`에서 저널에 먼저 쓰고(MarketLoop.cs:180) 곧바로 `PublishSnapshotIfDue`가 `Snapshot`→`GetBook`을 호출(196)하므로 두 번째 주문 처리 중 예외가 난다. 예외는 `Request.Run`이 다시 던져(231) `ExecuteAsync`가 끝나고 호스트가 내려간다. 주문은 이미 저널에 있으므로 재시작해도 첫 `Subscribe`나 첫 스냅샷에서 같은 예외가 나는 것으로 보인다(코드 추적. 서버 전체 경로를 실제로 돌려 보지는 않았음. 엔진 수준 재현만 함). TASK-004 PROGRESS도 "수량·호가 잔량 한도를 계약으로 정하라"고 미해결로 적었다. 가격 상한은 엔진이 막지만 수량은 막지 않는다. 관리자 키가 필요한 crash와 달리 이 경로는 무인증이다.
  제안: (1) 서버 입구에서 수량 상한을 검증하고(예: 한 주문 1,000,000주, 임의 지정이며 계약 v0.3에 `InvalidQuantity` 범위로 명시), (2) 가능하면 엔진 쪽 거부 사유로도 넣어 저널 앞 단계에서 걸러지게 한다(메인 판단 필요: 엔진 변경). (3) 아래 P1-1의 "저널에 쓴 뒤 예외" 정책을 같이 정한다. (4) 회귀 테스트: 거대 수량 2건 후에도 서버가 응답하고 재시작이 성공해야 한다.

## P1

[P1] src/Hoga.Server/MarketLoop.cs:219-232 - 어떤 요청이든 예외 하나가 시장 루프 전체를 종료시키고, 저널에 쓴 뒤의 예외는 재시작 불능이 된다 (REVIEW-4A P1-1 이월)
  근거: `Request.Run`은 모든 예외를 `MarketUnavailableException`으로 요청자에게 알린 뒤 다시 던져 루프가 끝난다. 주석은 "저널에 이미 쓰였을 수 있어 부분 적용 상태로 계속하지 않는다"는 의도다. 이 선택 자체는 이해되지만, (a) 읽기 전용 요청(`Explain`, `Hash`, `Subscribe`)의 예외도 같은 처리라서, 예를 들어 `MarketProjection.Explain`의 `InvalidOperationException("Active order is absent...")`(MarketProjection.cs:96)나 `_orders[...]` 키 누락 같은 투영 오류 하나로 시장이 죽는다. (b) 쓰기 명령은 저널에 먼저 들어가므로 엔진이 예외를 던지는 명령은 REVIEW-4A에서 지적한 대로 복구 불능 명령이 된다. 지금은 위 P0가 실제 입력 경로다. 이 이월 항목은 서버가 생기면서 외부 입력으로 닿게 되었다.
  제안: 읽기 요청은 예외를 요청자에게만 돌려주고 루프는 유지. 쓰기 명령은 (a) 입력 검증을 저널 앞에 둘 것, (b) Restore가 엔진 예외에 seq를 붙여 알릴 것(REVIEW-4A 제안), (c) 정책(건너뜀 금지 vs 중단)을 ADR 0004에 기록할 것.

[P1] src/Hoga.Server/Program.cs:52 (그리고 `POST /api/admin/scenario` 없음), web/src/api/serverMarketSource.ts:144 - 병합된 main에서 시나리오 실행과 비교 실험 화면이 서버 모드에서 동작하지 않는다
  근거: CONTRACT v0.2는 `POST /api/admin/scenario`와 `GET /api/experiments/{id}`(실험 객체)를 서버 몫으로 정했다. 서버는 `/api/admin/scenario`를 매핑하지 않았고(없는 경로라 404 또는 405), `/api/experiments/{id}`는 항상 404를 돌려준다. `Hoga.Server`는 `Hoga.Bots`를 참조하지도 않는다. 화면(`runScenario`, `getExperiment`)은 이 엔드포인트를 호출하므로 VITE_USE_MOCK=false에서 시나리오 버튼은 오류 메시지, 비교 화면은 "찾을 수 없음"이 된다. TASK-004 PROGRESS가 5장 범위 밖이라고 명시했으므로 지시서 위반은 아니고 문서화된 미완이다. 다만 main에 합쳐진 시점에서 계약과 구현의 차이가 남아 있다.
  제안: 후속 TASK(봇을 서버에 연결, `Hoga.Bots` 참조, 실험 JSON 제공, scenario 엔드포인트)를 열기 전까지 CONTRACT에 "v0.2 서버 구현 범위" 표를 붙이거나 화면에서 서버 모드일 때 해당 버튼을 비활성화한다. 확인 필요: scenario가 서버에서 어떤 상태 변경을 할지(실행 중 시장에 봇을 붙이는지, 별도 실험인지)는 지시서에 없어 메인 결정이 필요하다.

[P1] src/Hoga.Server/MarketLoop.cs:115 (JournalHeader.Create), src/Hoga.Journal/Serialization.cs - 서버가 엔진 옵션을 헤더에 남기지 않고 엔진 기본값에 의존한다 (REVIEW-4A P1-2 이월)
  근거: 서버는 `JournalHeader.Create(basePrice, TickSizeTable)`로 만든 헤더에서 `new MatchingEngine(basePrice, rule)`를 만든다. 즉 `EngineOptions` 기본값(`ViEnabled=true, IsKospi200=false, RandomSeed=0`)이 저널 재생에 암묵적으로 쓰인다. 지금은 일관되지만 엔진 기본값이나 옵션 의미가 바뀌면 기존 저널이 같은 seq로 다른 상태(특히 단일가 종료 시각의 난수)로 재생되며 재시작 시 아무 검증도 하지 않는다(복구 후 해시를 비교할 기준이 저널에 없다). 서버 `/api/state/hash`의 crash 전후 비교는 같은 프로세스 안에서만 의미가 있다.
  제안: 003B에서 헤더에 옵션을 넣을 때까지, 최소한 서버 시작 시 사용하는 옵션을 로그에 남기고 ADR 0004에 "서버는 기본 옵션 고정"을 적는다. 003B 시작 전에 서버가 쓰는 기본값을 고정하는 테스트(예: 특정 저널의 기대 해시 골든)를 추가한다.

[P1] tests/Hoga.Server.Tests/MarketTests.cs - 새 분기 중 테스트가 없는 것이 여럿이다
  근거: 테스트 메서드는 5개(거부 사유, 400 형식, crash+실제 호스트 재시작, 404/401, VI 단일가와 SignalR)뿐이다. 코드에는 있으나 테스트에서 찾지 못한 분기: 큐 가득/루프 종료 시 503, 대기 시간 초과 504와 "처리 전 건너뜀"(`SkipIfQueued`), 관리자 키 미설정 시 401(테스트는 항상 키를 설정한 팩터리), 헤더 2개 이상, 설정 검증 실패(`Speed`, `BasePrice`, `ValidateOnStart`), 거대 수량(P0), `Crash`의 해시 불일치 예외 경로, `Subscribe` 1초 전송 시간 초과, 저널 이어쓰기 후 `o-{seq}` 주문 ID 이어짐(재시작 후 새 주문 접수), 일반장에서 `AdvanceTime`을 쓰지 않는다는 점(저널에 줄이 늘지 않음). 확인 필요: 위 항목 중 일부는 SignalR 테스트 안에서 간접 확인될 수 있으나 이름과 단언으로는 찾지 못했다.
  제안: 최소한 키 미설정 401, 503/504 한 건씩, 설정 검증, 재시작 후 신규 주문, 일반장 저널 무증가 테스트를 추가. Trait("Rule", ...)가 필요한 항목은 없음(규칙 ID 신규 구현 없음).

[P1] tests/Hoga.Bots.Tests/ExperimentTests.cs, src/Hoga.Experiments/BatchReport.cs - 봇 분기와 보고서 계산에 직접 테스트가 없다
  근거: 단위 테스트는 FatFinger와 PanicSeller뿐이다. MarketMaker(재고 보정 ±180 한도, 500ms 주기), Noise(주기·방향), Momentum(상승/하락/보합 세 갈래)은 해시 재현 테스트로 간접만 검증된다(분기가 바뀌어도 해시만 다시 맞추면 통과). `BatchReport`의 분위수(`sorted[24]`, `sorted[74]`), 중앙값(`Half`, 음수 홀수 처리), 구간 경계, 시연 시드 선택(동률 시 작은 시드)과 CLI 인자 파싱(`args.Length == 6`, 중복 옵션)에는 테스트가 없다. SUMMARY 수치는 이번에 재생성으로 검증했지만 자동화되어 있지 않아 코드가 바뀌면 문서가 조용히 어긋날 수 있다.
  제안: `Half`·분위·시드 선택을 순수 함수로 분리해 소규모 입력 테스트를 추가하고, Momentum 3분기와 MarketMaker 재고 보정을 `MarketView` 입력 테스트로 고정. 선택 사항으로 "시드 1~100 SUMMARY 해시" 골든 테스트(느리면 별도 Trait).

## P2

[P2] src/Hoga.Bots/MarketSimulation.cs:13-15, docs/experiments/SUMMARY.md - 관측 절단이 VI 켬 쪽에 유리하게 작용할 수 있다
  근거: 240초에 VI가 진행 중인 run이 켬 기준 Calm 1, FatFinger 6, PanicSell 16이다. 이 run들은 진행 중 단일가 체결가가 최저가에 반영되지 않는다. SUMMARY가 이 수를 표로 공개하고 한계도 적었으므로 정직성 규칙 위반은 아니다. 다만 PanicSell의 "VI 켬 더 높음 29건"에는 16건 중 일부가 섞여 있을 수 있다(확인 필요: 해당 run 제외 시 결과).
  제안: 후속 실험에서 종료 시각을 VI 종료까지 연장하거나, 진행 중 run을 제외한 민감도 표를 추가.

[P2] src/Hoga.Bots/Bots.cs:93-95, docs/experiments/SUMMARY.md - FatFinger의 최저가 지표는 구조상 거의 정해진 결과다
  근거: FatFinger는 하한가+20원(7,020원)에 매도해 VI 끔 쪽 최저가가 사실상 이 값이다. Δ는 "VI 켬 최저가 − 7,020"에 가까워 켬이 높게 나오는 것이 설계에서 거의 보장된다. 시드를 고르지는 않았고 SUMMARY의 한계 문구가 이를 완화하지만, "100/100"이 독립 증거처럼 읽히지 않게 설명을 더하면 좋다.
  제안: SUMMARY에 "FatFinger 충격 크기를 사람이 정했기 때문에 방향이 설계에 내재"라는 한 줄을 추가.

[P2] src/Hoga.Server/Program.cs:53-61 - 관리자 키 보호에 시도 제한이 없고 키가 평문 HTTP 헤더로 오간다
  근거: 로컬 URL(`localhost:5280`) 기본이라 현재 위험은 낮다. 키 길이/복잡도 검증 없음(짧은 키 허용). 화면은 `sessionStorage`에 보관(탭 닫으면 삭제, XSS에는 노출). 공개 배포 시 HTTPS 필수.
  제안: 배포 전 TASK에 "HTTPS 강제, 키 최소 길이, crash 호출 빈도 제한"을 넣는다.

[P2] src/Hoga.Server/MarketLoop.cs:60-79,185 - 루프 안에서 SignalR 전송을 `await`해 느린 연결이 처리 루프를 최대 1초씩 붙잡을 수 있다
  근거: `Subscribe`와 `Broadcast`가 루프 안에서 최대 1초(임의 지정) 기다린다. 그룹 전송은 연결별 큐에 넣는 방식이라 실제 지연은 작을 것으로 보이지만(확인 필요) 최악의 경우 시장 처리가 멈춘다. 설계 문서에 인지된 선택으로 적혀 있다.
  제안: 전송을 별도 채널의 소비자로 분리하거나 부하 시험으로 확인.

[P2] docs/adr/0001-single-thread-event-sourced-engine.md:9 - ADR은 "종목마다 하나의 스레드"인데 서버 구현은 단일 처리 흐름(OS 스레드 고정 아님)이다
  근거: TASK-004 PROGRESS가 "논리적인 단일 실행 흐름"이라고 정직하게 밝혔다. 엔진은 락이 없고 비동기 `await` 뒤에 다른 스레드풀 스레드에서 이어질 수 있다. 직렬 실행이라 데이터 경쟁은 없고 메모리 가시성도 `await`로 보장된다고 판단하나, ADR 문구와 달라졌다.
  제안: ADR 0001 또는 0003에 "서버에서는 채널 단일 소비자로 직렬화, 스레드 고정은 요구하지 않음"을 한 줄 추가.

[P2] src/Hoga.Server/MarketLoop.cs:168 - 논리 시각이 `정수 ms × Speed`로 양자화된다
  근거: `(long)elapsed.TotalMilliseconds * Speed`로 배속 10이면 10ms 단위로만 증가한다. 단조성은 유지되어 결정성에는 영향이 없다.
  제안: 필요하면 `(long)(elapsedTicks * Speed / ticksPerMs)` 식으로 정밀도를 높이되 현 상태로도 무방.

[P2] src/Hoga.Server/MarketProjection.cs (주문·투영 상태), docs/tasks/TASK-004-PROGRESS.md - 주문 투영(`_orders`)과 저널이 무한히 커진다
  근거: 종료된 주문도 `_orders`에 영구 보관(설명 조회용). VI 중 100ms 입력이 저널에 쌓인다. 복구는 전체 재생(`OpenExisting`이 한 번 읽고 `Recovery.Restore`가 다시 읽음, 시작 시 2회). 진행 문서에 스냅샷(003B) 제안이 있음.
  제안: 공개 배포 전 보존 한도·스냅샷 계획을 정한다.

[P2] docs/api/CONTRACT.md, src/Hoga.Server/Program.cs - 계약 문구와 서버 동작의 해석 차이
  근거: (a) 계약은 `UnknownOrder`를 "이미 체결·취소 포함"으로 적고 404는 "없는 주문"이라 했는데 서버는 처음부터 없는 ID는 404, 종료된 주문은 200+`UnknownOrder`로 나눈다(화면은 둘 다 처리). (b) `X-Admin-Key`, 503/504, 새 연결 직후 최신 `bookSnapshot` 전송은 계약에 없다(양쪽 PROGRESS가 계약 반영을 제안). (c) 연속 매매 중 `expectedPrice` 필드는 null이 아니라 아예 생략됨(계약: "추가되며 없으면 null"). (d) `lastPrice`는 `engine.DynamicReferencePrice`(첫 체결 전에는 기준가)이며 계약 예시의 "마지막 체결가"와 같은 뜻인지 확인 필요. 필드 이름과 형은 `contract-v02.json` 픽스처와 서버 익명 객체를 대조해 일치함(camelCase, `orderCount`, `queueAhead`, VI 객체 포함).
  제안: 다음 계약 버전에 위 항목을 반영(메인 담당).

[P2] src/Hoga.Server/MarketLoop.cs:41-48 - 주문 접수 요청 한 번이 `Place` 결과를 만들 때 `SingleOrDefault`로 거부 이벤트를 찾는다
  근거: 한 주문은 거부 이벤트가 최대 1개라 현재는 안전하다. 엔진이 한 명령에서 여러 거부를 낼 수 있게 바뀌면 `InvalidOperationException`으로 루프가 종료된다(P1-1 정책과 연결).
  제안: `FirstOrDefault` 사용 또는 P1-1 정책으로 흡수.
