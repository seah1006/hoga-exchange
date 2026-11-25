# TASK-008: 봇·시나리오 서버 연결과 REVIEW-5 후속 (서버 008S, 화면 008W)

- 브랜치: `task/TASK-008S`(서버), `task/TASK-008W`(화면). 동시에 진행하고 메인이 둘 다 병합한 뒤 통합 확인한다.
- 담당: 구현 에이전트 (Codex) 2명
- 선행: TASK-004, TASK-005, TASK-006B, REVIEW-5 수정 병합 완료(main `ec5d5ff` 이후)
- 선행 문서: `AGENTS.md`, `docs/api/CONTRACT.md` **v0.3**(이 계약이 기준), `docs/reviews/REVIEW-5.md`, `docs/tasks/TASK-004.md` 5장, `docs/tasks/TASK-004-PROGRESS.md`, `docs/tasks/TASK-005-PROGRESS.md`, `docs/tasks/TASK-006B-PROGRESS.md`

## 008S 서버 (`src/Hoga.Server`, 필요하면 `src/Hoga.Bots`에 최소 공개 API)
1. **봇 시장** (TASK-004 5장): 서버 안에서 `Hoga.Bots` 봇이 처리 루프의 같은 큐로 주문을 넣는다(봇 주문도 저널 기록, 순번 부여). 설정 `Market:Bots`(기본 켜짐, 테스트는 끔). 봇 난수 시드는 설정값, 봇 이름별 독립 스트림(TASK-005 방식).
   - `POST /api/admin/scenario` `{ "name": "Calm" | "FatFinger" | "PanicSell" }` → `{ "ok": true }`, 모르는 이름은 400 `UnknownScenario`, 관리자 키 필요.
   - FatFinger·PanicSell은 한 번 실행 후 일정 논리 시간(임의 지정, 보고) 뒤 Calm으로 돌아간다.
2. **실험 조회**: `GET /api/experiments/{id}` 계약 v0.3의 ID 규칙. `demo`는 SUMMARY.md 규칙으로 정한 시드. 요청 시 계산하고 결과는 작은 개수만 캐시(상한 임의 지정, 보고). 형식 오류·범위 밖은 404.
3. **접속 직후 이력 재전송**(REVIEW-5 2번): 계약 v0.3 "접속 직후" 순서대로 새 연결에만 보낸다(그룹 전체가 아니라 그 연결에만).
4. **루프 견고성**(REVIEW-5 5번): 상태를 바꾸지 않는 요청(설명, 해시, 실험 조회)의 예외는 그 요청만 실패시키고 루프는 계속 돈다. 저널 기록 뒤 엔진·투영 예외는 지금처럼 정지(이유 주석 유지).
5. **메모리 상한**(REVIEW-5 6번): 봇이 계속 주문하므로 주문 투영을 무한히 쌓지 않는다. 종료(체결·취소·거부) 주문은 최근 N개(임의 지정)만 설명 조회용으로 보관하고, 그 밖은 404 대신 "기록 보관 기간이 지났습니다" 404 메시지. 엔진 내부(`_acceptedIds`)는 건드리지 말고 측정값만 보고.
   - **측정**: 봇 켠 상태 10배속으로 실제 30분 이상 돌려 프로세스 메모리(작업 집합)와 저널 크기, 재시작 복구 시간을 보고. 1GB 서버에서 하루 운영 가능한지 추정과 근거.
6. 테스트: 시나리오 401/400/200, 실험 `demo`·`exp-FatFinger-16`·잘못된 ID, 접속 직후 이력 순서, 읽기 요청 예외 후에도 주문 처리, 투영 상한, 봇 켠 상태 crash 전후 해시 일치.

## 008W 화면 (`web/`)
1. 비교 화면은 `demo`를 조회한다(목 구현체도 `demo`를 받도록, 목 기존 테스트 유지).
2. 새 연결이 열리면 체결·봉 목록을 비우고 서버가 다시 보내는 이력으로 채운다(계약 v0.3). 첫 `bookSnapshot` 수신 전까지는 "동기화 중" 표시.
3. 내 주문 목록의 상태 동기화(REVIEW-5 3번): 체결·취소·거부된 주문은 취소 버튼을 숨기고 상태를 표시한다. 방법(설명 조회 폴링 또는 `trade`/`bookSnapshot` 기반 갱신)은 선택하고 이유를 보고. 서버 부하를 고려해 폴링이면 간격과 대상 제한.
4. 수량 입력은 1~1,000,000 정수만 허용(계약 v0.3 서버 한도와 같은 값, 화면 상수로 두고 출처 주석).
5. 테스트: 재연결 시 목록 초기화, 체결 완료 주문 취소 버튼 숨김, 수량 경계, 목 `demo` 조회.

## 하지 않을 것
- 엔진 규칙 변경, 배포(TASK-007), 저널 스냅샷·헤더 옵션(TASK-003B), 계약 변경(필요하면 제안)

## 완료 조건
- 008S: `dotnet build HogaExchange.slnx -warnaserror`, `dotnet test HogaExchange.slnx` 통과(테스트 수), 5번 측정 결과, curl로 시나리오 전환과 `GET /api/experiments/demo` 결과 요약
- 008W: `npm run lint`, `npm run test -- --run`, `npm run build` 통과(테스트 수)
- 각자 `docs/tasks/TASK-008S-PROGRESS.md` / `TASK-008W-PROGRESS.md` 기록, 작업 브랜치에 의미 단위 커밋(push 하지 않음)
