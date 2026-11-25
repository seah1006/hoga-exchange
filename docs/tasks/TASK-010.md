# TASK-010: 성능 측정 (엔진 벤치마크와 서버 부하)

- 브랜치: `task/TASK-010`
- 담당: 구현 에이전트 (Codex)
- 선행 문서: `AGENTS.md`, `docs/PLAN.md` 4장 2단계·8장(성능 목표: 단일 종목 초당 10만 주문, 임의 지정), `docs/adr/0001`, `docs/adr/0004-journal-format.md`
- 목적: 면접에서 "얼마나 빠른가"에 숫자와 측정 방법으로 답할 수 있게 한다. 숫자를 좋게 보이게 고르지 않는다.

## 만들 것
1. `benchmarks/Hoga.Benchmarks` (BenchmarkDotNet, `Hoga.Engine`·`Hoga.Journal` 참조, 솔루션에 추가하되 `dotnet test`에는 포함되지 않게)
   - 접속매매 지정가 주문 처리(체결 없는 적재 / 체결 1건 / 여러 단계 휩쓸기), 취소, 단일가 합치가격 계산(호가 깊이 10·100·1,000), 상태 해시, 저널 쓰기(flush 매번 / 배치)
   - 처리량(ops/s)과 할당량(`MemoryDiagnoser`). 입력 호가창은 시드 고정으로 만든다.
2. `tools/Hoga.LoadTest` (콘솔): 실행 중인 서버에 REST 주문을 동시 N개 클라이언트로 보내 처리량과 지연 p50/p95/p99/최대를 잰다. 봇은 끈 서버(`Market__Bots=false`) 기준. 주문은 거부·체결·적재가 섞이게 시드 고정.
3. `docs/perf/PERF.md`: 측정 PC 사양(CPU, 코어, RAM, OS, .NET 버전), 명령, 결과 표, 해석(병목이 어디인지: 엔진 / 저널 flush / HTTP / 단일 루프 큐), PLAN 목표 대비, 한계. 임의 지정 값 표시.

## 규칙
- 엔진 코드 최적화는 하지 않는다(이번은 측정만). 병목과 개선안은 PERF.md "제안"에.
- 한 명령 실행이 8분을 넘지 않게 한다(에이전트 실행 시간 제한). BenchmarkDotNet은 `--job short` 등으로 짧게 돌리고, 긴 정식 측정 명령은 PERF.md에 적어 메인이 따로 돌린다.
- 측정 중 띄운 서버는 반드시 종료한다. 포트는 5290(임의 지정).

## 완료 조건
- 빌드 경고 0, 기존 테스트 전부 통과(수)
- 짧은 벤치마크 결과와 부하 테스트(동시 1·8·32) 결과가 PERF.md에 있음
- `task/TASK-010` 커밋(push 안 함), `docs/tasks/TASK-010-PROGRESS.md`
