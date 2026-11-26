#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*@[A-Za-z0-9][A-Za-z0-9.-]*$')] [string] $Server,
    [Parameter(Mandatory)] [string] $Key,
    [Parameter(Mandatory)] [string] $Destination,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')] [string] $Container = 'hoga-hoga-1'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$keyFile = Get-Item -LiteralPath $Key
if ($keyFile.PSIsContainer) { throw 'Key must be a file.' }
$destinationPath = (New-Item -ItemType Directory -Force -Path $Destination).FullName
$sshOptions = @('-i', $keyFile.FullName, '-o', 'StrictHostKeyChecking=yes', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10')
# docker cp is needed because the private named volume is not directly readable by SCP.
$prepare = @'
set -eu
umask 077
stage=$(mktemp -d /tmp/hoga-backup.XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
files=$(docker exec __CONTAINER__ ls -1 /data/backups)
latest=$(printf '%s\n' "$files" | grep -E '^hoga-[0-9]{21}Z\.zip$' | LC_ALL=C sort | tail -n 1)
test -n "$latest"
docker cp "__CONTAINER__:/data/backups/$latest" "$stage/$latest"
chmod 600 "$stage/$latest"
printf '%s/%s\n' "$stage" "$latest"
trap - EXIT
'@
$prepare = $prepare.Replace('__CONTAINER__', $Container).Replace("`r`n", "`n")
$remote = $null
$local = $null
try {
    # Passing the script on stdin avoids nested quoting differences between Windows and Unix SSH clients.
    $output = $prepare | & ssh @sshOptions $Server 'sh -s'
    if ($LASTEXITCODE -ne 0 -or @($output).Count -ne 1 -or $output -cnotmatch '^(?<directory>/tmp/hoga-backup\.[A-Za-z0-9]{8})/(?<file>hoga-[0-9]{21}Z\.zip)$') {
        throw 'Could not prepare a valid backup for download.'
    }
    $remote = $Matches.directory
    $file = $Matches.file
    $local = Join-Path $destinationPath ('.hoga-' + [guid]::NewGuid().ToString('N') + '.tmp')
    if ($IsWindows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        [void][IO.File]::Create($local).Dispose()
        & icacls $local /inheritance:r /grant:r ('*' + $identity + ':(F)') | Out-Null
        if ($LASTEXITCODE) { throw 'Could not restrict backup permissions.' }
    }
    else {
        [void][IO.File]::Create($local).Dispose()
        & chmod 600 $local
        if ($LASTEXITCODE) { throw 'Could not restrict backup permissions.' }
    }
    & scp @sshOptions "${Server}:$remote/$file" $local
    if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $local).Length -le 0) { throw 'Backup transfer failed.' }
    $target = Join-Path $destinationPath $file
    [IO.File]::Move($local, $target, $true)
    Write-Output $target
}
finally {
    if ($local -and (Test-Path -LiteralPath $local)) { Remove-Item -LiteralPath $local -Force }
    if ($remote) {
        & ssh @sshOptions $Server "rm -rf -- '$remote'"
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not remove the private remote staging directory; remove it manually.' }
    }
}
