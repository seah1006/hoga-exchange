# KRX 규칙 명세 (RULES)

이 문서는 엔진이 따르는 한국거래소 유가증권시장·코스닥시장 주권 매매 규칙을 정리한다. 모든 규칙에는 ID가 있고, 엔진 코드와 테스트는 이 ID를 참조한다.

- 확인일: 2026-11-24 (조사 보고서: `docs/research/RULES_RESEARCH_2026-11-24.md`)
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
| R-TICK-03 | 2023-01-25 이전 호가단위표를 날짜·시장에 따라 적용. 유가: 1,000 미만 1 / 1,000~5,000 5 / 5,000~10,000 10 / 10,000~50,000 50 / 50,000~100,000 100 / 100,000~500,000 500 / 500,000 이상 1,000. 코스닥: 100,000원 이상은 모두 100원, 그 아래는 유가와 같음 | 2단계 |

R-TICK-03 근거: KRX 투자자 교육자료(2022-02), 교보·한양·한화증권 공지, 연합뉴스 2022-11-01 (조사 보고서 `docs/research/RULES_RESEARCH_2026-11-24.md` 6항). 구 조문 원문은 확보하지 못해 확신도 중간.

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
| R-AUCT-02 | 합치가격 P에서: P 미만 매도와 P 초과 매수는 전 수량 체결. P의 호가는 매도·매수 중 한쪽 전 수량, 다른 쪽은 매매수량단위(주권 1주) 이상을 가격·시간 우선으로 체결 | 1단계 |
| R-AUCT-02a | 매도 합계와 매수 합계가 합치하는 가격이 정확히 두 개면, R-AUCT-02의 "다른 쪽 1주 이상" 조건을 채우지 못해도 거래를 성립시키고 가격은 R-AUCT-05로 정한다 | 1단계 |
| R-AUCT-03 | 단일가매매도 가격·시간 우선. 같은 가격 호가 사이에 수량 배분은 하지 않는다 | 1단계 |
| R-AUCT-04 | 모든 단일가매매는 예정 마감시점부터 30초 이내 임의 시각에 종료(랜덤 엔드). 엔진은 주입된 시드로 종료 시각을 정한다 | 1단계 |
| R-AUCT-05 | 합치가격이 둘 이상이면 (1) 직전 가격과 같은 합치가격이 있으면 그 가격, (2) 직전 가격이 가장 높은 합치가격보다 높거나 가장 낮은 합치가격보다 낮으면 직전 가격에 가장 가까운 가격, (3) 직전 가격이 합치가격들 사이에 있으면 직전 가격보다 높은 합치가격 중 가장 가까운 가격 | 1단계 |
| R-AUCT-07 | 단일가매매에서 체결 수량이 0이면 연장하지 않고 접속매매로 전환한다 | 1단계 |
| R-AUCT-08 | 합치가격 후보는 호가가 없는 가격을 포함해 호가단위 격자 위 모든 가격(상하한가 이내). 4항 2호의 1주 조건은 그 가격에 다른 쪽 호가가 있을 때만 적용 | 1단계 (조문 해석, 메인 결정 2026-11-25, 확신도 중간) |
| R-AUCT-06 | 시가(또는 매매 재개 후 첫 가격)가 상·하한가로 정해지면 해당 쪽 호가에 수량 배분 | 2단계 |

출처: 한국거래소 규정/제도 「단일가매매시 체결방법」(유가증권), 「매매체결방법」(코스닥), 유가증권시장 업무규정 제23조 4~6항(코스닥 업무규정 제18조 같은 문구). R-AUCT-05의 (3)은 2025-02-05 개정으로 신설됐다(메인 에이전트가 조문 사본으로 직접 확인). R-AUCT-07은 유가 시행세칙 제35조 2항(2022-12-22 신설).

R-AUCT-05 테스트 예시: 합치 {90,100,110}, 직전 100 → 100 / {110,120}, 직전 100 → 110 / {90,110}, 직전 100 → 110 / {90,120}, 직전 100 → 120 / {90,95}, 직전 100 → 95. (가격은 설명용 숫자이며 테스트에서는 호가단위에 맞는 가격으로 바꿔 쓴다.)

## 5. 변동성완화장치 (R-VI)
| ID | 규칙 | 상태 |
|---|---|---|
| R-VI-01 | 동적 VI: 참조가격 = 호가 제출 직전 체결가격(그 호가가 여러 가격을 연속 체결해도 참조가격은 제출 직전 값으로 고정). 잠정 체결가격이 참조가격 대비 발동률 **이상** 오르거나 내리면(경계 포함) 발동 | 1단계 |
| R-VI-01a | 한 호가가 연속 체결하다 발동 조건에 닿으면, 닿기 직전 가격까지는 접속매매로 체결되고, 남은 수량만 단일가매매 호가로 넘어간다 | 1단계 |
| R-VI-01b | 직전 가격이 1,000원 미만이고 잠정 체결가격이 3호가단위 이내면 발동하지 않는다(유가 시행세칙 제41조의2 4항 6호, 코스닥 조문은 미확인) | 2단계 |
| R-VI-02 | 동적 VI 발동률: 접속매매 중 KOSPI200 구성종목 3%, 유가 일반·코스닥 6%. 종가 단일가 중 2% / 4% | 1단계(접속매매 비율만), 종가 단일가 비율은 2단계 |
| R-VI-03 | 정적 VI: 참조가격 = 직전 단일가격(시가 결정 전에는 당일 기준가격). 발동률 10% | 1단계 |
| R-VI-04 | 발동 시 2분간 단일가매매로 전환(단일가 시간대였다면 연장). 종료는 R-AUCT-04 랜덤 엔드. VI 단일가 중에도 신규·취소 호가를 받고, 새 VI는 발동하지 않는다 | 1단계 |
| R-VI-05 | 하루 발동 횟수 제한 없음 | 1단계 |
| R-VI-06 | 발동 판정은 발동가격을 만들지 않고 정수 비교로 한다: 상승 `p*100 >= ref*(100+rate)`, 하락 `p*100 <= ref*(100-rate)` (rate는 % 정수) | 1단계 (호가단위 처리 원문 미확인) |
| R-VI-07 | VI 단일가 체결 후: 정적 참조가격 = 그 단일가, 동적 참조가격 = 그 단일가(직전 체결가) | 1단계 (조문 논리로 도출, 확신도 중간) |

출처: 한국거래소 규정/제도 「종목별 변동성완화장치」(유가증권시장, 코스닥시장).
단순화: 종목 구분(KOSPI200 여부)은 시나리오 설정값. 파생상품 최종거래일 1% 규칙, 정리매매·단기과열 종목 배제, 신규상장일 미적용은 다루지 않는다. 1단계는 시가 단일가를 생략하므로 장 시작 시 직전 체결가격과 정적 참조가격을 모두 기준가격으로 둔다. 동적·정적 VI가 같은 체결에서 동시에 조건을 만족하면 정적 VI로 기록한다(임의 지정, KRX는 "중복 시 하나만 적용"이라고만 안내).

VI 테스트 예시(증권사 안내 예시를 바탕으로 호가 배치를 임의 지정): 직전 체결가 10,000원, 6% → 하락 발동 조건 9,400원 이하. 매수 호가 10,000×300, 9,800×300, 9,600×300, 9,500×300, 9,400×200, 9,300×1,000이 있을 때 9,400원 매도 1,500주가 들어오면 9,500원까지 1,200주만 체결되고 VI 발동, 남은 300주는 9,400원 단일가 호가로 대기.

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
| 2023-01-25 이전 호가단위 구 조문 원문 | 2차 자료로 표 확정(R-TICK-03), 구현은 2단계 |
| VI 발동가격의 호가단위 처리 (R-VI-06) | 정수 비교로 판정 |
| VI 단일가 중 정정 호가의 시간우선 처리 | 1단계는 정정 명령이 없으므로 해당 없음 |
| VI 단일가 후 참조가격 갱신 (R-VI-07) | 조문 논리대로 구현, 직접 문장 확인 못 함 |
| 코스닥 시행세칙의 VI 비율·1,000원 미만 예외 조문 | 유가 기준으로 구현 |

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
| R-AUCT-01 | `MatchingEngine.FindAuction`, `ExecuteAuction` | `AuctionPriceTests.Krx_book_clears_at_7830_for_600`, `AuctionInvariantTests.I8_every_auction_has_only_one_trade_price` |
| R-AUCT-02 | `MatchingEngine.FindAuction` | `AuctionPriceTests.Krx_book_clears_at_7830_for_600`, `AuctionInvariantTests.I9_book_is_not_crossed_after_auction` |
| R-AUCT-02a | `MatchingEngine.FindAuction` | `AuctionPriceTests.Adjacent_candidates_without_at_price_fill_can_clear`, `VolatilityTests.Adjacent_tick_exception_closes_without_crossed_book` |
| R-AUCT-03 | `MatchingEngine.ExecuteAuction` | `VolatilityTests.Krx_example_matches_600_and_allocates_at_price_in_fifo_order` |
| R-AUCT-04 | `SplitMix64.NextAuctionExtensionMs`, `MatchingEngine.TryTriggerVi` | `AuctionTimingTests.SplitMix64_matches_reference_vector`, `VolatilityTests.Auction_ends_at_seeded_time_in_inclusive_window` |
| R-AUCT-05 | `MatchingEngine.ChooseAuctionPrice` | `AuctionPriceTests.Rule_examples_choose_expected_price` |
| R-AUCT-07 | `MatchingEngine.CompleteAuction` | `VolatilityTests.Cancel_during_auction_can_leave_no_match_and_release_before_next_command` |
| R-AUCT-08 | `MatchingEngine.FindAuction` | `AuctionPriceTests.Empty_tick_prices_prevent_crossed_release_in_regression_book`, `AuctionPriceTests.Grid_uses_the_tick_of_each_price_band` |
| R-VI-01 | `MatchingEngine.Place`, `TryTriggerVi` | `VolatilityTests.Dynamic_threshold_is_inclusive`, `AuctionInvariantTests.I7_continuous_trades_do_not_touch_either_trigger` |
| R-VI-01a | `MatchingEngine.Place` | `VolatilityTests.Sweep_fills_1200_before_9400_and_queues_remaining_300` |
| R-VI-02 | `MatchingEngine.TryTriggerVi` | `VolatilityTests.Dynamic_threshold_is_inclusive` |
| R-VI-03 | `MatchingEngine.TryTriggerVi` | `VolatilityTests.Static_uses_cumulative_move_then_resets_and_can_trigger_again` |
| R-VI-04 | `MatchingEngine.Process`, `Place`, `TryTriggerVi` | `AuctionQueryTests.Indicative_auction_does_not_mutate_and_orders_report_waiting_until_release`, `VolatilityTests.Cancel_during_auction_can_leave_no_match_and_release_before_next_command` |
| R-VI-05 | `MatchingEngine.CompleteAuction` | `VolatilityTests.Static_uses_cumulative_move_then_resets_and_can_trigger_again` |
| R-VI-06 | `MatchingEngine.TouchesVi` | `VolatilityTests.Dynamic_threshold_is_inclusive`, `AuctionInvariantTests.I7_continuous_trades_do_not_touch_either_trigger` |
| R-VI-07 | `MatchingEngine.CompleteAuction` | `VolatilityTests.Static_uses_cumulative_move_then_resets_and_can_trigger_again`, `VolatilityTests.Cancel_during_auction_can_leave_no_match_and_release_before_next_command` |
