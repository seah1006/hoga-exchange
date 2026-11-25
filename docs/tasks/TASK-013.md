# TASK-013: 운영 체계 (CI, 장애 알림, 메트릭·로그, 백업)

- 브랜치: `task/TASK-013` (기준 `dev/service`). `main`과 공개 서버는 건드리지 않는다.
- 담당: 구현 에이전트 (Codex)
- 기준 문서: `AGENTS.md`, `deploy/DEPLOY.md`, `docs/adr/0005-accounts-and-rounds.md`, `docs/HANDOFF.md`
- 동시 작업: TASK-011(서버 회원·계좌, `Program.cs`에 등록 코드 추가), TASK-012(화면). 서버 변경은 새 파일(`Operations/` 등)에 두고 `Program.cs`에는 등록 몇 줄만.

## 만들 것
1. **CI** `.github/workflows/ci.yml`: push·PR에서 ubuntu-latest, .NET 10 SDK, `dotnet build -warnaserror`, `dotnet test`(벤치마크 제외), Node LTS로 `web` lint·test·build. 테스트 결과 요약을 잡 요약에. 캐시(NuGet, npm). README에 배지.
2. **장애 알림** `.github/workflows/uptime.yml`: 30분마다(임의 지정) 공개 `/healthz`와 골목장부 헬스 확인. 실패하면 라벨 `outage` 이슈를 열거나(이미 열려 있으면 댓글) 복구되면 닫는다. `GITHUB_TOKEN`만 사용(추가 비밀값 없음). 수동 실행(`workflow_dispatch`) 지원.
3. **메트릭** `GET /metrics`(Prometheus 텍스트 형식): 주문 접수·거부(사유별)·체결 수, 처리 루프 큐 길이, 처리 지연 히스토그램(저널 기록·엔진 처리 구간 분리), 현재 접속 수, 프로세스 메모리. `System.Diagnostics.Metrics` 기반으로 직접 텍스트 출력(새 패키지 없이). 외부 노출 금지: 관리자 키 또는 사설망 요청만 허용.
4. **구조적 로그**: Production에서 JSON 콘솔 로그(내장 포매터). 요청 로그에 경로·상태·지연, 사용자 ID는 해시 앞부분만. 비밀값·쿠키·이메일 로그 금지.
5. **백업**: 서버 안에서 매일 초기화 직후(+ 시작 시 1회) SQLite 온라인 백업(`/data/backups/hoga-YYYYMMDD.db`, 7개 보관, 임의 지정). DB 파일이 아직 없으면(TASK-011 전) 건너뛰고 로그만. PC용 `deploy/pull_backup.ps1`(최신 백업을 scp로 내려받기, 키 경로는 매개변수).
6. **배포 문서**: `deploy/DEPLOY.md`에 메트릭 확인, 백업·복원 절차, CI·장애 알림 설명 추가. Compose에 `/data` 볼륨(저널·DB·키·백업) 정리 제안(기존 저널 볼륨 호환 유지).
7. **CD 초안**(실행하지 않음): `.github/workflows/deploy.yml.disabled` 또는 문서로 "태그 push 시 이미지 빌드→서버 배포" 설계. 서버 SSH 키를 GitHub 비밀값에 넣어야 하므로 **사용자 결정 전까지 비활성**.

## 테스트
- 메트릭 엔드포인트 접근 제어(외부 401/404, 관리자 키 200), 카운터 증가, 형식 파싱
- 백업: 임시 SQLite로 백업 생성·7개 보관 회전, DB 없음 처리
- 로그에 비밀값·이메일이 없는지(테스트 로그 캡처)
- 워크플로 YAML은 `actionlint`가 없으면 문법만 검토하고 "미검증" 표시

## 하지 않을 것
- 공개 서버 접속·배포, GitHub 저장소 설정 변경(비밀값·브랜치 보호), 회원·계좌 기능(TASK-011)

## 완료 조건
- 빌드 경고 0, 전체 테스트 통과(수), 로컬에서 `/metrics` 출력 예시, 백업 파일 생성 확인
- `docs/tasks/TASK-013-PROGRESS.md`, `task/TASK-013` 커밋(push 안 함)
