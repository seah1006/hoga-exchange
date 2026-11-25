# TASK-009: 실험 화면 확장 (서버·실험 009S, 화면 009W)

- 브랜치: `task/TASK-009S`, `task/TASK-009W` (동시 진행, 메인이 병합 후 통합 확인)
- 담당: 구현 에이전트 (Codex) 2명
- 선행 문서: `AGENTS.md`, `docs/api/CONTRACT.md` **v0.4**, `docs/experiments/SUMMARY.md`, `docs/tasks/TASK-005-PROGRESS.md`, `docs/reviews/REVIEW-5A.md`의 P2(관측 절단, FatFinger 지표 편향)
- 목적: 발표에서 "VI 효과"를 시드 한 개가 아니라 **100개 시드 분포**로 보여 주고, 청중이 다른 시드를 직접 골라 볼 수 있게 한다. 숫자와 그래프가 서로 다르게 보이는 문제(카드 최저가 7,020원 대 선 최저점 약 9,200원)를 없앤다.

## 009S 서버·실험 (`src/Hoga.Bots`, `src/Hoga.Experiments`, `src/Hoga.Server`)
1. run에 `minAtMs`, `maxAtMs` 추가(계약 v0.4). 기존 수치(최저·최고가, VI 횟수, SUMMARY 표)는 **바뀌면 안 된다**. 바뀌면 멈추고 보고.
2. `--batch`가 `docs/experiments/summary.json`(계약 v0.4 형식)도 만든다. SUMMARY.md와 같은 계산 결과를 쓴다(따로 다시 계산하지 말고 한 번 계산한 값을 두 파일에 쓴다). `demo-fatfinger-16.json`도 새 필드를 넣어 다시 만든다.
3. 서버 `GET /api/experiments/summary`: 빌드 산출물에 포함한 `summary.json`을 읽어 그대로 돌려준다. 파일이 없으면 503. 600 run을 서버에서 계산하지 않는다(공개 서버 CPU 1/8).
4. 테스트: 새 필드가 최저·최고 체결 시각과 일치, summary.json의 시나리오별 수치가 SUMMARY.md 표와 일치, 서버 summary 응답 200/503, 기존 실험 결과 해시(새 필드 제외) 불변.

## 009W 화면 (`web/`)
1. 비교 화면을 "실험" 화면으로 확장:
   - 시나리오 탭(평온·팻 핑거·패닉 매도)
   - 시드 100개 분포 그래프(시드별 Δ 점 또는 막대, 0 기준선, 시연 시드 강조). 라이브러리는 이미 있는 것만(새 의존성 금지, 필요하면 SVG 직접)
   - 요약 카드: 중앙값, VI 켬이 더 높음/동률/더 나쁨 개수, 240초에 VI 진행 중이던 run 수
   - 분포에서 시드를 누르면 아래 비교 그래프가 그 시드(`exp-{scenario}-{seed}`)로 바뀜. 처음엔 `demo`
   - 비교 그래프에 최저·최고점 표식(`minAtMs`, `maxAtMs`), 범례에 "선: 1초 종가, 점: 실제 최저·최고 체결"
   - 화면 아래 한계 문구(`caveat`)를 그대로 표시
2. 목 구현체도 summary와 새 필드를 제공(목 테스트 유지). 서버 모드는 계약 v0.4 엔드포인트 사용.
3. 테스트: 탭 전환, 시드 선택 시 실험 재조회, 표식 위치(최저점 좌표가 `minAtMs`에 대응), 404·503 처리, 목 모드.

## 하지 않을 것
- 엔진 규칙 변경, 봇 행동 변경(시드 결과가 바뀌는 변경), 계약 변경(필요하면 제안)

## 완료 조건
- 009S: 빌드 경고 0, 전체 테스트 통과(수), `--batch` 재생성 후 SUMMARY.md 수치 diff 없음(줄바꿈 제외), summary.json 시나리오별 요약 출력
- 009W: lint, test -- --run, build 통과(수)
- 각자 `docs/tasks/TASK-009S-PROGRESS.md` / `TASK-009W-PROGRESS.md`, 작업 브랜치 커밋(push 안 함)
