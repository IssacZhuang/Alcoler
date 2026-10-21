param([string]$CrateDirectory = $PSScriptRoot, [Parameter(Mandatory = $true)][string]$Library)
$ErrorActionPreference = 'Stop'
$sha = [System.Security.Cryptography.SHA256]::Create()
$files = [System.Collections.Generic.List[string]]::new()
foreach ($relative in @('Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', 'build.rs', '.cargo/config.toml')) {
    if (Test-Path -LiteralPath (Join-Path $CrateDirectory $relative)) { $files.Add($relative) }
}
$crateRoot = (Resolve-Path -LiteralPath $CrateDirectory).Path.TrimEnd('\', '/')
$sourceDirectory = Join-Path $crateRoot 'src'
if (Test-Path -LiteralPath $sourceDirectory) {
    foreach ($path in [System.IO.Directory]::GetFiles($sourceDirectory, '*.rs', [System.IO.SearchOption]::AllDirectories)) {
        $files.Add($path.Substring($crateRoot.Length + 1).Replace('\', '/'))
    }
}
$files.Sort([System.StringComparer]::Ordinal)
$entries = [System.Text.StringBuilder]::new()
foreach ($relative in $files) {
    $text = [System.IO.File]::ReadAllText((Join-Path $crateRoot $relative)).Replace("`r`n", "`n").Replace("`r", "`n")
    $hash = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))) -replace '-', '').ToLowerInvariant()
    [void]$entries.Append($relative).Append([char]0).Append($hash).Append([char]10)
}
$sourceHash = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($entries.ToString())))) -replace '-', ''
$sourceHash = $sourceHash.ToLowerInvariant()
$stream = [System.IO.File]::OpenRead($Library)
try { $binaryHash = (([System.BitConverter]::ToString($sha.ComputeHash($stream))) -replace '-', '').ToLowerInvariant() } finally { $stream.Dispose() }
[System.IO.File]::WriteAllText("$Library.source.sha256", "$sourceHash`n$binaryHash`n")
Write-Output "source: $sourceHash"
Write-Output "binary: $binaryHash"
