# AGENTS.md (에이전트 작업 규칙)

이 저장소에서 일하는 모든 AI 에이전트(Claude, Codex)가 따르는 규칙입니다. 계획은 `docs/PLAN.md`, 규칙 명세는 `docs/RULES.md`가 기준입니다.

## 역할
- **메인 에이전트(Aside/Claude)**: 작업 지시서 작성, 리뷰, 검증, 병합, push. 사용자와 소통.
- **구현 에이전트(Codex)**: `docs/tasks/TASK-XXX.md` 지시서대로 구현. 지시서 범위 밖은 건드리지 않는다.

## 절대 규칙
1. **비밀값 금지**: 키, 비밀번호, 토큰을 코드, 문서, 테스트, 커밋 메시지, 로그 출력에 쓰지 않는다. 필요한 값은 루트 `.env`(gitignore 됨)에서 환경 변수로만 읽는다.
2. **push 금지**: 구현 에이전트는 자기 작업 브랜치에 커밋까지만 한다. `main` 병합과 push는 메인 에이전트가 한다.
3. **지시서 범위 지키기**: 지시서에 없는 기능, 의존성, 구조 변경은 하지 않는다. 필요하면 보고에 "제안"으로 적는다.
4. **검증하고 보고**: 끝내기 전에 지시서의 "완료 조건" 명령을 직접 실행하고, 실제 출력 요약을 보고에 붙인다. 실행하지 못한 검증은 "미검증"이라고 쓴다.
5. **규정 정직성**: KRX 규칙을 추측으로 구현하지 않는다. `docs/RULES.md`에 있는 규칙 ID만 구현하고, 명세가 모호하면 구현을 멈추고 보고에 질문으로 남긴다.

## 엔진 규칙 (Hoga.Engine)
- 시스템 시계(`DateTime.Now`, `DateTimeOffset.UtcNow`, `Stopwatch` 기반 판단)와 전역 난수(`Random.Shared`, 시드 없는 `new Random()`)를 쓰지 않는다. 시각과 난수는 주입받는다.
- 가격·수량은 `long`. `double`, `float`, `decimal` 금지.
- 처리 순서가 `Dictionary`/`HashSet` 열거 순서에 의존하면 안 된다. 순서가 필요하면 정렬된 자료구조나 명시적 순번을 쓴다.
- 엔진 안에서 스레드, `Task.Run`, `Parallel`, 락을 쓰지 않는다(ADR 0001).
- 규칙을 구현하거나 바꾸면 해당 규칙 ID를 테스트 이름이나 `[Trait("Rule", "R-...")]`에 넣는다.
- 테스트를 지우거나 기대값만 바꿔서 통과시키지 않는다.

## 기술 기준
- .NET SDK 10 (이 PC: 10.0.401), C# 최신, Nullable 활성화, 경고는 오류로 처리
- 솔루션: 루트 `HogaExchange.slnx`
- 네임스페이스 루트: `Hoga` (예: `Hoga.Engine`, `Hoga.Server`)
- 테스트: xUnit, FsCheck(속성 기반)
- 화면: `web/` (React + Vite, TASK-006부터)
- 커밋 메시지: `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `refactor:` + 한국어 요약
- 저장소 경로에 한글이 없어야 한다. 현재 `C:\Users\USER\dev\hoga-exchange`.

## 로컬 실행 (예정)
- 전체 빌드·테스트: `dotnet build HogaExchange.slnx -warnaserror` 후 `dotnet test HogaExchange.slnx`
- 포트(임의 지정): 서버 5280, 화면 개발 서버 5173

## 문서 위치
- 계획: `docs/PLAN.md`
- 규칙 명세와 추적표: `docs/RULES.md`
- 설계 결정: `docs/adr/`
- 작업 지시서: `docs/tasks/`
- 리뷰 결과: `docs/reviews/`
