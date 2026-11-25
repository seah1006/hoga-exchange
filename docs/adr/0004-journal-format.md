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
- Append 전에 구조 검증(null 명령/주문 ID/계좌 ID, 정의되지 않은 Side, 미지원 엔진 명령, 마지막+1 아닌 seq)을 수행한다. 잘못된 구조는 기록하지 않으며 세션은 계속 사용 가능하다. 정상 OrderRejected는 기록한다. Append 성공 후 엔진을 적용한다. 쓰기 IO 실패 시 해당 명령은 엔진에 적용되지 않으며 writer와 세션은 중단한다. 실패 후 Dispose는 버퍼를 다시 flush하지 않는다.
- 기본 flushEvery=1(임의 지정): 매 명령 StreamWriter.Flush 후 FileStream.Flush(true). 승인된 입력 손실을 줄이는 쪽을 택했다. 하드웨어/OS의 영속성 보장 한계는 남는다.
- N개마다 flush 옵션은 처리량을 늘리지만 강제 종료 때 아직 flush하지 않은 최대 N-1개 입력을 잃을 수 있다. 이 비내구성 배치 모드는 flush 전에 엔진에 적용되고 호출자에게 반환된 마지막 배치를 잃을 수 있으므로, append-before-engine은 매 명령 디스크 영속성과 같지 않다. Dispose는 잔여 입력을 flush한다. 헤더는 즉시 flush한다.
- FileMode.CreateNew로 기존 파일 덮어쓰기 방지. 단일 소유자, 단일 스레드 API. 기존 저널 이어쓰기는 제공하지 않는다.
- 경로는 생성자로 받으며 운영 호출자가 data/journal/ 아래를 선택한다. 디렉터리는 호출자가 준비한다.

## 복구와 검증
- 헤더 설정으로 새 엔진을 만들고 처음부터 입력을 재생한다. expectedHeader 옵션은 호출자가 기대한 설정과의 불일치를 탐지한다.
- LF가 없는 마지막 레코드만 버리고 줄 번호와 경고를 반환한다. 완성된 JSON이어도 LF가 없으면 미완료 레코드로 본다. 헤더 미완성은 복구 불가.
- LF로 종료된 줄의 JSON/UTF-8/체크섬 손상, seq 건너뜀, payload와 seq 불일치는 마지막 줄이어도 예외. 중간 손상을 건너뛰지 않는다.
- Reader는 한 줄씩 바이트를 읽고 완성된 줄만 엄격한 UTF-8로 해석한다. Commands는 지연 열거이며 파일을 복제하거나 명령 목록을 쌓지 않는다. Warnings는 열거 완료 후 확정된다. Recovery.Restore는 이벤트를 보관하지 않는다. ReplayVerifier는 새 엔진 두 개로 같은 파일을 스트리밍 재생하며 각 명령의 이벤트 수열과 seq 및 최종 상태 해시를 비교한다. 메모리는 엔진 상태와 현재 줄/현재 명령 이벤트 크기에 비례한다.
- 복구 중 엔진 예외는 줄 번호와 seq를 포함한 InvalidDataException으로 감싸며 원인은 InnerException으로 보존한다. 미등록 타입도 줄 번호와 seq를 포함하며 원래 UnknownCommandTypeException을 InnerException에 보존한다.
- 장점: 사람이 읽을 수 있고 독립적으로 손상을 확인할 수 있다. 단점: 긴 저널은 처음부터 재생하는 CPU/IO 비용이 크고, 단일 거대 레코드는 그 줄 크기만큼 메모리를 쓴다.

## 스냅샷
TASK-003B 예정.

## 고정 와이어 DTO와 골든 파일 (리뷰 수정)
- 엔진 record 직접 직렬화를 제거했다. place-limit/v1 payload 필드는 순서대로 orderId, side, price, quantity, accountId, sequence, logicalTimeMs; cancel/v1은 orderId, sequence, logicalTimeMs다. side는 정확히 문자열 Buy/Sell만 허용한다.
- DTO의 모든 생성자 필드가 필수이며 null 식별자, 누락 필드, 알 수 없는 필드/Side를 거부한다. 고정 JsonPropertyName으로 CLR 속성명과 분리한다.
- tests/Hoga.Journal.Tests/Golden/basic.jsonl은 헤더 + a 매수(990,5) + b 매도(1010,3) + a 취소, 총 4줄. 이름과 샘플 값은 임의 지정. BOM 없는 UTF-8/LF 바이트와 재생 상태 해시 7a983442c1c2156ee5a6b2befbba9f00904d0b947a0a71683dc9a22ac6e80b9f를 고정한다. 형식 변경 시 골든 테스트가 실패한다.
- 배포 전 초안 v1을 고정한 것이며 기존 엔진 record/PascalCase/숫자 enum 초안 파일은 지원하지 않는다. 향후 형식 변경은 버전/호환 정책과 새 골든 파일을 함께 설계한다.
- 헤더 엔진 옵션/버전 호환 확장은 TASK-003B에서 수행한다. 기존 이어쓰기 미지원, 단일 소유자이며 읽는 동안 파일을 변경하지 않는다.
