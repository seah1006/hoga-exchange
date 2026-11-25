범위: `6f91afd3b338689324d9fc5f8315340fd0b6cf0b..bd096cca92eb3150dc0e56325aa33608fe60a23c` (`git merge-base main HEAD`..`HEAD`)
등급별 개수: HIGH 1, MEDIUM 3, LOW 2

[HIGH] src/Hoga.Engine/MatchingEngine.Queries.cs:28 - 상태 해시가 호가창만 포함하여 동일 해시 엔진이 다음 명령에서 다르게 동작함
  근거: `ComputeStateHash()`는 `_bids`와 `_asks`만 직렬화하고 `_acceptedIds`, `_lastSequence`, `BasePrice`(`UpperLimit`/`LowerLimit`)를 제외한다(33행 주석에도 명시됨). 예를 들어 주문 `"o1"`을 접수 후 취소하거나 전량 체결해 호가창이 빈 엔진과 아무 주문도 받지 않은 빈 엔진은 동일한 해시(`af5570f5...`)를 반환하지만, 다음 `PlaceLimitOrder("o1", ...)`에서 전자는 `DuplicateOrderId`로 거부하고 후자는 수락한다. 또한 거부 명령으로 `_lastSequence`만 증가한 엔진과 초기 엔진도 해시가 같지만 다음 `Sequence` 검증 결과가 달라진다.
  제안: 상태 동등성 목적에 맞게 `_lastSequence`, `_acceptedIds`(정렬된 순서로), `BasePrice`(또는 상하한)를 해시 입력에 포함하거나, 호가창 전용 해시라면 메서드명과 계약을 `ComputeBookHash()` 등으로 명확히 분리한다.

[MEDIUM] src/Hoga.Engine/MatchingEngine.Queries.cs:43 - 호가창/대기열 수량 합산 시 `long` 오버플로 가능
  근거: `Place()`는 `order.Quantity > 0`만 검사해 `long.MaxValue`까지 여러 건의 주문을 같은 가격대에 접수할 수 있다. 그러나 `GetLevels()`의 `level.Value.Sum(order => order.Remaining)`(43행)과 `GetQueuePosition()`의 `checked(quantity + cursor!.Value.Remaining)`(23행)은 `checked` 합산을 수행하므로, 동일 가격대 잔량 합이 `long.MaxValue`를 넘으면 유효한 주문 상태에서도 `OverflowException`이 발생한다.
  제안: 주문 접수 시점(`Place`)에 가격대 또는 종목 전체 누적 수량 상한을 두어 오버플로를 사전 차단하거나, 조회 집계에 `Int128` 등을 사용한다.

[MEDIUM] src/Hoga.Engine/PriceLimits.cs:16 - `basePrice`가 1일 때 하한가가 0으로 계산되어 0원 주문이 허용됨
  근거: `basePrice == 1`(`MinBasePrice`)일 때 `raw = (1 * 3) / 10 = 0`, `band = Math.Max(1, 0) = 1`이 되어 `Lower`가 `rule.FloorToTick(1 - 1) = 0`으로 반환된다(17행). `TickSizeTable.IsOnTick(0)`은 `0 % 1 == 0`으로 `true`이므로 `MatchingEngine`이 `Price = 0`인 주문을 정상 접수한다.
  제안: `Lower` 계산 결과를 최소 호가(`1` 또는 `MinBasePrice`) 이상으로 보정(`Math.Max(1, ...)`)하거나 `MatchingEngine.Place`에서 `order.Price <= 0`을 거부한다.

[MEDIUM] src/Hoga.Engine/MatchingEngine.cs:27 - 빈 문자열/공백 `OrderId`가 허용되고 `AccountId`는 검증되지 않음
  근거: `Process()`는 `ArgumentNullException.ThrowIfNull(command.OrderId)`로 `null`만 차단할 뿐 `""` 또는 `"   "` 같은 빈/공백 `OrderId`를 그대로 접수해 `_acceptedIds`와 `_active`에 등록한다. 또한 `PlaceLimitOrder.AccountId`는 `null`이나 빈 값 검사가 전혀 없으며 엔진 내부에서 저장·검증되지 않는다.
  제안: `string.IsNullOrWhiteSpace(command.OrderId)` 및 `PlaceLimitOrder.AccountId`에 대한 유효성 검증(예외 또는 `OrderRejected`)을 추가한다.

[LOW] src/Hoga.Engine/PriceLimits.cs:8 - 기준가(`basePrice`)의 호가단위 정합성 및 규칙 반환값 검증 누락
  근거: `PriceLimits.Compute()`와 `MatchingEngine` 생성자(`MatchingEngine.cs:14`)는 `basePrice`가 `rule.IsOnTick(basePrice)`를 만족하는지 확인하지 않는다(예: `new MatchingEngine(9_941, new TickSizeTable())`가 허용됨). 또한 커스텀 `ITickSizeRule`이 0 이하의 호가단위를 반환할 경우 `%` 연산에서 `DivideByZeroException`이 발생한다.
  제안: `Compute()`에서 `rule.GetTickSize(basePrice) <= 0` 및 필요 시 `rule.IsOnTick(basePrice)` 검증을 추가한다.

[LOW] tests/Hoga.Engine.Tests/InvariantTests.cs:8 - 속성 테스트가 단일 `basePrice`(9,940)만 사용해 호가단위 경계 교차 시나리오를 검증하지 않음
  근거: `InvariantTests`의 모든 `[Property]` 테스트(`I1`~`I6`)가 `NewEngine() => new(9_940, Rule)`을 고정 사용한다. `9_940`의 하한(`6_960`)과 상한(`12_920`)은 모두 호가단위 10원 구간(`[5_000, 20_000)`) 안에 있어, 상·하한이 서로 다른 호가단위 구간에 걸치는 경우(예: `2_000`, `19_990`)나 `basePrice = 1` 경계는 속성 테스트에서 전혀 검증되지 않는다.
  제안: `basePrice`도 FsCheck 입력으로 생성해 다양한 호가단위 구간과 경계값에서 불변식이 유지되는지 검증한다.
