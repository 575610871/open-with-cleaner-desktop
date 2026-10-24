$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$testExecutable = Join-Path $PSScriptRoot 'CatalogStatusTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /langversion:5 /main:CatalogStatusTests "/out:$testExecutable" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll "$PSScriptRoot\RegistryEngine.cs" "$PSScriptRoot\EngineTests.cs" "$PSScriptRoot\CatalogStatus.cs" "$PSScriptRoot\CatalogStatusTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Availability test compilation failed.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Availability tests failed.' }
