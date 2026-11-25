# TASK-003A 진행 기록

## 끝난 것
1. 프로젝트·변환기 등록형 명령 직렬화·헤더 (`177812d`).
2. JSON Lines 저널 쓰기/읽기·체크섬·seq 검증·처음부터 재생·ReplayVerifier·ADR 0004 (`30ce8b6`).
3. 손상 처리와 회귀 테스트: 무작위 명령 10,000개, 마지막 줄 잘림 3종, 중간 체크섬 손상, seq 건너뜀, 헤더 불일치/손상/버전, payload seq 불일치, 미등록 명령, flush/덮어쓰기 방지, FsCheck 25회.
4. Windows에서 활성 writer와 reader의 파일 공유 모드 충돌을 테스트로 발견하여 reader를 명시적 FileShare.ReadWrite로 수정. 읽는 동안 추가 쓰기를 하지 않는 단일 소유자 사용 전제.

## 실제 최종 검증
- `dotnet build HogaExchange.slnx -warnaserror`: 성공, 경고 0 / 오류 0.
- `dotnet test HogaExchange.slnx`: 엔진 54 + 저널 19 = 총 73건 통과, 실패 0 / 건너뜀 0.
- 10,000개 재생 상태 해시 및 원래 이벤트 수열 일치, ReplayVerifier 두 번 재생 일치.
- `git diff --check`: 통과.
- `git diff -- src/Hoga.Engine`: 출력 없음. 엔진 수정 없음.
- `git ls-files`의 data/bin/obj 추적 파일 없음. 저장소 data 파일 생성 없음. 테스트는 OS 임시 폴더를 만들고 Dispose에서 삭제.
- 의미 단위 자기 브랜치 커밋만 수행. push 및 main 병합 없음.

## 설계 요약
- UTF-8 JSON Lines, seq=0 헤더(formatVersion/basePrice/tickSizeRuleId), 이후 연속 seq 명령.
- seq/type/payload 원문에 SHA-256 앞 16자 체크섬. LF로 끝나지 않은 최종 줄만 경고와 함께 폐기, 완전한 줄 손상은 예외.
- 기본 flushEvery=1(임의 지정): 매 입력 디스크 flush, 손실 최소화 대신 처리량 저하. N개 옵션과 Dispose 잔여 flush 제공.
- 형식 버전/타입 이름/테스트 난수 시드·일부 옵션은 임의 지정으로 코드 또는 ADR에 표시.

## 다음 할 것 / 미완료
- TASK-003A 구현 미완료 없음. 메인 에이전트 독립 검증·리뷰 대기.
- 스냅샷, 스냅샷 기반 복구, AdvanceTime 변환기 등록은 TASK-003B 예정.
- 기존 파일 이어쓰기는 제공하지 않는다. 현 범위는 새 저널 생성과 처음부터 재생.

## 엔진 API 관련 제안
- API 추가·변경 없음. 현재 두 명령의 재생은 공개 API로 충분하다.
- 이후 엔진이 예외를 던지는 입력까지 저널에 기록하기 전 검사해야 한다면, 상태를 변경하지 않는 명령 유효성 검사 API를 검토할 것. 현재 JournaledEngine은 엔진 예외 시 세션을 중단하며 호출자가 잘못된 enum/null 등의 입력을 사전 검증해야 한다. 거부 이벤트로 끝나는 정상적인 주문 거부는 그대로 기록·재생된다.
- TASK-002 엔진 옵션이 추가되면 헤더 설정 및 생성기를 함께 확장해야 한다. 명령 변환기는 하나 추가 등록하는 구조이다.

## 리뷰 수정
- 1단계: 기록 전 null/Side/지원 명령/연속 순번 검증, 고정 DTO와 필수/추가 필드 검증, 줄 스트리밍 읽기, 이벤트 미보관 복구, 두 재생의 명령별 이벤트 비교, 복구 오류 줄 번호·seq, 호가 규칙 해석기 주입.
- 쓰기 실패는 faulted로 전환하며 Dispose 시 실패 버퍼를 재전송하지 않음.
- 검증: dotnet build HogaExchange.slnx -warnaserror 경고 0/오류 0; dotnet test HogaExchange.slnx --no-build 엔진 54 + 저널 19 = 73 통과.
- 다음 단계: 골든 파일 및 경계/IO 회귀 테스트, ADR 갱신.
