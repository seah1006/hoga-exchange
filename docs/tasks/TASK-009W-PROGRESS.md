# TASK-009W 진행 기록

## 구현

- `web/src/Experiments.tsx`, `ExperimentCharts.tsx`, `Experiments.css`: 실험 화면에 평온·팻 핑거·패닉 매도 탭, 5개 요약 카드, 시드별 Δ SVG 산점도(0 기준선, 시연 다이아몬드, 선택 테두리), 시드 상세 및 계약의 `caveat` 원문을 배치했다. 포인터는 그래프의 시드 열을 클릭하고, 키보드는 탭 방향키와 시드 선택 목록을 쓸 수 있다.
- 처음에는 팻 핑거 `demo`를 별도로 조회한다. 시드를 선택하면 `exp-{scenario}-{seed}` 상세를 새로 요청하고 늦게 도착한 이전 응답은 무시한다. **임의 지정:** 다른 시나리오에서는 시드를 직접 고를 때까지 상세를 비우고, 시연 탭으로 돌아오면 `demo`를 다시 보여 준다. 요약 503은 다시 시도할 수 있으며, `demo`에는 영향을 주지 않는다. 상세 404는 찾을 수 없음으로 표시한다.
- 상세 선에는 1초 종가만, 극값 점에는 각 run의 `minPrice`·`maxPrice`와 `minAtMs`·`maxAtMs`를 사용한다. 차이는 절댓값이 아니라 부호 있는 Δ를 표시한다. **임의 지정:** 발표용 팔레트, 글자 크기, 축 눈금·여백, 점의 크기·모양과 클릭 영역. 추가 라이브러리는 없다.
- `web/src/api/contract.ts`, `marketSource.ts`, `serverMarketSource.ts`: 계약 v0.4 타입과 `GET /api/experiments/summary` 호출을 추가했다. 허브와 다른 REST 경로는 수정하지 않았다. `App.tsx`는 탐색 버튼과 실험 컴포넌트 연결만 변경했고 `DemoPanels.tsx`의 관리자 기능은 유지했다.
- `web/src/api/mockExperiments.ts`, `mockMarketSource.ts`: 요약 집계와 한계 문구는 `SUMMARY.md`를 그대로 반영했다. 개별 시드 값과 상세 시세·극값 시각·VI 구간은 **임의 지정한 목 데이터**이며 화면에 표시한다. 팻 핑거 시연 시드 16과 Δ 2,470원, 두 최저가 9,490원·7,020원은 `SUMMARY.md`의 값이다. 기존 목 `exp-1` 호환 경로는 유지했다.

## 검증

- Windows PowerShell 7 / Node v26.5.0. `web/`에서 지정된 `npm_config_script_shell`, `NODE_OPTIONS`를 설정했으며 캐시는 작업 폴더 `web/.npm-cache/`를 사용했다. `npm ci`: 291개 설치.
- `npm run lint`: 오류·경고 0. `npm run test -- --run`: **6파일, 54개 통과**(기존 48 + 신규 6). `npm run build`: TypeScript 검사 및 Vite 74모듈 번들 성공. 기존 SignalR의 `PURE` 주석에 대한 Rollup 경고 2개만 발생했다.
- 테스트에서 시나리오 탭 및 키보드 전환, 100개 시드와 시연 강조, 서버 계약 경로와 시드 재조회, ms 기반 극값 SVG 좌표, 404·503와 요약 재시도, 늦은 응답 무시, 300개 목 시드와 집계의 일치를 확인했다.
- **미검증:** TASK-009S 실제 서버 연동과 1280~1920px 브라우저 육안 확인. 개발 서버는 띄우지 않았으며 5173 리스너가 없는 상태를 확인했다. 계약, 엔진, 봇, 의존성 변경 없음. push 없음.

## 제안

- 계약 변경 제안 없음. 009S 통합 후 `demo`의 7,020원 점과 1초 종가 선이 분리되는지, 요약 파일 누락 시 503에서도 상세가 유지되는지 실제 브라우저에서 확인한다.
