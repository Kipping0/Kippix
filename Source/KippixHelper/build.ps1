# Builds Kippix Helper (KippixHelper.dll) from src\ with the C# compiler that ships with Windows, makes KippixHelper.pext and
# installs it into Playnite. Close Playnite first. See docs/BUILDING.md.
param([string]$PlaynitePath = "", [switch]$CompileOnly)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$log = Join-Path $root "build.log"
"Kippix Helper build $(Get-Date -Format s)" | Set-Content $log

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color; Add-Content $log $msg }
function Fail($msg) { Say "ERROR: $msg" "Red"; exit 1 }

# .NET Framework compiler
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
if (-not (Test-Path "$fw\csc.exe")) { Fail "The .NET Framework 4 compiler (csc.exe) wasn't found." }
$wpf = Join-Path $fw "WPF"

# Playnite
$candidates = @()
if ($PlaynitePath) { $candidates += $PlaynitePath }
$candidates += "$env:LOCALAPPDATA\Playnite", "$env:ProgramFiles\Playnite", "${env:ProgramFiles(x86)}\Playnite"
$playnite = $null
foreach ($c in $candidates) { if ($c -and (Test-Path (Join-Path $c "Playnite.SDK.dll"))) { $playnite = $c.TrimEnd('\'); break } }
if (-not $playnite) { Fail "Couldn't find Playnite." }
$sdk = Join-Path $playnite "Playnite.SDK.dll"

# Compile
$out = Join-Path $root "build"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

$refs = @(
    $sdk,
    "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll", "$fw\System.Web.Extensions.dll",
    "$fw\System.Drawing.dll", "$fw\System.Windows.Forms.dll",
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$wpf\UIAutomationClient.dll", "$wpf\UIAutomationTypes.dll"
)
if (Test-Path "$fw\System.Runtime.dll") { $refs += "$fw\System.Runtime.dll" }
$sources = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }

$lines = @("/nologo", "/target:library", "/optimize+", "/platform:anycpu", "/warn:1", "/codepage:65001", "/out:""$out\KippixHelper.dll""")
$lines += $refs | ForEach-Object { "/r:""$_""" }
$lines += $sources | ForEach-Object { """$_""" }
$rsp = Join-Path $out "csc.rsp"
Set-Content -Path $rsp -Value $lines -Encoding ASCII

Say "Compiling..."
$ErrorActionPreference = "Continue"
$output = & "$fw\csc.exe" "@$rsp" 2>&1
$code = $LASTEXITCODE
$ErrorActionPreference = "Stop"
Remove-Item $rsp -ErrorAction SilentlyContinue
$output | ForEach-Object { Write-Host $_; Add-Content $log $_ }
if ($code -ne 0) { Fail "Compile failed (errors above, also in build.log)." }
Say "Compiled OK." "Green"
if ($CompileOnly) { exit 0 }

Copy-Item (Join-Path $root "extension.yaml") $out
if (Test-Path (Join-Path $root "icon.png")) { Copy-Item (Join-Path $root "icon.png") $out }

# Shareable .pext
$pext = Join-Path $root "KippixHelper.pext"
$zip = Join-Path $root "KippixHelper.zip"
if (Test-Path $pext) { Remove-Item $pext -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Rename-Item $zip $pext

# Install
if (Get-Process -Name "Playnite.DesktopApp", "Playnite.FullscreenApp" -ErrorAction SilentlyContinue) {
    Fail "Playnite is still running, so the add-on couldn't be installed. Close Playnite and run Install.bat again."
}
$portable = -not (Test-Path (Join-Path $playnite "unins000.exe"))
if ($portable) { $extRoot = Join-Path $playnite "Extensions" } else { $extRoot = "$env:APPDATA\Playnite\Extensions" }
$dest = Join-Path $extRoot "KippixHelper_3f6c2d1e-8a47-4b9e-b1d5-7e2a9c4f0b18"   # (1.0.4) the folder a release installs to: only ever one copy
New-Item -ItemType Directory $dest -Force | Out-Null
Copy-Item "$out\*" $dest -Force
Say "Installed to: $dest" "Green"
