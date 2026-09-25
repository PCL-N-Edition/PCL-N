param(
    [Parameter(Mandatory = $true)][string]$HostExecutable,
    [Parameter(Mandatory = $true)][string]$JavaHome
)
$ErrorActionPreference = 'Stop'
$hostPath = (Resolve-Path -LiteralPath $HostExecutable).Path
$suffix = if ($IsWindows) { '.exe' } else { '' }
$javaPath = (Resolve-Path -LiteralPath (Join-Path $JavaHome "bin/java$suffix")).Path
$javacPath = Join-Path $JavaHome "bin/javac$suffix"
$release = Get-Content -LiteralPath (Join-Path $JavaHome 'release') -Raw
if ($release -notmatch '(?m)^JAVA_VERSION="(?:1\.)?(\d+)') { throw 'Cannot identify fixture Java major' }
$javaMajor = [int]$Matches[1]
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("nexa-jni-smoke-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    & $javacPath -encoding UTF-8 -d $scratch (Join-Path $PSScriptRoot 'fixtures/JvmHostSmoke.java')
    if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
    $cocoaProbe = Join-Path $scratch 'libnexa-cocoa-probe.dylib'
    if ($IsMacOS) {
        & clang -dynamiclib -framework Cocoa -I (Join-Path $JavaHome 'include') -I (Join-Path $JavaHome 'include/darwin') -o $cocoaProbe (Join-Path $PSScriptRoot 'fixtures/JvmHostCocoaSmoke.m')
        if ($LASTEXITCODE -ne 0) { throw 'Cocoa probe compilation failed' }
    }
    function Write-Field($writer, [string]$value) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($value)
        $writer.Write([int]$bytes.Length)
        $writer.Write($bytes)
    }
    foreach ($mode in @('normal', 'throw', 'exit', 'wait')) {
        $payload = [IO.MemoryStream]::new()
        $writer = [IO.BinaryWriter]::new($payload)
        $writer.Write([int]0x4E4A564D)
        $writer.Write([int]1)
        Write-Field $writer $javaPath
        Write-Field $writer $scratch
        Write-Field $writer 'JvmHostSmoke'
        $jvm = @('-Xmx64m', '-Xcheck:jni', '-Dfile.encoding=UTF-8', '-Dnexa.fixture=test value')
        if ($javaMajor -ge 9) { $jvm += @('--add-opens', 'java.base/java.lang=ALL-UNNAMED') }
        if ($IsMacOS) { $jvm += @('-XstartOnFirstThread', '-Xdock:name=Nexa JNI Smoke', "-Dnexa.cocoa.probe=$cocoaProbe") }
        $jvm += @('-cp', $scratch)
        $writer.Write([int]$jvm.Length)
        foreach ($value in $jvm) { Write-Field $writer $value }
        $game = @($mode, '', '中文😀')
        $writer.Write([int]$game.Length)
        foreach ($value in $game) { Write-Field $writer $value }
        $bytes = $payload.ToArray()
        $writer.Dispose()
        $payload.Dispose()
        $start = [Diagnostics.ProcessStartInfo]::new($hostPath)
        $start.ArgumentList.Add('--jvm-host')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $child = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $child.StandardOutput.ReadToEndAsync()
            $stderr = $child.StandardError.ReadToEndAsync()
            $pipe = [IO.BinaryWriter]::new($child.StandardInput.BaseStream)
            $pipe.Write([int]$bytes.Length)
            $pipe.Write($bytes)
            $pipe.Flush()
            $child.StandardInput.Close()
            if ($mode -eq 'wait') {
                if ($child.WaitForExit(3000)) { throw 'Waiting JVM exited early' }
                $child.Kill($true)
            }
            if (!$child.WaitForExit(15000)) { throw 'JVM host timed out' }
            $output = $stdout.GetAwaiter().GetResult()
            $errorOutput = $stderr.GetAwaiter().GetResult()
            $expectedCode = switch ($mode) { 'normal' { 0 } 'throw' { 1 } 'exit' { 7 } }
            if ($mode -ne 'wait' -and $child.ExitCode -ne $expectedCode) {
                throw "JVM $mode exit=$($child.ExitCode): $errorOutput"
            }
            if ($mode -eq 'normal' -and ($output -notmatch 'NEXA_JNI_MAIN_RETURNED' -or $output -notmatch 'NEXA_JNI_BACKGROUND_FINISHED' -or $errorOutput -notmatch 'NEXA_JNI_STDERR')) { throw "Missing output: $output $errorOutput" }
            if ($IsMacOS -and $output -notmatch 'NEXA_JNI_COCOA_MAIN_THREAD') { throw "Missing Cocoa first-thread evidence: $errorOutput" }
            if ($mode -eq 'throw' -and $errorOutput -notmatch 'NEXA_JNI_EXCEPTION') { throw "Missing exception: $errorOutput" }
            if ($mode -eq 'wait' -and $output -notmatch 'NEXA_JNI_WAITING') { throw "JVM never started: $errorOutput" }
            Write-Output "PASS: JNI host $mode"
        }
        finally {
            if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit() }
            $child.Dispose()
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('nexa-jni-smoke-')) { throw 'Invalid cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
