param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$PluginProject
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PluginProject)) {
    $PluginProject = Join-Path $repoRoot 'PCL.Plugin\PCL.Plugin.csproj'
}
$PluginProject = [System.IO.Path]::GetFullPath($PluginProject)
if (-not (Test-Path -LiteralPath $PluginProject -PathType Leaf)) {
    throw "PCL.Plugin project not found: $PluginProject. Clone the private plugin repository into PCL.Plugin or pass -PluginProject."
}

dotnet build $PluginProject -c $Configuration "-p:PclNRoot=$repoRoot" -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$pluginDirectory = Split-Path -Parent $PluginProject
$pluginAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\PCL.Plugin.dll"
$pluginAbstractionsAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\PCL.N.Plugin.Abstractions.dll"
$pluginI18nAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\PCL.N.Plugin.i18n.dll"
$pluginSdkAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\PCL.N.Plugin.Sdk.dll"
$pluginUiAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\PCL.N.Plugin.UI.dll"
$pluginUiAvaloniaAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\PCL.N.Plugin.UI.Avalonia.dll"
$pluginBouncyCastleAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\BouncyCastle.Cryptography.dll"
$pluginHarmonyAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\0Harmony.dll"
$pluginJsonCanonicalizerAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\jsoncanonicalizer.dll"
$pluginEs6NumberSerializerAssembly = Join-Path $pluginDirectory "bin\$Configuration\net10.0\es6numberserializer.dll"
foreach ($assembly in @($pluginAssembly, $pluginAbstractionsAssembly, $pluginI18nAssembly, $pluginSdkAssembly, $pluginUiAssembly, $pluginUiAvaloniaAssembly, $pluginBouncyCastleAssembly, $pluginHarmonyAssembly, $pluginJsonCanonicalizerAssembly, $pluginEs6NumberSerializerAssembly)) {
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
        throw "Plugin assembly was not produced: $assembly"
    }
}

dotnet run --project (Join-Path $repoRoot 'PCL.Desktop\PCL.Desktop.csproj') `
    -c $Configuration `
    "-p:PclPluginAssembly=$pluginAssembly" `
    "-p:PclPluginAbstractionsAssembly=$pluginAbstractionsAssembly" `
    "-p:PclPluginI18nAssembly=$pluginI18nAssembly" `
    "-p:PclPluginSdkAssembly=$pluginSdkAssembly" `
    "-p:PclPluginUiAssembly=$pluginUiAssembly" `
    "-p:PclPluginUiAvaloniaAssembly=$pluginUiAvaloniaAssembly" `
    "-p:PclPluginBouncyCastleAssembly=$pluginBouncyCastleAssembly" `
    "-p:PclPluginHarmonyAssembly=$pluginHarmonyAssembly" `
    "-p:PclPluginJsonCanonicalizerAssembly=$pluginJsonCanonicalizerAssembly" `
    "-p:PclPluginEs6NumberSerializerAssembly=$pluginEs6NumberSerializerAssembly"
exit $LASTEXITCODE
