[CmdletBinding(DefaultParameterSetName='Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName='Deploy')][string]$Server,
    [Parameter(Mandatory, ParameterSetName='Deploy')][string]$Key,
    [Parameter(Mandatory, ParameterSetName='Build')][switch]$BuildOnly,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$RemoteDir = 'hoga',
    [uri]$HealthUrl = 'https://hoga-158-180-72-47.sslip.io/healthz',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
function Run([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Native command failed (exit $LASTEXITCODE)." }
}
if (!$BuildOnly) {
    if ($Server -notmatch '^[A-Za-z0-9_-]+@[A-Za-z0-9.-]+$') { throw 'Server must be user@host.' }
    if (!(Test-Path -LiteralPath $Key -PathType Leaf)) { throw 'SSH identity file not found.' }
    $Key = (Resolve-Path -LiteralPath $Key).Path
    if ($HealthUrl.Scheme -ne 'https') { throw 'The public health check requires HTTPS.' }
}
$repo = Split-Path $PSScriptRoot
Push-Location $repo
try {
    $sha = git rev-parse --short HEAD
    if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[0-9a-f]+$') { throw 'Cannot resolve release commit.' }
    $release = "$sha-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))"
    $image = "hoga-exchange:$release"
    $stage = Join-Path $PSScriptRoot "bin/$release"
    New-Item -ItemType Directory -Path $stage | Out-Null
    Run { & $Dotnet publish src/Hoga.Server/Hoga.Server.csproj -c Release -r linux-x64 --self-contained false -o "$stage/publish" }
    Push-Location web
    $oldShell = $env:npm_config_script_shell
    $oldNode = $env:NODE_OPTIONS
    $oldMock = $env:VITE_USE_MOCK
    $oldBase = $env:VITE_API_BASE
    try {
        $env:npm_config_script_shell = (Get-Command pwsh).Source
        $env:NODE_OPTIONS = '--no-experimental-webstorage'
        $env:VITE_USE_MOCK = 'false'
        $env:VITE_API_BASE = [string]::Empty # same origin, even if a local Vite .env exists
        Run { npm ci }
        Run { npm run build }
        Copy-Item dist "$stage/web" -Recurse
    } finally {
        $env:npm_config_script_shell = $oldShell
        $env:NODE_OPTIONS = $oldNode
        $env:VITE_USE_MOCK = $oldMock
        $env:VITE_API_BASE = $oldBase
        Pop-Location
    }
    Run { docker build --platform linux/amd64 -t $image -f "$PSScriptRoot/Dockerfile" $stage }
    if ($BuildOnly) { Write-Output "Image: $image"; return }

    $file = 'docker-compose.hoga.yml'
    $archive = Join-Path $stage 'image.tar'
    Run { docker image save --output $archive $image }
    try {
        $remote = "~/$RemoteDir/releases/$release"
        Run { ssh -o BatchMode=yes -i $Key $Server "set -eu; test -f ~/$RemoteDir/.env; mkdir -p $remote" }
        Run { scp -o BatchMode=yes -i $Key $archive "$PSScriptRoot/$file" "${Server}:$remote/" }
        # The server .env is read by Compose only. Never echo rendered config or inspect container env.
        Run { ssh -o BatchMode=yes -i $Key $Server "set -eu; cd ~/$RemoteDir; chmod 600 .env; docker load -i releases/$release/image.tar; rm releases/$release/image.tar; HOGA_IMAGE=$image docker compose --env-file .env -p hoga -f releases/$release/$file up -d --wait --wait-timeout 120" }
        for ($i = 0; $i -lt 12; $i++) {
            try {
                $status = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 5
                if ($status.ok -eq $true -and $null -ne $status.seq) {
                    Write-Output "Healthy: $image (seq=$($status.seq))"
                    return
                }
            } catch { Write-Warning 'Public health check has not succeeded yet.' }
            Start-Sleep -Seconds 5
        }
        throw "Public health check failed. Inspect the release and roll back if required: $release"
    } finally { Remove-Item -LiteralPath $archive -ErrorAction SilentlyContinue }
} finally { Pop-Location }
