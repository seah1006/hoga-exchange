# REVIEW-1: TASK-001 엔진 뼈대 (최종 정리)

- 범위: `6f91afd..bd096cc` (task/TASK-001, 커밋 4개)
- 1단계: Claude Opus 5.5 (`REVIEW-1A.md`) - P0 0 / P1 4 / P2 5
- 2단계: Gemini 4 Hydrogen, 맥락 없음 (`REVIEW-1B.md`) - HIGH 1 / MEDIUM 3 / LOW 2
- 메인 직접 확인: 빌드 경고 0, 테스트 41/41 통과, 엔진 금지 API 0건, 아래 지적 코드 열어 확인

| # | 등급 | 내용 | 출처 | 확인 | 조치 |
|---|---|---|---|---|---|
| 1 | **P0** | `ComputeStateHash()`가 호가창만 포함. 접수 ID 이력(`_acceptedIds`), 마지막 순번, 기준가·상하한가·호가단위 규칙이 빠져 해시가 같아도 다음 명령에 다르게 반응할 수 있음. 복구 증명(ADR 0001, 시연 5단계)의 근거가 무너지므로 REVIEW_GUIDE P0-2로 올림 | A(P1), B(HIGH) | 예 (`MatchingEngine.Queries.cs`) | 이번에 수정 |
| 2 | P1 | 기준가 1원이면 하한가 0원 → 0원 주문·체결 가능 | A, B | 예 (`PriceLimits.cs`) | 이번에 수정: 1원 미만 가격 거부(`InvalidPrice`), RULES에 R-LIMIT-05(임의 지정 단순화) 추가 |
| 3 | P1 | 빈 문자열·공백 `OrderId` 접수 | B | 예 (`MatchingEngine.cs` Place) | 이번에 수정: `InvalidOrderId` 거부 |
| 4 | P1 | 속성 테스트가 기준가 9,940 하나만 사용, 가격대 경계를 넘는 상하한가·매칭 미검증 | A | 생성기 확인 | 이번에 수정: 경계 근처 기준가 여러 개 |
| 5 | P1 | 가격 우선(R-MATCH-01)을 속성 테스트로 검증 안 함 | A | 예 | 이번에 수정: I4 확장 또는 I4b 추가 |
| 6 | P2 | 같은 가격대 잔량 합이 long 초과 시 조회 예외 | A, B | 예 | 기록만. 수량 상한은 서버 입력 검증(TASK-004)에서 정함 |
| 7 | P2 | `Trade.Sequence`가 명령 순번이라 한 명령의 체결이 같은 값 | A | 예 | 기록만 (TASK-002에서 체결 순번 필요하면 추가) |
| 8 | P2 | `LogicalTimeMs`, `AccountId` 미사용·미검증 | A, B | 예 | 기록만 (TASK-002에서 시각 사용) |
| 9 | P2 | 잘못된 Side 예외 경로 테스트 없음, 추적표 일부 누락 | A | - | 기록만 |
