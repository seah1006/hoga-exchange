# TASK-009S 진행 기록

## 완료
- 계약 v0.4의 `minAtMs`·`maxAtMs`를 기존 run 응답에 추가했다. 연속매매는 명령의 논리 시각, 단일가매매는 `AuctionCompleted.AtMs`를 쓴다. 동일 극값은 첫 체결 시각을 보존한다. 기존처럼 시작 기준가도 극값 후보에 포함하며, 그 가격에 실제 체결이 없을 때에만 시각을 `0ms`로 표시한다(임의 지정).
- `--batch`는 기존 시드 범위 1~100 및 240초 관측(모두 기존 임의 지정)을 유지한다. 분위수·부호별 개수·종료 시 VI 중인 run 수를 한 번 집계해서 Markdown과 JSON에 공유한다. 중앙값은 두 배 정수로 계산해 시연 선정에 사용하고, JSON에 쓸 때만 정확한 `decimal`로 변환한다.
- `GET /api/experiments/summary`는 빌드·publish 산출물의 `summary.json` 바이트를 변환 없이 전송한다. 파일 또는 상위 경로가 없으면 기존 오류 봉투로 503을 보내며 실험을 다시 계산하지 않는다.
- 엔진·봇 행동·계약 문서는 수정하지 않았다. 작업 브랜치에만 커밋하고 push하지 않았다.

## 검증 결과
- Windows, .NET SDK 10.0.404. `dotnet build HogaExchange.slnx -warnaserror`: 경고 0개, 오류 0개. `dotnet test HogaExchange.slnx`: 총 177개(Engine 103, Journal 34, Bots 14, Server 26), 실패·건너뜀 0개. 이 환경에서는 NuGet 감사 조회를 끄고 실행했다.
- `dotnet run --project src/Hoga.Experiments -- --batch --out docs/experiments`: 300쌍·600 run 재생성. `git diff --exit-code --ignore-cr-at-eol -- docs/experiments/SUMMARY.md` 통과(줄바꿈 외 차이 없음), 기존 파일을 유지했다. 시연 JSON도 `minAtMs`, `maxAtMs`만 제거하면 원본과 동일하다.
- `0aed3ee`의 기존 어셈블리로 시나리오 Calm/FatFinger/PanicSell 및 각 시드 1~100 순서로 계산한 비교 응답 JSON을 연결해 SHA-256 기준선을 만들었다. 새 필드 2개만 제외한 현재 응답의 해시가 동일하다: `2e2a0841f1ec08d3351c9c4cd11f851130946f30615943f6f1c8d9d123a5e691`.
- 시연 FatFinger 16: VI 켬 최저 9,490원(189,890ms), 최고 10,260원(60,000ms), 발동 1회. VI 끔 최저 7,020원(60,200ms), 최고 10,290원(167,800ms), 발동 0회.
- 자동 테스트로 첫 극값 체결 시각, 단일가 실제 종료 시각, 요약 JSON과 Markdown의 공통 수치, 서버 200(바이트 동일)·503을 확인했다. ASP.NET 테스트 호스트만 사용했으며 별도 포트에 서버를 띄우지 않았다.
- `dotnet publish src/Hoga.Server -c Release -r linux-x64 --self-contained false`: 출력의 `summary.json`과 원본 SHA-256 일치(`d1573fb0fa8db3386cc7d5639f9b6e9bf6e5e394490ceeb09a3ad478d8dd4e33`).

| 시나리오 | Δ 최소/P25/중앙값/P75/최대(원) | VI 켬 더 나쁨/동률/더 높음 | 종료 VI 켬/끔 | VI 발동 켬 합계 |
|---|---|---|---|---:|
| Calm | 0/0/0/0/0 | 0/100/0 | 1/0 | 1 |
| FatFinger | 1500/2270/2465/2650/2990 | 0/0/100 | 6/0 | 106 |
| PanicSell | 0/0/0/130/1300 | 0/71/29 | 16/0 | 37 |

## 미검증 및 제안
- 009W 통합 화면과 007 Linux 컨테이너 실행·공개 환경은 별도 작업으로 미검증이다. NuGet 보안 감사도 미검증이다.
- 다음 계약 개정에 시작 기준가가 체결 없이 극값으로 남는 경우 `0ms` 처리만 명시하는 것을 제안한다. 문서 설명이 한 줄 늘어나지만 실제 체결 시각과 기준가 표식을 구분할 수 있다. 현재 계약 문서는 수정하지 않았다.
- 구현 커밋: `04f3533`(극값 시각·배치), `f571e5b`(서버 요약 API).
