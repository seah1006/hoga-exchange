# ADR 0004: 입력 저널 형식과 처음부터 재생

- 상태: 채택 (TASK-003A)

## 형식
- UTF-8 BOM 없음, JSON Lines, LF 종료. 형식 버전 1(임의 지정).
- 각 줄은 `seq`, `type`, `payload`, `checksum`. 첫 줄은 seq=0, type=header.
- 헤더 payload: formatVersion, basePrice, tickSizeRuleId. 버전 및 호가 규칙 불일치는 예외.
- 명령 seq는 1부터 연속 증가하며 payload 내부 Sequence와 같아야 한다.
- type은 place-limit/v1, cancel/v1(임의 지정). CLR 타입명 자동 로딩 없이 ICommandConverter를 명시적으로 등록한다. 새 명령은 변환기 하나를 등록한다. 미등록 타입은 UnknownCommandTypeException.
- 체크섬: invariant 10진 seq + LF + type + LF + payload 원본 JSON 텍스트를 UTF-8로 인코딩한 SHA-256 앞 16자리 소문자 hex. 헤더도 보호한다. 보안 인증이 아닌 우발적 손상 탐지용이다.

## 쓰기 / flush
- JournaledEngine.Process는 Append 후 엔진 Process를 호출한다. 엔진 예외가 나면 세션을 중단한다. 직접 JournalWriter를 쓰는 호출자도 같은 순서를 지켜야 한다. 엔진 API상 예외를 던지는 잘못된 입력은 호출자가 사전 검증해야 한다(거부 이벤트를 반환하는 정상적인 주문 거부와 구별).
- 기본 flushEvery=1(임의 지정): 매 명령 StreamWriter.Flush 후 FileStream.Flush(true). 승인된 입력 손실을 줄이는 쪽을 택했다. 하드웨어/OS의 영속성 보장 한계는 남는다.
- N개마다 flush 옵션은 처리량을 늘리지만 강제 종료 때 아직 flush하지 않은 최대 N-1개 입력을 잃을 수 있다. Dispose는 잔여 입력을 flush한다. 헤더는 즉시 flush한다.
- FileMode.CreateNew로 기존 파일 덮어쓰기 방지. 단일 소유자, 단일 스레드 API. 기존 저널 이어쓰기는 제공하지 않는다.
- 경로는 생성자로 받으며 운영 호출자가 data/journal/ 아래를 선택한다. 디렉터리는 호출자가 준비한다.

## 복구와 검증
- 헤더 설정으로 새 엔진을 만들고 처음부터 입력을 재생한다. expectedHeader 옵션은 호출자가 기대한 설정과의 불일치를 탐지한다.
- LF가 없는 마지막 레코드만 버리고 줄 번호와 경고를 반환한다. 완성된 JSON이어도 LF가 없으면 미완료 레코드로 본다. 헤더 미완성은 복구 불가.
- LF로 종료된 줄의 JSON/UTF-8/체크섬 손상, seq 건너뜀, payload와 seq 불일치는 마지막 줄이어도 예외. 중간 손상을 건너뛰지 않는다.
- ReplayVerifier는 처음부터 두 번 재생하여 이벤트 record 수열과 최종 상태 해시 및 마지막 seq를 비교한다.
- 장점: 사람이 읽을 수 있고 독립적으로 손상을 확인할 수 있다. 단점: 전체 파일과 이벤트를 메모리에 읽으며 긴 저널은 처음부터 재생하는 비용이 크다.

## 스냅샷
TASK-003B 예정.
