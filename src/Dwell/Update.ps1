param([Parameter(Mandatory = $true)][string]$Request)
$ErrorActionPreference = 'Stop'
$requestPath = [IO.Path]::GetFullPath($Request)
$jobDirectory = [IO.Path]::GetDirectoryName($requestPath)
$updateRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Dwell\updates')) + '\'
if (-not $jobDirectory.StartsWith($updateRoot, [StringComparison]::OrdinalIgnoreCase)) { exit 1 }
$job = Get-Content -LiteralPath $requestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$targetRoot = [IO.Path]::GetFullPath($job.Target).TrimEnd('\') + '\'
$stageRoot = [IO.Path]::GetFullPath($job.Stage).TrimEnd('\') + '\'
if (-not $stageRoot.StartsWith($jobDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 1 }
function Resolve-Child([string]$root, [string]$relative) {
    $resolvedPath = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
    if (-not $resolvedPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid path' }
    return $resolvedPath
}
$backupRoot = Join-Path $jobDirectory 'backup'
$changed = [Collections.Generic.List[string]]::new()
$existing = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$success = $false
$updateMutex = $null
$mutexOwned = $false
try {
    $parent = Get-Process -Id $job.ParentId -ErrorAction SilentlyContinue
    if ($parent -and -not $parent.WaitForExit(30000)) { throw 'App is still running' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $updateMutex = [Threading.Mutex]::new($false, "Local\Dwell.$sid")
    try { $mutexOwned = $updateMutex.WaitOne(30000) }
    catch [Threading.AbandonedMutexException] { $mutexOwned = $true }
    if (-not $mutexOwned) { throw 'App is still running' }
    foreach ($relative in $job.Files) {
        $destination = Resolve-Child $targetRoot $relative
        $source = Resolve-Child $stageRoot $relative
        $backup = Resolve-Child ($backupRoot + '\') $relative
        if (Test-Path -LiteralPath $destination) {
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($backup)) | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup
            $existing.Add($relative) | Out-Null
        }
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $changed.Add($relative)
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    $success = $true
}
catch {
    for ($index = $changed.Count - 1; $index -ge 0; $index--) {
        $relative = $changed[$index]
        $destination = Resolve-Child $targetRoot $relative
        if ($existing.Contains($relative)) {
            Copy-Item -LiteralPath (Resolve-Child ($backupRoot + '\') $relative) -Destination $destination -Force
        }
        elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
    }
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'Dwell\update-error.txt'), $job.FailureText)
}
finally {
    if ($mutexOwned) { $updateMutex.ReleaseMutex() }
    if ($updateMutex) { $updateMutex.Dispose() }
    Start-Process -FilePath (Join-Path $targetRoot 'Dwell.exe') -WorkingDirectory $targetRoot -WindowStyle Hidden
    # Only this GUID directory under the verified updates root is removed.
    if ($success -and $jobDirectory.StartsWith($updateRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $jobDirectory -Recurse -Force
    }
}
