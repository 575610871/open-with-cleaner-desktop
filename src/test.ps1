$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$testExecutable = Join-Path $PSScriptRoot 'EngineTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /langversion:5 /main:OpenWithCleaner.EngineTests "/out:$testExecutable" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Runtime.Serialization.dll "$PSScriptRoot\RegistryEngine.cs" "$PSScriptRoot\EngineTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'In-memory engine tests failed.' }
