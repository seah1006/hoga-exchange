# 호가 거래소 공개 배포 (메인 에이전트 전용)

Oracle Ubuntu 24.04 x64, Docker·Caddy·2GB swap이 이미 설치된 골목장부 서버에 앱 하나만 추가한다. 골목장부의 DB·앱은 재기동하지 않는다. Hoga는 호스트 포트를 열지 않고 골목장부 Caddy가 붙은 외부 Docker 네트워크 `sss_edge`에서만 `hoga:8080`으로 응답한다. 도메인 `hoga-158-180-72-47.sslip.io`, 컨테이너 192MiB, GC 힙 96MiB, 봇 `0.10`, 매일 `05:00` 서울, 이전 저널 둘 보관은 임의 지정이다. 봇 수치는 정상 장의 시간 평균 대상이며 배치·시나리오 순간 상한이 아니다.

## 첫 배포

1. 메인이 통합 `main`에서 빌드·테스트를 확인한다. 서버에서 `free -m`, `swapon --show`, `docker stats --no-stream` 및 골목장부 HTTPS `/api/health/db` 응답을 기준선으로 기록한다. 제한 합 900+192=1,092MiB는 물리 RAM보다 크므로 스왑이 있어도 동시 피크를 감당한다고 보지 않는다. 네트워크 이름은 `docker network ls`와 `docker network inspect sss_edge`로 확인한다. 다른 이름이면 `HOGA_EDGE_NETWORK`로 지정한다.
2. 서버에 `install -d -m 700 ~/hoga` 후 `umask 077`을 적용해 `~/hoga/.env`를 안전하게 만들고 `Market__AdminKey`를 설정한다. 저장소·명령 인자·로그에 키를 쓰지 않고, 서버 파일 편집 중 화면 노출도 피한다. `chmod 600 ~/hoga/.env`로 제한한다. `docker compose config`/환경이 포함된 `docker inspect` 결과를 로그에 남기지 않는다.
3. 기존 Caddyfile을 백업한다. `~/sss`의 기존 Caddyfile 끝에 아래 diff만 넣는다(`deploy/Caddyfile.hoga.snippet`). Caddyfile이 단일 파일 bind mount인 경우 파일을 교체하는 편집기는 컨테이너가 이전 inode를 계속 볼 수 있다. 백업 후 기존 파일에 직접 덧붙이고 컨테이너 안에서 실제 내용을 확인한다.

   ```diff
   +hoga-158-180-72-47.sslip.io {
   +    reverse_proxy hoga:8080
   +}
   ```

   ```sh
   cd ~/sss
   cp -p Caddyfile Caddyfile.pre-hoga
   # 위 사이트 블록을 중복 없이 추가한다.
   docker compose -p sss -f docker-compose.serve.yml exec -T caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
   docker compose -p sss -f docker-compose.serve.yml exec -T caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
   ```

   DNS와 공개 TCP 80/443을 확인한다. 인증서 발급이나 앱 시작 전에는 새 호스트만 일시적으로 실패할 수 있다.
4. 메인의 PC에서 실행한다. SSH 호스트 키를 신뢰할 수 있는 방법으로 미리 확인한다. SDK가 PATH에 없으면 `-Dotnet 'C:\Program Files\dotnet\dotnet.exe'`를 추가한다.

   ```powershell
   pwsh ./deploy/deploy_hoga.ps1 -Server 'ubuntu@158.180.72.47' -Key '<SSH 키 파일 경로>'
   ```

   PC에서 `dotnet publish -r linux-x64`와 `npm ci`/`npm run build`(서버 모드, 같은 출처)를 실행하고 런타임 이미지로 만든다. SCP는 이미지와 Compose만 `~/hoga/releases/<release>/`에 전송한다. 서버는 `.env`를 유지하고 `docker load` 및 `docker compose -p hoga ... up -d --wait`만 실행한다. 스크립트는 컨테이너와 외부 HTTPS `/healthz`를 확인한다. `-BuildOnly`로 전송 없이 로컬 이미지만 빌드할 수 있다.
5. 공개 페이지의 HTML/JS, WebSocket 연결, 체결·호가, 관리자 팻 핑거 시연을 확인한다. 운영 `/api/admin/reset`은 기존 장을 비우므로 공개 장에서 진단용으로 호출하지 않는다. 골목장부 `/api/health/db`·기존 화면과 응답 시간, 다음 날까지 `free -m`, `docker stats --no-stream`, `swapon --show`, `df -h`, 컨테이너 재시작/OOM 상태를 기준선과 비교한다. 특히 지속적인 스왑 증가, 골목장부 지연, Hoga OOM이 있으면 Hoga부터 내린다.

## 업데이트·되돌리기

- 업데이트는 4·5번 반복. 이전 릴리스의 Compose와 이미지 태그를 보관한다(태그는 `hoga-exchange:<release>`). 동일 `-p hoga` 프로젝트와 `hoga_journal` 볼륨을 쓴다.
- 이전 릴리스로 되돌릴 때 서버 `~/hoga`에서 실행한다. 저널의 역호환성이 전제다. 저널 형식이 다르면 원본 볼륨을 삭제하지 말고 별도 복구를 계획한다.

  ```sh
  cd ~/hoga
  release='<이전 릴리스 디렉터리명>'
  HOGA_IMAGE="hoga-exchange:$release" docker compose --env-file .env -p hoga -f "releases/$release/docker-compose.hoga.yml" up -d --wait --wait-timeout 120
  ```

- 처음 배포를 철회하거나 자원이 모자라면 현재 릴리스의 위 명령에서 `up -d --wait --wait-timeout 120` 대신 `down`을 쓴다. 볼륨 보존을 위해 **`-v`를 붙이지 않는다**. Hoga 사이트 블록만 제거(또는 이후 골목장부 변경이 없으면 백업을 기존 inode에 덮어쓰기)하고 Caddy를 validate/reload한다. 골목장부 헬스와 리소스를 다시 확인한다.
- 정기 초기화는 프로세스가 실행 중일 때 관측한 서울 시각을 기준으로 한다. 종료 중 지나간 정기 작업을 재실행하지 않는다. 초기화 후 화면/REST에서 전 장 기록은 조회되지 않으며, 이전 두 개의 저널은 볼륨에만 남는다. 초기화와 컨테이너 재시작은 별개다.

## 로컬 리허설 재현

`-BuildOnly`로 생성한 이미지 태그를 `HOGA_IMAGE`에, 전용 브리지 이름을 `HOGA_EDGE_NETWORK`에 설정한다. `Market__AdminKey`는 셸의 환경 변수로만 난수 생성한다. 전용 네트워크에만 별도 Caddy를 붙여 `/healthz`와 정적 자산을 확인한다(로컬 5292, 임의 지정). 골목장부의 네트워크, 볼륨은 건드리지 않는다. `docker stats --no-stream`의 앱 메모리와 현재 저널의 `place-limit/v1` 행을 30초 간격으로 센다. 시나리오 전 평상시 신규 주문 수의 차이를 경과 시간으로 나눈다. 검증 후 리허설 Compose 프로젝트에만 `down -v`를 실행하고 별도 앱 이미지와 브리지를 제거한다. 수치는 `docs/tasks/TASK-007-PROGRESS.md`에 기록했다.
