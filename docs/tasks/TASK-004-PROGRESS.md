# TASK-004 진행 기록

## 끝난 것
- `src/Hoga.Server`: HOGA 단일 시장 호스팅, 주문 접수·취소·한국어 설명, 상태 해시, 관리자 crash, SignalR, 없는 실험 404. TASK-004 1~4장·6장만 구현했다. 계약 v0.2 문서와 엔진 코드는 건드리지 않았다.
- `tests/Hoga.Server.Tests`: REST 계약/실패 형식, 주문 수명과 FIFO, 관리자 키, 모의 crash 및 호스트 재기동, SignalR 첫 호가·새 연결·VI/체결/봉을 검증한다. 관리자 키는 테스트 팩터리마다 난수로 생성한다.
- `src/Hoga.Journal`: `JournalWriter.OpenExisting` 공개 API와 `Recovery.Restore`의 선택적 `observe` 콜백을 추가했다. 기존 파일을 검증하고 마지막 미완료 줄만 제거한 뒤 연속 seq로 이어 쓴다. 내부 `AdvanceTime` 변환기를 기본 레지스트리에 등록했다(`advance-time/v1`, 이름은 임의 지정). **이 변환기는 원래 TASK-003B 범위였으므로 003B에서 중복 구현하지 않는다.** 기존 envelope·골든 파일·기대 해시는 그대로다.
- 구현 커밋: `e4d7533` 저널, `691c5f3` 서버, `ff8cb7b` 서버 테스트. 진행 기록은 별도 문서 커밋.

## 설계 요약
- 한 `BackgroundService`의 `Channel` 단일 소비자가 엔진과 저널을 독점한다. 논리적인 단일 실행 흐름이며 비동기 continuation의 OS 스레드 고정은 요구하지 않는다. 명령마다 seq 할당 → 디스크 flush를 포함한 append → 엔진 → 이벤트 순서이며, 조회·연결 초기화·복구도 같은 큐를 지나 상태를 앞지르지 않는다. 계좌 태그 `rest`, API 주문 ID `o-{seq}`, 큐 용량 1,024는 임의 지정이다.
- 요청 대기 기본 5초(임의 지정, `Market__RequestTimeoutMs`). 큐가 꽉 차거나 루프가 종료됐으면 503, 대기 초과는 504. 처리 전 타임아웃·접속 취소는 건너뛰고 이미 시작한 명령은 중간 취소하지 않는다. 기록/적용 파이프라인에 예상 못 한 실패가 나면 잘못된 상태로 후속 명령을 받지 않고 중단한다.
- 단조 경과 시간으로 논리 ms를 주입한다. 기본 1배, `Market__Speed=10`으로 시연 가능(설정 허용 1~1,000 정수, 상한 임의 지정). 시작 시 마지막 기록 시각부터 이어가고 서버가 꺼져 있던 시간은 반영하지 않는다. VI 동안 100ms마다(임의 지정) `AdvanceTime`을 저널에 넣어 주문이 없어도 마감한다. 일반장에는 주기 입력이 필요 없으므로 쓰기 및 seq/해시 변화를 피한다.
- crash는 큐의 복구 장벽이다. 저널을 닫고 기존 엔진과 주문/봉/VI 투영을 버린 다음 `Recovery.Restore`로 새 엔진과 투영을 만든다. 과거 시세 이벤트를 다시 배포하지 않으며, 장벽 전후의 같은 seq·해시를 로그로 검증한 뒤 후속 요청을 재개한다. VI 중에는 별도 HTTP 조회 사이에도 새로운 시각 명령이 진행될 수 있다.
- 변경 호가는 최신 것으로 합쳐 최소 100ms 간격(최대 10Hz)으로 배포한다. 접속/재접속은 그룹에 넣기 전에 해당 연결에 최신 호가를 한 번 즉시 보낸다. 체결·봉·VI는 개별 이벤트로 보낸다. SignalR 전송 취소 한도 1초는 임의 지정이다.
- 기본 기준가 10,000원, 저널 파일명 `market.jsonl`, `X-Admin-Key` 헤더는 임의 지정이다. 포트 5280은 지시서 값이다. 관리자 키는 `Market__AdminKey` 또는 ASP.NET 설정의 `Market:AdminKey`에서만 읽으며 설정되지 않으면 관리자 호출을 허용하지 않는다.

## 실제 최종 검증
1. `dotnet build HogaExchange.slnx -warnaserror`: 경고 0 / 오류 0. `dotnet test HogaExchange.slnx`: Engine 103 + Journal 34 + Server 10 = **147 통과**, 실패/건너뜀 0. Journal 기존 골든, 이어쓰기의 미완료 줄·완료 줄 손상·seq 연속성, `AdvanceTime` 재생 검증 포함.
2. `dotnet run --project src/Hoga.Server` 및 `curl.exe`: 5280에 `Buy` 10,050원 100주 주문 → `{"orderId":"o-1","accepted":true,"reason":null,"ruleId":null}`, crash → `{"ok":true}`. 전후 seq=1, SHA-256 `969790588f4b9843b2f6eaa5e6850be321d7fb388dcc4f35d6e147302bdeef1a` 동일. `restoredFromJournal` false → true, 서버 로그에서도 두 해시 일치. 실행한 서버 프로세스 트리 종료, 포트 응답 없음, 임시 저널 제거 확인.
3. `.env`와 `data/journal/` 무추적 및 ignore 확인. `git diff --check` 통과.
4. `task/TASK-004`에서만 커밋. main 병합/push 없음.

## 다음 할 것 / 미검증
- 5장 봇·시나리오 연결 및 실험 데이터 제공은 범위 밖. 현재 실험 API는 404, 시나리오 API는 미구현이다.
- SignalR 테스트는 TestServer의 Long Polling을 사용했다. 브라우저/Vite 프록시의 WebSocket, 고부하에서 송신 루프·503/504의 장시간 검증, 쓰기 중 실제 OS 강제 중단은 수행하지 않았다. 실제 호스트 재생과 꼬리 손상은 테스트로 검증했다.

## 제안
- 계약 v0.2에 관리자 키 헤더 `X-Admin-Key`, 503/504 및 504에서 이미 시작한 입력은 적용될 수 있다는 점을 명시한다. `DELETE`의 처음부터 없는 주문은 404, 이미 종료된 기존 주문은 200/`UnknownOrder`로 구분했다는 점도 명문화하면 화면이 분기를 명확히 처리할 수 있다.
- 현재 엔진의 `long` 호가 합산 범위 초과는 별도 계약이 없다. 공개 배포 전 수량·호가 잔량 한도를 계약으로 정하고, 긴 저널에서 복구 시간이 문제가 되면 TASK-003B 스냅샷으로 첫 재생 비용을 줄일 것을 제안한다.
