# 인계 문서 (PC 사이 이어 하기)

마지막 갱신: 2026-11-25 밤, 집 PC. 다른 PC에서 시작할 때 이 문서부터 읽는다. 에이전트 메모리는 PC 사이에 옮겨지지 않는다.

## 1. 지금 상태 (main)
| 작업 | 상태 |
|---|---|
| TASK-001 엔진 뼈대, TASK-002 단일가·VI, TASK-003A 저널·재생 | 병합 (학교 PC) |
| TASK-006A 화면 시제품(목) | 병합 (학교 PC) |
| TASK-004 서버(REST, SignalR, crash 복구) | 병합 (집 PC) |
| TASK-005 봇 시장, VI 켜짐/꺼짐 비교 실험, 시드 1~100 요약 | 병합 (집 PC) |
| TASK-006B 화면을 실제 서버에 연결 | 병합 (집 PC) |
| REVIEW-5 (위 세 작업 두 단계 리뷰) | P0 1건 수정·병합. P1은 `docs/reviews/REVIEW-5.md` 표 |
| TASK-008S/W 서버 봇·시나리오·실험 조회·접속 이력, 화면 주문 상태 | 병합 (집 PC) |
| REVIEW-6 (TASK-008 두 단계 리뷰) | P0 0건. P1 3건 수정·병합(체결 목록 누락, 실험 계산 루프 분리, 간헐 실패 테스트). 나머지는 `docs/reviews/REVIEW-6.md` |
| TASK-009S/W 실험 화면 확장(시드 100개 분포, 시드 선택, 실제 극값 표식) | 병합·배포 |
| TASK-010 성능 측정(BenchmarkDotNet, REST 부하) | 병합. 결과는 docs/perf/PERF.md(참고용 짧은 측정) |
| REVIEW-7 (009·010·007 두 단계 리뷰) | P0 0건. 배포 전 수정 7건 병합(장 간 주문 ID, 초기화 실패 복구, 요청 제한, 자원 한도 등) |
| TASK-003B 스냅샷·헤더 엔진 옵션 | 안 함 |
| TASK-007 공개 배포 | **배포됨** 2026-11-25, 아래 6장 |

검증(집 PC, main): 빌드 경고 0, .NET 테스트 169개(Engine 103, Journal 34, Server 24, Bots 8), 화면 lint 0 / 테스트 48개 / build 통과. 서버(봇 켬, 10배속)와 화면을 함께 띄워 브라우저에서 봇 체결, 팻 핑거 → 동적 VI 단일가, 비교 화면(시드 16: VI 켬 최저 9,490원 / 끔 7,020원), 새로고침 후 이력 복구, 체결 완료 주문 취소 버튼 숨김을 확인했다.

## 2. 실행 방법
```powershell
git pull --ff-only
dotnet build HogaExchange.slnx -warnaserror
dotnet test HogaExchange.slnx

# 서버 (관리자 키는 셸에서만 정한다. 저장소·문서에 쓰지 않는다)
$env:Market__AdminKey = '<직접 정한 값>'
$env:Market__Speed = '10'          # 시연 배속
dotnet run --project src/Hoga.Server   # http://localhost:5280, 봇 기본 켜짐(Market:Bots)

# 화면 (다른 창)
cd web
npm ci
$env:VITE_USE_MOCK = 'false'       # 'true'면 서버 없이 목 데이터
npm run dev                         # http://localhost:5173, /api·/hubs는 5280으로 프록시
```
- 저널 기본 경로 `src/Hoga.Server/data/journal/market.jsonl`(gitignore). 깨끗한 시장으로 시작하려면 서버를 끄고 이 파일을 지운다.
- 비교 실험 직접 실행: `dotnet run --project src/Hoga.Experiments -- --seed 16 --scenario FatFinger --out exp.json`

### PC별 주의
- 집 PC: .NET SDK 10.0.404, Node 26.5.0. npm 스크립트 전에 `$env:npm_config_script_shell=(Get-Command pwsh).Source`와 `$env:NODE_OPTIONS='--no-experimental-webstorage'`가 필요하다(없으면 "The system cannot find the path specified", jsdom localStorage 충돌).
- 학교 PC: AGENTS.md 기준 SDK 10.0.401, 경로 `C:\Users\USER\dev\hoga-exchange`.

## 3. 다음 할 일 (추천 순서)
1. 4장 봇 운영 방식 결정 후 그에 맞춘 서버 수정(봇 주문 빈도, 초기화 명령)
2. REVIEW-5·REVIEW-6 P1 남은 것: 504 뒤 주문 ID(멱등 키), 재시작 시 시나리오·봇 상태 복원, 접속 이력 전송이 루프를 잡는 문제, 오래된 주문 설명 조회의 저널 재탐색, 날짜가 바뀔 때 화면 1분봉 초기화, 봇·전송 예외가 루프를 죽이는 경로
3. TASK-003B 스냅샷과 헤더 엔진 옵션(재시작 복구 시간 단축, 4장과 연결)
4. TASK-007 배포(4장 결정 후)
5. 비교 화면 그래프는 1초 시계열이라 VI 꺼짐 쪽 순간 최저가(7,020원)가 선에 안 보인다. 카드 숫자와 그래프 최저점이 달라 보이므로 "1초 종가 기준" 표기나 최저점 표시가 필요하다(TASK-005 제안)

## 4. 결정이 필요한 것 (사용자)
- **공개 서버 봇 운영 방식**: 봇이 10배속에서 실제 초당 약 256건 주문한다. 측정 결과는 5장. 엔진은 접수한 주문 ID를 계속 보관하고 저널도 계속 커져서, 1GB 서버에서 봇을 계속 돌리면 메모리·디스크·재시작 시간이 함께 늘어난다. 선택지: (가) 공개 서버는 1배속 + 봇 주문 빈도 낮춤, (나) 시연할 때만 봇 켬, (다) 매일 정해진 시각에 시장 초기화(저널 비우기). 추천은 (가)+(다)이고, 단점은 초기화 때 이전 체결 기록이 사라진다는 것이다.
- 발표일과 발표 시간(PLAN 9장)

## 5. 운영 측정 (봇 켬, 10배속, 집 PC, Release)
측정 스크립트 `scripts/Measure-Market.ps1`. 30분 목표였으나 측정을 띄운 서브에이전트 세션이 끝나면서 서버도 약 18분(21:44~22:02)에 종료됐다.

| 실제 경과 | 작업 집합(메모리) | 저널 |
|---|---:|---:|
| 4분 | 137.5MB | 20.4MB |
| 6분 | 148.0MB | 30.8MB |
| 13분 | 181.7MB | 59.5MB |
| 17분 | 183.7MB | 70.6MB (명령 402,423줄, 논리 약 3시간) |

- 재시작 복구: 위 70.7MB 저널을 복사해 봇 끄고 띄우면 집 PC에서 7초 만에 응답(seq 402,422, 해시 일치), 복구 직후 메모리 152MB.
- 단순 환산(추정): 1배속 하루(논리 24시간)면 명령 약 320만, 저널 약 560MB, 메모리는 명령당 약 0.15KB씩 늘어 약 600MB, 복구는 집 PC 기준 1분 안팎. 공개 서버(1GB, 1/8 OCPU, 골목장부가 약 443MB 사용)에서는 메모리가 모자라고 복구도 훨씬 느릴 것이다. 그래서 4장의 봇 운영 방식 결정이 배포(TASK-007) 전에 필요하다.

## 6. 공개 서버 (2026-11-25 배포)
- 주소: https://hoga-158-180-72-47.sslip.io (골목장부와 같은 Oracle 서버, 컨테이너 `hoga-hoga-1`, 192MiB 제한)
- 설정: 1배속, 봇 빈도 0.10(평균 초당 약 2.6건), 매일 05:00(서울) 시장 초기화, 이전 저널 2개 보관. 모두 임의 지정.
- 관리자 키: Aside Vault "호가 거래소 관리자 키 (공개 서버)"와 서버 `~/hoga/.env`에만 있다. 저장소에 없다.
- 배포·되돌리기: `deploy/DEPLOY.md`. 업데이트는 `pwsh ./deploy/deploy_hoga.ps1 -Server ubuntu@158.180.72.47 -Key <키> -HealthUrl https://hoga-158-180-72-47.sslip.io/healthz -Dotnet 'C:\Program Files\dotnet\dotnet.exe'` (작업 트리가 깨끗해야 한다).
- 배포 직후 실측: 호스트 사용 335MB/952MB(여유 616MB), hoga 82MiB/192MiB, 골목장부 헬스 200.
- 발표 주의: 공개 서버는 1배속이라 VI 단일가가 실제로 2분 걸린다. 5분 발표에서는 로컬 10배속(`Market__Speed=10`)이 낫고, 공개 주소는 백업과 질의응답용으로 쓴다.
## 7. 서비스화 작업 (dev/service 브랜치, 발표 뒤 배포)
- 사용자 결정(2026-11-25): 모의투자 서비스, 이메일+비밀번호, 매일 새 라운드(현금 1,000만 원 + 1,000주, 임의 지정), SQLite, 우선순위는 계정·주문 소유권 / 가상 계좌·손익·랭킹 / 운영 체계. 설계는 `docs/adr/0005-accounts-and-rounds.md`, 계약 v0.5 3장, 지시서 TASK-011(서버)·012(화면)·013(운영).
- 발표 버전은 태그 `presentation-v1`, `main`과 공개 서버는 발표가 끝날 때까지 그대로 둔다.
- **상태(2026-11-25 밤)**: 세 작업 모두 Codex 사용량 한도로 중간에 멈췄다. 진행분은 각 작업 브랜치에 `wip:` 커밋으로 보존(검증 전, 빌드·테스트 미확인). 사용자 결정으로 Codex 한도가 풀리면 같은 브랜치에서 이어서 한다.
  - task/TASK-011 `75fc7fe`: Accounts/, Auth/, Ranking/ 초안, MarketLoop·Program 수정
  - task/TASK-012 `e854b57`: Members.tsx, MemberOrders.tsx, mockMemberSource.ts, Orders.tsx 대체 중
  - task/TASK-013 `f10897b`: .github/workflows(ci, uptime), Operations/(메트릭·백업), pull_backup.ps1
