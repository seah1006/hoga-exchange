# REVIEW-2A: TASK-006A 1단계(규칙 기반) 리뷰

- 범위: `6f91afd..16e46e2` (브랜치 task/TASK-006A, 커밋 5개, web/ 25개 파일 + PROGRESS 1개)
- 등급별 개수: **P0 0건 / P1 5건 / P2 7건**
- 직접 실행: `npm run lint` 통과(경고 0), `npm run test -- --run` 3파일 9개 통과, `npm run build` 통과(41 모듈). 코드는 수정하지 않았다.
- 확인한 것: 비밀값 없음, lockfile resolved 호스트는 registry.npmjs.org 하나, `.env`/`node_modules`/`dist`/`.npm-cache` 미추적(`.env.example`만 추적), web/ 밖 변경은 `docs/tasks/TASK-006A-PROGRESS.md` 하나뿐, 의존성은 런타임 3개(lightweight-charts, react, react-dom)이며 버전 고정.
- 브라우저로 화면을 직접 보지는 않았다(범례 색 건은 코드와 색 계산으로 확인).

## P1

[P1] web/src/DemoPanels.tsx:24,26 - 비교 화면 범례는 "노란 음영"인데 실제 음영은 회색으로 보인다
  근거: 음영 `<rect fill="#f4c371" opacity=".18">`를 패널 배경 #101d32 위에 겹치면 합성색이 약 RGB(57,59,61)이다. 채도가 거의 없는 짙은 회색이라 노랗게 보이지 않는다. 메인이 브라우저에서 본 결과와 같다. 범례 글자는 #ffe0a9(노란색)라 음영과 어긋난다.
  제안: opacity를 .4~.5로 올리고(합성색 약 RGB(110,100,77) 이상) 가능하면 음영 위쪽에 "단일가매매" 라벨을 직접 붙인다. 또는 범례에 음영과 같은 색 칩(작은 사각형)을 넣어 실제 색에서 가져온다. 렌더 테스트로 rect fill/opacity와 범례 문구가 짝인지 확인해도 좋다.

[P1] web/src/api/mockMarketSource.ts:47 - VI 중 "예상 체결가·수량"이 호가창과 무관한 상수다
  근거: `expectedPrice = priceOnGrid(vi.triggerPrice + 100)`, `expectedQuantity = 1800` 고정이다. VI 중 호가창(asks/bids)은 여전히 `last` 기준 ±10원 단위로 그려지는데(39~44줄), 예상가는 그보다 약 600원 아래다. 합치가격(R-AUCT-01)이라면 호가 교차 구간 안에 있어야 하는데 화면의 매수/매도 잔량과 모순된다. 해제 때 실제 체결가는 이 값과 같게 만들어서 "맞아 보이게" 되어 있다.
  제안: 단일가 중에는 호가창 자체를 트리거 가격 주변 접수 호가로 만들어, 예상가를 그 호가에서 산출한다(간단 합치가격 계산 몇 줄). 어렵다면 PROGRESS의 "제안" 절에 "예상 체결가·수량은 목의 임의 지정값이며 호가창과 연동되지 않음"을 적고, 화면에 "(시연용 값)" 표기를 붙인다.

[P1] web/src/api/mockMarketSource.ts:40-44,134-136 - 내 주문의 "대기 순번"이 틱마다 무작위로 바뀌고, 체결 가능한 주문도 "대기"로 설명한다
  근거: 호가 잔량·주문 수는 매 프레임 갱신되는 `random`에서 나온다. 설명 패널은 `book.seq`가 바뀔 때마다 다시 조회(Orders.tsx:23)하는데 `queueAhead`는 그 레벨 `orderCount`/`quantity`를 합산하므로 같은 주문의 "n번째, 앞에 N주"가 1초마다 오르내린다. 실제 대기열은 시간 우선이라 순번은 줄기만 한다. 또 매수 10,100원처럼 매도 최우선호가 위의 주문도 접수되어 `Resting`으로 설명되고 체결되지 않는다(체결 시뮬레이션이 없음).
  제안: 주문 접수 시점의 순번과 앞 수량을 주문에 저장하고, 이후에는 단조 감소(예: 프레임마다 일부 체결)만 시킨다. 교차 가격 주문은 거부하지 말고 체결 흐름(`Filled`/`PartiallyFilled`)을 한 번 흉내 내거나, 설명을 "시연용 목: 체결 모의 없음"으로 명시한다.

[P1] web/src/api/mockMarketSource.test.ts:58-70 - 새 거부 사유 3종과 상태 전이에 테스트가 없다
  근거: 목이 만드는 거부 사유는 AboveUpperLimit, BelowLowerLimit, InvalidTick, InvalidOrder인데 테스트는 AboveUpperLimit(12,930)만 확인한다. REVIEW_GUIDE의 "새 거부 사유마다 테스트" 기준 미충족. 이미 취소한 주문 재취소(`OrderNotOpen`), 없는 주문 설명(null), 거부된 주문이 상태·해시를 바꾸지 않음(6번 원칙)도 없다. 지시서 4장 최소 항목(포맷터, 결정성, 가격 범위, 렌더 1개)은 충족한다.
  제안: 하한가 미만(6,950), 호가단위 위반(10,005), 수량 0·소수, 재취소, 없는 주문 설명을 한 테스트에 묶고, 거부 전후 `getStateHash()`가 같은지 확인한다. 경계값(6,960 / 12,920 정확히 접수) 케이스도 추가한다.

[P1] web/src/api/mockMarketSource.ts:162-165,18 - `restoredFromJournal`이 한 번 true가 되면 영영 true로 남는다
  근거: `restored`는 crash에서만 true로 바뀌고 내려가지 않는다. 계약상 이 값은 "마지막 기동이 저널 재생이었다"는 뜻인데, 복구 후 수십 분이 지나 새 이벤트가 쌓여도 "재생으로 복구됨" 배지가 그대로 붙는다. 해시가 복구 전과 같다는 비교(DemoPanels.tsx:58)도 crash 직후 한 번만 의미가 있다.
  제안: 복구 후 첫 새 액션이 기록될 때 `restored = false`로 내리거나, 계약에 "마지막 기동 기준"이라고 명시하고 화면 문구를 "마지막 복구 결과"로 바꾼다. 어느 쪽이든 PROGRESS "제안"에 적는다.

## P2

[P2] web/src/api/mockMarketSource.ts:111-114 - `runScenario`가 VI 중 `{ ok: false }`를 돌려준다(계약은 `{ ok: true }`만 정의)
  근거: 계약 표에는 실패 응답이 없다. 화면은 이 응답으로 안내 문구를 만든다(DemoPanels.tsx:41).
  제안: 006B 전에 계약에 실패 형태(예: 409)를 메인에 제안한다. PROGRESS "제안"에 추가.

[P2] web/src/api/mockMarketSource.ts:117-120 - 계약에 없는 거부 사유 문자열(BelowLowerLimit, InvalidTick, InvalidOrder)
  근거: 계약 예시는 AboveUpperLimit / R-LIMIT-04만 있다. InvalidOrder는 ruleId가 null이다. Orders.tsx:7의 문구표도 이 이름에 묶인다.
  제안: 서버 쪽 사유 코드 목록을 계약에 확정해 달라고 제안. 문구표에 모르는 사유 폴백(이미 있음)은 유지.

[P2] web/src/api/mockMarketSource.ts:3 - 데이터 계층이 화면 포맷터(`../format`)를 가져와 설명 메시지를 만든다
  근거: 실제 서버에서는 `message`가 서버가 만든 한국어 문장이라 이 의존은 목에만 있다. 분리는 대체로 잘 되어 있다(화면은 `MarketSource` 인터페이스와 `contract.ts`만 보고, `main.tsx`만 목 구현체를 안다).
  제안: 목 안에 작은 포맷 함수를 두면 api/ 폴더가 화면 코드에 의존하지 않는다. 006B에서 구현체 선택 코드를 `createSource()` 같은 한 함수로 빼면 더 쉽다.

[P2] web/src/DemoPanels.tsx:10 - 실험 ID `'exp-1'` 하드코딩(목은 `EXPERIMENT_ID` 상수를 export)
  근거: 서버 구현체로 바뀌면 ID 출처가 둘이 된다.
  제안: prop이나 상수로 한 곳에서 받는다.

[P2] web/src/MarketPanels.tsx:7 - VI 남은 시간은 논리 시간 기준이라 10배속에서는 실제로 12초 만에 끝나는데 "약 120초 남음"으로 표시된다
  근거: 계약상 논리 ms 기준이라 계약과는 일치하고 배속 배지(App.tsx:22)도 있다. 다만 프로젝터 시연에서 실제 시계와 다르다. 프레임이 논리 10초 단위라 120→110→…처럼 10초씩 줄어든다.
  제안: 배너에 "(논리 시간)"이나 "배속 적용 시 실제 약 n초"를 병기한다.

[P2] web/src/api/mockMarketSource.ts:56 - `aggressorSide`를 직전 가격과의 비교로 정한다(같으면 Buy)
  근거: 호가 교차 모델이 없어 임의 지정이다. 체결 목록의 매수/매도 색이 실제 의미를 갖지 않는다.
  제안: 임의 지정임을 PROGRESS에 한 줄 표기.

[P2] web/src/App.test.tsx:10-12 - 첫 렌더 테스트가 실제 `setInterval`(10배속 1초)을 켠 채 `advance()` 횟수에 의존한다
  근거: 느린 환경에서 타이머가 중간에 한 프레임 더 돌리면 3번째 프레임 기대가 어긋날 수 있다. 지금은 통과하며 454ms로 끝난다.
  제안: `vi.useFakeTimers()`를 쓰거나 타이머를 켜지 않는 주입 옵션을 둔다.

## 요청 중점 항목 요약
- 계약 일치: 필드명(camelCase), `vi` null 처리, `phase` 값(Continuous/CallAuction), `expectedPrice/Quantity` null, `viEvent` Released의 `auctionPrice`, 거부 응답 형태 모두 계약과 일치. 단 `runScenario` 실패 응답과 거부 사유 문자열은 계약에 없음(P2).
- 가격 규칙: 모든 가격이 `priceOnGrid`(10원 반올림 후 상하한가 클램프)나 `last ± 10n`에서 나오고 필터가 상하한가를 지킨다. 기준가 9,940, 상·하한 12,920 / 6,960은 RULES 예시와 같고 5,000~20,000원 구간이라 호가단위 10원이 맞다. 테스트도 이를 검증한다. 가격이 가격대 경계를 넘는 시나리오가 없어 단일 단위로 충분하다.
- 결정성: `Math.random`, `Date.now` 없음. LCG(시드)로만 진행하고 시간은 논리 시각. 실시간 타이머는 `advance()` 호출 간격만 정하며 상태는 결정적이다.
- 거짓 숫자: 위 P1 3건(음영 색, 예상 체결가, 대기 순번)과 P2 1건(VI 남은 시간 표기).
- 데이터 계층 분리: 양호(위 P2 참고).
- 라이선스: Apache-2.0 LICENSE와 NOTICE를 `public/licenses/`에 포함하고 화면 하단에 TradingView 링크와 저작권 문구, 차트 자체 로고(`attributionLogo`)를 표시한다. 조건 충족으로 판단.
- 테스트: 지시서 4장 4항목 모두 충족(포맷 3, 목 4, 렌더 2).
