# TASK-008S 진행 기록

## 구현

- `src/Hoga.Server/LiveBots.cs`, `MarketLoop.cs`, `appsettings.json`: 봇 기본 켬(`Market:Bots`), 시드 `Market:BotSeed=42`(임의 지정). TASK-005 전략과 이름별 SplitMix64, 별도 적정가치 스트림을 재사용한다. 실험 경로는 수정하지 않았다. `src/Hoga.Bots/AssemblyInfo.cs`로 서버에만 내부 전략을 열어 공개 API를 늘리지 않는다.
- 100ms 논리 격자(실험과 같은 임의 지정)에서 최대 20틱씩(임의 지정) 단일 큐의 맨 뒤에 봇 작업 하나를 넣는다. 봇마다 살아 있는 주문만 바꾸고, REST와 같은 `Process`에서 순번과 저널을 먼저 적용한다. 경쟁하는 요청 때문에 논리 시각이 역행하지 않게 보정한다. 시나리오 명령도 같은 큐다. FatFinger는 다음 틱, PanicSell은 다음 초 격자에 1회 실행한다. PanicSell은 시연용 최소 2회 하락을 가정(임의 지정)한다. 선택 후 논리 180초(임의 지정, VI 최대 150초 고려)에 Calm으로 복귀한다. FatFinger의 미체결 잔량은 TASK-005처럼 유지한다. PanicSell의 잔량은 복귀·전환 시 취소한다. 봇이 꺼졌을 때는 시나리오 요청은 수락하되 봇을 실행하지 않는다.
- `Program.cs`, `ExperimentCache.cs`: 설정/환경 변수의 관리자 키를 `X-Admin-Key`로만 검증한다(키 미설정도 401). 비교 실험은 요청 시 계산하고 8개 FIFO 캐시(임의 지정)로 막는다. `demo`와 정규 ID는 같은 캐시 항목이다.
- `MarketProjection.cs`, `MarketLoop.cs`: 주문·체결·봉 투영은 기록 재생으로 다시 만든다. 새 연결에 1분봉(논리 24시간을 하루로 간주, 임의 지정) → 최근 체결 최대 50건(계약) → `bookSnapshot`만 직접 보낸 뒤 그룹에 합류시켜 라이브 전송에 추월당하지 않게 한다. 종료 순서 기준 최근 4,096개(임의 지정)를 두어 10배속에서도 설명을 잠시 유지하며 활성 주문은 만료시키지 않는다. 오래된 후보는 주문 ID를 실제 저널로 확인한다. 취소·시각 명령의 순번을 만료 주문으로 오인하지 않고 별도 무한 집합도 없지만, 오래된 설명 조회는 저널 앞부분을 탐색한다(루프 밖에서 한 번에 1개, 임의 지정).
- 읽기 요청 예외는 해당 요청을 실패시키고 장을 유지한다. 저널을 쓴 뒤 상태 적용 예외는 기존대로 멈춘다. crash API는 큐 장벽 안에서 엔진 해시를 직접 비교하고 진행 중인 봇 상태는 유지한다. 실제 재시작 시 기존 활성 주문과 재고는 명령·체결에서 복구하지만 라이브 시나리오/난수/적정가치 스트림 진행은 재개하지 않는다.
- 엔진·계약 변경 없음. 테스트 키는 런타임에서 생성한다. `scripts/Measure-Market.ps1`은 저널 및 로그를 저장소 밖 TEMP에 놓고 실제 재시작의 첫 상태 응답까지 시간을 잰 뒤 서버를 종료한다.

## 검증

- Windows PowerShell 7 / .NET SDK 10.0.404: `dotnet build HogaExchange.slnx -warnaserror`, 경고/오류 각 0. `dotnet test HogaExchange.slnx` 총 165개 통과(Engine 103, Journal 34, Server 20, Bots 8), 실패·건너뜀 각 0.
- `dotnet run --no-build --project src/Hoga.Experiments -- --batch --out src/Hoga.Experiments/bin/experiment-check`: 300쌍. demo JSON SHA-256 `210195f5d0ff14eb6390de39d89b52deb0e88938ed45f4c9a5f1e87bac2945b5` 동일. `SUMMARY.md`는 생성기의 LF와 Windows 작업 트리의 CRLF 차이만 있고, Git의 `--ignore-cr-at-eol` 비교 결과 변경 없음.
- curl(임시 포트 5289): 키 없음 401, 잘못된 이름 400 `UnknownScenario`, FatFinger/PanicSell/Calm 모두 200 `{"ok":true}`. 저널에 두 충격 봇 주문을 확인했다. `/api/experiments/demo`는 `exp-FatFinger-16`, seed 16, 241점씩, VI 켬/끔 최저가 9,490원/7,020원을 반환했다. 검증 서버 프로세스 종료.

## 운영 측정

- Release, 봇 켬, 10배속, 포트 5288(임의 지정), 실제 30분 목표 측정 실행 중. 240.2초 관측 중간값: 작업 집합 137,535,488B, 표본 최대 138,395,648B, 저널 20,438,639B. 같은 저널 속도를 단순 환산하면 약 7.35GB/24시간(10배속) 또는 약 0.74GB/24시간(1배속). RAM 1GB와 디스크는 별개이며 RAM 하루 한도는 30분 최종 추세와 실제 재시작 결과 전까지 확정하지 않는다. `_acceptedIds`·활성 주문은 제한하지 않았다.
- 미검증: 전체 30분 최종 샘플, 같은 저널 실제 재시작 시간, 측정 서버 최종 종료(실행 스크립트 내부에 정리 단계 있음), 하루 실측. 30분 산출물은 커밋하지 않는다.

## 제안

- 장기 운영 전 일별 저널 순환·스냅샷과 장 마감 단위 ID 정책을 TASK-003B/007에서 평가한다. 복구 시간, 디스크 및 누적 엔진 메모리를 함께 줄일 수 있지만 운영·복구 계약이 필요하다. 오래된 설명 조회가 잦아지면 디스크 인덱스와 별도 타임아웃을 검토한다.
