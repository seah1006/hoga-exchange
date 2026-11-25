# TASK-007: 공개 배포 준비 (운영 설정 + 배포 파일)

- 브랜치: `task/TASK-007`
- 담당: 구현 에이전트 (Codex). 실제 서버 반영은 메인.
- 선행: TASK-004, 006B, 008 병합 완료. 계약 **v0.4**(`reset`, `marketReset`, `healthz`)
- 참고: 골목장부 배포 문서 `C:\Users\my\dev\seoul-startup-sim\docs\DEPLOY.md` (같은 서버, 읽기만)
- 사용자 결정(2026-11-25): 공개 서버는 **1배속 + 봇 저빈도 + 매일 초기화**. 알고 있는 단점: 초기화 때 이전 체결 기록이 사라진다.

## 서버 사실 (2026-11-25 메인 확인)
- Oracle Always Free `VM.Standard.E2.1.Micro`(메모리 1GB, 1/8 OCPU), 공개 IP 158.180.72.47, Ubuntu 24.04 Minimal, Docker + Caddy, 2GB swap.
- 골목장부가 컨테이너 제한 합 900MiB, 실측 사용 약 443MiB(2026-11-20).
- HANDOFF 5장: 봇 10배속에서 실제 초당 약 256건 주문, 18분에 메모리 184MB·저널 71MB. 그대로는 1GB 서버에 못 올린다.

## 1. 운영 설정 (`src/Hoga.Server`)
1. 봇 빈도 설정 `Market:BotIntensity`(기본 1.0 = 지금과 같음). 공개 서버 값은 1배속에서 **초당 주문 3건 이하**(임의 지정)가 되게 정하고 실측으로 확인. 시나리오(팻 핑거 등) 동작은 빈도와 무관하게 시연 가능해야 한다.
2. 매일 초기화: 설정 `Market:DailyResetLocalTime`(기본 비활성, 공개 서버 `05:00`, 시간대 `Asia/Seoul`, 임의 지정). 처리 루프 안에서: 현재 저널을 `market-YYYYMMDD-HHmmss.jsonl`로 이름 바꿔 보관(최근 2개만 남기고 삭제, 임의 지정) → 새 저널·새 엔진·새 투영·새 봇 → `marketReset` → `bookSnapshot`. 시각 판단은 주입된 `TimeProvider`로(테스트 가능하게).
3. `POST /api/admin/reset`(관리자 키) = 같은 절차, `reason: "Admin"`.
4. `GET /healthz`(계약 v0.4).
5. 루프 견고성(REVIEW-6 8번): SignalR 전송 실패(취소 외 예외 포함)는 로그만 남기고 시장 처리를 계속. 봇 배치에서 저널 기록 **전** 예외는 그 배치만 버리고 계속, 기록 **후** 예외는 지금처럼 정지.
6. 서버가 `web/dist` 정적 파일을 같은 출처로 제공(단일 컨테이너). 개발 모드 동작(Vite 프록시)은 유지.

## 2. 화면 (`web/`, 최소 변경)
- `marketReset` 수신 시 체결·봉·내 주문 목록을 비우고 "시장이 초기화되었습니다" 안내. 목 모드 영향 없음. 테스트 추가.
- 다른 화면 파일은 TASK-009W가 동시에 고치고 있으므로 `App.tsx`, `api/` 쪽 최소 변경만 하고 실험 화면 파일(`DemoPanels.tsx` 등)은 건드리지 않는다.

## 3. 배포 파일 (`deploy/`)
- 런타임 이미지용 `deploy/Dockerfile`(서버에서 빌드하지 않는다. PC에서 `dotnet publish -r linux-x64`와 `npm run build` 산출물을 넣는다).
- `deploy/docker-compose.hoga.yml`: 컨테이너 하나, 메모리 제한 192MiB(임의 지정), `DOTNET_GCHeapHardLimit`, 저널 볼륨, 앱 포트 호스트 비노출, 골목장부 Caddy 네트워크에 붙는 방법. 환경 변수: `Market__AdminKey`는 서버의 `.env`에서만(저장소에 값 없음), `Market__Speed=1`, `Market__BotIntensity`, `Market__DailyResetLocalTime=05:00`.
- `deploy/Caddyfile.hoga.snippet`: `hoga-158-180-72-47.sslip.io`(임의 지정) 사이트 블록. 골목장부 Caddyfile에 넣을 diff를 문서로.
- `deploy/deploy_hoga.ps1`: 빌드 → 전송(scp) → 원격 `docker compose up -d` → 헬스체크. SSH 키 경로와 호스트는 매개변수(저장소에 키 없음).
- `deploy/DEPLOY.md`: 절차, 되돌리기, 메모리 점검(`free -m`, `docker stats`), 골목장부 영향 확인 방법.

## 4. 로컬 리허설 (이 PC에 Docker 29.4 있음)
- Compose로 띄워 1배속·공개 설정에서 `docker stats` 메모리를 30초 간격으로 측정. 한 명령이 8분을 넘지 않게(에이전트 실행 시간 제한) 나눠서 하고, 끝나면 컨테이너를 내린다.
- 매일 초기화는 가짜 시계 테스트로 검증하고, 실제로는 `reset` API로 한 번 확인.

## 하지 않을 것
- 실제 서버 접속·반영(메인이 한다), 골목장부 파일·DB 변경, 엔진 규칙 변경, 계약 변경(필요하면 제안)

## 완료 조건
- 빌드 경고 0, .NET·화면 테스트 전부 통과(수), 봇 빈도 실측(초당 주문 수), 리허설 메모리 표
- `task/TASK-007` 커밋(push 안 함), `docs/tasks/TASK-007-PROGRESS.md`
