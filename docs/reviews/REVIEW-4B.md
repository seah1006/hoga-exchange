# 코드 리뷰: Hoga.Journal (TASK-003A)

- 대상 저장소: `C:\Users\USER\dev\hoga-003a` (브랜치: `task/TASK-003A`)
- 검토 범위: `d2e9d86cdf8656c39f148f9e0eed3749d8c75fea..64723287c02981abc37ec47084715472f3814086` (`git merge-base main HEAD`..HEAD, `src/` 및 `tests/`)
- 테스트 실행: `dotnet test HogaExchange.slnx` (Hoga.Journal.Tests 19개 통과, Hoga.Engine.Tests 54개 통과)
- 건수 요약: 총 7건 (HIGH 1, MEDIUM 3, LOW 3)

---

[HIGH] src/Hoga.Journal/Journal.cs:71-80 - 대용량 저널 파일에서 전체 파일을 메모리에 다중 복제하여 OOM 위험
- 근거: `JournalReader.Read`가 `FileStream` 전체를 `MemoryStream`에 복사(`input.CopyTo(buffer)`)한 뒤 `buffer.ToArray()`, `UTF8Encoding.GetString(...)`, `text.Split('\n')`를 연쇄 호출해 파일 크기의 약 5~6배에 달하는 힙/LOH 메모리를 점유한다. 여기에 `commands` 리스트, `Recovery.Restore`(`Journal.cs:121-130`)의 `events` 리스트, `ReplayVerifier.Verify`(`Journal.cs:135-141`)의 2회 복구 결과 동시 보관까지 더해져 장기 실행으로 커진 저널에서 메모리 부족이 발생할 수 있다.
- 제안: `JournalReader`가 `\n`(0x0A) 바이트 경계 기준으로 청크 스트리밍 파싱을 수행하도록 변경해 라인 단위 메모리만 사용하고, `Recovery.Restore`와 `ReplayVerifier.Verify`도 명령 및 이벤트를 스트리밍으로 재생·해시 검증하도록 개선한다.

[MEDIUM] src/Hoga.Journal/Journal.cs:154-159 - 엔진 사전 검증 실패 시 잘못된 명령이 저널에 먼저 영구 기록되어 복구 불가능 상태 유발
- 근거: `JournaledEngine.Process`는 `_writer.Append(command)`로 명령을 저널에 기록·플러시한 뒤 `Engine.Process(command)`를 호출한다. 그러나 `MatchingEngine.Process`(`src/Hoga.Engine/MatchingEngine.cs:29, 36, 44`)는 `command.OrderId == null`, 잘못된 `Side` enum 값, 미지원 `OrderCommand` 서브타입 등에 대해 예외를 던진다. 이 경우 잘못된 명령이 이미 저널에 기록된 채 `_faulted = true`가 되며, 이후 `Recovery.Restore`를 실행해도 동일 명령을 재생하다 같은 예외로 실패해 저널 전체가 복구 불가능해진다.
- 제안: `JournaledEngine.Process`에서 `_writer.Append` 호출 전에 `OrderId` null 여부, `Side` 유효성 등 엔진이 거부하는 구조적 사전 조건을 먼저 검증해 잘못된 명령이 저널에 남지 않도록 한다.

[MEDIUM] src/Hoga.Journal/Journal.cs:40-54, 154-159 - `flushEvery > 1`일 때 엔진 적용 전 디스크 영속성 보장 깨짐
- 근거: `JournaledEngine`의 주석은 `guarantees journal-before-engine ordering`을 명시하지만, `flushEvery > 1`로 생성하면 `JournalWriter.Append`가 `_pending < _flushEvery`인 동안 `StreamWriter` 메모리 버퍼에만 쓰고 `Flush()`(`_stream.Flush(flushToDisk: true)`)를 호출하지 않는다. 그 상태에서 `Engine.Process(command)`가 즉시 실행되어 이벤트가 외부로 반환되므로, 배치 플러시 전에 프로세스가 죽으면 이미 적용된 명령이 디스크에 남지 않는다.
- 제안: `flushEvery > 1` 옵션은 비내구성(성능 우선) 배치 모드임을 API 명칭·문서에 명확히 드러내거나, `JournaledEngine`에서는 매 명령 플러시(`flushEvery = 1`)만 허용하는 방안을 검토한다.

[MEDIUM] src/Hoga.Journal/Journal.cs:149-153 - `JournaledEngine`이 커스텀 `ITickSizeRule`을 지원하지 않음
- 근거: `JournalHeader.CreateEngine`, `Recovery.Restore`, `ReplayVerifier.Verify`는 모두 `Func<string, ITickSizeRule>? resolveRule = null`을 받아 커스텀 틱 규칙을 지원하지만, `JournaledEngine` 생성자(`Journal.cs:149`)에는 `resolveRule` 파라미터가 없어 항상 기본 `TickSizeTable`만 허용하는 `header.CreateEngine()`을 호출한다. 따라서 커스텀 `ITickSizeRule`을 사용하는 세션은 `JournaledEngine`으로 실행할 수 없다.
- 제안: `JournaledEngine` 생성자에도 `Func<string, ITickSizeRule>? resolveRule = null`을 추가해 `header.CreateEngine(resolveRule)`에 전달하도록 일관성을 맞춘다.

[LOW] src/Hoga.Journal/Journal.cs:10, 13 및 src/Hoga.Journal/Serialization.cs:19-20 - 봉투/헤더(camelCase)와 명령 페이로드(PascalCase) 간 JSON 직렬화 옵션 불일치
- 근거: `JournalFormat.Json`은 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`를 사용해 `JournalLine`과 `JournalHeader`를 camelCase로 직렬화하는 반면, `JsonCommandConverter<T>.Write`/`Read`는 옵션 없이 기본 `JsonSerializerOptions`를 사용해 명령 페이로드(`OrderId`, `Sequence` 등)를 PascalCase로 직렬화한다(`JournalTests.cs:104`와 `111`에서도 혼용 확인). 왕복 직렬화는 동작하나 동일 JSONL 레코드 내부의 명명 규칙이 통일되지 않았다.
- 제안: `JsonCommandConverter<T>`에도 `JournalFormat.Json`과 동일한 `JsonSerializerOptions`를 적용해 JSON 스키마의 키 명명 규칙을 일관되게 맞춘다.

[LOW] src/Hoga.Journal/Journal.cs:56-65 - `JournalWriter.Flush()`의 `_faulted` 미검사 및 `Dispose()` 내 이중 플러시 가능성
- 근거: `JournalWriter.Append`는 `_faulted`를 검사하지만 `Flush()`는 `_faulted` 검사가 없어 장애 상태에서도 외부 호출 시 불완전한 버퍼를 디스크로 플러시할 수 있다. 또한 `Dispose()`에서 `Flush()`가 실패해 예외를 던진 뒤에도 `finally { _writer.Dispose(); }`가 `StreamWriter.Dispose()` 내부에서 다시 플러시를 시도할 수 있다.
- 제안: `Flush()` 진입 시 `_faulted` 상태면 `InvalidOperationException`을 던지고, `Flush()` 실패 시 `_faulted = true`로 전환하도록 보완한다.

[LOW] tests/Hoga.Journal.Tests/JournalTests.cs:1-147 - 경계 케이스 테스트 누락
- 근거: 빈 파일(0바이트), 헤더만 존재하는 파일, 중복 시퀀스(`0, 1, 1`), 마지막 줄이 멀티바이트 UTF-8 문자(예: 한글 `OrderId`) 중간에서 잘린 경우, CRLF(`\r\n`) 개행 파일에 대한 명시적 테스트가 없다.
- 제안: 위 5가지 경계 케이스를 `JournalTests`에 추가해 회귀를 방지한다.
