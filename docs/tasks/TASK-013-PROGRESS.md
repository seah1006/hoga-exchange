# TASK-013 진행 기록

- 작업 브랜치 `task/TASK-013` (기준 `dev/service`), 2026-11-26 서울 기준.
- 사용량 한도로 중단된 미검증 WIP `f10897b`를 이어받았다. WIP의 새 운영 코드, CI·공개 헬스, Compose, 다운로드 스크립트와 초기 테스트를 검토·실행했다. 운영 로그 범주 제한을 전체 로깅 공급자에 적용하고 로그 캡처 검사를 강화했으며, 프로세스 종료 원인을 예외 유형으로만 남기도록 했다. 배포·복원 절차와 비활성 CD 초안을 완성했다. 기존 커밋을 고치지 않았고 `.task013/`은 스테이지하지 않았다.
- 공개 서버에 접속하지 않았으며 GitHub 설정 변경, 배포, push를 하지 않았다. 새 NuGet 패키지 없음. `Program.cs`는 운영 등록/미들웨어/엔드포인트와 기존 관리자 키 검사 이동만 포함한다.

## 구현 선택과 임의 지정

- CI: Ubuntu, .NET 10 SDK, 경고 오류화, 테스트 프로젝트만 실행, NuGet/npm 캐시, TRX/Vitest 잡 요약, README 배지. Node **24 LTS 메이저 고정**은 임의 지정.
- 장애: 두 공개 헬스 주소를 기본값으로 제공하고 `HOGA_HEALTH_URL`/`SSS_HEALTH_URL`로 덮어쓸 수 있다. Actions 기본 토큰, `issues: write`, 서비스별 `outage` 이슈/반복 실패 댓글/복구 닫기. UTC 매시 17·47분, 요청 10초·잡 5분 제한은 임의 지정.
- 메트릭: `System.Diagnostics.Metrics` + `MeterListener`로 메모리 집계하고 Prometheus 0.0.4로 제공한다. 라벨은 열거된 거부 이유와 고정 처리 단계뿐이다. 히스토그램 경계(초) 0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1은 임의 지정. 직접 루프백 또는 관리자 키만 허용하고 공개 Caddy에서는 404로 차단한다. TCP 피어는 forwarded-header 처리 전에 확인한다. 기존 저널 재생·엔진 처리 전 HTTP 검증 실패는 집계하지 않는다.
- 로그: Production JSON, 정적 라우트 템플릿·상태·경과 시간·인증 사용자 ID SHA-256 앞 12자리(임의 지정). 요청 원문이나 예외 메시지 대신 허용된 범주와 안전한 필드만 출력한다.
- 백업: 시작 시와 정기 초기화 성공 직후 단일 백그라운드 작업이 실행된다. 서울 날짜 파일을 임시 파일로 완성한 뒤 같은 디렉터리에서 원자 교체한다. 보관 7개(임의 지정). 기존 저널 볼륨은 유지하고 새 `/data` 볼륨에 DB·키용 디렉터리·백업을 둔다.

## TASK-011 병합 시 연결할 곳

1. `src/Hoga.Server/Operations/BackupService.cs`의 `ISqliteSnapshot` 구현을 TASK-011이 쓰는 `Microsoft.Data.Sqlite`로 추가한다. 독립 연결을 사용해 `SqliteConnection.BackupDatabase`로 staging DB에 온라인 백업하고 모든 연결을 닫은 뒤 성공을 반환한다. `OperationsRegistration.AddOperations()`에서 `OfflineFileSnapshot` 등록을 교체한다. 운영 설정에서 `Operations:AllowOfflineDatabaseCopy`를 켜지 않는다. 이 대체 구현은 정지된 픽스처 전용, 기본 비활성, `-wal`/`-shm`/`-journal` 존재 시 건너뜀이다.
2. DB 구성은 `Market:DatabasePath`로 통일한다. 기본은 서버 content root 기준 `data/hoga.db`, Compose에서는 `Market__DatabasePath=/data/hoga.db`와 `Operations__BackupDirectory=/data/backups`이다. 인증 키의 `/data/keys` 바인딩 및 소유권은 TASK-011 통합 때 확인한다.
3. `src/Hoga.Server/MarketLoop.cs`의 `OnTick()`에서 정기 `ResetMarket("Daily", now)` 성공 후 호출하는 `IRoundBackupTrigger.AfterDailyReset(...)`이 라운드 종료 훅이다. TASK-011 병합 시 라운드 결과의 SQLite 트랜잭션 커밋이 이 호출보다 먼저 완료되도록 유지한다. 회원/계좌 초기화와 시장 초기화가 분리되면 두 작업의 성공 뒤 한 번만 호출한다. 실패나 관리자 수동 리셋에는 호출하지 않는다. 부팅 시 DB 초기화보다 백업이 먼저 실행되지 않도록 수명 주기 순서도 확인한다. 계좌 부족 등 엔진 이전 주문 거부 카운터는 TASK-011에서 고정 사유로 연결할 수 있다.

## 실행 결과

- `.NET SDK 10.0.404`: `dotnet build HogaExchange.slnx -warnaserror`와 로깅 변경 후 `--no-restore` 재빌드 모두 경고 0·오류 0. `dotnet test HogaExchange.slnx --no-build --no-restore` 및 최종 TRX 검증 통과. Engine 103 + Journal 34 + Bots 14 + Server 37 = **188개**(기준 184 + 운영 4). 테스트가 접근 제어, 메트릭 파싱/카운터, 7개 보관/미존재/사이드카, 캡처 로그 전체의 키·이메일·쿠키 비노출과 ID 해시를 검증했다.
- Node 24.22.1: 기존 설치 의존성으로 `npm run lint`, Vitest **58개**, `npm run build` 성공. 번들에 기존 `@microsoft/signalr`의 Rollup PURE 주석 경고 2건(.NET 경고 아님). Vitest JSON과 TRX의 잡 요약 추출 형식을 확인했다.
- Production 로컬 `127.0.0.1:5294`: `/healthz` 200; `/metrics` 직접 200, 전달 헤더+인증 없음 401, 전달 헤더+관리자 키 200. `text/plain; version=0.0.4; charset=utf-8`, JSON 콘솔 라우트·상태·지연 확인. 서버 종료 및 포트 미리스닝 확인.

  ```text
  hoga_orders_accepted_total 2
  hoga_orders_rejected_total{reason="OffTick"} 1
  hoga_trades_total 1
  hoga_market_processing_seconds_count{stage="journal"} 3
  hoga_market_processing_seconds_count{stage="engine"} 3
  hoga_market_queue_length 0
  hoga_connections 0
  hoga_process_working_set_bytes 64307200
  ```

- 로컬 시작 시 정지 SQLite 테스트 DB로 `hoga-20261126.db` 생성(8,192바이트), `PRAGMA integrity_check=ok`, 삽입한 테스트 행 조회 확인. 실제 온라인 백업이나 일일 결과 저장과의 결합 검증은 TASK-011 이후다.
- `actionlint` 미설치: GitHub 워크플로는 YAML 파싱, Bash/Node 스크립트 구문만 검사했으며 **Actions에서의 동작은 미검증**이다. Compose `config --quiet`와 `pull_backup.ps1` PowerShell 파서 통과. 새 `npm ci`, Linux Actions 러너, 실제 Caddy·Compose 기동, SCP, 운영 복원은 실행하지 않았다.

## 남은 결정

CD는 `deploy/DEPLOY.md`에 태그 push → Ubuntu CI·이미지 빌드 → 보호된 배포 잡 → SSH로 Hoga 갱신/헬스 및 롤백 흐름만 기술했다. 전용 SSH 키의 GitHub secret 등록, 제한 계정, production 승인자, 태그 정책과 자동배포 활성화는 사용자 결정 전까지 보류한다.
