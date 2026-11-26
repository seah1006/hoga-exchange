# TASK-012 진행 기록

## 인계 및 구현

- `task/TASK-012`의 미검증 WIP `e854b57` 위에서 이어받았다. 서버 TASK-011은 병합하지 않고 목 구현체와 계약 v0.5 픽스처를 사용했다. 서버·계약·의존성 변경 없음.
- `App.tsx`, `Members.tsx`, `DemoPanels.tsx`, `styles.css`: 헤더 로그인·가입 대화상자, 비밀번호 표시/숨김, 닉네임 메뉴와 탈퇴 재확인, 비로그인 주문 제한, Admin 전용 관리자 패널과 접힌 운영 키 입력, 공개 랭킹 탭, 개인정보 안내. 오류·빈 상태·로딩 안내와 재시도를 포함한다. 비밀번호 입력은 전송 직전에 화면에서 비우고, 인증 HTTP 오류의 서버 원문은 전달하지 않는다.
- `MemberOrders.tsx`(기존 `Orders.tsx` 대체): 주문 가능/묶인 현금·주식, 평가액·손익·수익률, `floor(주문 가능 현금 / 주문 가격)`과 매도 가능 수량을 표시한다. 초과 주문은 화면에서 막고 서버 거부도 한국어로 안내한다. `GET /api/account/orders`·`trades`를 목록의 원본으로 쓰고, `accountUpdate`는 계좌에 즉시 반영하고 목록 재조회를 요청한다. 주문·취소 직후, 연결 상태 변경, `marketReset` 때도 재조회한다. 미체결 잔량이 있는 주문만 취소할 수 있으며 설명 패널은 유지했다.
- `api/contract.ts`, `api/marketSource.ts`, `api/serverMarketSource.ts`, `main.tsx`: 계약 v0.5 자료형과 회원·계좌·랭킹 REST, 쿠키 포함 요청, 앱 시작·로그인 등 세션 변경 후 CSRF 발급. POST/DELETE에 토큰을 붙이고 HTTP 400 `BadCsrfToken`에서만 한 번 다시 발급해 재시도한다. 로그인·가입·로그아웃·탈퇴 후 허브를 새 세션으로 다시 연결한다.
- `api/mockMemberSource.ts`: 새로고침하면 사라지는 목 회원·계좌·랭킹 및 라운드 보관. 체결은 현재가 기준의 단순 목 시뮬레이션이며 실제 매칭 엔진이나 생산 인증이 아니다. `Members.test.tsx`, `App.test.tsx`, `ServerApp.test.tsx`, `api/memberApi.test.ts`, `api/serverMarketSource.test.ts`, `api/fixtures/contract-v05.json`으로 새 흐름과 기존 회귀를 검증했다.

## 임의 지정

- 앱의 목록 동시 조회는 최근 `accountUpdate`를 200ms로 묶는다. 현재 랭킹 10초 갱신은 ADR의 **임의 지정** 주기를 따르고 지난 랭킹은 선택 시 조회한다. CSS 1050px·640px 분기, 색상과 여백, 목 라운드 날짜·랭킹 예시는 **임의 지정**. 시작 자산 및 인증 입력 제한은 ADR/계약이 지정한 값 그대로다.

## 검증

- Node v26.5.0 / PowerShell 7. `web/`에서 지정 환경 변수 설정 후 `npm ci`로 291개 설치했다(동일 잠금 파일). `npm run lint`: 오류·경고 0. `npm run test -- --run`: **8파일, 71개 통과**(기준 58 + 13). `npm run build`: TypeScript 검사 및 Vite 76모듈 번들 성공. 기존 SignalR `PURE` 주석에 대한 Rollup 경고 2개.
- 목 개발 서버 `127.0.0.1:5175`: 인덱스와 앱 엔트리 HTTP 200 확인, 서버 종료 후 해당 포트 리스너 없음 확인. 1440px·390px 각각 jsdom에서 로그인 대화상자, 주문·체결·계좌·랭킹 및 지난 라운드 흐름을 확인했다. **육안 미검증**: jsdom은 실제 CSS 레이아웃이나 캔버스를 검증하지 않는다. **실제 TASK-011 서버 연결 미검증**: 쿠키/SignalR 전달과 배포 환경을 포함한다. push 없음.

## 제안

- 계약 v0.5 계좌 예시의 `valuation`, `pnl`, `returnBp`가 해당 예시의 현금·주식·최근가로 계산한 값과 일치하지 않는다. 추후 문서 예시를 정정하고 서버와 공유할 픽스처를 맞추는 것을 제안한다. 예시·테스트 갱신이 필요하지만 API 필드 변경은 필요 없다.
- 통합 시 체결 외 주문 상태 변동(예: 단일가 대기 진입)도 `accountUpdate` 알림으로 목록 재조회를 일으키는지 확인한다. 보장이 없다면 전용 내 주문 변경 알림을 후속 계약으로 검토한다. 단점은 발행·소유권 검증 및 재연결 테스트 범위가 늘어난다는 점이다.
