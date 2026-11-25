# TASK-007: 공개 배포 (기존 Oracle VM에 함께 올리기)

- 브랜치: `task/TASK-007`
- 담당: 구현 에이전트 (Codex) + 메인(실제 서버 반영)
- 선행: TASK-004, TASK-006B 병합 후
- 참고: 골목장부 배포 문서 `seoul-startup-sim/docs/DEPLOY.md` (같은 서버)

## 서버 사실 (2026-11-25 메인 확인)
- Oracle Always Free `VM.Standard.E2.1.Micro`(메모리 1GB, 1/8 OCPU), 인스턴스 `sss-web`, 공개 IP 158.180.72.47, Ubuntu 24.04 Minimal, Docker + Caddy, 2GB swap.
- 골목장부가 컨테이너 제한 합 900MiB(DB 300 + 앱 520 + Caddy 80), 실측 사용 약 443MiB(2026-11-20).
- 접속 키 `~/.ssh/sss_oracle`은 **이 실습실 PC에 없다**. 실제 반영은 키가 있는 PC에서 하거나 사용자가 키를 옮겨야 한다(키는 저장소·문서에 넣지 않는다).

## 목표
골목장부를 해치지 않고 hoga-exchange를 `https://hoga-158-180-72-47.sslip.io`(임의 지정)로 공개한다.

## 만들 것
- `deploy/docker-compose.hoga.yml`: .NET 서버 컨테이너 하나(화면 정적 파일 포함 또는 서버가 `web/dist` 제공). 메모리 제한 160MiB(임의 지정), `DOTNET_GCHeapHardLimit`로 힙 상한, 저널 볼륨.
- 기존 Caddy에 사이트 블록 하나 추가하는 방법(골목장부 Caddyfile 수정이 필요하면 diff로 제시하고 메인이 반영). 앱 포트는 호스트에 노출하지 않는다.
- `deploy/deploy_hoga.ps1`: PC에서 `dotnet publish`(linux-x64)와 `npm run build` 후 전송·재시작. 서버에서 빌드하지 않는다.
- 배포 전후 점검: 서버 메모리 여유(`free -m`), 두 사이트 헬스체크, 골목장부 응답 시간 변화.

## 하지 않을 것
- 골목장부 DB·데이터 변경, 서버 OS 설정 변경(필요하면 메인에게 제안)

## 완료 조건
- 로컬 리허설(Compose)로 메모리 사용량 측정 표
- 실제 반영은 메인이 키가 있는 환경에서 하고, 공개 주소 헬스체크와 화면 확인 결과를 기록
