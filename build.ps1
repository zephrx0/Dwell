$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$compiler = (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue).Source
if (-not $compiler) {
    $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'Inno Setup 6.3 or later is required. Install it or add ISCC.exe to PATH.'
}

dotnet build Dwell.slnx -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test tests/Dwell.Core.Tests/Dwell.Core.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$stage = Join-Path $artifacts ('install-' + [Guid]::NewGuid().ToString('N'))
try {
    dotnet publish src/Dwell/Dwell.csproj -c Release --no-build -o $stage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $app = Get-Item -LiteralPath (Join-Path $stage 'Dwell.exe')
    $version = $app.VersionInfo.FileVersion
    $output = Join-Path $artifacts "Dwell-$version-win-x64"
    New-Item -Path $output -ItemType Directory -Force | Out-Null
    & $compiler "/DAppVersion=$version" "/DAppSource=$stage" "/O$output" (Join-Path $PSScriptRoot 'installer\Dwell.iss')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $artifactsRoot = (Resolve-Path -LiteralPath $artifacts).Path
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $artifactsRoot) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}
