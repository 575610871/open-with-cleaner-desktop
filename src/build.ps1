param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$destination = Join-Path $OutputDirectory 'Open With Cleaner Desktop Suoh Edition.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 "/out:$destination" "/win32manifest:$PSScriptRoot\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.Runtime.Serialization.dll "$PSScriptRoot\AssemblyInfo.cs" "$PSScriptRoot\Program.cs" "$PSScriptRoot\RegistryEngine.cs" "$PSScriptRoot\ChooserDiagnostics.cs" "$PSScriptRoot\CatalogStatus.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $destination
