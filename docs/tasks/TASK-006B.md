# TASK-006B: 화면을 실제 서버에 연결

- 브랜치: `task/TASK-006B`
- 담당: 구현 에이전트 (Codex)
- 선행: TASK-006A 병합 완료. **TASK-004(서버)와 동시에 진행한다.** 둘 다 `docs/api/CONTRACT.md` v0.2를 기준으로 하고, 통합 확인은 둘 다 병합된 뒤 메인이 한다.
- 선행 문서: `AGENTS.md`, `docs/api/CONTRACT.md`, `docs/tasks/TASK-006A.md`, `docs/tasks/TASK-006A-PROGRESS.md`, `docs/reviews/REVIEW-2.md`(4, 5, 7번이 이 작업으로 넘어옴)

## 목표
`MarketSource`의 실제 서버 구현체를 만들어 `VITE_USE_MOCK=false`일 때 서버(REST + SignalR)에 붙는다. 목 구현체는 그대로 유지한다(서버 없이 시연할 수 있는 백업).

## 만들 것
1. `ServerMarketSource`: REST는 `fetch`, 실시간은 `@microsoft/signalr`. 서버 주소는 `VITE_API_BASE`(기본: 같은 출처). 개발 서버는 Vite 프록시로 `/api`, `/hubs`를 5280으로 넘긴다.
2. 계약 v0.2의 실패 응답(400/401/404) 처리. 주문 거부(`accepted:false`)는 에러가 아니라 화면 메시지.
3. 재연결: SignalR 자동 재연결, 끊김·재연결 상태 표시, 재연결 후 최신 `bookSnapshot`으로 화면 갱신.
4. REVIEW-2에서 넘어온 것:
   - 4번: VI 배너의 예상 체결가·수량은 서버 `bookSnapshot.expectedPrice/expectedQuantity`를 쓰고, 서버 모드에서는 "목 데이터 예시값" 표기를 없앤다.
   - 5번: 설명 패널은 서버 `explanation`을 그대로 표시.
   - 7번: VI 남은 시간 표시 기준을 정한다(서버가 주는 논리 시각과 배속 기준). 선택과 이유를 보고.
5. 관리자 버튼은 관리자 키를 화면 입력값(세션 저장소)으로 받아 헤더에 붙인다. 키를 코드·빌드 산출물에 넣지 않는다.

## 테스트
- `ServerMarketSource`를 가짜 fetch와 가짜 허브로 단위 테스트(계약 예시 JSON을 픽스처로 사용): 메시지 파싱, 실패 응답, 재연결 후 갱신
- 목 모드 기존 테스트 전부 유지

## 하지 않을 것
- 서버 코드, 계약 변경(필요하면 제안)

## 완료 조건
1. `npm run lint`, `npm run test -- --run`, `npm run build` 통과(테스트 수)
2. `VITE_USE_MOCK=true` 화면이 그대로 동작(메인이 브라우저로 확인)
3. 서버가 아직 없으면 실제 연결 확인은 "미검증"으로 보고. 서버가 병합돼 있으면 `dotnet run --project src/Hoga.Server`와 함께 띄워 확인
4. `task/TASK-006B` 브랜치에 의미 단위 커밋 (push 하지 않음)
