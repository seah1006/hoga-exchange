# REVIEW-7A (1단계, 규칙 기준)

- 범위: `git diff 0aed3ee..HEAD` (main). TASK-009S, TASK-009W, TASK-010, TASK-007 (운영 설정, 매일 초기화, deploy/).
- 리뷰어: Claude Sonnet 5.5 (읽기 전용). 기준: REVIEW_GUIDE.md, AGENTS.md, TASK-007/009/010 및 PROGRESS, CONTRACT v0.4.
- 개수: **P0 0 / P1 6 / P2 8**
- 실행: `dotnet test HogaExchange.slnx` 중 Engine 103, Bots 14 통과. Journal·Server 테스트는 메인이 띄운 Hoga.Server(5280)가 dll을 잡고 있어 빌드 실패(MSB3027)로 실행하지 못했다(서버를 건드리지 않기 위해 그대로 둠). 서버 쪽 판단은 코드 읽기 기준이다.
- 확인한 것: 엔진 코드(`Hoga.Engine`)는 이 범위에서 변경 없음. deploy/·테스트·문서 diff에서 비밀값 없음(`Market__AdminKey`는 `${…:?}` 참조뿐, `.env`는 .gitignore). 앱 포트는 호스트에 게시하지 않음(`ports:` 없음). 실험 수치는 불변(아래 P2-5 참조).

## P0

없음.

## P1

[P1] src/Hoga.Server/MarketLoop.cs:81,337 (+ 초기화 ResetMarket 243-) - 초기화 후 `seq`와 주문 ID(`o-N`)가 1부터 재사용된다
  근거: ID가 `"o-" + seq`이고 `StartEmpty()`가 `_seq=0`으로 되돌린다. `DELETE /api/orders/{id}`는 `KnowsOrder(id)`만 확인하고 소유자 검증이 없어, 초기화 전 `o-5`를 들고 있는 클라이언트(오프라인·탭 방치, SignalR 재연결 지연)가 새 장의 `o-5`를 취소할 수 있다. `explanation`도 새 주문의 설명을 돌려준다. TASK-007-PROGRESS도 이를 인지하고 제안으로만 남겼다. 매일 05:00 자동 초기화라 공개 후 매일 발생한다.
  제안: 공개 전 최소 완화. (a) 주문 ID에 세대 접두를 넣는다(`o-<generation>-<seq>`, 세대는 저널 보관 시각 또는 시작 시각). 계약 추가만이면 v0.4 호환(ID는 불투명 문자열). 또는 (b) 초기화 시 `seq`를 0으로 되돌리지 않고 이어 간다(저널 헤더에 시작 seq). 어느 쪽이든 `MayHaveExpired`의 `o-<seq>` 파싱도 함께 수정. 팀 계약 변경이므로 승인 필요.

[P1] src/Hoga.Server/MarketLoop.cs:243-282, 304-308 - 초기화 도중 `File.Move` 실패 시 루프가 죽고, 정기 초기화는 그날 건너뛴다
  근거: 251행에서 `_writer`를 Dispose·null 한 뒤 262행 `File.Move`가 IOException을 내면 `StartEmpty`에 도달하지 못한다. 관리자 호출은 `Request.Run`이 non-readOnly 예외를 재던지고, 정기 호출은 `OnTick`에서 그대로 전파되어 `ExecuteAsync`가 종료된다(호스트 중지 후 컨테이너 `restart: unless-stopped`로 재시작). 저널은 그대로라 복구는 되지만, (1) 재시작 후 `ScheduleDailyReset`이 다음 05:00을 잡아 그날 초기화가 누락되고, (2) 실패 원인이 지속되면(볼륨 권한·디스크 가득) 관리자 reset마다 프로세스가 죽는다. 순서상 "이름 바꾸기 성공 후 StartEmpty 전 크래시"는 재시작 시 빈 장으로 시작해 안전하다(원자적 문제 없음). 저널 I/O 실패 주입 테스트가 없다(PROGRESS도 미검증으로 인정).
  제안: Move를 먼저 시도하고(writer를 닫은 뒤), 실패하면 `OpenExisting`으로 writer를 복구해 같은 상태로 계속하며 503/로그만 남긴다. 정기 초기화 실패 시 `_nextDailyReset`을 몇 분 뒤로 재시도. `File.Move` 실패 주입 테스트 1개(읽기 전용 디렉터리 또는 대상 잠금) 추가.

[P1] deploy/docker-compose.hoga.yml:5-17 - 메모리 한도에 swap 제한이 없고, 로그 크기·CPU 제한도 없다
  근거: `mem_limit: 192m`만 있으면 Docker는 기본으로 `memswap`을 메모리의 2배로 허용(cgroup v1/swap accounting 켜진 호스트)해 호스트의 2GB swap을 최대 192MiB 추가로 쓸 수 있다. DEPLOY.md도 "골목장부 한도 900+192MiB가 물리 RAM보다 크다"고 인정했다. json-file 로그도 기본 무제한이라 `Market signal ... delivery failed` 같은 경고가 반복되면 디스크가 찬다. CPU 제한이 없어 `/api/experiments/exp-*-<1..1000>`(ExperimentCache는 1개씩 직렬이지만 240초 시뮬레이션을 반복 계산)이 공유 vCPU를 점유할 수 있다.
  제안: `memswap_limit: 192m`(swap 없음), `cpus: '0.5'`(임의), `logging: {driver: json-file, options: {max-size: 10m, max-file: '3'}}`, `pids_limit` 추가. 가능하면 `/api/experiments/exp-*`에 Caddy 쪽 요청 제한(또는 서버 호스트에서 `demo`만 공개).

[P1] deploy/docker-compose.hoga.yml:26-29, deploy/Caddyfile.hoga.snippet:2 - 골목장부와 네트워크 `sss_edge`를 공유하고 별칭이 `hoga`로 일반적이다
  근거: 외부 네트워크가 골목장부 Caddy가 속한 `sss_edge`다. 이 네트워크에 골목장부 앱·DB가 함께 붙어 있다면 공개 취약 앱인 Hoga(익명 쓰기 API, 192MiB)가 같은 L2에서 DB 컨테이너에 도달할 수 있다(문서에 DB가 이 네트워크에 있는지 확인하라는 절차가 없다). 별칭 `hoga`는 다른 컨테이너와 충돌 가능성이 낮지만 공유 네트워크의 일반명이다.
  제안: DEPLOY.md 1단계에 `docker network inspect sss_edge`로 DB/내부 서비스가 붙어 있지 않음을 확인하는 항목 추가. 가능하면 전용 네트워크 `hoga_edge`를 만들고 Caddy 컨테이너만 `docker network connect`로 붙인다(골목장부 compose는 건드리지 않음). 별칭은 `networks: edge: aliases: [hoga-exchange]`로 명시하고 snippet도 맞춘다.

[P1] deploy/deploy_hoga.ps1:21-30 - 릴리스 태그는 `git rev-parse HEAD`지만 빌드는 작업 트리를 쓴다(미커밋 변경·다른 브랜치가 섞일 수 있음)
  근거: dirty 검사와 브랜치 검사가 없다. 이미지 이름은 커밋을 가리키는데 내용은 다를 수 있어 롤백 기준이 흐려진다. 또한 `summary.json`은 `Condition="Exists(...)"`라 파일이 없으면 조용히 빠지고(앱은 503 반환), 배포 스크립트는 `/healthz`만 확인하므로 누락을 못 잡는다.
  제안: 스크립트 시작에 `git status --porcelain`이 비어 있고 `main`인지 검사(아니면 중단 또는 `-AllowDirty`). 헬스 확인에 `/api/experiments/summary` 200 확인 추가.

[P1] src/Hoga.Server/Program.cs:62-82 및 배포 구성 - 공개 쓰기 API(주문·취소)와 `/api/admin/*`에 요청 제한이 없다
  근거: 누구나 주문을 반복 전송하면 저널(디스크, 다음 05:00까지 무제한 증가)과 프로젝션 메모리(GC 힙 96MiB 하드 리밋 초과 시 OutOfMemory로 프로세스 종료)가 커진다. 관리자 키 무차별 대입에도 제한이 없다. 주문 ID가 연속이므로 봇·타인 주문 일괄 취소도 가능하다. 단일 처리 큐라 PERF 기준 약 700건/s면 큐가 막혀 정상 사용자 지연이 커진다. 이번 범위에서 새로 생긴 위험은 아니지만 이번에 공개 서버로 나가는 첫 배포다.
  제안: 최소한 .NET `AddRateLimiter`(IP 단위 고정창, 예: 주문 20/s·관리자 10/min, 임의)와 Caddy 쪽 제한, 전체 저널 크기 상한(초과 시 주문 거부 또는 즉시 초기화) 중 하나. 팀 규칙상 "비용·계약 변경" 아니므로 TASK-007 후속으로 제안.

## P2

[P2-1] src/Hoga.Server/LiveBots.cs:36-37,58 - 저빈도 강도의 부동소수점 floor
  근거: `Math.Floor(n * intensity)`는 0.1에서는 안전하지만(0.1 이중 정밀도가 0.1보다 약간 크고 `10m*0.1`이 정확히 m), 임의 강도(예 0.3, 0.7)는 `n*intensity`가 정수 바로 아래로 내려가 틱이 한 번 밀릴 수 있다. 같은 값이면 플랫폼 간에도 결정적(IEEE)이라 P0는 아니다. 엔진은 영향 없음(저널이 정본).
  제안: 강도를 정수 분수(예 `Market:BotIntensityPercent`) 또는 `long` 누적 카운터로 처리. 현 공개 값 0.10만 쓸 거면 주석 한 줄.

[P2-2] src/Hoga.Server/LiveBots.cs:49-50,56 - `Requote(...)!` null 허용 연산자가 전략 시각 조건에 의존
  근거: `View(500, engine)`는 `MarketMaker`의 `TimeMs % 500` 조건을 만족해 지금은 null이 아니다. 전략 시각 조건이 바뀌면 NRE로 봇 배치가 버려지고 `_fired=true`라 시나리오가 소실된다. 또 재시작 후 `Restore`는 봇 `LiveOrders`만 복원하고 `SplitMix64` 스트림은 시드에서 다시 시작하므로 재시작 전후 봇 난수 열은 이어지지 않는다(저널이 정본이라 상태 해시에는 무관).
  제안: `?? []` 대신 null이면 건너뛰고 `_fired`를 false로 두거나, 테스트에 "프라이밍이 null이 아님" 단언 추가. 재시작 후 스트림 비연속은 문서에 한 줄.

[P2-3] src/Hoga.Server/MarketLoop.cs:96-130,247 - 초기화가 이전 저널 스캔(`ExplainOrder`)이 끝나길 시장 큐 안에서 기다린다
  근거: `_expiredRead.WaitAsync(_stopping)`는 `Invoke` 안에서 실행되어 스캔 동안 주문 처리가 멈춘다. 저널은 하루치(05:00 초기화)라 보통 짧지만 큰 저널에서는 지연된다.
  제안: 대기에 타임아웃을 주고 초과 시 reset을 거부, 또는 reader가 보관 후 파일을 읽도록 변경.

[P2-4] deploy/DEPLOY.md:8,32,37 - 운영 절차 보강
  근거: 서버 IP(`158.180.72.47`)와 도메인이 저장소에 있다(비밀은 아님, 공개 도메인이라 의도적). 다음이 비어 있다: `.env` 값에 `$`가 있으면 Compose가 보간하므로 키는 `$`를 피한다는 안내, 오래된 `releases/` 디렉터리와 `hoga-exchange:*` 이미지 정리 절차(2GB swap 서버 디스크), 이미지 `aspnet:10.0` 태그 고정(다이제스트).
  제안: 위 세 줄 추가.

[P2-5] docs/experiments (SUMMARY.md, summary.json, demo-fatfinger-16.json) - 수치는 불변, 중복 정보 일부 누락
  근거(확인한 것): summary.json을 직접 계산해 SUMMARY.md와 비교했다. 분위수·구간 집계(Calm 0/0/0/0/0, FatFinger 1500/2270/2465/2650/2990, PanicSell 0/0/0/130/1300), 부호별 개수(0/100/0, 0/0/100, 0/71/29), 시나리오별 시드 100개의 `delta == minOn − minOff`, FatFinger VI 발동 합계 106·Calm 1·PanicSell 37, 240초 VI 중 run 수(6/16/1), 시연 시드 16·Δ2470(켬 9490, 끔 7020)이 모두 일치한다. 시연 JSON의 변경은 `minAtMs`·`maxAtMs` 추가(시드 16: 켬 189890·60000, 끔 60200·167800)이며 가격 필드는 유지된다. `SUMMARY.md`는 이 범위에서 수정되지 않았다. 다만 summary.json에는 거부 합계와 VI 발동 합계가 시나리오 요약에 없고(시드별 `viTriggers`만), SUMMARY.md의 "거부 0건"은 JSON에서 검증할 수 없다.
  제안: JSON에 `rejections`·`viTriggers` 합계 추가 시 SUMMARY.md와 자동 일치 테스트(`ExperimentRegressionTests`에 있으면 확장).

[P2-6] src/Hoga.Bots/MarketSimulation.cs:101-112 - 극값 시각의 동률 처리 정책
  근거: 최저가가 같은 체결이 여러 번이면 첫 체결 시각을 쓰고(`_minAtMs is null` 조건), 시작 기준가와 같은 극값은 시각 0이 아니라 첫 실제 체결 시각으로 대체된다(주석에 있음). 일반 Math.Min과 MinPrice 값은 같다. 결정성 영향 없음. UI가 `minAtMs=0`을 "체결 없음"으로 읽을 수 있다는 점만 CONTRACT에 한 줄 필요.
  제안: CONTRACT에 `minAtMs`가 0이면 기준가 유지·체결 없음임을 명시.

[P2-7] docs/perf/PERF.md - 벤치마크 해석은 대체로 정직하나 몇 가지 보정
  근거(좋은 점): 환경·경합(다른 worktree 실행)·ShortRun 한계·HTTP와 미세 벤치 분리·closed-loop 한계·0.73%·"23배는 REST 가속이 아님"을 모두 적었다. 보정할 점: (1) 표에 `730,593.6`처럼 7자리를 쓰지만 ShortRun 3회에 오차(StdDev)를 싣지 않아 정밀해 보인다. (2) 측정 기준이 `0aed3ee`이고 HEAD의 MarketLoop 변경(저널 시도 카운터, 초기화)은 반영되지 않았다는 점이 머리에 있지만 결론의 "운영"과 구분이 더 필요하다. (3) NVMe 로컬 flush 839 ops/s는 Oracle 블록 볼륨에서는 다를 수 있어 서버 용량 산정에 쓰면 안 된다(문서 한계에 한 줄 추가). (4) "디스크 동기 flush가 우선 의심되는 병목"은 가설이며 큐 대기 구간 측정 전에는 확정이 아니다(이미 제안 항목으로 남아 있음).
  제안: 표를 3자리 유효숫자와 StdDev 열로 바꾸거나 "±수십 %" 문구를 표 바로 위에 둔다.

[P2-8] docs/tasks/TASK-007-PROGRESS.md:검증 - 테스트 개수가 현재와 다르다
  근거: "Bots 8"이라고 적었으나 현재 Bots 테스트는 14개(009S 회귀 테스트 합류). Engine 103은 일치. TASK-010-PROGRESS의 169개도 합류 이전 수치다. 추적표 위반은 아니나 통합 후 총합(현재 Engine 103 + Bots 14 + Journal 34 + Server ?)을 다시 적어야 한다.
  제안: 메인이 통합 후 전체 테스트 실행 결과로 개수를 갱신.

## 확인 결과 요약 (요청 항목별)

| 항목 | 결과 |
|---|---|
| deploy/ 비밀값 | 없음. 키는 서버 `.env`와 `${…:?}` 참조. 스크립트가 config/inspect를 출력하지 않음. 이미지 안에도 키 없음 |
| 앱 포트 호스트 노출 | 없음(`ports:` 없음, EXPOSE만). 컨테이너 내부 0.0.0.0:8080은 네트워크 내부 한정 |
| 골목장부 충돌 | Caddyfile 단일 파일 inode 주의는 문서에 있음. 네트워크 공유 위험은 P1 |
| 메모리 제한 | 192m와 GC 96MiB는 측정(최대 약 38MiB) 대비 여유. swap·로그·CPU 제한 부재는 P1 |
| 초기화 원자성 | 이름 변경 후 크래시는 안전, 이름 변경 실패는 P1(복구 경로 없음) |
| 초기화 중 주문 | 단일 큐 직렬이라 중간 상태 노출 없음. 초기화 전 접수된 응답은 폐기된다 |
| 주문 ID 재사용 | P1 |
| 봇 저빈도와 결정성 | 엔진 결정성은 영향 없음(저널이 정본). 강도 0.10에서 floor 안전, 일반값은 P2-1 |
| 실험 수치 | 불변, summary.json과 SUMMARY.md 일치 |
| 벤치마크 정직성 | 양호, 보정은 P2-7 |
