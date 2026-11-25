# REVIEW-4A: TASK-003A 1단계(규칙 기반) 리뷰

- 범위: `git merge-base main HEAD`..HEAD (task/TASK-003A, 3커밋 `177812d`, `30ce8b6`, `6472328`). src/Hoga.Journal, tests/Hoga.Journal.Tests, ADR 0004, PROGRESS.
- 기준: REVIEW_GUIDE.md, AGENTS.md, TASK-003(단계 나눔), ADR 0001/0004
- 등급별 개수: **P0 0건 / P1 4건 / P2 6건**
- 검증: `dotnet test`(임시 복사본, 저장소 비변경) 통과. 추가로 임시 프로브 테스트 4개를 돌려 확인함(아래 "확인한 것"). 프로브는 복사본과 함께 삭제함.

## 확인한 것 (문제 없음)
- 순서: `JournaledEngine.Process`는 `Append`(+flush) 후 `Engine.Process`. 직렬화 실패(미등록 타입)는 파일에 쓰기 전에 예외.
- 체크섬: seq/type/payload 원문, 헤더 포함, SHA-256 앞 16자 소문자 hex, invariant 숫자. 쓰기 경로의 payload 텍스트와 읽기 경로 원문이 일치함을 프로브로 확인(한글, `+<>"\`, U+2028, 이모지가 든 OrderId 왕복 성공).
- 구분: LF 없는 끝 조각만 경고로 버림(UTF-8 다중 바이트 중간 절단도 프로브로 확인, 경고 1건). LF로 끝난 줄의 JSON/UTF-8/체크섬/seq 건너뜀/payload seq 불일치는 전부 예외. 조용히 건너뛰는 경로 없음.
- CRLF: 파일을 CRLF로 바꿔도 읽기 성공(프로브). 쓰기는 `NewLine="\n"`, BOM 없음.
- 엔진 미수정(`git diff main HEAD -- src/Hoga.Engine` 비어 있음), data/·*.jsonl 추적 파일 없음, `data/journal/` gitignore 있음, 비밀값 없음.
- 테스트 정리: 임시 폴더 `Dispose`에서 삭제. writer는 메서드 끝에서 먼저 닫힘.
- 엔진 쪽 규칙(결정성, 정수 가격 등)은 엔진 무변경이므로 해당 없음.

## P1

[P1] src/Hoga.Journal/Journal.cs:171-177, 139-143 - 엔진이 예외를 던지는 명령이 저널에 먼저 들어가 복구 불능("독약 명령")이 된다
  근거: 프로브로 확인. `PlaceLimitOrder(Side=(Side)7)`를 `JournaledEngine.Process`에 넣으면 Append 성공 후 엔진이 ArgumentOutOfRangeException. 세션은 중단되지만 저널에는 그 줄이 남아 있다. 이후 `Recovery.Restore`는 같은 예외를 줄 번호 없이 그대로 던져 서버 재시작이 영구 실패한다. ADR은 "호출자가 사전 검증"이라고만 적었고 코드는 강제하지 않는다. 쓰기 전 기록이라는 지시서 방침 자체는 맞다.
  제안: (a) `JournaledEngine`에 엔진 쪽 상태 비변경 검증 단계를 두거나(PROGRESS가 이미 제안), 최소한 (b) Restore가 엔진 예외를 `InvalidDataException($"seq {n} 재생 중 엔진 예외")`로 감싸 원인 seq를 알리고, (c) 이 상황의 회귀 테스트와 ADR 문구("복구 불능 가능")를 추가. 메인과 정책 결정 필요: 예외 명령을 저널에서 무시할지(조용한 건너뜀 금지 원칙과 충돌) 중단할지.

[P1] src/Hoga.Journal/Serialization.cs:64-78 - TASK-002 옵션을 받을 헤더 자리와 버전 정책이 아직 없다
  근거: 헤더는 `(FormatVersion, BasePrice, TickSizeRuleId)`뿐이고 `CreateEngine`은 `new MatchingEngine(BasePrice, rule)` 고정. `FormatVersion != CurrentVersion`이면 예외라서 옵션을 넣으며 버전을 올리면 v1 저널을 못 읽는다. TASK-002 병합 후 `EngineOptions(ViEnabled, IsKospi200, RandomSeed)`를 `MatchingEngine(basePrice, tickRule, options)`에 넘겨야 하는데 특히 RandomSeed가 헤더에 없으면 재생 결정성이 깨진다. 명령 쪽은 변환기 한 개 추가(`advance-time/v1`)로 충분해 구조가 받아준다.
  제안: 003B(또는 병합 직후)에 `JournalHeader`에 nullable `Options`(ViEnabled, IsKospi200, RandomSeed)를 추가. v1(옵션 없음)=엔진 기본 옵션으로 해석하고 읽기는 `FormatVersion <= Current` 허용, 쓰기는 항상 최신 버전. `expectedHeader` 비교는 record 동등성이므로 옵션도 값 동등 record로 유지. 이벤트 record 추가 시 `ReplayVerifier`의 `SequenceEqual`이 값 동등이 되도록 확인.

[P1] src/Hoga.Journal/Serialization.cs:16-23 - 저널 와이어 형식이 엔진 CLR 타입의 속성명과 enum 숫자에 직접 묶여 있다
  근거: `JsonCommandConverter<T>`가 기본 옵션으로 엔진 record를 그대로 직렬화한다(PascalCase 속성, `Side`는 숫자). 엔진 record 속성 이름 변경, 속성 추가(TASK-002가 명령을 확장), enum 순서 변경이 `place-limit/v1` 저널을 조용히 다른 의미로 만들거나(누락 속성은 기본값) 읽기에 실패하게 한다. 체크섬은 파일 변조만 막는다. 형식을 고정하는 골든 테스트도 없다.
  제안: 변환기별로 고정 DTO(또는 명시적 속성명) 사용, enum은 문자열 또는 숫자 고정값 문서화, 그리고 `place-limit/v1`, `cancel/v1`의 정확한 한 줄 텍스트를 고정한 골든 테스트 추가. 누락/추가 속성은 거부하도록 `UnmappedMemberHandling.Disallow`, 필수 속성 지정 검토.

[P1] tests/Hoga.Journal.Tests/JournalTests.cs - 중점 항목 중 테스트 빈 곳
  근거: 실제 동작은 프로브로 정상이었으나 회귀 테스트가 없다: CRLF 파일, 비ASCII/특수문자 ID 왕복, 중간의 잘못된 UTF-8, 빈 파일, 잘못된 enum으로 엔진 예외(위 P1), writer IO 실패 후 faulted 동작, 다중 바이트 문자 중간 절단. 또한 절단 테스트(1/25/100바이트)는 마지막 줄 길이에 의존하는 암묵 가정이 있다.
  제안: 위 케이스를 추가. 절단 테스트는 마지막 줄 길이를 계산해 경계값(LF만 제거, 줄 전체 제거, 줄 직전 LF까지 제거)을 명시한다.

## P2

[P2] src/Hoga.Journal/Journal.cs:66-70, 58 - faulted 상태 Dispose도 StreamWriter가 버퍼를 flush한다
  근거: `_writer.Dispose()`는 내부 버퍼를 쓴다. Append 중 IO 예외로 faulted가 된 뒤 Dispose하면 호출자에게 실패로 보고된 명령 줄이 부분/전체로 파일에 남을 수 있다(이후 재생에는 포함).
  제안: faulted면 버퍼를 버리고 스트림만 닫거나, 이 동작을 ADR에 적는다.

[P2] src/Hoga.Journal/Journal.cs:119-120 - UnknownCommandTypeException은 catch 대상이 아니라 줄 번호 없이 올라온다
  근거: 필터는 Json/InvalidData/Argument/InvalidOperation만 감싼다. 의도(명확한 예외)는 충족하나 몇 번째 줄인지 모른다.
  제안: 줄 번호를 메시지에 넣은 채 감싸되 `InnerException`으로 타입 유지, 또는 예외에 LineNumber 속성 추가.

[P2] src/Hoga.Journal/Journal.cs:97-103 - 봉투(envelope)의 추가 필드와 enum 범위는 검증되지 않는다
  근거: 체크섬은 seq/type/payload만 덮는다. 줄에 임의 필드가 붙어도 통과하고, 알 수 없는 enum 값은 읽기에서 통과해 엔진에서 터진다(위 P1 연결).
  제안: 읽기 시 `UnmappedMemberHandling.Disallow`, 변환기 Read에서 enum 검증.

[P2] src/Hoga.Journal/Journal.cs:153-154 - ReplayVerifier에 expectedHeader 인자가 없다
  근거: `Recovery.Restore`는 받지만 Verifier는 전달 경로가 없다. 또 같은 프로세스에서 두 번 재생하므로 프로세스 전역 상태에 의존하는 비결정성은 못 잡는다.
  제안: 인자 추가, 가능하면 별도 프로세스/새 엔진으로 검증했음을 문서화.

[P2] tests/Hoga.Journal.Tests/SerializationTests.cs:24 - `advance-time/v1`을 "미등록" 예시로 사용
  근거: 003B에서 이 변환기를 등록하면 이 테스트가 의미를 잃거나 깨진다.
  제안: `"no-such-type/v1"` 등 영구 미등록 이름으로 교체.

[P2] src/Hoga.Journal/Journal.cs:35-43, 81-84 - 운영 한계 기록
  근거: 헤더 쓰기 실패 시 부분 파일이 남고, 디렉터리 자동 생성 없음, 전체 파일을 메모리에 읽음(ADR에 단점으로 이미 명시), LF만 잘린 완전한 마지막 레코드(JSON 온전)도 폐기(ADR 명시). 줄 경계에서 정확히 잘려 나간 꼬리는 원리상 탐지 불가.
  제안: 현 범위에서는 문서화로 충분. 003B의 스냅샷에서 마지막 seq를 별도 기록하면 꼬리 유실을 일부 탐지 가능.

## 참고: PROGRESS 문서
- "저널 19건 통과"는 이번 실행의 파일 기준과 일치하는지 병합 전에 한 번 재확인 권장(복사본 실행은 프로브 포함 25건이라 직접 대조 불가).
