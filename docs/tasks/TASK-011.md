# TASK-011: 회원·주문 소유권·가상 계좌·랭킹 (서버)

- 브랜치: `task/TASK-011` (기준 `dev/service`). 병합은 메인이 `dev/service`로. **`main`과 공개 서버는 건드리지 않는다**(발표 버전 보호).
- 담당: 구현 에이전트 (Codex)
- 기준 문서: `docs/adr/0005-accounts-and-rounds.md`, `docs/api/CONTRACT.md` **v0.5 3장**, `AGENTS.md`, `docs/adr/0001`, `0004`, `docs/tasks/TASK-007-PROGRESS.md`(초기화·세대), `docs/reviews/REVIEW-7-FIX.md`
- 동시 작업: TASK-012(화면, `web/`), TASK-013(운영: CI·메트릭·로그·백업, `Program.cs` 일부). 충돌을 줄이게 새 기능은 새 파일(`Auth/`, `Accounts/`, `Ranking/` 폴더 등)에 두고 `Program.cs`에는 등록 코드만.

## 만들 것
1. **영속**: EF Core SQLite(`Market:DatabasePath`, 기본 `data/hoga.db`), 마이그레이션 포함. Identity Core(사용자, 비밀번호 해시, 잠금), 라운드 결과 테이블, 로그인 감사 테이블(성공·실패, 시각, IP 앞부분만). Data Protection 키 `Market:KeysPath`(기본 `data/keys`).
2. **인증 API**: 계약 v0.5 그대로. 쿠키(HttpOnly, Secure(개발은 예외), SameSite=Lax), Antiforgery 헤더 `X-CSRF-TOKEN`, 실패 응답 코드. 가입 요청 제한(IP당 시간당 N, 임의 지정). SignalR 허브는 쿠키 인증 사용자 식별.
3. **관리자**: `Admin` 역할. CLI `dotnet Hoga.Server.dll create-admin --email <e> --nickname <n>`(비밀번호는 표준 입력에서, 화면에 출력하지 않음). 관리자 API는 Admin 세션+CSRF 또는 기존 `X-Admin-Key`.
4. **주문 소유권**: 로그인 사용자만 주문. `PlaceLimitOrder.AccountId` = 사용자 ID. 취소·설명은 소유자만(아니면 404).
5. **계좌 원장(투영)**: ADR 0005 3·4번. 라운드 시작 자산(현금 10,000,000원, 1,000주, 임의 지정)은 설정값. 사용자 원장은 첫 조회·첫 주문 때 생성. 처리 루프 안에서 저널 기록 **전에** 잔고 검사(부족하면 `InsufficientCash`/`InsufficientShares`, 저널 미기록). 체결 정산(매수자 현금 차감·주식 증가, 매도자 반대, 지정가보다 싸게 체결된 매수는 차액 현금 해제), 취소·초기화 시 hold 해제. 저널 재생으로 같은 원장이 나와야 한다. `GET /api/state/hash`에 `ledgerHash` 필드 추가(계약 확장 제안으로 PROGRESS에 기록).
6. **계좌·내 주문·내 체결 API**, SignalR `accountUpdate`(본인 연결에만).
7. **랭킹**: 현재 라운드 10초마다 계산(루프 안에서 투영만 읽기, 임의 지정), 상위 50 + me. 라운드 종료(매일 초기화·관리자 초기화) 때 최종 순위를 SQLite에 저장. 지난 라운드 조회.
8. **탈퇴**: 계약대로.
9. **봇**: `bot:` 계정은 잔고 검사 제외(무한 유동성 공급자). 사용자 주문과 봇 주문의 체결이 사용자 원장에 올바르게 반영돼야 한다.

## 테스트 (필수)
- 가입·로그인·로그아웃·me·잠금·중복 이메일/닉네임·약한 비밀번호, CSRF 없는 POST 400, 비로그인 주문 401
- 남의 주문 취소·조회 404, 관리자 API 권한(일반 사용자 403 또는 401, 계약에 맞게)
- 잔고: 매수 hold, 부분 체결 정산, 싸게 체결된 매수 차액 해제, 매도 수량 부족 거부(저널 미기록), 취소 해제, 초기화 후 새 라운드 자산
- 결정성: 사용자·봇 주문이 섞인 흐름 → crash/재시작 → 상태 해시와 원장 해시 동일
- 랭킹 계산(동률 규칙), 라운드 종료 저장·조회, 탈퇴
- 속성 기반(FsCheck): 임의 주문·취소 수열 후 사용자 전체 현금+hold 합과 주식+hold 합이 체결 정산 규칙과 맞음(현금·주식 보존)

## 하지 않을 것
- 엔진 규칙 변경, 화면, CI·메트릭(TASK-013), 이메일 인증·비밀번호 찾기, `main` 병합·배포

## 완료 조건
- 빌드 경고 0, 전체 테스트 통과(수), 로컬 서버로 curl 시나리오(가입 → csrf → 주문 → 계좌 조회 → 체결 후 계좌 → 랭킹) 결과 요약
- `docs/tasks/TASK-011-PROGRESS.md`, `task/TASK-011` 커밋(push 안 함)
