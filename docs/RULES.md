# KRX 규칙 명세 (RULES)

이 문서는 엔진이 따르는 한국거래소 유가증권시장·코스닥시장 주권 매매 규칙을 정리한다. 모든 규칙에는 ID가 있고, 엔진 코드와 테스트는 이 ID를 참조한다.

- 확인일: 2026-11-24
- 출처는 한국거래소 규정/제도 안내 페이지(regulation.krx.co.kr)를 우선한다. 원문을 옮겨 적지 않고 요약한다.
- 공식 해석이 아니다. 단순화한 부분은 "단순화"에, 확인하지 못한 부분은 "미확인"에 적는다.

## 0. 공통 표현
- 가격과 수량은 정수(원, 주)로 다룬다. 엔진에서 `double`, `float`, `decimal`을 쓰지 않는다.
- 시각은 엔진에 주입되는 논리 시각(ms)이다. 엔진은 시스템 시계를 읽지 않는다.
- "절사"는 호가단위의 배수로 내림한다는 뜻이다: `floor(x / tick) * tick`.

## 1. 호가가격단위 (R-TICK)
| ID | 규칙 | 상태 |
|---|---|---|
| R-TICK-01 | 2023-01-25부터 유가증권·코스닥·코넥스 주권 공통 호가가격단위: 2,000원 미만 1원 / 2,000~5,000원 미만 5원 / 5,000~20,000원 미만 10원 / 20,000~50,000원 미만 50원 / 50,000~200,000원 미만 100원 / 200,000~500,000원 미만 500원 / 500,000원 이상 1,000원 | 1단계 |
| R-TICK-02 | 호가 가격은 그 가격이 속한 가격대 호가단위의 배수여야 한다 | 1단계 |
| R-TICK-03 | 2023-01-25 이전 호가단위표를 날짜에 따라 적용 | 2단계 (원문 확인 후) |

출처: 삼성증권·대신증권 제도개편 공지(2023-01), 연합뉴스 2023-01-17 보도. 구간 경계는 "이상~미만"으로 해석했다(예: 2,000원은 5원 단위).

## 2. 가격제한폭과 상하한가 (R-LIMIT)
| ID | 규칙 | 상태 |
|---|---|---|
| R-LIMIT-01 | 가격제한폭 = 기준가격 × 0.3을 **기준가격의 호가단위**로 절사 | 1단계 |
| R-LIMIT-02 | 상한가 = 기준가격 + 가격제한폭, 하한가 = 기준가격 − 가격제한폭. 각각 **그 결과 가격의 호가단위**로 절사 | 1단계 |
| R-LIMIT-03 | R-LIMIT-01의 금액이 가장 낮은 호가단위(1원) 미만이면 1원으로 한다 | 1단계 |
| R-LIMIT-04 | 상한가보다 높거나 하한가보다 낮은 호가는 접수하지 않는다 | 1단계 |
| R-LIMIT-05 | 호가 가격은 1원 이상(임의 지정 단순화. 기준가 1원이면 산식상 하한가가 0원이 되기 때문) | 1단계 |

검증 예시(테스트로 고정):
| 기준가격 | 폭(1차 절사) | 상한가 | 하한가 | 근거 |
|---|---|---|---|---|
| 9,940 | 2,980 | 12,920 | 6,960 | KRX 코스닥 규정 안내 예시 |
| 9,980 | 2,990 | 12,970 | 6,990 | KRX 유가증권 규정 안내 예시 |
| 19,990 | 5,990 | 25,950 | 14,000 | 자체 계산: 25,980이 50원 단위 구간이라 2차 절사로 25,950 |
| 3 | 1 | 4 | 2 | 자체 계산: 0.9원 → 0 → R-LIMIT-03으로 1원 |

출처: 한국거래소 규정/제도 「기준가격/가격제한폭/상하한가」(코스닥), 유가증권시장 산출방법 안내, 유가증권시장 업무규정 제20조.
단순화: 기준가격은 시나리오 설정값(전일 종가 역할)으로 준다. 권리락·액면변경 등 별도 기준가격은 다루지 않는다.

## 3. 접속매매 (R-MATCH)
| ID | 규칙 | 상태 |
|---|---|---|
| R-MATCH-01 | 가격 우선: 매수는 높은 가격, 매도는 낮은 가격이 먼저 | 1단계 |
| R-MATCH-02 | 시간 우선: 같은 가격에서는 먼저 접수된 호가가 먼저 | 1단계 |
| R-MATCH-03 | 체결가격은 먼저 대기하던 호가의 가격 | 1단계 |
| R-MATCH-04 | 부분 체결 후 남은 수량은 같은 가격·순번으로 대기 | 1단계 |

단순화: 1단계는 지정가 호가만. 시장가·조건부지정가·최유리 등은 2단계 이후. 자기 체결(같은 계좌 매수·매도) 방지는 하지 않는다.

## 4. 단일가매매 (R-AUCT)
| ID | 규칙 | 상태 |
|---|---|---|
| R-AUCT-01 | 일정 시간 접수한 호가를 하나의 가격으로 체결한다. 가격은 "가장 많은 수량을 체결시킬 수 있는 가격"(합치가격) | 1단계 |
| R-AUCT-02 | 합치가격보다 낮은 매도·높은 매수는 전량 체결. 합치가격에서는 한쪽은 전량, 다른 쪽은 가격·시간 우선으로 체결 | 1단계 |
| R-AUCT-03 | 단일가매매도 가격·시간 우선. 같은 가격 호가 사이에 수량 배분은 하지 않는다 | 1단계 |
| R-AUCT-04 | 모든 단일가매매는 예정 마감시점부터 30초 이내 임의 시각에 종료(랜덤 엔드). 엔진은 주입된 시드로 종료 시각을 정한다 | 1단계 |
| R-AUCT-05 | 합치가격이 여러 개면 직전 가격과 같은 가격, 없으면 직전 가격에 가장 가까운 가격 | 1단계 (미확인 표시) |
| R-AUCT-06 | 시가(또는 매매 재개 후 첫 가격)가 상·하한가로 정해지면 해당 쪽 호가에 수량 배분 | 2단계 |

출처: 한국거래소 규정/제도 「단일가매매시 체결방법」(유가증권), 「매매체결방법」(코스닥). R-AUCT-05는 한국증권학회지 논문(시가 단일가매매 호가접수시간 단축 연구)의 제도 설명과 KRX 배출권시장 체결방법 안내를 근거로 했고, 주식시장 규정 원문은 아직 확인하지 못했다.

## 5. 변동성완화장치 (R-VI)
| ID | 규칙 | 상태 |
|---|---|---|
| R-VI-01 | 동적 VI: 참조가격 = 호가 제출 직전 체결가격. 그 호가로 체결될 가격(잠정 체결가격)이 참조가격 ± 발동률을 벗어나면 발동 | 1단계 |
| R-VI-02 | 동적 VI 발동률: 접속매매 중 KOSPI200 구성종목 3%, 유가 일반·코스닥 6%. 종가 단일가 중 2% / 4% | 1단계(접속매매 비율만), 종가 단일가 비율은 2단계 |
| R-VI-03 | 정적 VI: 참조가격 = 직전 단일가격(시가 결정 전에는 당일 기준가격). 발동률 10% | 1단계 |
| R-VI-04 | 발동 시 2분간 단일가매매로 전환(단일가 시간대였다면 연장). 종료는 R-AUCT-04 랜덤 엔드 | 1단계 |
| R-VI-05 | 하루 발동 횟수 제한 없음 | 1단계 |
| R-VI-06 | 발동가격 = 참조가격 ± (참조가격 × 발동률). 발동가격의 호가단위 처리 | 미확인 (1단계는 계산값 그대로 비교하고 단순화로 표시) |

출처: 한국거래소 규정/제도 「종목별 변동성완화장치」(유가증권시장, 코스닥시장).
단순화: 종목 구분(KOSPI200 여부)은 시나리오 설정값. 파생상품 최종거래일 1% 규칙, 정리매매·단기과열 종목 배제, 신규상장일 미적용은 다루지 않는다.

## 6. 서킷브레이커 (R-CB)
| ID | 규칙 | 상태 |
|---|---|---|
| R-CB-01 | 지수가 전일 대비 8% 이상 하락해 1분 지속: 20분 매매 중단 후 재개 | 2단계 |
| R-CB-02 | 15% 이상 하락 + 1단계 발동 시점 대비 1% 이상 추가 하락, 1분 지속: 20분 중단 후 재개 | 2단계 |
| R-CB-03 | 20% 이상 하락 + 2단계 대비 1% 이상 추가 하락, 1분 지속: 당일 장 종료 | 2단계 |

출처: 찾기 쉬운 생활법령정보 「서킷브레이커」. 여러 종목과 지수 계산이 필요해서 2단계로 미룬다.

## 7. 미확인 목록
| 항목 | 현재 처리 |
|---|---|
| 2023-01-25 이전 호가단위표(유가·코스닥 차이) | 2단계에서 원문 확인 후 구현 |
| 합치가격 복수 시 주식시장 규정 원문 (R-AUCT-05) | 근거 문헌대로 구현하고 테스트 이름에 `Unverified` 표시 |
| VI 발동가격의 호가단위 처리 (R-VI-06) | 계산값 그대로 비교 |

## 8. 추적표
구현이 진행되면 각 행에 테스트 이름을 채운다. 규칙 ID에 테스트가 없으면 리뷰에서 P1이다.

| 규칙 ID | 코드 위치 | 테스트 |
|---|---|---|
| R-TICK-01 | `TickSizeTable.GetTickSize` | `PriceRuleTests.Tick_boundaries` |
| R-TICK-02 | `TickSizeTable.IsOnTick`, `MatchingEngine.Place` | `PriceRuleTests.Tick_boundaries`, `MatchingTests.Rejects_invalid_placement`, `InvariantTests.I2_trades_use_valid_prices` |
| R-LIMIT-01 | `PriceLimits.Compute` | `PriceRuleTests.Specified_limit_examples`, `PriceRuleTests.Limits_use_each_results_tick_near_price_boundaries`, `PriceRuleTests.Max_base_price_computes_without_overflow` |
| R-LIMIT-02 | `PriceLimits.Compute` | `PriceRuleTests.Specified_limit_examples`, `PriceRuleTests.Limits_use_each_results_tick_near_price_boundaries` |
| R-LIMIT-03 | `PriceLimits.Compute` | `PriceRuleTests.Specified_limit_examples` |
| R-LIMIT-04 | `MatchingEngine.Place` | `MatchingTests.Rejects_invalid_placement`, `MatchingTests.Accepts_orders_at_both_limits`, `InvariantTests.I2_trades_use_valid_prices` |
| R-LIMIT-05 | `MatchingEngine.Place` | `MatchingTests.Rejects_price_below_one_even_at_zero_lower_limit`, `BookTests.All_reject_reasons_only_advance_the_sequence` |
| R-MATCH-01 | `MatchingEngine.Place` | `MatchingTests.Better_price_trades_first_at_resting_price`, `InvariantTests.I4_trades_take_best_price_then_earliest_resting_order` |
| R-MATCH-02 | `MatchingEngine.Place` | `MatchingTests.Partial_fill_keeps_priority_and_large_order_sweeps_multiple_levels`, `InvariantTests.I4_trades_take_best_price_then_earliest_resting_order` |
| R-MATCH-03 | `MatchingEngine.Place` | `MatchingTests.Better_price_trades_first_at_resting_price` |
| R-MATCH-04 | `MatchingEngine.Place` | `MatchingTests.Partial_fill_keeps_priority_and_large_order_sweeps_multiple_levels`, `BookTests.Book_and_queue_reflect_partial_fill_and_cancellation` |
| R-AUCT-01~05 | (TASK-002) | |
| R-VI-01~05 | (TASK-002) | |
