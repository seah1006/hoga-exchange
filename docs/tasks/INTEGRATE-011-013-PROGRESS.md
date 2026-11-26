# INTEGRATE-011-013 진행 기록

- 작업 브랜치 `task/INTEGRATE-011-013`, 기준 `c43b1a5`. 이 worktree에서만 수정·커밋했다. `main`, 공개 서버, push 작업은 하지 않았다.

## 항목별 결과

1. **운영 테스트 정리**: 호스트 종료 뒤 `SqliteConnection.ClearAllPools()`를 호출하고 임시 디렉터리를 삭제한다. 테스트 키 폴더를 각 팩터리의 임시 디렉터리로 분리했다. 운영 테스트 4건 모두 통과.
2. **온라인 백업**: 별도 비풀링 읽기 전용 소스·쓰기가 가능한 staging 연결로 `SqliteConnection.BackupDatabase`를 사용한다. Unix staging 권한 0600, SQLite 저널 모드 `DELETE`인 독립 파일로 저장하며 완성 후 날짜별로 원자 교체한다. 오프라인 복사 구현·설정은 삭제했다. WAL을 켠 원본을 열어 둔 상태에서 대상의 `PRAGMA integrity_check=ok`와 커밋된 값 `42`, 서울 기준 최근 7일치 보관 및 같은 날짜 덮어쓰기를 검사한다. 정기 초기화의 `SaveRound`가 `CommitAsync`를 완료하고 저널 회전·초기화를 성공한 뒤 `AfterDailyReset`을 호출한다. 가상 시계 테스트에서 시작 백업을 지운 뒤 정기 백업의 `RoundEntry` 1위 행을 읽어 이 순서를 검증했다. DB 마이그레이션은 호스트 기동 전 완료된다.
   - `.round`와 Data Protection 키를 일일 SQLite 산출물에 넣지 않기로 했다. `.round`는 원장 재생의 기준 자산과 라운드가 들어 있어 일치하는 세대의 활성 저널과 **함께** 보존해야 한다. 별도 비동기 파일 사본을 한 백업으로 보이면 저널 회전과 경합해 잘못된 복구를 유도할 수 있다. SQLite 백업과 별개로 일관된 볼륨 스냅샷 또는 서비스 정지 중 보존한다. 키는 기존 세션 유지에만 필요하고 인증 비밀이므로 별도 접근 제한·암호화 대상으로 관리한다. 이 절차를 `deploy/DEPLOY.md`에 남겼다.
3. **로그인 정책·메트릭**: HTTPS 테스트 클라이언트로 매수자와 매도자가 가입하고 인증 상태의 CSRF 토큰을 받아 주문한다. 비회원 주문 401을 먼저 확인한다. 수락 2, `OffTick` 거부 1, 체결 1, 저널·엔진 히스토그램 표본 각 3을 계속 검증한다. 관리 범위·로그 비밀 비노출 테스트도 보존했다.
4. **TASK-012 병합**: `git merge --no-ff task/TASK-012` (`6be3ef2`), 머지 충돌 없음. Vite의 `HOGA_VITE_API_TARGET`으로 REST와 허브 프록시를 함께 바꿀 수 있다(기본 5280 유지). 실행 설정 수정 중 발견한 Node `process` 타입 의존은 Vite `loadEnv`로 제거했다.
5. **실통합**: 서버 `127.0.0.1:5295`(봇 켬, 10배속, 격리 임시 데이터)와 Vite `127.0.0.1:5176`(`VITE_USE_MOCK=false`)를 기동해 동일 Vite 오리진에서 아래 흐름을 확인했다. HTTP와 SignalR WebSocket 클라이언트로 확인했고, 브라우저의 육안 레이아웃 검증은 하지 않았다.
6. **계약 v0.5 정정**: `ledgerHash`를 문서와 화면 타입·픽스처·관리자 패널에 추가했다. 복구 비교는 엔진과 원장 해시가 모두 같을 때만 일치로 표시한다. 계좌 예시는 평가액 20,555,000원, 손익 555,000원, 수익률 277bp로 정정했다. 실제 401/403 `Unauthorized` 응답과 관리자 세션 CSRF·운영 키 예외를 명시했다. 잔고가 바뀌지 않는 단일가 진입·종료에도 주문 상태가 바뀌므로 화면은 `bookSnapshot.phase` 변경 시 내 주문을 재조회하고 회귀 테스트로 확인한다.
7. **전체 검증**: .NET SDK 10.0.404에서 `dotnet build HogaExchange.slnx -warnaserror -p:NuGetAudit=false` 경고·오류 0 (`NuGetAudit=false`: 외부 감사 서비스 호출 제외). `dotnet test HogaExchange.slnx --no-build --no-restore` 192/192 통과(Engine 103, Journal 34, Bots 14, Server 41). PowerShell 스크립트 셸과 지정 Node 옵션으로 `npm ci --offline`(291개), lint(경고·오류 0), Vitest(8파일 72/72), TypeScript/Vite 빌드(76모듈) 통과. Vite 빌드의 기존 SignalR PURE 주석 경고 2건은 유지된다.

## 로컬 실통합 실제 출력 요약

```text
Vite index / entry 200, VITE_USE_MOCK=false
가입 201 → 로그인 상태 CSRF 200 (두 개의 독립 세션)
주문 200 accepted → 계좌 200, 1,000주 → 1,001주
봇 10,020원 × 1주 체결; WebSocket 초기 및 체결 후 accountUpdate 수신
내 주문 Filled, 내 체결 200; ledgerHash 확인
랭킹 200, 2명, 내 순위 2위
로그아웃 204 → 주문 401 Unauthorized
다른 로그인 사용자의 기존 주문 취소 404 NotFound
관리자 생성 CLI 종료 코드 0 → 관리자 로그인 200 → CSRF 200 → Calm 시나리오 200 ok=true (운영 키 없음)
일반 회원의 관리자 요청 403 Unauthorized
최종 Vite 설정으로 CSRF 200 및 허브 negotiate 200 재확인
서버·Vite 모두 종료, 5295·5176 미리스닝, 임시 데이터 제거
```

## 커밋

- `5ad913d` 온라인 SQLite 백업과 운영 테스트 통합
- `6be3ef2` TASK-012 머지
- `c813ffb` 화면과 회원 API 계약 v0.5 정합화
