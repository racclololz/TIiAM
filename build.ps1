$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:"$root\src\app.manifest" /win32icon:"$root\assets\tiam.ico" /out:"$root\bin\Tiam.exe" `
    /r:"$fw\WPF\UIAutomationClient.dll" /r:"$fw\WPF\UIAutomationTypes.dll" /r:"$fw\WPF\WindowsBase.dll" `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
"built $root\bin\Tiam.exe"
