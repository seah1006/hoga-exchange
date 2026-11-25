# Code Review (3B)

- **대상 저장소**: `C:\Users\USER\dev\hoga-002` (브랜치 `task/TASK-002`)
- **검토 범위**: `d2e9d86cdf8656c39f148f9e0eed3749d8c75fea..19b88a7b632ce1906687649184cb7929c4200e06` (`git merge-base main HEAD..HEAD`, `src/` 및 `tests/`만 검토)
- **테스트 실행**: `dotnet test HogaExchange.slnx` -> 전체 96개 통과 (실패 0, 건너뜀 0)
- **발견 건수**: HIGH 0, MEDIUM 2, LOW 2 (총 4건)

---

## Findings

### [MEDIUM] src/Hoga.Engine/MatchingEngine.Auction.cs:32 - 커스텀 `ITickSizeRule` 사용 시 `FindAuction`이 1원 단위로 전체 가격 구간을 순회함
- **근거**: `FindAuction`의 루프가 `price += tickRule is TickSizeTable ? tickRule.GetTickSize(price) : 1`로 되어 있다. `TickSizeTable`이 아닌 커스텀 `ITickSizeRule` 구현체가 들어오면 `asks[0].Price`부터 `bids[0].Price`까지 매 1단위마다 `IsOnTick(price)`를 호출하며 순회한다. `PriceLimits.MaxBasePrice`가 `10_000_000`(상한 `13_000_000`, 하한 `7_000_000`)이므로 스프레드가 큰 경우 `GetIndicativeAuction()` 또는 `CompleteAuction()` 1회 호출에 최대 수백만 번(예: 6,000,000회) 반복할 수 있다.
- **제안**: 커스텀 `ITickSizeRule`에도 `NextTick(price)` 형태의 메서드를 제공하거나, 최소한 `ITickSizeRule`이 호가 단위 경계 정렬을 만족할 때 `GetTickSize(price)`만큼 건너뛰거나, 호가 잔량 레벨과 직전가/기준가 인접 후보만 평가하는 방식으로 시간 복잡도를 호가 레벨 수 기준으로 제한한다.

### [MEDIUM] tests/Hoga.Engine.Tests/AuctionInvariantTests.cs:11 - 속성 테스트(`I7`~`I9`)가 단일 호가 단위(`10_000`)만 다루고 경매 중 취소·무체결(`AuctionNoMatch`) 및 경매 중 주문 누적을 검증하지 않음
- **근거**:
  1. `I8`, `I9`는 `PopulateAuction`에서 항상 `9400` 매수/매도 주문을 먼저 넣고 취소를 전혀 실행하지 않으므로, 경매 중 전량/일부 취소로 호가가 비거나 한쪽만 남거나 교차가 해소되어 `AuctionNoMatch`가 발생하는 경로가 속성 테스트에서 한 번도 실행되지 않는다.
  2. `I7`은 매 반복마다 `s.Advance((i + 1L) * 150_000)`으로 즉시 경매를 종료시키므로, `CallAuction` 중에 여러 주문이 쌓이거나 취소된 뒤 체결되는 다단계 흐름을 섞어 검증하지 않는다.
  3. `I7`~`I9` 모두 `basePrice = 10_000`, 가격 범위 `7500..12500`(10원 호가 단일 구간)만 생성한다. `InvariantTests.cs`처럼 틱 크기가 바뀌는 경계 기준가(`1_990`, `4_990`, `19_990`, `49_950`, `199_900`, `499_500`)를 VI·단일가 경매 속성 테스트에서는 다루지 않는다. (단, `I7`의 VI 발동과 `I8`/`I9`의 단일가 체결 자체는 매 실행마다 실제로 동작하므로 vacuous pass는 아니다.)
- **제안**: `AuctionInvariantTests`에 (1) 다중 `BasePrices`(틱 경계 포함) 적용, (2) 경매 중 `CancelOrder` 및 다양한 `LogicalTimeMs` 간격(`_actualEndMs` 전/후 혼합)을 포함한 상태 전이 속성 테스트를 추가해 `AuctionNoMatch`와 `CallAuction` 누적/취소 경로를 커버한다.

### [LOW] src/Hoga.Engine/MatchingEngine.Queries.cs:81 - `GetLevels`의 레벨 수량 합산이 `long` 오버플로에 취약함
- **근거**: `FindAuction`은 누적 매수/매도 잔량 합산에 `Int128`을 사용해 오버플로를 방지하고 있으나, `CalculateAuction()`이 `FindAuction`에 전달하는 `BookLevel`을 만드는 `GetLevels`는 `level.Value.Sum(order => order.Remaining)`을 사용한다. 동일 가격대에 `long.MaxValue`를 초과하는 합계 수량이 쌓이면 `CalculateAuction()` 또는 `GetBook()` 호출 시 `OverflowException`이 발생한다.
- **제안**: `GetLevels`에서도 누적 수량 계산 시 `Int128` 또는 명시적 범위 정책을 적용해 `FindAuction`과 오버플로 내성을 맞춘다.

### [LOW] src/Hoga.Engine/MatchingEngine.cs:34 - `Process`에서 `LogicalTimeMs`의 단조 증가 및 상한 검증이 없음
- **근거**: `Process`는 `Sequence`의 연속성(`_lastSequence + 1`)과 `OrderId` 널 여부만 검사하고 `command.LogicalTimeMs`의 음수 여부나 역행(이전 명령보다 작은 시각)을 검증하지 않는다. 이로 인해 직전 `ViReleased.AtMs`보다 과거 시각으로 다음 `ViTriggered.AtMs`가 기록될 수 있으며, `long.MaxValue - 150_000`을 초과하는 시각이 들어오면 `TryTriggerVi` 내부의 `checked(time + 120_000)`에서 `OverflowException`이 발생한다.
- **제안**: 엔진 계약상 단조 비감소 논리 시각을 전제한다면 `Process` 진입부에서 `_lastLogicalTimeMs` 대비 역행 및 허용 범위를 검증하고, 역행 입력을 허용하는 설계라면 그 의도를 API 계약으로 명시한다.

---

## 검증 요약 (이상 없음으로 확인된 항목)

- **단일가 해제 후 역전 방지**: `FindAuction`에서 `volume >= betterBuy && volume >= betterSell` 및 최소 한쪽 잔량(`atBuy` 또는 `atSell`) 전량 체결 조건(`best` 및 `withoutMinimum`)을 강제하므로, `ExecuteAuction` 이후 매수 최우선 호가는 항상 매도 최우선 호가보다 낮다.
- **가격제한폭·틱 유효성**: 모든 대기 주문이 `Place`에서 `[LowerLimit, UpperLimit]` 및 `IsOnTick` 검증을 통과했고, `FindAuction`도 `[asks[0].Price, bids[0].Price]` 범위의 `IsOnTick` 가격만 후보로 취하므로 체결가는 항상 유효 범위 및 호가 단위 위에 있다. `TickSizeTable`은 경계(`2_000`, `5_000`, `20_000`, `50_000`, `200_000`, `500_000`)에서 인접 틱이 모두 나누어떨어져 `price += GetTickSize(price)`가 경계 틱을 건너뛰지 않는다.
- **배분 우선순위**: `ExecuteAuction`은 가격 우선(`_bids` 내림차순, `_asks` 오름차순) 후 동일 가격 내 FIFO(`LinkedList.First`)로만 체결하며 비례 배분(pro-rata)은 없다.
- **Sweep 및 트리거 기준가 고정**: `Place`에서 `submittedReference = DynamicReferencePrice`를 루프 진입 전 1회만 읽어 스윕 중 동적 VI 기준가가 흔들리지 않으며, `TryTriggerVi`가 수량 차감 전에 검사되어 잔여 수량만 경매 호가창에 남는다. `TouchesVi`는 `>=`, `<=` 경계 포함이며 `MaxBasePrice = 10_000_000` 범위에서 `price * 100`, `reference * 110`은 `long` 오버플로가 없다.
- **랜덤 엔드 결정성 및 해시**: `SplitMix64.NextAuctionExtensionMs`는 `[0, 30_000]` ms를 편향 없는 rejection sampling으로 결정론적 생성하며, `ComputeStateHash`에 `ViEnabled`, `IsKospi200`, `Phase`, `DynamicReferencePrice`, `StaticReferencePrice`, `_rng.State`, `_ongoingVi` 및 `_actualEndMs`가 모두 포함된다. `ViEnabled = false`일 때 `TryTriggerVi`가 즉시 `false`를 반환해 RNG나 VI 상태를 건드리지 않는다.
- **경매 종료 시각 초과 명령**: `Process`에서 `Phase == CallAuction && command.LogicalTimeMs >= _actualEndMs`일 때 `CompleteAuction`을 1회 실행하고 `Phase = Continuous`로 전환한 뒤 해당 명령을 처리하므로 경매 건너뛰기나 중복 실행이 발생하지 않는다.
