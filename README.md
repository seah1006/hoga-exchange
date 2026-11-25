# hoga-exchange

[![CI](https://github.com/seah1006/hoga-exchange/actions/workflows/ci.yml/badge.svg)](https://github.com/seah1006/hoga-exchange/actions/workflows/ci.yml)

한국거래소(KRX) 매매 규정을 조항 단위로 구현한 C# 모의 거래소입니다.
같은 시장을 **VI(변동성완화장치)를 켜고 / 끄고** 두 번 돌려, 제도가 가격 급변을 어떻게 막는지 나란히 보여 줍니다.

> 상태: 설계 단계 (2026-11-24 시작). 코드는 아직 없습니다.

## 무엇이 다른가
- **KRX 규정 그대로**: 호가가격단위, 가격제한폭(상하한가), 동적·정적 VI, 단일가매매를 규정 원문 기준으로 구현하고, 규정 조항마다 테스트를 연결합니다. ([규칙 명세](docs/RULES.md))
- **VI 켜짐/꺼짐 비교 실험**: 결정적 엔진이라 같은 시드의 봇 시장을 조건만 바꿔 다시 돌릴 수 있습니다.
- **"왜 체결 안 됐나" 설명**: 주문마다 대기 순번, VI 대기, 상하한가 거부 사유와 규칙 ID를 보여 줍니다.

## 기술
- 엔진·서버: C# / .NET 10, ASP.NET Core, SignalR
- 화면: React + Vite
- 테스트: xUnit, 속성 기반 테스트(FsCheck)

## 문서
| 문서 | 내용 |
|---|---|
| [docs/PLAN.md](docs/PLAN.md) | 목표, 범위, 일정, 시연 시나리오, 결정 기록 |
| [docs/RULES.md](docs/RULES.md) | KRX 규칙 명세와 출처, 단순화한 부분, 추적표 |
| [docs/api/CONTRACT.md](docs/api/CONTRACT.md) | 서버-화면 메시지 계약 |
| [docs/adr/](docs/adr/) | 설계 결정 기록 |
| [docs/tasks/](docs/tasks/) | 구현 작업 지시서 |
| [AGENTS.md](AGENTS.md) | 에이전트 작업 규칙 |
| [REVIEW_GUIDE.md](REVIEW_GUIDE.md) | 리뷰 기준 |

## 주의
- 교육·포트폴리오용 모의 거래소입니다. 실제 시장 데이터나 주문을 다루지 않으며 투자 조언이 아닙니다.
- KRX 규정 요약은 한국거래소 공개 자료를 바탕으로 정리한 것이고, 공식 해석이 아닙니다. 단순화한 부분은 [RULES.md](docs/RULES.md)에 밝혀 둡니다.
