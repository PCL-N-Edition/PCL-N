param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
# Rejection cases intentionally return 1; assertions below decide success, not the native-command preference.
$PSNativeCommandUseErrorActionPreference = $false
$binary = (Resolve-Path -LiteralPath $Executable).Path
& $binary --help
if ($LASTEXITCODE -ne 0) { throw 'Benchmark help failed.' }
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('nexa-benchmark-contract-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workspace) | Out-Null
# Keep this small test directory for inspection; it contains no downloaded game files.
$inputFile = Join-Path $workspace 'pack.mrpack'
$zip = [IO.Compression.ZipFile]::Open($inputFile, [IO.Compression.ZipArchiveMode]::Create)
try {
    $entry = $zip.CreateEntry('modrinth.index.json')
    $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.Write('{"formatVersion":1,"game":"minecraft","versionId":"fixture","name":"Fixture","dependencies":{"minecraft":"1.21.1"},"files":[]}') }
    finally { $writer.Dispose() }
} finally { $zip.Dispose() }
$output = Join-Path $workspace 'must-not-exist'
$fakeJava = Join-Path $workspace 'java'
[IO.File]::WriteAllText($fakeJava, 'must never execute')
$badHash = '0' * 64
& $binary $inputFile $badHash $fakeJava $fakeJava $output 2048 60 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $output)) { throw 'Hash mismatch must fail before executing Java or creating the game root.' }
& $binary $inputFile $badHash $fakeJava $fakeJava $workspace 2048 60 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1) { throw 'Existing output directories must be rejected.' }
foreach ($limits in @(@('0','60'), @('2048','0'), @('2048','1801'), @('12289','60'))) {
    & $binary $inputFile $badHash $fakeJava $fakeJava $output $limits[0] $limits[1] 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $output)) { throw 'Invalid limits must not create or launch a game.' }
}
Write-Host 'Benchmark input boundary checks passed. No real Minecraft run was performed.'
exit 0
