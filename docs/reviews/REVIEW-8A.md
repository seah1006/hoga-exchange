# REVIEW-8A: 1단계 리뷰(규칙 기준), 서비스 변경 전체

- 범위: `git diff presentation-v1..dev/service` (61개 파일, +3277/-371). 회원·세션·CSRF, 주문 소유권, 저널 기반 계좌 원장, 랭킹, SQLite, 운영(CI·장애 알림·메트릭·로그·백업), 화면 회원·계좌·랭킹.
- 리뷰어: Claude Sonnet 5.5 (읽기 전용, 코드 수정 없음)
- 기준: REVIEW_GUIDE.md, AGENTS.md, ADR-0005, CONTRACT v0.5, TASK-011·012·013(+PROGRESS), INTEGRATE-011-013-PROGRESS. 공개 서비스이므로 보안은 P0 기준.
- 개수: **P0 3건, P1 9건, P2 9건**
- 확인: `dotnet test HogaExchange.slnx` 통과(Journal 34, Bots 14, Engine 103, Server 42, 실패 0). 코드 읽기 기반 리뷰이며 공격은 실제로 실행하지 않았다.
- 문제 없음으로 확인한 항목: SQL 주입(EF LINQ·`ExecuteUpdate/Delete`만 사용, 마이그레이션은 고정 DDL), 주문·취소·설명 API의 소유권(`OwnsOrder`, 타인 주문과 없는 주문이 같은 404), CSRF(가입·로그인·로그아웃·탈퇴·주문·취소·관리자 쿠키 경로 모두 `Csrf` 호출, 허브에는 클라이언트 호출 메서드가 없음), 관리자 권한 상승 경로 없음(역할 부여는 CLI뿐), 운영 로그에서 비밀번호·이메일·쿠키 미기록(운영 카테고리 3개만 허용, 요청 로그는 라우트 템플릿과 사용자 해시만), 원장의 주문 보유·정산·취소 산식(가격 개선 환불 포함), 저널 기록 전 잔고 검사, 라운드 저장 후 회전·백업 순서.

---

## P0 (합치기 전에 고칠 것)

[P0] src/Hoga.Server/Auth/MemberEndpoints.cs:79-93 - 로그인 응답과 시간 차이로 이메일 존재 여부가 드러남(계정 열거)
  근거: (1) 가입된 이메일을 5회 틀리면 `429 LockedOut`, 없는 이메일은 몇 번을 틀려도 `401 InvalidCredentials`(93행). 공격자는 임의 이메일에 5회 요청만으로 존재 여부를 구별한다. (2) 없는 이메일(`user is null`)은 81행에서 비밀번호 해시 계산을 건너뛰어 응답이 수십 ms 빠르다. 존재하는 이메일은 PBKDF2를 계산한다. 시간 측정만으로 구별된다.
  제안: 없는 이메일에도 고정 더미 해시로 `PasswordHasher.VerifyHashedPassword`를 한 번 실행해 시간을 맞춘다. 잠금 상태도 외부에는 `401 InvalidCredentials`와 같은 응답으로 숨기거나(계약 v0.5의 `LockedOut` 변경 필요), 이메일이 아니라 IP·이메일 조합 단위의 제한(429)으로 일반화한다. 없는 이메일에도 동일한 실패 카운터를 쌓는 방법이 있다.

[P0] src/Hoga.Server/Auth/MemberEndpoints.cs:58,62 - 가입 시 `409 EmailTaken`으로 이메일 존재 여부가 직접 노출됨
  근거: 이메일 인증이 없는 구조에서 아무 이메일이나 넣어 보는 것만으로 가입 여부를 확인할 수 있다. IP당 시간당 10회(Members.cs:68-74)만 막는다. CONTRACT.md:182에 명시된 동작이라 계약 위반은 아니지만 점검 기준(계정 열거)에는 걸린다. 62행은 이메일 중복이 아닌 다른 `CreateAsync` 실패(예: 사용자명 검증)도 `EmailTaken`으로 잘못 돌려준다.
  제안: 선택지를 사용자가 결정한다. (a) 계약을 바꿔 가입 후 항상 같은 응답을 주고 이메일 인증으로 확정한다(이번 범위 밖: ADR-0005가 메일 발송을 제외). (b) 현 동작을 "교육용 서비스의 허용 위험"으로 ADR에 명시하고, 가입 제한을 강화(IP+전역 한도, 아래 P0 로그인 제한과 함께)한다. 어느 쪽이든 62행은 실제 사유별 코드를 돌려주거나 일반 오류로 바꾼다.

[P0] src/Hoga.Server/Program.cs:41-43, src/Hoga.Server/Auth/MemberEndpoints.cs:70-98 - 로그인에 IP 요청 제한이 없고 전역 단일 세마포어를 거침
  근거: 속도 제한 정책은 `signup`(Members.cs:68), `orders`, `admin`뿐이다. `/api/auth/login`에는 없다. 영향 세 가지. (1) 잠금 우회가 아니라 잠금 악용: 공격자가 5회 요청만으로 피해자 계정을 5분간 잠그고 반복하면 상시 잠금(DoS)이 된다. (2) 계정당 5회/5분이라 서로 다른 이메일에 흔한 비밀번호를 뿌리는 스프레이 공격은 속도 제한 없이 무한하다(최소 길이 10자, 복잡도 규칙 없음). (3) `LoginGate`(70행)가 프로세스 전체에서 로그인을 1개씩 직렬화하므로 해시 계산(수십 ms) 중 다른 모든 사용자의 로그인이 대기한다. 로그인 요청을 쏟아내면 정상 로그인이 모두 막히고 대기 요청이 무제한 쌓인다. 또 모든 시도(없는 이메일 포함)가 `LoginAudits` 행을 추가해(91행) 디스크가 무한히 늘 수 있다.
  제안: `login` 속도 제한 정책을 추가한다(IP당 분당 N회와 이메일 해시당 N회, 임의 지정). 전역 `LoginGate`는 사용자별 키 잠금(예: `ConcurrentDictionary<string, SemaphoreSlim>`)이나 Identity의 낙관적 동시성 처리로 바꾼다. `LoginAudits`에는 보관 기간(예: 30일)과 정리 작업을 둔다. 잠금 시간은 실패 횟수에 따라 늘리는 방식을 검토한다.

---

## P1 (이번에 고치거나 이유를 남기고 다음 작업으로)

[P1] src/Hoga.Server/Auth/MemberEndpoints.cs:37-42 - 로그아웃이 서버 쪽 세션을 무효화하지 않음
  근거: `SignOutAsync`는 브라우저 쿠키만 지운다. 쿠키 검증(Members.cs:53-59)은 보안 스탬프만 비교하는데 로그아웃은 스탬프를 바꾸지 않는다. 쿠키가 한 번 유출되면(공용 PC, 로그 등) 로그아웃 뒤에도 슬라이딩 14일(기본값) 동안 유효하다. 비밀번호 변경 기능이 없어 스탬프가 바뀌는 경로도 탈퇴뿐이다. 마찬가지로 관리자 역할을 DB에서 제거해도 쿠키의 역할 클레임이 만료까지 남는다(검증기가 역할 클레임을 갱신하지 않음).
  제안: 로그아웃 시 `UpdateSecurityStampAsync`(모든 기기 로그아웃, 단점: 다른 기기도 풀림)를 호출하거나 `ExpireTimeSpan`을 짧게(예: 24시간 절대 만료) 둔다. 역할 변경 시에도 스탬프를 갱신하는 절차를 CLI에 둔다.

[P1] src/Hoga.Server/Auth/MemberEndpoints.cs:100-109 - 탈퇴의 비밀번호 확인에 시도 제한이 없음
  근거: `CheckPasswordAsync`만 호출하고 `AccessFailedAsync`를 부르지 않으며 속도 제한도 없다. 쿠키를 탈취한 공격자는 잠금 없이 비밀번호를 추측해 볼 수 있다(탈퇴 성공 시 계정 삭제까지 가능). 이 엔드포인트가 비밀번호 오라클이 된다.
  제안: 로그인과 같은 실패 카운터와 속도 제한을 적용한다.

[P1] src/Hoga.Server/MarketHub.cs:19-27, src/Hoga.Server/Program.cs:137 - 허브에 Origin 검사가 없고 연결 후에는 로그아웃·세션 무효화가 반영되지 않음
  근거: SignalR은 WebSocket 연결에서 Origin을 검사하지 않는다. 쿠키가 SameSite=Lax라 다른 사이트(cross-site)에서는 쿠키가 가지 않지만, 같은 등록 가능 도메인의 다른 서브도메인(같은 서버에서 `hoga-…`와 `sss-…` 사이트를 운영)이 침해되면 same-site로 간주되어 피해자의 `accountUpdate`(잔고·평가액)를 받을 수 있다. 또 로그아웃·탈퇴 후에도 기존 연결은 `_online`(AccountMarket.cs:14)에 남아 계속 계좌 갱신을 받는다(탈퇴는 107행에서 제거, 로그아웃은 제거 경로 없음). 화면은 세션 변경 때 재연결하지만 서버가 보장하지 않는다. `sss-158-180-72-47.sslip.io`가 공개 접미사로 취급되는지는 확인하지 못했다(그렇다면 위험이 낮아진다).
  제안: 허브 맵핑에 허용 Origin 검사를 추가한다(`RequireHost`나 연결 시 `Origin` 헤더를 설정값과 비교). 로그아웃 엔드포인트에서 해당 사용자의 `_online` 연결을 끊거나, 인증 상태 변화 시 허브를 재연결시키는 서버 푸시를 둔다.

[P1] Program.cs 및 deploy/Caddyfile.hoga.snippet - 보안 응답 헤더가 없음(HSTS, CSP, X-Frame-Options/frame-ancestors, nosniff)
  근거: 이번 변경에서 로그인·탈퇴 버튼이 있는 화면이 공개되는데 코드와 Caddy 스니펫 어디에서도 해당 헤더를 설정하지 않았다(검색에서 발견되지 않음). 클릭재킹으로 주문·탈퇴 클릭을 유도할 수 있고, 관리자 키를 `sessionStorage`에 보관하므로(web/src/api/adminKey.ts:4-9) XSS 방어선(CSP)이 중요하다.
  제안: Caddy에 `header { Strict-Transport-Security …; X-Content-Type-Options nosniff; Content-Security-Policy "default-src 'self'; frame-ancestors 'none'…" }`를 추가한다. `/api/auth/*` 응답에는 `Cache-Control: no-store`를 둔다.

[P1] src/Hoga.Server/Accounts/AccountLedger.cs:57-61,99-108, src/Hoga.Server/Accounts/AccountMarket.cs:20-36 - 재생 중 원장 불변식 검증이 없고, `.round` 파일이 없으면 현재 설정으로 조용히 재구성됨
  근거: `Apply`는 현금·주식·보유분이 음수가 되어도 검사하지 않는다(`checked`는 오버플로만 잡음). 라운드 메타데이터 `.round`가 사라지거나 다른 세대의 것이 놓이면 `LoadRound`(23-33행)는 오류 없이 현재 `InitialCash` 설정과 현재 날짜로 새로 만든다. 그 상태로 저널을 재생하면 라이브 때와 다른 잔고가 되어도 재시작이 성공하고 `Crash`/해시 검증도 같은 틀린 원장끼리 비교한다. DEPLOY.md가 "`.round`와 저널을 한 세트로 보존"이라고 적었지만 코드가 이를 강제하지 않는다. 또 백업은 SQLite만이라(BackupService.cs:68-86) 저널·`.round`·키는 자동 백업이 없다.
  제안: 저널이 비어 있지 않은데 `.round`가 없으면 시작을 거부한다(운영자 개입 요구). `Apply` 끝에서 해당 계정 현금·주식·보유분 비음수와 `_open`의 보유분 합 일치를 검사하고 위반 시 경보 로그와 안전 정지를 한다. 속성 테스트(LedgerProperties)에 "음수 잔고 없음" 단언을 추가한다.

[P1] src/Hoga.Server/Accounts/AccountMarket.cs:64-109 - 시장 루프 안에서 SQLite I/O를 기다리며, SQLite 기본 설정(저널 모드 DELETE, 쓰기 잠금 경합)을 씀
  근거: 라운드 저장(`SaveRound`)과 탈퇴(`Withdraw`)는 단일 시장 루프에서 실행되고, 로그인 감사 기록·가입은 별도 요청 스레드에서 같은 DB 파일에 쓴다. WAL을 켜지 않아 쓰기 중에는 읽기도 막히고 busy timeout 기본값(30초) 동안 루프가 정지할 수 있다. 정지 중에는 모든 주문과 체결이 멈춘다(헬스체크도 `market.Health` 경유라 실패). INTEGRATE 진행 기록은 "WAL을 켠 원본"을 테스트했다고 쓰지만 운영 연결 문자열(Members.cs:30-31)에는 WAL 설정이 없다.
  제안: 시작 시 `PRAGMA journal_mode=WAL; busy_timeout=…`를 설정한다. 라운드 저장은 루프 밖에서 스냅샷(순위 배열)을 넘겨 실행하되 회전 전 완료를 보장하는 구조를 검토한다.

[P1] src/Hoga.Server/Accounts/MarketEndpoints.cs:11-36, Members.cs:68-74 - 가상 다계정(여러 계정 생성)으로 랭킹을 조작할 수 있고 계정 단위 제한이 없음
  근거: 이메일 인증이 없고 가입 제한은 IP당 시간당 10개뿐이다(ADR-0005가 위험을 인정하고 "완화"라 적음). 계정 A가 계정 B들의 매도 호가를 높은 가격에 사 주는 식으로 자산을 한 계정에 모을 수 있다(자기 체결 방지 없음, 계정 간 합의 체결 허용). 1위 상품(랭킹) 자체가 서비스의 핵심이라 무결성 영향이 크다. 주문 제한도 IP 단위(20건/초)라 계정당 한도가 없어 한 사용자가 여러 IP로 저널을 키울 수 있다.
  제안: (임의 지정 값은 결정 필요) 같은 IP·기기에서 가입한 계정끼리의 체결 표시, 가입 후 일정 시간 랭킹 제외, 계정당 주문 속도 제한을 검토한다. 최소한 "랭킹은 비공식"이라는 고지를 화면에 둔다.

[P1] src/Hoga.Server/Auth/MemberEndpoints.cs:91, deploy/pull_backup.ps1:43, deploy/DEPLOY.md - 개인정보 보관·삭제 범위가 탈퇴 시점에 맞지 않음
  근거: (1) `LoginAudits`의 IP 앞 3바이트(/24)는 개인정보로 볼 수 있는데 보관 기간과 삭제가 없다(탈퇴와 연결되지 않아 지워지지도 않는다). (2) 탈퇴 후에도 최근 7일치 SQLite 백업(BackupService.cs:51)에 이메일·비밀번호 해시가 남고 `pull_backup.ps1`로 내려받은 로컬 사본은 기한이 없다. (3) `pull_backup.ps1:43`의 파일 권한 제한(`chmod 600`)은 비Windows 한정이라(`-not $IsWindows`) Windows PC에서는 ACL 제한이 없다. (4) 보관 라운드 결과(`RoundEntry.MemberId`)와 저널의 `u-…` 계정 ID는 남는다(닉네임만 익명화). 사용자 ID와 이메일의 연결이 삭제되면 사실상 가명 처리지만 개인정보 안내(TASK-012 8항)에 이 범위가 적혀 있는지 확인이 필요하다.
  제안: 개인정보 안내에 백업 보관(최대 7일+로컬 사본)·로그인 기록 보관 기간·라운드 기록 가명 보관을 명시한다. `LoginAudits`는 30일 후 삭제. 스크립트는 Windows에서 `icacls`로 소유자 전용 권한을 설정한다.

[P1] tests/Hoga.Server.Tests/MemberTests.cs - 보안·경계 테스트가 비어 있음
  근거: 파일의 테스트는 세 개(계약 흐름, 소유권·재생·탈퇴, 재시작)다. 없는 것: 이메일 열거 응답 동일성, 로그인 속도 제한, 로그아웃 후 쿠키 재사용, 허브 `accountUpdate`가 본인 연결에만 가는지(타인·익명 연결 미수신), 탈퇴 중 도착한 주문 거부(`_withdrawn`), 단일가(VI) 중 부분 체결의 원장 정산, 초기화 직전 미체결 주문의 보유분 해제, 음수 잔고 불변식. 허브 관련 서버 테스트도 MarketTests의 시장 데이터 전송뿐이다(계좌 비공개 검증 없음).
  제안: 위 항목별 테스트를 추가한다. 특히 허브 비공개 테스트와 단일가 중 부분 체결 정산 테스트를 먼저 둔다.

---

## P2 (기록만)

[P2] src/Hoga.Server/Auth/Members.cs:32-35 - Data Protection 키가 평문 XML로 `/data/keys`에 저장됨
  근거: 키 보호(`ProtectKeysWith…`)가 없다. 컨테이너·볼륨이 노출되면 쿠키와 안티포저리 토큰을 위조할 수 있다(관리자 역할 쿠키 포함). Dockerfile이 0700으로 만든 점과 `.gitignore`의 키 제외는 좋다. 키는 SQLite 백업에 포함되지 않아 복구 시 전원 재로그인이 필요하다(DEPLOY.md에 기재됨).
  제안: 호스트 키 암호화 수단이 생기면 적용하고, 키 폴더를 접근 통제된 별도 백업 대상으로 둔다.

[P2] src/Hoga.Server/Auth/MemberEndpoints.cs:17-20 - 닉네임에 유니코드 혼동 문자와 제로폭 문자 허용
  근거: `char.IsControl`만 거르므로 서식 문자(제로폭 공백 등)와 형태가 같은 다른 문자로 "탈퇴한 사용자" 사칭이나 중복 닉네임 우회가 가능하다(정규화는 NFC와 대문자 변환만, 18행의 동일 비교는 정확 일치).
  제안: `UnicodeCategory.Format`·결합 문자 제한과 NFKC 정규화 후 비교.

[P2] src/Hoga.Server/Program.cs:21-35 - 신뢰 프록시를 서브넷 전체로 지정
  근거: `sss_edge` 브리지에 붙은 다른 컨테이너(골목장부 등)도 XFF를 보내 클라이언트 IP를 위조할 수 있고, 그러면 IP 제한이 무력해진다. 주석은 `ForwardLimit = 1`만 언급한다.
  제안: Caddy의 고정 IP 하나만 신뢰하거나 해당 컨테이너에 고정 주소를 부여한다.

[P2] src/Hoga.Server/Operations/OperationsRegistration.cs:52-67 - `/metrics`에 요청 제한이 없고 보호가 Caddy 차단에만 의존
  근거: 관리자 키 검증이 있지만 `admin` 속도 제한 정책 밖이라, Caddy 규칙이 빠진 배포에서는 키 무차별 대입이 가능하다. 직접 루프백 판정도 전달 헤더 부재에 의존한다.
  제안: `/metrics`에 `RequireRateLimiting("admin")`를 붙인다.

[P2] src/Hoga.Server/Accounts/AccountLedger.cs:141-145 - 주문 내역 200건 제한이 열린 주문도 목록에서 밀어냄
  근거: `Push`가 가장 오래된 항목을 제거하므로 미체결 주문이 200건 넘는 거부·체결 기록 뒤로 밀리면 화면에 보이지 않아 사용자가 취소할 수 없다(`_open`에는 남아 정산은 정상). 거부된 주문도 기록을 소모한다.
  제안: 열린 주문은 별도 목록으로 항상 반환한다.

[P2] src/Hoga.Server/Accounts/AccountMarket.cs:50-58 - 계좌 갱신 실패 로그에 예외 객체가 포함됨
  근거: `LogWarning(ex, …)`는 운영 허용 카테고리(MarketLoop)에서 예외 메시지·스택을 출력한다. 현재 예외에 개인정보는 없지만 MarketLoop의 다른 경고 로그들은 이번 변경에서 예외 유형만 기록하도록 바뀌었는데 이곳만 빠져 일관되지 않다.
  제안: `ex.GetType().Name`만 기록한다.

[P2] src/Hoga.Server/Ranking/AccountEndpoints.cs:30-41, AccountMarket.cs:37-42 - 같은 날짜에 관리자 초기화가 여러 번이면 마지막(가장 짧은) 라운드 결과가 그 날짜의 순위로 반환됨
  근거: 진행 기록에 설계로 적혀 있으나 하루 전체 순위가 짧은 라운드로 덮여 보인다. 라운드 이력도 `Rounds` 테이블에 무기한 쌓인다(삭제 정책 없음).
  제안: 일일 초기화와 관리자 초기화 결과를 구분하거나 날짜별로 최종 일일 결과를 우선 반환한다. 보관 기간을 둔다.

[P2] src/Hoga.Server/Auth/Members.cs:53-59 - 요청마다 DB 조회로 보안 스탬프를 확인함(허브 연결 협상 포함)
  근거: 정확하지만 SQLite 읽기가 시장 루프의 쓰기와 경합할 수 있다(위 WAL 항목과 연결). 짧은 캐시로 완화 가능.

[P2] .github/workflows/ci.yml, uptime.yml - 액션이 태그(v4, v7)로만 고정됨
  근거: 공급망 위험을 줄이려면 커밋 SHA 고정이 낫다. 권한은 `contents: read`, `issues: write`로 최소화되어 있어 위험은 낮다. 공개 저장소라면 장애 이슈가 공개된다는 점도 알아 둘 것.

---

## 결정이 필요한 항목 (사용자)
1. P0 가입 `EmailTaken`: 현 동작을 위험 수용으로 기록할지, 응답 통일(이메일 인증 포함)로 계약을 바꿀지.
2. P0 로그인 `LockedOut` 응답: 계약 v0.5의 `429 LockedOut` 유지 여부(유지하면 열거는 남는다).
3. P1 다계정 랭킹 조작 대응 범위(가입 제한 강화, 랭킹 자격 조건, 고지만).

## 면접에서 이렇게 설명하면 됨 (리뷰 방법)
- 무엇을: 규칙 문서 기준으로 보안(인증·CSRF·열거·소유권)과 원장 결정성을 코드 읽기와 테스트로 점검했다.
- 왜: 공개 서비스는 기능이 맞아도 열거·제한 누락 같은 운영 위험이 먼저 터진다.
- 대안 대비: 실제 공격 실행(침투 테스트)은 하지 않아 오탐 가능성은 낮지만 놓친 동적 취약점이 있을 수 있다.
