# TASK-003A 진행 기록

## 끝난 것
- 1단계: Hoga.Journal 및 테스트 프로젝트, 명령 변환기 등록, 버전/기준가격/호가 규칙 ID 헤더.
- 엔진 수정 없음. 변환기 추가로 신규 명령 확장 가능.

## 다음 할 것
- 저널 쓰기/읽기, 체크섬, 처음부터 재생, ReplayVerifier 및 손상 테스트.

## 검증
- dotnet build HogaExchange.slnx -warnaserror: 경고 0 / 오류 0.
- dotnet test HogaExchange.slnx: 엔진 54 + 저널 3 = 57건 통과.

## 막힌 것 / 제안
- 없음. 스냅샷 및 AdvanceTime 등록은 TASK-003B 예정.

## 2단계 완료
- JSON Lines, SHA-256 앞 16자 체크섬, 연속 seq 검증, 마지막 미종료 줄 경고, 처음부터 재생과 ReplayVerifier 구현.
- JournaledEngine으로 저널 선기록 순서 제공. ADR 0004 작성.
- build 경고 0/오류 0, 기존 전체 57건 통과. 다음: 손상 및 1만 명령 테스트.
