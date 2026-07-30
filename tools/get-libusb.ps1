<#
    Скачивает libusb-1.0.dll (x64) и кладёт его рядом с собранным exe.
    Нужен только для vendor-команд (палитра, эмиссивность, gain и т.п.).
    Приём кадров и измерение температуры работают без него.

    Использование:
        pwsh -File tools\get-libusb.ps1
        pwsh -File tools\get-libusb.ps1 -Version 1.0.27 -Configuration Release
#>
param(
    [string]$Version = "1.0.27",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$targets = @(
    (Join-Path $root "src\ThermalApp\bin\x64\$Configuration\net9.0-windows"),
    (Join-Path $root "src\ThermalApp\bin\$Configuration\net9.0-windows"),
    (Join-Path $root "src\ThermalApp.Probe\bin\x64\$Configuration\net9.0-windows"),
    (Join-Path $root "src\ThermalApp.Probe\bin\$Configuration\net9.0-windows")
)

$url = "https://github.com/libusb/libusb/releases/download/v$Version/libusb-$Version.7z"
$tmp = Join-Path $env:TEMP "libusb-$Version"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$archive = Join-Path $tmp "libusb.7z"

Write-Host "Скачиваю $url"
Invoke-WebRequest -Uri $url -OutFile $archive

$sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
if (-not $sevenZip) {
    $candidate = "C:\Program Files\7-Zip\7z.exe"
    if (Test-Path $candidate) { $sevenZip = $candidate } else {
        throw "Нужен 7-Zip для распаковки. Установите его (winget install 7zip.7zip) или скачайте libusb-1.0.dll вручную с https://github.com/libusb/libusb/releases"
    }
}
$exe = if ($sevenZip -is [string]) { $sevenZip } else { $sevenZip.Source }

& $exe x -y -o"$tmp" $archive | Out-Null

$dll = Get-ChildItem -Path $tmp -Recurse -Filter "libusb-1.0.dll" |
       Where-Object { $_.FullName -match "VS20\d\d\\MS64|MinGW64|x64" } |
       Select-Object -First 1
if (-not $dll) { $dll = Get-ChildItem -Path $tmp -Recurse -Filter "libusb-1.0.dll" | Select-Object -First 1 }
if (-not $dll) { throw "libusb-1.0.dll не найден в архиве" }

foreach ($t in $targets) {
    if (Test-Path $t) {
        Copy-Item $dll.FullName (Join-Path $t "libusb-1.0.dll") -Force
        Write-Host "Скопировано в $t"
    }
}
Write-Host "Готово. Источник: $($dll.FullName)"
