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

## 제안
- `triggerPrice`를 그리드에 맞는 잠정 체결가로 해석. `logicalTimeMs`는 거래소 논리 시각이며 2분 VI는 기본 10배속에서 실제 12초. 실시간 연장 여부 필드는 계약에 없어 목은 연장하지 않는다.
- `exp-1`은 FatFinger 고정 비교 실험. 알 수 없는 주문/실험 조회의 불일치 예외는 목에서 `null`로 처리하며 006B에서 HTTP 404 매핑을 확정할 것을 제안한다.
- UI 시연용 가격 경로·예상 체결 수량·시드 42·복구 해시 알고리즘은 목에서 임의 지정. 규칙 엔진이나 영구 저널을 대체하지 않는다.
- 계약 문서 변경 없음. 화면/목에서 해석이 필요한 항목은 구현 후 이 섹션에 추가.

