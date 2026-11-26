# 호가 거래소 공개 배포 (메인 에이전트 전용)

Oracle Ubuntu 24.04 x64, Docker·Caddy·2GB swap이 이미 설치된 골목장부 서버에 앱 하나만 추가한다. 골목장부의 DB·앱은 재기동하지 않는다. Hoga는 호스트 포트를 열지 않고 골목장부 Caddy가 붙은 외부 Docker 네트워크 `sss_edge`에서만 `hoga:8080`으로 응답한다. 도메인 `hoga-158-180-72-47.sslip.io`, 컨테이너 192MiB, GC 힙 96MiB, 봇 `0.10`, 매일 `05:00` 서울, 이전 저널 둘 보관은 임의 지정이다. 봇 수치는 정상 장의 시간 평균 대상이며 배치·시나리오 순간 상한이 아니다.

컨테이너의 swap 포함 메모리 한도는 RAM 한도와 같은 192MiB(스왑 미사용), CPU는 0.5개, `json-file` 로그는 10MiB × 3파일로 제한한다(임의 지정).

## 첫 배포

1. 메인이 통합 `main`에서 빌드·테스트를 확인한다. 서버에서 `free -m`, `swapon --show`, `docker stats --no-stream` 및 골목장부 HTTPS `/api/health/db` 응답을 기준선으로 기록한다. 제한 합 900+192=1,092MiB는 물리 RAM보다 크므로 스왑이 있어도 동시 피크를 감당한다고 보지 않는다. 네트워크 이름은 `docker network ls`와 `docker network inspect sss_edge`로 확인한다. 다른 이름이면 `HOGA_EDGE_NETWORK`로 지정한다. Caddy와 Hoga가 함께 붙는 브리지의 사설 IPv4 CIDR을 확인해 `~/hoga/.env`의 `HOGA_EDGE_IPV4_SUBNET`에 지정한다. 해당 서브넷에서 오는 바로 앞 프록시의 `X-Forwarded-For` 한 홉만 신뢰한다.
2. 서버에 `install -d -m 700 ~/hoga` 후 `umask 077`을 적용해 `~/hoga/.env`를 안전하게 만들고 `Market__AdminKey`를 설정한다. 저장소·명령 인자·로그에 키를 쓰지 않고, 서버 파일 편집 중 화면 노출도 피한다. `chmod 600 ~/hoga/.env`로 제한한다. `docker compose config`/환경이 포함된 `docker inspect` 결과를 로그에 남기지 않는다.
3. 기존 Caddyfile을 백업한다. `~/sss`의 기존 Caddyfile 끝에 아래 diff만 넣는다(`deploy/Caddyfile.hoga.snippet`). Caddyfile이 단일 파일 bind mount인 경우 파일을 교체하는 편집기는 컨테이너가 이전 inode를 계속 볼 수 있다. 백업 후 기존 파일에 직접 덧붙이고 컨테이너 안에서 실제 내용을 확인한다.

   ```diff
   +hoga-158-180-72-47.sslip.io {
   +    @metrics path /metrics /metrics/ /metrics/*
+    respond @metrics 404
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
   pwsh ./deploy/deploy_hoga.ps1 -Server 'ubuntu@158.180.72.47' -Key '<SSH 키 파일 경로>' -HealthUrl 'https://hoga-158-180-72-47.sslip.io/healthz'
   ```

   PC에서 `dotnet publish -r linux-x64`와 `npm ci`/`npm run build`(서버 모드, 같은 출처)를 실행하고 런타임 이미지로 만든다. SCP는 이미지와 Compose만 `~/hoga/releases/<release>/`에 전송한다. 서버는 `.env`를 유지하고 `docker load` 및 `docker compose -p hoga ... up -d --wait`만 실행한다. 스크립트는 컨테이너와 외부 HTTPS `/healthz`를 확인한다. `-HealthUrl`은 배포 대상의 공개 HTTPS 헬스 주소로 필수 지정한다. 작업 트리가 깨끗하지 않으면 중단하며, 명시적으로 `-AllowDirty`를 전달한 경우만 예외다. 게시 결과에 `summary.json`이 없으면 이미지 빌드 전에 중단한다. `-BuildOnly`로 전송 없이 로컬 이미지만 빌드할 수 있다.
5. 공개 페이지의 HTML/JS, WebSocket 연결, 체결·호가, 관리자 팻 핑거 시연을 확인한다. 운영 `/api/admin/reset`은 기존 장을 비우므로 공개 장에서 진단용으로 호출하지 않는다. 골목장부 `/api/health/db`·기존 화면과 응답 시간, 다음 날까지 `free -m`, `docker stats --no-stream`, `swapon --show`, `df -h`, 컨테이너 재시작/OOM 상태를 기준선과 비교한다. 특히 지속적인 스왑 증가, 골목장부 지연, Hoga OOM이 있으면 Hoga부터 내린다.

## 업데이트·되돌리기

- 업데이트는 4·5번 반복. 이전 릴리스의 Compose와 이미지 태그를 보관한다(태그는 `hoga-exchange:<release>`). 동일 `-p hoga` 프로젝트와 기존 `hoga_journal`, 신규 `hoga_data` 볼륨을 쓴다. 이전 Compose로 롤백하면 신규 볼륨이 마운트되지 않을 수 있지만 삭제하면 안 된다.
- 이전 릴리스로 되돌릴 때 서버 `~/hoga`에서 실행한다. 저널의 역호환성이 전제다. 저널 형식이 다르면 원본 볼륨을 삭제하지 말고 별도 복구를 계획한다.

  ```sh
  cd ~/hoga
  release='<이전 릴리스 디렉터리명>'
  HOGA_IMAGE="hoga-exchange:$release" docker compose --env-file .env -p hoga -f "releases/$release/docker-compose.hoga.yml" up -d --wait --wait-timeout 120
  ```

- 처음 배포를 철회하거나 자원이 모자라면 현재 릴리스의 위 명령에서 `up -d --wait --wait-timeout 120` 대신 `down`을 쓴다. 볼륨 보존을 위해 **`-v`를 붙이지 않는다**. Hoga 사이트 블록만 제거(또는 이후 골목장부 변경이 없으면 백업을 기존 inode에 덮어쓰기)하고 Caddy를 validate/reload한다. 골목장부 헬스와 리소스를 다시 확인한다.
- 정기 초기화는 프로세스가 실행 중일 때 관측한 서울 시각을 기준으로 한다. 종료 중 지나간 정기 작업을 재실행하지 않는다. 초기화 후 화면/REST에서 전 장 기록은 조회되지 않으며, 이전 두 개의 저널은 볼륨에만 남는다. 초기화와 컨테이너 재시작은 별개다.

## 로컬 리허설 재현

`-BuildOnly`로 생성한 이미지 태그를 `HOGA_IMAGE`에, 전용 브리지 이름을 `HOGA_EDGE_NETWORK`에, 그 사설 IPv4 CIDR을 `HOGA_EDGE_IPV4_SUBNET`에 설정한다. `Market__AdminKey`는 셸의 환경 변수로만 난수 생성한다. 전용 네트워크에만 별도 Caddy를 붙여 `/healthz`와 정적 자산을 확인한다(로컬 5292, 임의 지정). 골목장부의 네트워크, 볼륨은 건드리지 않는다. `docker stats --no-stream`의 앱 메모리와 현재 저널의 `place-limit/v1` 행을 30초 간격으로 센다. 시나리오 전 평상시 신규 주문 수의 차이를 경과 시간으로 나눈다. 검증 후 리허설 Compose 프로젝트에만 `down -v`를 실행하고 별도 앱 이미지와 브리지를 제거한다. 수치는 `docs/tasks/TASK-007-PROGRESS.md`에 기록했다.

## CI 및 공개 장애 알림

- `CI`: push/PR마다 Ubuntu에서 .NET 10 경고를 오류로 빌드하고 `tests/*/*.csproj`만 실행(벤치마크 실행 제외)한다. 별도 잡에서 Node 24 LTS(메이저 고정, 임의 지정)로 `npm ci`, lint, test, build를 수행한다. NuGet/npm 다운로드 캐시를 사용하고 Actions 잡 요약에 테스트 수를 표시한다.
- `Public uptime`: 기본 브랜치에서 매시 17분·47분 UTC(30분 간격, 임의 지정) 실행하며 수동 실행도 가능하다. 호가 `https://hoga-158-180-72-47.sslip.io/healthz`, 골목장부 `https://sss-158-180-72-47.sslip.io/api/health/db`가 기본값이다. 필요하면 저장소 변수 `HOGA_HEALTH_URL`, `SSS_HEALTH_URL`로 각각 덮어쓴다. URL에 자격 증명을 넣지 않는다. 각 HTTPS 요청의 타임아웃은 10초(임의 지정)이며 2xx만 정상이다.
- 기본 `GITHUB_TOKEN`에 워크플로의 `issues: write` 권한만 부여한다. 실패 서비스별로 `outage` 라벨의 봇 이슈를 생성하고 이후 실패는 같은 이슈에 댓글을 남긴다. 복구하면 복구 댓글 후 자동으로 닫는다. Actions 예약 실행은 지연될 수 있으므로 즉시 장애 통보를 보장하지 않는다. GitHub 알림 수신 여부는 사용자의 저장소 구독 설정에 따른다.

## 메트릭과 로그

- 공개 Caddy는 `/metrics`를 관리자 키 유무와 관계없이 404로 차단한다. 앱은 프록시를 거치지 않은 직접 루프백(전달 헤더 없음) 또는 `X-Admin-Key` 검증 성공 요청에만 응답한다. Docker 사설망 전체를 신뢰하지 않는다. 배포 시 기존 호가 사이트 블록에도 위 차단 규칙을 반영한다.
- 서버 내부에서만 다음처럼 확인한다(아래 `compose`는 해당 릴리스의 `--env-file .env -p hoga -f …` 옵션을 포함한다). 키 없이 컨테이너 루프백을 사용한다.

  ```sh
  docker compose --env-file .env -p hoga -f "releases/$release/docker-compose.hoga.yml" exec -T hoga bash -ec '
    exec 3<>/dev/tcp/127.0.0.1/8080
    printf "GET /metrics HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3
    cat <&3
  '
  ```

- `hoga_orders_accepted_total`, `hoga_orders_rejected_total{reason=…}`, `hoga_trades_total`은 라이브 엔진의 신규 주문·체결 기준이다(봇 포함, 과거 저널 재생 및 HTTP 요청 단계의 검증 실패 제외). `hoga_market_queue_length`는 대기열, `hoga_connections`는 SignalR 연결 수, `hoga_process_working_set_bytes`는 RSS다. `hoga_market_processing_seconds` 히스토그램은 `journal`과 `engine`을 분리한다. 카운터는 프로세스 재시작 시 0부터 센다.
- Production 콘솔은 JSON이며 요청 로그에는 코드에 정의된 라우트 템플릿(일치하지 않으면 `(unmatched)`), 상태 코드, 지연(ms), 인증 사용자 ID SHA-256 앞 12자리(임의 지정)만 기록한다. 쿼리·본문·헤더·원본 경로는 기록하지 않으며 프레임워크 로그에 의한 우회 노출을 막기 위해 운영 범주만 허용한다. `docker compose … logs --tail=50 hoga`로 확인하되 로그에 환경 변수나 인증 데이터를 함께 출력하지 않는다. 프록시 접근 로그를 추가할 때도 별도로 같은 원칙을 적용한다.

## 볼륨과 백업

- `hoga_data:/data`는 SQLite `/data/hoga.db`, Data Protection 키용 `/data/keys`(TASK-011에서 연결), 백업 `/data/backups`를 보관한다. 기존 `hoga_journal:/data/journal`을 중첩 마운트해 현재 저널을 그대로 쓴다. 첫 적용 전 볼륨 이름과 컨테이너 `app` 사용자의 쓰기 권한을 확인하고, `down -v`나 기존 저널 볼륨 이름 변경은 금지한다.
- 시작 시, 관리자·정기 초기화 성공 직후, 매시간(임의 지정) `hoga-<UTC시각>Z.zip`을 생성한다. SQLite 온라인 백업, 활성 `journal/market.jsonl`, 해당 세대 `.round`, 보관 중인 이전 저널과 Data Protection `keys/`를 같은 묶음으로 보존한다. ZIP 생성이 완료되어야 다운로드 목록에 나타난다.
- **개인정보 보관:** 서버 백업과 내려받은 사본 모두 생성 기준 7일(임의 지정) 후 삭제한다. 내려받은 사본 삭제는 관리자가 담당한다. 백업에는 이메일, 비밀번호 해시, 세션 보호 키가 함께 들어 있으므로 접근 제한과 암호화 저장이 필수다. 로그인 감사는 30일(임의 지정) 후 정리하며 탈퇴 시 해당 사용자의 라이브 감사 행을 삭제한다. 이전 백업에는 만료까지 남을 수 있다. 저널과 과거 순위의 계정 ID는 가명으로 남는다.
- `Market__AllowedHubOrigins__0` 등으로 별도 브라우저 출처를 정확히 등록할 수 있다. 기본값은 요청과 동일한 출처다. `Market__AllowMissingRoundMetadata=true`는 신뢰할 수 있는 레거시 마이그레이션에서만 한 번 사용하고 곧바로 해제한다. 현재 설정으로 자산·라운드를 재구성하므로 일반 복원에는 쓰지 않는다.

  ```powershell
  pwsh ./deploy/pull_backup.ps1 -Server '<계정>@<호스트>' -Key '<SSH 키 파일 경로>' -Destination '<보안 디렉터리>'
  ```

### 복원 절차 (메인 에이전트 전용)

1. 신뢰된 묶음 한 개를 격리된 빈 디렉터리에 풀고 `sqlite3 -readonly <디렉터리>/hoga.db 'PRAGMA integrity_check;'`가 `ok`인지 확인한다. 앱 릴리스, `Market:BasePrice`, 저널 경로 및 `.round` 세대를 확인한다. 백업마다 다른 세대의 구성 요소를 섞지 않는다. 활성 저널의 마지막 미완성 줄만 재생 시 복구가 허용된다.
2. 호가 서비스만 멈춘 뒤 기존 `/data/hoga.db`와 `-wal`, `-shm`, `-journal` 사이드카, `journal/`, `keys/`를 함께 격리 보관한다. 다른 서비스와 볼륨은 건드리지 않는다.
3. 같은 묶음의 `hoga.db`, `journal/` 내용, `keys/`를 `/data`의 대응 위치에 설치한다. DB 사이드카를 이전 세트와 섞지 않는다. 파일 0600, 디렉터리 0700 및 이미지의 `app` 소유권을 설정한다.
4. 호가만 기동하여 `/healthz`와 `/api/state/hash`의 `stateHash`·`ledgerHash`, 라운드 조회와 로그인 상태를 점검한다. 복원 시점 이후의 변경은 돌아오지 않으며, 그 이후 로그아웃 또는 탈퇴가 발생했다면 복원 전에 세션 무효화 및 탈퇴 재반영을 운영자가 검토한다. 실패하면 다시 멈추고 보존한 데이터·릴리스로 되돌린다.

## CD 초안 (문서만, 비활성)

1. 버전 태그 `v*` push(패턴 임의 지정)가 통합 `main`의 CI 통과 커밋을 가리킬 때만 Ubuntu에서 .NET 10/Node 24로 동일 테스트를 수행하고 Linux x64 이미지와 Compose를 만든다. 배포 산출물은 불변 태그/다이제스트 및 체크섬으로 식별한다. 현재 수동 경로에 맞춰 이미지 tar와 Compose를 전달하면 별도 레지스트리 자격 증명이 필요 없다.
2. 별도 배포 잡은 승인 필수 production Environment와 동시 배포 잠금을 적용한다(설계안). 최소 권한 전용 SSH 계정, 서버 호스트 키 핀, 저장소/환경 비밀값의 전용 SSH 키가 필요하다. GitHub의 임시 키 파일과 서버의 임시 전송물은 정리하고 키·서버 `.env`는 절대 잡 로그/산출물에 포함하지 않는다. 서버에서 `docker load` 후 기존 `-p hoga` 및 볼륨으로 Hoga만 갱신한다.
3. Compose health와 두 공개 헬스를 이전 기준선과 비교한다. 실패하면 이전 호환 이미지·Compose를 사용하고, DB 스키마가 역호환되지 않으면 자동 복원을 하지 않고 운영자가 수동 대응한다. Caddy/골목장부는 CD 범위에 포함하지 않는다.

**사용자 결정 필요:** CD 활성화 여부, 전용 SSH 계정/키 발급 및 GitHub Environment secret 등록 허용 여부, 배포 승인자와 태그·릴리스 정책을 정한다. 결정 전에는 실행 가능한 배포 워크플로, 저장소 설정 또는 운영 서버를 변경하지 않는다.
