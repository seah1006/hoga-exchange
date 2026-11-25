# TASK-004: ASP.NET Core 서버 (주문 API, SignalR 시세 배포, 엔진 호스팅)

- 브랜치: `task/TASK-004`
- 담당: 구현 에이전트 (Codex)
- 선행: **TASK-001, TASK-002, TASK-003 병합 후 시작**. 봇·시나리오 연결(5장)은 TASK-005 병합 후 같은 브랜치에서 이어서 한다.
- 선행 문서: `AGENTS.md`, `docs/api/CONTRACT.md`(이 계약이 기준), `docs/adr/0001`, `docs/tasks/TASK-003.md`

## 목표
엔진을 하나의 전용 처리 루프에서 돌리는 서버를 만들고, 계약(CONTRACT.md)대로 REST와 SignalR을 제공한다. 화면(TASK-006B)이 목 데이터 대신 이 서버에 붙을 수 있어야 한다.

## 만들 것

### 1. 프로젝트
- `src/Hoga.Server` (ASP.NET Core, `Hoga.Engine`·`Hoga.Journal` 참조)
- `tests/Hoga.Server.Tests` (xUnit, `Microsoft.AspNetCore.Mvc.Testing`)
- 포트 5280(임의 지정, AGENTS.md). 설정은 `appsettings.json`과 환경 변수.

### 2. 엔진 호스팅 (ADR 0001)
- 종목 하나당 `BackgroundService` 하나가 `System.Threading.Channels`의 명령 큐를 순서대로 처리한다. 엔진 객체는 이 루프 밖에서 만지지 않는다.
- 명령 처리 순서: 순번 부여 → 저널 기록 → 엔진 처리 → 이벤트 배포. REST 요청은 명령을 큐에 넣고 결과 이벤트를 기다려 응답한다(타임아웃과 에러 응답 정의).
- 시각: 서버가 실제 경과 시간을 논리 시각으로 바꿔 명령에 붙인다. 시연 배속 옵션(기본 1배, 시연 시 10배 등)을 둔다. 주기적으로 `AdvanceTime`을 넣어 단일가 종료가 늦지 않게 한다.
- 시작 시 `Recovery.Restore`로 저널에서 복구하고 `restoredFromJournal`을 기록한다.

### 3. REST (CONTRACT.md 2장)
- `POST /api/orders`, `DELETE /api/orders/{id}`, `GET /api/orders/{id}/explanation`, `GET /api/state/hash`
- `POST /api/admin/crash`: 처리 루프를 멈추고 엔진을 버린 뒤 저널에서 다시 복구한다(프로세스 종료 대신 "재시작 흉내"). 복구 전후 해시를 로그에 남긴다.
- 관리자 API는 설정의 관리자 키 헤더가 있어야 동작한다(키는 `.env`/환경 변수, 저장소에 넣지 않음).
- `explanation.message`는 한국어 문장을 서버가 만든다(대기 순번, 단일가 대기와 예상 종료 시각, 거부 사유와 규칙 ID).

### 4. SignalR (`/hubs/market`)
- `bookSnapshot`(초당 최대 10회로 묶음), `trade`, `candle`(1분봉 집계), `viEvent`. 필드는 계약 그대로.
- 접속 직후 최신 `bookSnapshot` 한 번을 보낸다.

### 5. 봇·시나리오 연결 (TASK-005 병합 후)
- 서버 안에서 봇 시장을 돌리는 옵션. `POST /api/admin/scenario`로 Calm/FatFinger/PanicSell 전환.
- `GET /api/experiments/{id}`: TASK-005 실행기로 만든 실험 JSON을 돌려준다(미리 계산해 파일로 두거나 요청 시 계산, 선택과 이유를 보고).

### 6. 테스트
- 주문 접수·거부(상하한가, 호가단위)가 계약 형식대로 응답
- 같은 주문 흐름 → `crash` → 복구 후 상태 해시가 같음
- SignalR 클라이언트로 접속해 `bookSnapshot`을 받음
- 관리자 키 없이 관리자 API 호출 시 401

## 하지 않을 것
- 엔진 규칙 변경, 화면, 배포 스크립트(TASK-007), 로그인

## 완료 조건
1. `dotnet build HogaExchange.slnx -warnaserror`, `dotnet test HogaExchange.slnx` 통과(테스트 수 보고)
2. `dotnet run --project src/Hoga.Server` 후 curl로 주문 1건, 상태 해시 조회, crash 후 해시 비교 결과
3. `git status`에 `.env`, `data/` 없음
4. `task/TASK-004` 브랜치에 의미 단위 커밋 (push 하지 않음)
