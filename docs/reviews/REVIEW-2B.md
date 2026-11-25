# 코드 리뷰 보고서 (REVIEW-2B)

- 대상 저장소: `C:\Users\USER\dev\hoga-006a` (브랜치 `task/TASK-006A`)
- 검토 범위: `6f91afd3b338689324d9fc5f8315340fd0b6cf0b..16e46e28447c3b563f7154b7eff3287a7ad7f92c`
- 검증 실행 결과 (`web/`): `npm run lint` 통과, `npm run test -- --run` 통과 (3개 파일, 9개 테스트), `npm run build` 통과
- 등급별 건수: HIGH 0건, MEDIUM 0건, LOW 4건

## 발견 사항

[LOW] web/src/App.tsx:16 - 캔들 배열 갱신 시 시간순 정렬을 보장하지 않음
  근거: `setCandles((old) => [...old.filter((bar) => bar.startMs !== message.data.startMs).slice(-59), message.data])`는 기존 `startMs`를 제거한 뒤 무조건 배열 끝에 새 캔들을 붙인다. 현재 `MockMarketSource`는 항상 오름차순 `startMs`만 발행하므로 문제가 드러나지 않지만, 재연결이나 비순차 메시지로 과거 분봉이 갱신되면 `CandleChart.tsx:23`의 `bars.setData(...)`가 오름차순 위반으로 예외를 던질 수 있다.
  제안: `startMs` 기준 오름차순으로 정렬한 뒤 마지막 60개만 유지하도록 `.sort((a, b) => a.startMs - b.startMs).slice(-60)` 형태로 정리한다.

[LOW] web/src/DemoPanels.tsx:22 - 비교 실험 부제목에 시나리오명이 하드코딩되어 있음
  근거: `Comparison` 컴포넌트가 `data.scenario`를 사용하지 않고 `<p className="muted">같은 시드 {data.seed} · 팻 핑거 · VI만 켜고 끈 결과</p>`로 `팻 핑거`를 고정 표기한다. 같은 파일 36행에 이미 `names: Record<Scenario, string>` 매핑이 존재한다.
  제안: `names` 매핑을 파일 상단으로 올려 `Comparison`에서도 `names[data.scenario]`를 출력한다.

[LOW] web/src/DemoPanels.tsx:41 - 관리자 패널의 복구 전후 해시 비교가 비동기 서버 환경에서 타이밍에 민감함
  근거: `restore()`가 `getStateHash()`, `crash()`, `getStateHash()`를 순차 호출한다. `MockMarketSource`에서는 마이크로태스크로 연속 실행되어 `setInterval` 틱이 끼어들지 않지만, 실제 HTTP 기반 `MarketSource`로 교체하면 두 호출 사이에 시세 틱이 진행되어 복구가 정상이어도 `beforeHash !== hash.stateHash`로 `불일치`가 표시될 수 있다. 또한 `result.ok`가 `false`여도 `setHash(after)`가 함께 출력된다.
  제안: `result.ok`가 `false`일 때는 해시 비교 결과를 갱신하지 않도록 분기하고, 필요 시 서버 `crash` 응답이나 `getStateHash` 계약에서 복구 기준 `seq`를 함께 확인하도록 보완한다.

[LOW] web/src/MarketPanels.tsx:22 - 매도 또는 매수 호가가 비어 있을 때 빈 상태 안내가 없음
  근거: `TradeList`는 `첫 체결 대기 중`, `Orders`는 `접수된 주문이 없습니다.`를 표시하지만, `OrderBook`은 `book.asks`나 `book.bids`가 빈 배열일 때 `매도`/`매수` 구분 행 아래에 아무 행도 표시하지 않는다. 상하한가 도달 등으로 한쪽 호가가 비는 상황에서 빈 영역만 남는다.
  제안: `book.asks.length === 0` 또는 `book.bids.length === 0`일 때 `<tr><td colSpan={2} className="muted">호가 없음</td></tr>` 같은 안내 행을 추가한다.
