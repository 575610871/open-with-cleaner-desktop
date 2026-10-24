$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testExecutable = Join-Path $PSScriptRoot 'CatalogUiTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /langversion:5 /main:CatalogUiTests "/out:$testExecutable" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "$PSScriptRoot\Program.cs" "$PSScriptRoot\RegistryEngine.cs" "$PSScriptRoot\ChooserDiagnostics.cs" "$PSScriptRoot\CatalogStatus.cs" "$PSScriptRoot\CatalogUiTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'UI filtering tests failed.' }
