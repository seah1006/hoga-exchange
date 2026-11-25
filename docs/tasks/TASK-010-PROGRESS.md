# TASK-010 진행 기록

- [x] `AGENTS.md`, `TASK-010.md`, `PLAN.md` 4·8장, ADR 0001·0004와 엔진·서버 API 확인. 시작점 `0aed3ee`.
- [x] 엔진·서버 코드 변경 없이 BenchmarkDotNet 벤치마크와 REST 부하 콘솔 구현. 새 NuGet은 BenchmarkDotNet뿐. 두 콘솔 모두 `IsTestProject=false`.
- [x] Release 빌드(`dotnet build HogaExchange.slnx -c Release -warnaserror --no-restore`) 경고 0 / 오류 0. 12개 BDN ShortRun 완료(87.77초). 다른 worktree 실행 관찰 결과를 `docs/perf/PERF.md`에 기록.
- [x] 봇을 끈 서버에서 동시 1·8·32 부하 측정. 모두 4,096건, 수락 3,072건 / 의도한 거부 1,024건 / 오류 0건. 각 서버 종료 및 5290 리스너 없음 확인. 첫 시작 시 도구의 가상 경로가 서버로 넘어가 실패하여 저널 경로를 작업 폴더 기준 상대 경로로 다시 실행했다.
- [x] 기본 병렬 Debug 빌드의 MSBuild 하위 노드 중간 종료(`MSB4166`) 이후 `dotnet build HogaExchange.slnx -warnaserror -m:1 -p:UseSharedCompilation=false` 재실행: 경고 0 / 오류 0. 같은 옵션의 `dotnet test HogaExchange.slnx -m:1 -p:UseSharedCompilation=false`: 엔진 103 + 봇 8 + 저널 34 + 서버 24 = 총 169 통과, 실패·건너뜀 0. 콘솔 프로젝트는 테스트 대상 아님.
- [x] 측정 사양, 명령, 결과, 한계, 개선 제안을 `docs/perf/PERF.md`에 정리. 정식 측정은 메인 에이전트 이관.

임의 지정: 데이터·시드·동시도·요청 수·배치 크기·포트(5290) 등은 `docs/perf/PERF.md`에 표시했다. 모든 실측 수치는 참고용 짧은 측정이다.
