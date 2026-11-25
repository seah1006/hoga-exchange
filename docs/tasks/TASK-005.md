# TASK-005: 봇 시장과 VI 켜짐/꺼짐 비교 실험 실행기

- 브랜치: `task/TASK-005`
- 담당: 구현 에이전트 (Codex)
- 선행: **TASK-001, TASK-002 병합 후 시작** (VI 켜기/끄기 옵션 필요)
- 선행 문서: `AGENTS.md`, `docs/PLAN.md` 7장, `docs/api/CONTRACT.md` 2장 "비교 실험 객체"

## 목표
서버 없이 엔진과 봇만으로 "같은 시드, VI만 다른" 두 시장을 돌려 계약 형식의 비교 실험 JSON을 만든다. 이 프로젝트의 간판 시연이므로 **정직한 실험**이 핵심이다.

## 만들 것

### 1. 프로젝트
- `src/Hoga.Bots` (classlib, `Hoga.Engine` 참조)
- `src/Hoga.Experiments` (console, `Hoga.Bots` 참조): `dotnet run -- --seed 42 --scenario FatFinger --out exp.json`
- `tests/Hoga.Bots.Tests`

### 2. 난수
- `System.Random`을 쓰지 않는다(.NET 버전에 따라 알고리즘이 바뀔 수 있어 재현성이 깨질 수 있음). 시드 기반 SplitMix64 또는 xoshiro256** 같은 명시적 알고리즘을 직접 구현하고, 알려진 테스트 벡터로 검증한다.
- 봇마다 `(실험 시드, 봇 이름)`에서 파생한 독립 난수 스트림을 쓴다. 봇 하나를 추가해도 다른 봇의 난수가 바뀌지 않게.

### 3. 시장 시뮬레이션
- 논리 시각을 고정 간격(100ms, 임의 지정)으로 진행. 각 틱에서 봇을 정해진 순서로 호출하고, 나온 명령에 순번을 매겨 엔진에 넣는다.
- 숨은 "적정가치"가 시드 기반 무작위 보행으로 움직이고, 일부 봇은 이를 참고한다.
- 봇:
  | 봇 | 행동 |
  |---|---|
  | MarketMaker | 적정가치 주변 양쪽에 여러 단계 호가 제시, 재고가 쌓이면 호가를 기울임 |
  | Noise | 현재가 근처 무작위 지정가 |
  | Momentum | 최근 가격이 오르면 사고 내리면 팖 |
  | PanicSeller | 시나리오 `PanicSell`에서 하락이 이어지면 점점 낮은 가격에 매도 |
  | FatFinger | 시나리오 `FatFinger`에서 정해진 시각에 큰 수량을 하한가 근처 가격으로 한 번 매도 |
- 모든 봇 주문은 호가단위·상하한가를 지킨다(엔진이 거부하면 그 횟수를 집계해 보고).

### 4. 비교 실험
- `RunExperiment(seed, scenario)` → 같은 시드로 `viEnabled=true`와 `false` 두 번 실행 → 계약의 `experiment` 객체(JSON). 초 단위 가격 시계열, VI 이벤트, 최저가·최고가.
- **정직성 규칙**:
  - 시연용 시드를 결과를 보고 고르지 않는다. 시드 1~100(임의 지정)을 모두 돌린 요약 리포트(`docs/experiments/SUMMARY.md`: 최저가 차이의 분포, VI 켠 쪽이 더 나빴던 시드 수)를 만들고, 시연 시드는 "중앙값에 가장 가까운 시드"처럼 규칙으로 정해 리포트에 적는다.
  - VI가 항상 낫다고 테스트로 단정하지 않는다. 결과는 측정하고 보고한다.

### 5. 테스트
- 난수 구현이 테스트 벡터와 일치
- 같은 시드·시나리오 → 실험 JSON의 해시가 같음(두 번 실행)
- 봇 하나를 추가해도 기존 봇의 난수 수열이 같음
- 모든 시나리오에서 엔진 거부 0건(또는 거부 사유별 집계가 리포트에 나옴)

## 하지 않을 것
- 서버, 화면, 엔진 규칙 변경(엔진 공개 API 추가가 필요하면 최소한으로 하고 보고)

## 완료 조건
1. `dotnet build HogaExchange.slnx -warnaserror`, `dotnet test HogaExchange.slnx` 통과(테스트 수 보고)
2. `dotnet run --project src/Hoga.Experiments -- --seed 42 --scenario FatFinger --out exp.json` 실행 결과 요약(두 run의 최저가, VI 발동 횟수)
3. 시드 1~100 요약 리포트 생성
4. `task/TASK-005` 브랜치에 의미 단위 커밋 (push 하지 않음)
