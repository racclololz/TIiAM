$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\WPF"
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:"$root\src\app.manifest" /out:"$root\bin\Tiam.exe" `
    /r:"$wpf\UIAutomationClient.dll" /r:"$wpf\UIAutomationTypes.dll" /r:"$fw\WPF\WindowsBase.dll" `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
"built $root\bin\Tiam.exe"
