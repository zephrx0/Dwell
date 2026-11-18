param([switch]$Package, [switch]$SingleFile)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet build Dwell.slnx -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test tests/Dwell.Core.Tests/Dwell.Core.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($SingleFile) {
    $stage = Join-Path $PSScriptRoot ('artifacts\bundle-' + [Guid]::NewGuid().ToString('N'))
    try {
        dotnet publish src/Dwell/Dwell.csproj -c Release -o $stage -p:PublishSingleFile=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $exe = Get-Item -LiteralPath (Join-Path $stage 'Dwell.exe')
        $version = $exe.VersionInfo.FileVersion
        $output = Join-Path $PSScriptRoot "artifacts\Dwell-$version-win-x64"
        New-Item -Path $output -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $exe.FullName -Destination (Join-Path $output 'Dwell.exe') -Force
    }
    finally {
        # The staging directory belongs to this publish invocation.
        if (Test-Path -LiteralPath $stage) {
            $artifactsRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'artifacts')).Path
            $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
            if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $artifactsRoot) {
                Remove-Item -LiteralPath $resolvedStage -Recurse -Force
            }
        }
    }
    return
}
$output = Join-Path $PSScriptRoot 'artifacts\Dwell'
dotnet publish src/Dwell/Dwell.csproj -c Release --no-build -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Package) {
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object Name -ne 'dwell-update.json' | ForEach-Object {
        $relative = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
        $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $output 'Dwell.dll')).Version.ToString()
    $manifest = [ordered]@{ Product = 'Dwell'; Version = $version; Runtime = 'win-x64'; Files = $files } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $output 'dwell-update.json'), $manifest, [Text.UTF8Encoding]::new($false))
    $zip = Join-Path $PSScriptRoot "artifacts\Dwell-$version-win-x64.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
            $relative = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
