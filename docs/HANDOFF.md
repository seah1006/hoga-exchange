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
| TASK-008S/W 서버 봇·시나리오·실험 조회·접속 이력, 화면 주문 상태 | 병합 (집 PC). 두 단계 리뷰는 아직 안 함 |
| TASK-003B 스냅샷·헤더 엔진 옵션 | 안 함 |
| TASK-007 공개 배포 | 안 함. 아래 4장 결정 필요 |

검증(집 PC, main): 빌드 경고 0, .NET 테스트 165개(Engine 103, Journal 34, Server 20, Bots 8), 화면 lint 0 / 테스트 47개 / build 통과. 서버(봇 켬, 10배속)와 화면을 함께 띄워 브라우저에서 봇 체결, 팻 핑거 → 동적 VI 단일가, 비교 화면(시드 16: VI 켬 최저 9,490원 / 끔 7,020원), 새로고침 후 이력 복구, 체결 완료 주문 취소 버튼 숨김을 확인했다.

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
1. TASK-008S/W 두 단계 리뷰(REVIEW-6) 후 P0 수정
2. REVIEW-5 P1 남은 것: 504 뒤 주문 ID(멱등 키), 서버·봇 테스트 빈 곳
3. TASK-003B 스냅샷과 헤더 엔진 옵션(재시작 복구 시간 단축, 4장과 연결)
4. TASK-007 배포(4장 결정 후)
5. 비교 화면 그래프는 1초 시계열이라 VI 꺼짐 쪽 순간 최저가(7,020원)가 선에 안 보인다. 카드 숫자와 그래프 최저점이 달라 보이므로 "1초 종가 기준" 표기나 최저점 표시가 필요하다(TASK-005 제안)

## 4. 결정이 필요한 것 (사용자)
- **공개 서버 봇 운영 방식**: 봇이 10배속에서 실제 초당 약 256건 주문한다. 측정 결과는 5장. 엔진은 접수한 주문 ID를 계속 보관하고 저널도 계속 커져서, 1GB 서버에서 봇을 계속 돌리면 메모리·디스크·재시작 시간이 함께 늘어난다. 선택지: (가) 공개 서버는 1배속 + 봇 주문 빈도 낮춤, (나) 시연할 때만 봇 켬, (다) 매일 정해진 시각에 시장 초기화(저널 비우기). 추천은 (가)+(다)이고, 단점은 초기화 때 이전 체결 기록이 사라진다는 것이다.
- 발표일과 발표 시간(PLAN 9장)

## 5. 운영 측정 (봇 켬, 10배속, 집 PC)
측정 결과는 아래에 채운다.
