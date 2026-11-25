# TASK-010 성능 측정

**참고용 짧은 측정** (2026-11-25 KST, `0aed3ee` 기준 엔진·서버). PLAN 8장의 목표는 단일 종목 100,000 주문/s(임의 지정)다. 미세 벤치마크와 HTTP 부하는 측정 경계가 다르므로 따로 비교한다.

## 측정 환경과 경합

| 항목 | 조회값 |
|---|---|
| CPU | AMD Ryzen 5 5600 6-Core Processor, 물리 6코어 / 논리 12스레드 |
| RAM | 34,243,710,976 B (약 31.9 GiB) |
| OS | Microsoft Windows 11 Pro x64, 10.0.26200 (빌드 26200) |
| .NET | SDK 10.0.404, .NET 및 ASP.NET Core 10.0.15 x64 |
| 디스크 | C: Colorful CN600 512GB PRO, NVMe SSD (벤치마크와 서버 저널 모두 같은 볼륨) |
| 벤치마크 도구 | BenchmarkDotNet 0.15.8 (버전 임의 지정), Release, ShortRun |

사양은 `Get-CimInstance Win32_Processor/Win32_ComputerSystem/Win32_OperatingSystem`, `dotnet --info`, `Get-Partition -DriveLetter C | Get-Disk`로 확인했다. 다른 작업을 멈추거나 CPU를 고정하지 않았다. REST 실행 직전/직후 전체 CPU는 동시 1에서 61→80%, 8에서 48→58%, 32에서 50→82%였다(`Win32_PerfFormattedData_PerfOS_Processor`, `_Total`). 그 시점에 명령줄에서 다른 `hoga-wt` 작업 경로를 식별할 수 있는 .NET 빌드/테스트 프로세스는 보이지 않았지만, 샘플 사이의 실행 및 공용 컴파일러·백그라운드 작업 경합을 배제할 수 없다. BDN 22:22:55~22:24:27 KST에는 시작·중간·종료 CPU가 56%·65%·96%였고, 종료 표본에서 `task-009` 경로의 `dotnet.exe`도 확인했다. 따라서 다른 worktree 실행이 겹쳤으며 케이스별 영향은 분리하지 못했다. CPU/디스크를 통제한 수치가 아니다.

## 엔진·저널 미세 벤치마크

- 접속매매·취소: VI를 끈 KRX 호가창, 기준가 10,000원, 방향당 16단계, 단계당 1주문(수량 1~10주, 시드 42). 입력 주문과 호가창은 측정 바깥에서 준비한다. 반복마다 독립 엔진 256개에서 각각 한 번만 처리하여 ID·호가창 누적을 막는다. 적재는 기존 가격 단계에 추가, 1건 체결과 8단계 휩쓸기는 잔량 없는 완전 체결, 취소는 최우선 매수 단계의 유일한 주문을 제거한다. 이 문단의 값·케이스 구성은 임의 지정이다.
- 단일가·해시: VI로 단일가에 진입한 뒤 트리거 주문을 취소하고, 양방향 각각 10/100/1,000 가격 단계가 겹치는 호가창(단계마다 1주문, 수량 1~10주, 시드 42)을 준비한다. 1,000개의 겹치는 가격을 만들기 위해 이 두 벤치마크에만 합성 1원 호가 규칙을 주입한다. 단일가는 합치가격 조회만, 해시는 같은 상태의 SHA-256 계산만 측정한다. 깊이·입력값·합성 규칙은 임의 지정이다.
- 저널: 실제 `JournalWriter.Append`의 직렬화, 체크섬, 파일 쓰기, `Flush(true)` 포함. 매 반복에 새 파일, 연속 256개 주문(임의 지정). 기본 매건 flush와 32개 배치(임의 지정)를 비교한다. 파일 생성·헤더 flush·종료·삭제는 측정 밖이며, 측정 구간의 256개는 두 설정 모두 마지막까지 flush된다. 배치는 강제 종료 시 최대 31건의 적용된 명령이 기록에 남지 않을 수 있다(ADR 0004).
- `ops/s = 1 / 평균 초`, `Allocated`는 `MemoryDiagnoser`의 작업당 관리 힙 할당(프로세스 RSS 아님). 256개 묶음은 BDN의 `OperationsPerInvoke`로 건당 환산했다. 엔진/저널 표에 HTTP 요청 지연 백분위수를 섞지 않는다.

**참고용 짧은 측정:** 12개 케이스 모두 완료(전체 87.77초). ShortRun은 프로세스 1회, warmup 3회, 측정 반복 3회다. BDN은 접속매매·취소와 저널 배치의 최소 반복 시간이 짧다고 경고했다. 짧은 반복과 호스트 경합으로 오차가 커서 작은 차이의 비교나 실제 용량 산정에는 쓰지 않는다.

| 작업 | 조건 | 평균 μs/op | ops/s | 관리 힙 할당/op |
|---|---|---:|---:|---:|
| 적재 | 체결 없음 | 1.3688 | 730,593.6 | 408 B |
| 1건 체결 | 1단계 | 2.0582 | 485,860.7 | 416 B |
| 휩쓸기 | 8단계 체결 | 11.6776 | 85,634.0 | 2,632 B |
| 취소 | 최우선 매수 주문 | 0.8174 | 1,223,319.5 | 56 B |
| 합치가격 | 방향당 10단계 | 2.500 | 400,027.9 | 4.25 KiB |
| 합치가격 | 방향당 100단계 | 17.442 | 57,333.0 | 28.38 KiB |
| 합치가격 | 방향당 1,000단계 | 160.336 | 6,236.9 | 268.49 KiB |
| 상태 해시 | 방향당 10단계 | 6.734 | 148,496.1 | 4.86 KiB |
| 상태 해시 | 방향당 100단계 | 48.552 | 20,596.3 | 35.13 KiB |
| 상태 해시 | 방향당 1,000단계 | 831.248 | 1,203.0 | 280.42 KiB |
| 저널 추가 | 매건 flush | 1,191.34 | 839.4 | 1.92 KiB |
| 저널 추가 | 32건당 flush | 51.41 | 19,452.0 | 1.93 KiB |

BDN 원본은 실행 후 `BenchmarkDotNet.Artifacts/results/`에서 확인할 수 있다.

## REST 부하: 참고용 짧은 측정

서버와 클라이언트 모두 이 PC에서 Release로 실행했다. 루프백 HTTP/1.1, 포트 5290(임의 지정), 봇 끔, 기준가 10,000원, 서버 기본 매건 저널 flush·기본 로그, SignalR 구독자 0명. 동시도마다 빈 저널과 새 서버로 시작했고 warmup 후 호가창은 유지했다. 클라이언트별 16건 warmup(임의 지정)은 집계하지 않는다. 동시 1/8/32, 각 4,096건(임의 지정), 연결 재사용, 클라이언트마다 한 번에 한 요청만 보내며 완료 후 다음 요청을 보낸다(closed-loop).

시드 42(임의 지정)의 클라이언트별 난수로 샘플을 만든다. 각 4건은 10,000원 매도·매수 각 하나(쌍의 수량 동일), 교차하지 않는 9,900원 매수 또는 10,100원 매도, 0주 주문이다. 정상 완료 시 중앙 가격의 매도·매수 수량이 같아 모두 상쇄되며, 바깥 가격의 주문은 적재된다. 0주는 의도한 엔진 `InvalidQuantity` 거부(HTTP 200)로 저널을 거친다. 동시 요청의 서버 도착 순서는 고정되지 않으며 POST 응답은 체결 건수를 돌려주지 않아 체결 수는 직접 집계하지 않았다.

| 동시 클라이언트 | elapsed s | ops/s¹ | p50 ms | p95 ms | p99 ms | 최대 ms | 수락 / 의도한 거부 / 오류² |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 5.630 | 727.51 | 1.261 | 2.129 | 3.256 | 6.962 | 3072 / 1024 / 0 |
| 8 | 6.670 | 614.12 | 11.799 | 23.044 | 29.110 | 41.747 | 3072 / 1024 / 0 |
| 32 | 8.484 | 482.78 | 49.785 | 141.012 | 273.082 | 911.059 | 3072 / 1024 / 0 |

¹ 위 세 실측에서는 모든 요청이 예상한 응답을 받았으므로 완료 처리량과 애플리케이션 처리량이 같다. 지연은 클라이언트에서 POST 직전부터 응답 본문 처리까지를 `Stopwatch`로 재고, 정렬 표본의 `ceil(N × p)`번째(1부터) 값으로 계산했다. ² 비정상 HTTP 상태, I/O, 타임아웃, 예상과 다른 응답 각각 0건. 각 측정 뒤 서버의 seq가 `4096 + 16 × 동시도`인지 확인했고, 서버 종료와 5290 포트 리스너 없음도 확인했다. 부하에서 서버 할당량은 수집하지 않았다.

## 재현 명령 (저장소 루트, PowerShell)

```powershell
dotnet build HogaExchange.slnx -warnaserror
dotnet test HogaExchange.slnx
dotnet build HogaExchange.slnx -c Release -warnaserror
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job short --filter '*'
```

공유 호스트에서 `MSB4166`이 발생하면 `-m:1 -p:UseSharedCompilation=false`를 빌드·테스트 명령에 추가한다. 이번 Debug 검증에서는 단일 노드 빌드 경고 0개, 기존 테스트 169개 통과를 확인했다. Release 빌드도 경고 0개였다.

REST는 아래를 실행한다. 종료 보장을 위해 측정마다 `finally`에서 직접 띄운 서버 프로세스만 종료하고, 실제 운영 저널이 아닌 무작위 이름의 측정 저널만 제거한다. 로컬 로그는 gitignore된 `BenchmarkDotNet.Artifacts/load`에 남긴다.

```powershell
$ErrorActionPreference = 'Stop'
$d = (Get-Command dotnet).Source
$url = 'http://127.0.0.1:5290'
$dir = 'BenchmarkDotNet.Artifacts/load'
New-Item -ItemType Directory -Force $dir | Out-Null
if (Get-NetTCPConnection -LocalPort 5290 -State Listen -ErrorAction SilentlyContinue) { throw '5290 already in use' }
$env:Market__Bots = 'false'
$env:Market__BasePrice = '10000'
$env:DOTNET_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT = 'Production'
foreach ($n in 1,8,32) {
    $env:Market__JournalPath = "$dir/$n-$([Guid]::NewGuid().ToString('N')).jsonl"
    $p = Start-Process -FilePath $d -ArgumentList @('src/Hoga.Server/bin/Release/net10.0/Hoga.Server.dll','--urls',$url) `
        -NoNewWindow -PassThru -RedirectStandardOutput "$dir/$n-server.log" -RedirectStandardError "$dir/$n-server-err.log"
    try {
        $ready = $false
        for ($i = 0; $i -lt 25; $i++) {
            if ($p.HasExited) { throw 'Server exited; check log' }
            try {
                $state = Invoke-RestMethod "$url/api/state/hash" -TimeoutSec 1
                if ($state.seq -eq 0 -and !$state.restoredFromJournal) { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        if (!$ready) { throw 'Fresh server not ready' }
        & $d tools/Hoga.LoadTest/bin/Release/net10.0/Hoga.LoadTest.dll $url $n 4096
        if ($LASTEXITCODE -ne 0) { throw 'Load test failed' }
        if ((Invoke-RestMethod "$url/api/state/hash" -TimeoutSec 5).seq -ne (4096 + 16 * $n)) { throw 'Sequence mismatch' }
    } finally {
        if (!$p.HasExited) { Stop-Process -InputObject $p; $p.WaitForExit() }
        Remove-Item -LiteralPath $env:Market__JournalPath -ErrorAction SilentlyContinue
        if (Get-NetTCPConnection -LocalPort 5290 -State Listen -ErrorAction SilentlyContinue) { throw '5290 still listening' }
    }
}
```

### 정식 측정 (메인 에이전트용, 이번 작업에서는 실행하지 않음)

다른 에이전트 빌드·테스트를 멈춘 뒤 위 Release 빌드를 실행한다. CPU 사용률·전원 설정·스토리지 상태를 기록하고, BDN `Default` job을 항목별로 분리 실행하여 변경 전·후 동일 조건에서 비교한다. 개별 명령이 8분에 접근하면 중단 후 파라미터나 항목을 더 좁힌다(`--list flat`으로 이름 확인).

```powershell
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --list flat
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*OrderBenchmarks.Rest'
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*OrderBenchmarks.MatchOne'
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*OrderBenchmarks.SweepEight'
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*OrderBenchmarks.Cancel'
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*QueryBenchmarks.IndicativeAuction*'
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*QueryBenchmarks.StateHash*'
dotnet run -c Release --no-build --project benchmarks/Hoga.Benchmarks -- --job default --filter '*JournalBenchmarks.Append*'
```

정식 REST 측정은 위 스크립트를 동시도별로 별도 실행하고 요청 수를 `4096`에서 `32768`로(두 위치 모두, 임의 지정) 바꿔 새 서버로 각 3회 이상(임의 지정) 반복한다. 실행 순서를 교대하고 CPU·디스크 샘플, 처리량과 개별 실행 p50/p95/p99·오류를 보존한다. 3분 클라이언트 예산에 닿으면 그 실행을 실패로 기록하고 요청 수를 낮춘다.

## 해석, 한계, 제안

- 순수 엔진에서 적재·1건 체결은 100,000 주문/s를 넘었지만 8단계 휩쓸기는 85,634 주문/s로 못 미쳤다. 같은 엔진도 주문당 체결 단계와 생성 이벤트 수에 따라 비용이 다르다. 준비된 호가창의 단발 처리이므로 장기 실행 시 ID 누적·GC와 실제 주문 분포는 포함하지 않는다. 단일가·해시는 주문 처리량 목표와 별개다. 깊이가 커질수록 할당·시간이 늘었고, 해시는 전체 호가 순회에 ID 정렬·직렬화·SHA-256 비용도 든다.
- 저널 매건 flush는 839 ops/s로 REST 최고치 728 ops/s와 같은 규모이며, flush 간격만 32로 늘리면 할당은 거의 그대로인데 저널 추가는 약 23배 빨랐다. 이 조건에서는 디스크 동기 flush가 우선 의심되는 병목이다. 서로 다른 실험이므로 두 수치의 차이를 HTTP 비용으로 보거나, 배치 시 REST가 23배 빨라진다고 해석할 수 없다.

- REST는 동시도를 1→32로 늘려도 처리량이 오르지 않았고, p99는 3.256→273.082 ms로 늘었다. 서버는 bounded channel의 단일 reader가 저널 쓰기와 flush, 엔진, projection, 발행을 순차 수행한다. 요청이 몰리면 앞 작업이 끝나길 기다리는 큐 지연이 생긴다. 32명은 큐 용량 1,024보다 작으므로 큐가 꽉 찼다는 뜻은 아니다. HTTP/JSON, 스케줄링, 클라이언트도 지연에 포함되며 이 수치로 각 비용을 분리하거나 높은 꼬리 지연을 특정 원인에 귀속할 수 없다.
- 이 부하는 순수 엔진 대비 다른 경로이며 실제 REST 최고 실측은 PLAN 100,000 주문/s의 약 0.73%다. closed-loop는 응답이 느리면 다음 발행도 늦춰 고정 도착률 시스템의 과부하 상황을 과소표현할 수 있다. 짧은 단회 표본이고 루프백·봇 꺼짐·구독자 없음·합성 주문인 만큼 운영 성능을 보장하지 않는다.
- **제안:** 먼저 큐 대기, 동기 flush, 엔진, projection/발행, HTTP 왕복의 구간별 시간을 기록하고 동일 부하를 재측정한다. 관측 장치도 부하를 만들므로 켠 실행과 끈 실행을 나눠 비교한다.
- **제안:** 저널 병목을 확인한 뒤 그룹 커밋(확인 응답은 flush 이후)과 비내구성 배치를 구분해 실험한다. 전자는 배치가 찰 때까지 기다리는 지연, 후자는 장애 시 미flush 승인 손실을 감수한다. 이번에는 영속성이나 엔진 동작을 변경하지 않았다.
