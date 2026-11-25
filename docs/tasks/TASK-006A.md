# TASK-006A: 화면 시제품 (목 데이터)

- 브랜치: `task/TASK-006A`
- 담당: 구현 에이전트 (Codex)
- 선행 문서: `AGENTS.md`, `docs/api/CONTRACT.md`, `docs/PLAN.md` 7장(시연 시나리오)

## 목표
서버 없이 **목 데이터만으로 시연 흐름 전체를 보여 주는 React 화면**을 만든다. 나중에(TASK-006B) 목 데이터 계층만 실제 서버 연결로 바꾸면 되도록, 화면 코드와 데이터 계층을 분리한다.

## 만들 것

### 1. 프로젝트
- `web/`에 Vite + React + TypeScript. 패키지 매니저 npm.
- 린트(ESLint), 테스트(Vitest + Testing Library).
- 차트: TradingView Lightweight Charts(Apache-2.0) 또는 동급 무료 라이브러리. 라이선스의 출처 표기 조건을 확인해 지키고, 보고에 적는다.
- 그 밖의 UI 라이브러리는 최소한으로. 추가한 의존성마다 이유를 보고에 적는다.

### 2. 데이터 계층
- `web/src/api/`에 계약(`docs/api/CONTRACT.md`)의 타입 정의.
- `MarketSource` 인터페이스: `subscribe(handler)`(bookSnapshot, trade, candle, viEvent 수신), `placeOrder`, `cancelOrder`, `getExplanation`, `getExperiment`, `runScenario`, `crash`, `getStateHash`.
- `MockMarketSource`: 시드 기반 결정적 목 시장. 같은 시드면 같은 흐름. `FatFinger` 시나리오에서 급락 → 동적 VI 발동(`phase: "CallAuction"`, `vi` 객체) → 2분 뒤(시연 배속 적용) 해제 흐름을 흉내 낸다. 가격은 계약 예시처럼 호가단위(이 가격대 10원)를 지킨다.
- 시연 배속: 목 시장 시간 배속 옵션(기본 10배, 임의 지정).
- 환경 변수 `VITE_USE_MOCK=true`일 때 목 사용. 실제 서버 구현체는 만들지 않는다.

### 3. 화면 (데스크톱 1280px 이상, 강의실 프로젝터용으로 글씨 크게)
1. **시장 화면**
   - 10단계 호가창(매도 위, 매수 아래, 잔량 막대), 현재가·상하한가·기준가 표시
   - 1분봉 차트 + 체결 목록
   - VI 배너: 진행 중이면 종류(동적/정적), 참조가격, 남은 시간("약 n초, 최대 30초 연장"), 규칙 ID 배지. 단일가 중에는 예상 체결가·수량
   - 주문창(매수/매도, 가격, 수량). 거부되면 사유와 규칙 ID 표시
   - 내 주문 목록. 주문을 누르면 **설명 패널**에 `explanation.message`와 대기 순번 표시
2. **비교 화면**: 같은 시드의 VI 켜짐/꺼짐 가격 그래프를 겹쳐서(또는 위아래로) 보여 주고, VI 구간을 음영으로 표시. 최저가 차이를 큰 숫자로 강조.
3. **관리자 패널**: 시나리오 버튼(Calm / FatFinger / PanicSell), "서버 강제 종료" 버튼, 상태 해시와 "재생으로 복구됨" 표시.

문구는 한국어. 금액은 `10,050원`처럼 천 단위 콤마.

### 4. 테스트
- 가격·수량·시각 포맷터 단위 테스트
- 목 시장 결정성: 같은 시드 두 번 → 같은 메시지 수열
- 목 시장이 만드는 모든 가격이 상하한가 안이고 호가단위를 지킴
- 시장 화면 렌더 테스트 1개(VI 배너가 `vi` 객체에 따라 나타남/사라짐)

## 하지 않을 것
- 서버, 엔진(C#) 코드 수정. `web/` 밖 파일 수정(이 지시서의 PROGRESS 파일 제외)
- 실제 서버 연결 구현체
- 로그인, 모바일 레이아웃

## 완료 조건 (직접 실행하고 결과를 보고)
이 PC 환경 함정은 `C:\Users\USER\dev\TEAM_HOGA.md` 참고.
1. `npm ci` (또는 첫 설치는 `npm install` 후 lockfile 커밋)
2. `npm run lint`, `npm run test -- --run`, `npm run build` 모두 통과. 테스트 수 보고
3. `npm run dev`로 띄워 시장 화면과 비교 화면이 뜨는지 확인(확인 방법과 결과 보고. 브라우저 확인을 못 하면 "미검증")
4. `git status`에 `node_modules/`, `dist/`, `.env`가 없음
5. `task/TASK-006A` 브랜치에 의미 단위 커밋 (push 하지 않음)

## 보고 형식
- 만든 파일 목록(요약), 추가한 의존성과 이유, 차트 라이브러리 라이선스 처리
- 완료 조건 각각의 실제 결과
- 계약이 모호했던 부분과 제안
