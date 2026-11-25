# TASK-006A 진행 기록

## 1단계 완료
- Vite·React·TypeScript, ESLint, Vitest(jsdom), Testing Library, 계약 타입과 한국어 포맷터를 추가.
- `npm install --no-audit --no-fund --fetch-retries=0`: 272 패키지 설치. npm 11의 esbuild 스크립트 승인 안내와 ESLint 구버전 안내가 있었지만 빌드에는 영향 없음.
- `npm run lint`, `npm run test -- --run`(3개), `npm run build` 통과.
- 다음: 결정적 목 시장과 가격 규칙 테스트, 화면, 완료 조건 재검증.
- 환경: pwsh와 외부 프로세스 cwd를 모두 web으로 설정. `npm_config_script_shell`에 pwsh 경로 지정; Node 26에서는 `NODE_OPTIONS=--no-experimental-webstorage`로 jsdom 충돌 방지. npm 캐시는 작업 범위를 지키도록 무시되는 `web/.npm-cache/` 사용.

## 2단계 완료
- `MarketSource`와 결정적 목 시세, 주문 처리 및 설명, VI 비교 실험, 인메모리 저널 재생을 추가.
- `npm run lint`, `npm run test -- --run`(2파일·7개), `npm run build` 통과.
- 다음: 시장·주문 화면, 비교·관리자 화면, 최종 검증. 현재 데이터 계층은 화면에 연결되기 전이다.

## 3단계 완료
- 시장 화면에 시세 요약, 10단계 호가창, 1분봉, 체결 목록, VI 배너, 주문·취소·설명 패널을 연결했다.
- Lightweight Charts 5.0.9(Apache-2.0): 작성자 고지와 TradingView 링크를 화면에, LICENSE/NOTICE를 배포 파일에 포함했다.
- `npm run lint`, `npm run test -- --run`(7개), `npm run build` 통과.
- 다음: 비교·관리자 화면, VI 렌더 테스트, 브라우저 확인과 설치/빌드 최종 확인.

## 4단계 완료
- 비교 화면에 같은 시드의 두 가격 궤적, VI 단일가 구간 음영, 최저가 차이를 추가했다. 관리자 패널은 시나리오 실행, 강제 종료/저널 복구, 전후 해시 비교를 지원한다.
- `npm ci --no-audit --no-fund`: 274 패키지 설치. `npm run lint`, `npm run test -- --run`(3파일·8개, VI 표시/해제 시장 화면 렌더 포함), `npm run build`(41 모듈 변환) 통과.
- 다음: 로컬 개발 서버 및 실제 브라우저 표시 확인, Git 작업 범위 최종 확인.

## 5단계 완료 조건 확인
1. `npm ci --no-audit --no-fund`: 274 패키지 설치, 종료 0.
2. `npm run lint`: 경고·오류 없이 통과. `npm run test -- --run`: 3파일·9개 통과(포맷 3, 목 4, 렌더 2). `npm run build`: 타입 검사와 41 모듈 번들 생성 통과.
3. `VITE_USE_MOCK=true`로 `npm run dev`: Vite 6.3.5, `http://127.0.0.1:5173/` 및 `/src/App.tsx` HTTP 200. jsdom으로 시장·비교 전환, 주문 거부 배지, 해시 복구 표시를 확인했다. 실제 브라우저의 캔버스와 프로젝터 화면은 이 구현 세션에서 미검증이며 메인 에이전트에 확인을 요청했다.
4. `git status --short --branch`와 `git check-ignore`: `web/node_modules/`, `web/dist/`, `web/.env`가 상태 목록에 없고 제외 규칙에 포함된다.
5. `task/TASK-006A` 단계별 의미 단위 커밋. 수정 범위는 web 및 이 파일. push 없음.
- 남은 것: 실제 브라우저 시각 확인, npm 11의 esbuild 스크립트 안내와 ESLint 9.28 지원 종료 경고의 후속 정리(이번 실행에는 영향 없음).

## REVIEW-2 수정 완료
- 비교 화면은 배경 위에 직접 그리는 짙은 노란색 `#81702a`(임의 지정)를 VI 단일가 구간과 범례 칩에 함께 사용한다. 패널 배경 `#101d32` 대비는 3.44:1이다.
- 복구 직후에만 `restoredFromJournal`을 노출한다. 다음 저널 이벤트에서 해제하며, 주문 접수·취소도 새 순번의 스냅샷을 발행해 관리자 패널의 이전 배지와 전후 해시 비교를 숨긴다. 거부는 복구 상태를 바꾸지 않는다.
- 상·하한가 위반, 호가단위 위반, 수량 0·음수·소수, 이미 취소된 주문의 재취소를 검증한다. 거부 전후 호가 스냅샷·주문 설명·해시가 같고, 화면의 호가창·내 주문도 유지되는 것을 확인했다.
- VI 배너의 예상 체결가·수량에 `목 데이터 예시값`을 붙였다. 목 값은 호가창과 연동되지 않으며 TASK-006B에서 실제 서버 값으로 대체한다.
- Node 26.5.0 환경에서 외부 프로세스 cwd를 `web/`으로 맞추고 `npm_config_script_shell`에 pwsh를 지정했다. 내장 localStorage와 jsdom의 충돌은 `NODE_OPTIONS=--no-experimental-webstorage`로 피했다. `npm run lint`, `npm run test -- --run`(3파일·17개), `npm run build`(41 모듈) 모두 통과했다.
- `VITE_USE_MOCK=true` 개발 서버에서 `/`, `/src/App.tsx`, `/src/DemoPanels.tsx`, `/src/styles.css` 응답이 모두 HTTP 200이었다. jsdom 렌더 테스트로 비교 그래프와 범례 칩, 복구 배지 해제, VI 안내를 확인했다. 실제 브라우저에서 수정 후 색을 눈으로 확인하는 작업은 미검증이다.
- 다음: 메인 에이전트의 브라우저 시각 확인 및 검증. 의존성·계약 변경 없음, push 없음.

## 제안
- `triggerPrice`를 그리드에 맞는 잠정 체결가로 해석. `logicalTimeMs`는 거래소 논리 시각이며 2분 VI는 기본 10배속에서 실제 12초. 실시간 연장 여부 필드는 계약에 없어 목은 연장하지 않는다.
- `exp-1`은 FatFinger 고정 비교 실험. 알 수 없는 주문/실험 조회의 불일치 예외는 목에서 `null`로 처리하며 006B에서 HTTP 404 매핑을 확정할 것을 제안한다.
- UI 시연용 가격 경로·예상 체결 수량·시드 42·복구 해시 알고리즘은 목에서 임의 지정. 규칙 엔진이나 영구 저널을 대체하지 않는다.
- 계약 문서 변경 없음. 화면/목에서 해석이 필요한 항목은 구현 후 이 섹션에 추가.
