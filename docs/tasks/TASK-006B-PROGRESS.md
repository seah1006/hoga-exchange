# TASK-006B 진행 기록

## 1단계 완료
- 지시서, CONTRACT v0.2, REVIEW-2 4·5·7번 및 TASK-004의 초기 스냅샷 요구를 확인했다. `MockMarketSource`와 기존 테스트 17개, 계약 문서는 수정하지 않았다.
- Windows PowerShell 7/Node 26.5.0에서 web 작업 디렉터리, `npm_config_script_shell`, `NODE_OPTIONS=--no-experimental-webstorage`, 작업 디렉터리 안의 `.npm-cache/`를 적용했다. 초기 `npm ci --no-audit --no-fund --fetch-retries=0`은 274개 패키지 설치, 종료 0. esbuild 설치 스크립트 승인 안내와 기존 의존성의 지원 중단 안내가 있었지만 실행에는 영향 없었다.

## 2단계 완료
- `VITE_USE_MOCK=false`에서 `ServerMarketSource`를 선택한다. 서버 주소 `VITE_API_BASE`가 비어 있으면 같은 출처를 쓰고, 개발 환경의 Vite는 `/api`와 `/hubs`(WebSocket 포함)를 `127.0.0.1:5280`(기존 임의 지정 포트)으로 넘긴다. 직접 추가한 의존성은 .NET 10과 주 버전을 맞춘 `@microsoft/signalr` 10.0.11뿐이다.
- REST는 `fetch`로 주문·취소·설명·실험·관리자 동작·상태 해시를 호출한다. HTTP 200의 `accepted:false`는 예외로 던지지 않고 주문 거부 사유와 규칙 배지를 표시한다. 설명·실험 조회 404는 기존 인터페이스의 `null`로 변환하고, 나머지 실패는 상태·코드·메시지를 담은 예외로 표시한다. 네트워크 및 JSON 읽기 실패도 처리한다.
- SignalR은 `bookSnapshot`, `trade`, `candle`, `viEvent`를 전달한다. 자동 재연결과 별도로 첫 접속 실패 또는 재연결 시도 소진 뒤에는 5초마다(임의 지정) 다시 접속한다. 재연결을 시작하면 마지막 시세임을 표시하고 매 새 연결에서 서버가 보내는 최신 `bookSnapshot`을 받은 뒤 연결됨으로 표시한다. 마지막 구독을 해제하면 재시도를 취소하고 연결을 닫는다. 계약에 체결·캔들 이력 API는 없어 연결이 끊긴 동안의 누락 이력은 복원하지 않는다.
- 관리자 키는 서버 모드의 비밀번호 입력란에서만 받으며, 브라우저 `sessionStorage`에만 저장한다(이름 `hoga.adminKey`, 임의 지정). 관리자 POST에만 `X-Admin-Key` 헤더(임의 지정, TASK-004와 조율)를 보낸다. 코드·빌드·테스트에 실제 키처럼 보이는 값은 없다.

## 3단계 완료: REVIEW-2
- 4번: 서버 모드는 `bookSnapshot.expectedPrice`/`expectedQuantity`를 그대로 사용하고 값이 비면 `미정`이라고 표시한다. `목 데이터 예시값` 배지는 목 모드에만 남겼다.
- 5번: `explanation.message`를 그대로 표시한다. 대기 순번은 서버가 내려준 `queueAhead.orders + 1`, 규칙 배지는 `ruleId`를 사용한다. 이전 요청이 진행 중일 때 새 스냅샷이 오면 후속 조회를 하나로 합쳐, 응답이 늦어도 설명을 표시한다.
- 7번: `ceil(max(0, scheduledEndMs - logicalTimeMs) / 1000)`을 거래소 논리 초로 표시한다. 서버 배속이 응답에 없고 바뀌거나 멈출 수 있으므로 브라우저 실제 초를 추정하지 않는다. 스냅샷을 받을 때만 갱신하며, 예정 마감이 지났더라도 서버가 VI 해제를 알리기 전에는 배너를 숨기지 않는다. 목 모드 문구와 배속 표시는 기존 그대로 유지했다.

## 4단계 완료 조건 확인
1. `npm run lint`: 경고·오류 없이 종료 0. `npm run test -- --run`: 5파일 32개 통과(기존 17개, 서버 소스 12개, 서버 모드 렌더 3개). `npm run build`: 타입 검사 및 70모듈 번들 성공, 종료 0. SignalR 10.0.11 내부 주석 2개를 Rollup이 제거한다는 경고만 있었다(빌드에는 영향 없음).
2. `VITE_USE_MOCK=true`로 `npm run dev`: `http://127.0.0.1:5173/`에서 `/`, `/src/main.tsx`, `/src/App.tsx`, `/src/styles.css` 모두 HTTP 200. 기존 시장/비교 렌더 테스트도 통과. 실제 브라우저 시각 확인은 메인 담당으로 남겼다.
3. 실제 TASK-004 서버 통합은 미검증. REST 계약 예시 JSON, 가짜 fetch/허브, 연결 상태와 재연결 후 최신 시세, 관리자 키 전송 범위를 자동 테스트로 확인했다.
4. `task/TASK-006B`에 의미 단위 커밋. 변경은 `web/`과 이 기록에 한정했다. `node_modules/`, `dist/`, `.env`, `.npm-cache/`는 Git 제외 목록에 있다. push 없음.

## 제안
- 계약 다음 버전에 관리자 헤더명 `X-Admin-Key`, 새 연결(재연결 포함) 직후 최신 `bookSnapshot` 전송을 명시한다. 이번에 계약 문서는 바꾸지 않았다.
- 실제 초 표시가 필요해지면 서버 배속과 시계 정지 여부를 계약에 추가하는 방안을 검토한다. UI가 실제 시간을 추정하는 대신 정확한 기준을 얻지만 서버·화면의 계약 변경이 필요하다.
