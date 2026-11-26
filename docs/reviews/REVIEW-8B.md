# 2단계 코드 리뷰: 보안·정합성

- 범위: `git diff presentation-v1..dev/service`, 코드·테스트·워크플로·deploy·API 계약을 대상으로 독립 검토
- 리뷰어: GPT-6.1 Terra
- 발견: HIGH 1건, MEDIUM 4건, LOW 0건

## HIGH

### H1. SQLite 백업만으로 주문 원장과 계좌를 재해 복구할 수 없음
- 위치: `src/Hoga.Server/Operations/BackupService.cs:68-86`, `src/Hoga.Server/MarketLoop.cs:191-196, 244-252, 327-337`, `deploy/pull_backup.ps1:19-24`
- 근거: 백업은 `snapshot.Copy(database, staging, stop)`으로 SQLite만 복사하고 다운로드도 `/data/backups`의 DB 파일만 가져온다. 계좌는 `Restore`에서 저널 명령을 `_ledger.Apply(command, events)`로 재생하여 구성하며, 저널이 없으면 `StartEmpty`로 시작한다.
- 영향: 저널 볼륨이 유실된 재해에서 DB 백업을 복원해도 해당 라운드의 현금·보유 주식·미체결 주문과 체결 이력이 초기 상태가 된다. 별도 시점의 DB만 되돌리면 회원·탈퇴 상태와 저널의 시점도 불일치할 수 있다. Data Protection 키도 다운로드 백업에 포함되지 않아 키 유실 시 기존 쿠키가 무효화된다.
- 제안: 시장 루프를 안전하게 정지하거나 일관된 체크포인트를 잡아 SQLite, 활성 저널, 라운드 메타데이터, Data Protection 키를 함께 보호·백업하고 동일 세트로 복구하는 절차와 재해 복구 테스트를 마련한다.

## MEDIUM

### M1. 이메일 존재가 가입 응답과 로그인 잠금·처리시간으로 노출됨
- 위치: `src/Hoga.Server/Auth/MemberEndpoints.cs:58-59, 79-85, 93`
- 근거: 가입 시 `FindByEmailAsync(email) is not null`이면 `409 EmailTaken`이다. 로그인은 사용자가 있을 때만 `CheckPasswordAsync`를 수행하며 잠긴 계정에는 `429 LockedOut`, 없는 계정에는 `401 InvalidCredentials`를 반환한다.
- 영향: 공격자가 가입 응답을 직접 확인하거나, 로그인 응답 시간·잠금 응답 차이를 이용해 가입 이메일을 열거할 수 있다. 로그인 CSRF 토큰은 익명으로 발급되므로 열거 방어가 아니다.
- 제안: 가입·로그인의 공개 응답을 계정 존재와 독립적으로 설계하고, 미존재·잠금 계정에도 더미 해시 검증 등 유사한 비용을 적용한다. 잠금 상태를 외부에 구별해 노출하지 않는다.

### M2. 로그인에 IP별 제한이 없어 계정 잠금 악용과 전체 로그인 지연 가능
- 위치: `src/Hoga.Server/Auth/MemberEndpoints.cs:35-36, 70-76, 79-97`, `src/Hoga.Server/Auth/Members.cs:43-44, 68-73`
- 근거: 가입에만 `.RequireRateLimiting("signup")`이 적용되고 로그인은 별도 IP 제한 없이 전역 `SemaphoreSlim LoginGate = new(1)`에서 직렬 처리된다. 기존 회원의 비밀번호 실패는 5회에 5분 잠금이다.
- 영향: 공격자는 여러 회원을 반복 잠가 로그인을 방해하거나, 여러 요청을 보내 단일 게이트와 DB 감사 기록을 소모해 다른 사용자의 로그인도 지연시킬 수 있다. 계정별 잠금은 분산 계정 대상 공격을 제한하지 않는다.
- 제안: 로그인에 IP·네트워크 및 계정 기준의 독립적인 속도 제한과 요청 크기 제한을 적용하고, 전역 직렬 구간과 대기열을 제한한다. 여러 인스턴스에서도 한도가 유지되도록 설계한다.

### M3. 탈퇴 비밀번호 확인에 실패 횟수 제한이 없음
- 위치: `src/Hoga.Server/Auth/MemberEndpoints.cs:45, 100-105`, `src/Hoga.Server/Auth/Members.cs:43-44`
- 근거: `DELETE /api/auth/me`는 로그인과 CSRF 확인 후 `users.CheckPasswordAsync(user, password)`를 직접 호출하지만 로그인처럼 `AccessFailedAsync`를 호출하지 않고 해당 엔드포인트에 속도 제한도 없다.
- 영향: 유효한 로그인 세션과 CSRF 토큰을 확보한 공격자는 로그인 잠금 정책을 우회해 해당 사용자의 비밀번호를 반복 추측할 수 있다. 세션 및 토큰 확보가 선행 조건이다.
- 제안: 탈퇴 등 민감한 재인증에도 별도의 계정·IP별 실패 제한을 적용하고 실패 감사 및 점진적 지연을 추가한다.

### M4. 다른 사용자의 체결로 현재가가 바뀌어도 내 평가액·수익률이 갱신되지 않음
- 위치: `src/Hoga.Server/Accounts/AccountMarket.cs:43, 50-56`, `src/Hoga.Server/Accounts/AccountLedger.cs:68-73, 92-111, 115-121`, `src/Hoga.Server/MarketLoop.cs:410-415`, `web/src/MemberOrders.tsx:35-60, 62-66`, `web/src/Members.tsx:74-75`
- 근거: 서버는 `AccountView`에서 현재가를 사용하지만 `Process`는 해당 명령에서 잔고가 바뀐 `accounts`에만 `PublishAccount`한다. 화면은 `accountUpdate`에서 저장한 `account.valuation`과 `account.returnBp`를 그대로 표시하고 가격 변화 자체로 계좌를 다시 조회하지 않는다.
- 영향: 내 주식을 보유한 채 다른 참가자의 체결로 시세가 변하면 호가 화면의 현재가와 계좌의 평가액·손익·수익률이 서로 다른 시점을 보여준다. 새로고침, 내 체결 또는 장 상태 변경까지 오래 유지될 수 있다.
- 제안: 가격 변화 시 보유 수량과 최신 가격으로 화면 평가액을 재계산하거나, 서버가 관련 계좌 업데이트를 제한된 빈도로 발행한다. 타인 체결만 발생하는 회귀 테스트를 추가한다.

## 검토 및 검증 메모

- 주문·계좌·개인 체결 API는 인증된 사용자 ID를 사용하고 주문 취소·설명은 소유권을 확인한다. 공개 시장 체결은 가격·수량 등만 제공한다. 검토한 범위에서 다른 사용자의 개인 계좌·주문·체결 직접 조회 경로는 확인하지 못했다.
- 상태 변경 HTTP 경로는 CSRF를 확인하며 관리자 키 경로는 별도 비밀 헤더를 사용한다. 허브에는 연결·연결 해제 외 공개 상태 변경 메서드가 없다. 운영 쿠키는 HttpOnly, Secure, SameSite=Lax로 설정된다.
- 탈퇴 시 DB 사용자를 삭제하고 각 HTTP 요청의 쿠키 principal 검증에서 사용자 존재·security stamp를 확인하며, 해당 사용자의 허브 구독을 제거한다. 관리자 생성은 서버 CLI에 한정된다.
- 주문 확인·처리는 단일 시장 루프에서 수행된다. 검토한 부분 체결·단일가·매수 가격 차액 정산과 라운드 교체는 예약 잔고를 처리하며 재시작은 저널을 재생한다. 이는 모든 장애 시나리오에 대한 증명은 아니다.
- 배포 구성상 DB·백업은 정적 웹 루트 밖의 볼륨에 있으며 Caddy는 `/metrics`를 차단한다. CI는 `pull_request`를 사용하고 `pull_request_target`은 확인되지 않았다. 실제 서버·GitHub 저장소 설정은 검증하지 않았다.
- `dotnet test HogaExchange.slnx --no-restore`: 통과 193건, 실패 0건. 부하·타이밍·재해 복구 실험은 수행하지 않았다.
