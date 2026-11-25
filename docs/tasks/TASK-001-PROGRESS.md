# TASK-001 진행 기록

## 끝난 것
- 1단계: .NET 10 솔루션·프로젝트·공통 빌드 설정, 호가단위 표와 가격제한폭, 경계·검증 예시 테스트.

- 2단계: 순번 검증, 명령·이벤트·거부, 지정가 접속매매·취소와 예제 테스트.

- 3단계: 호가창·대기 순번 조회, 호가창 SHA-256, FsCheck I1~I6, 규칙 추적표 갱신.

## 다음 할 것
- 4단계: 완료 조건 1~5 재실행.

## 막힌 것
- 없음.

## 실행한 검증
- 1단계 `dotnet build HogaExchange.slnx -warnaserror`: 성공, 경고 0 / 오류 0.
- 1단계 `dotnet test HogaExchange.slnx`: 20건 통과.

- 2단계 `dotnet build HogaExchange.slnx -warnaserror`: 성공, 경고 0 / 오류 0.
- 2단계 `dotnet test HogaExchange.slnx`: 32건 통과.
- 3단계 `dotnet build HogaExchange.slnx -warnaserror`: 성공, 경고 0 / 오류 0.
- 3단계 `dotnet test HogaExchange.slnx`: 41건 통과. FsCheck I1~I6 각각 200회.

## 제안
- 기준가격 범위는 지시서 예시대로 1~10,000,000원(임의 지정). 기준가격 1원에서 명시된 산식대로 하한은 0원이며 별도 최저 거래가격 규칙은 TASK-001에 없어 추가하지 않았다. 실거래 최저가격 제한이 필요하면 규칙을 명시해야 한다.
- NuGet 안정판 `FsCheck.Xunit 3.6.0`은 xUnit v2를 요구하므로 `xunit 2.9.3` 사용, `xunit.runner.visualstudio 4.0.1`, `Microsoft.NET.Test.Sdk 18.12.0` 사용. 엔진 패키지 참조 없음.
- 중복 ID의 범위는 해당 엔진에서 접수된 주문 전체(체결·취소 포함)로 해석했다. 거부된 ID는 재사용 가능하다. 복수 위반 시 수량 → 중복 → 가격 범위 → 호가단위 순서로 판정한다. 이벤트는 접수 → 체결별 거래 및 해당 대기 주문 완료 → 유입 주문 완료 순이다.
- 해시는 지시서대로 활성 호가창만 포함한다(명령 순번 진행 상태, 접수 ID 이력, 설정값 제외). 대기 중이 아닌 ID의 대기 순번은 `null`, 깊이 0은 빈 조회, 음수는 예외다.
- 개별 주문과 합계는 `long`이다. 집계 합이 `long.MaxValue`를 넘으면 조회에서 `OverflowException`이 발생한다. 주문 한도나 새 거부 사유는 추가하지 않았다. 운영 상한이 필요하면 별도 규칙을 정해야 한다.
