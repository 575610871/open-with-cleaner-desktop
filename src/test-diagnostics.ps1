$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testExecutable = Join-Path $PSScriptRoot 'ChooserDiagnosticsTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /langversion:5 /main:ChooserDiagnosticsTests "/out:$testExecutable" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll "$PSScriptRoot\RegistryEngine.cs" "$PSScriptRoot\EngineTests.cs" "$PSScriptRoot\ChooserDiagnostics.cs" "$PSScriptRoot\ChooserDiagnosticsTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic test compilation failed.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic tests failed.' }
