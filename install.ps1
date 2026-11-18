# GpgLite – instalator
#   .\install.ps1            -> dla bieżącego użytkownika (bez uprawnień administratora)
#   .\install.ps1 -AllUsers  -> dla wszystkich użytkowników, do Program Files (wymaga administratora; bezpieczniej,
#                               bo zwykły proces użytkownika nie może podmienić pliku DLL)
param([switch]$AllUsers)
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$addinClsid = '{7F3C2A9E-5B1D-4E8A-9C6F-2D4B8A1E3F57}'; $addinProgId = 'GpgLite.Connect'

if (-not (Test-Path (Join-Path $src 'GpgLite.dll'))) { throw "Brak pliku GpgLite.dll obok skryptu." }
# Wersja i pełna nazwa zestawu są odczytywane z pliku DLL – jedynym źródłem numeru wersji jest kod (AssemblyInfo.cs).
$asm = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $src 'GpgLite.dll'))
$version = $asm.Version.ToString()
$asmName = $asm.FullName
Write-Host "=== Instalacja GpgLite $version ===" -ForegroundColor Cyan

if ($AllUsers) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw "Instalacja -AllUsers wymaga uruchomienia jako administrator." }
    $dest = Join-Path $env:ProgramFiles 'GpgLite'
    $hive = 'HKLM:'
    $classes = @('HKLM:\Software\Classes\CLSID', 'HKLM:\Software\Classes\Wow6432Node\CLSID')
    $progRoot = 'HKLM:\Software\Classes'
    $addinRoots = @('HKLM:\Software\Microsoft\Office\Outlook\Addins', 'HKLM:\Software\WOW6432Node\Microsoft\Office\Outlook\Addins')
} else {
    $dest = Join-Path $env:LOCALAPPDATA 'GpgLite\bin'
    $classes = @('HKCU:\Software\Classes\CLSID', 'HKCU:\Software\Classes\Wow6432Node\CLSID')
    $progRoot = 'HKCU:\Software\Classes'
    $addinRoots = @('HKCU:\Software\Microsoft\Office\Outlook\Addins')
}

while (Get-Process OUTLOOK -ErrorAction SilentlyContinue) {
    Write-Host "Outlook jest uruchomiony. Zamknij go i naciśnij Enter..." -ForegroundColor Yellow
    [void](Read-Host)
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$dll = Join-Path $dest 'GpgLite.dll'
Copy-Item (Join-Path $src 'GpgLite.dll') $dll -Force
Unblock-File $dll
$codeBase = ([Uri]$dll).AbsoluteUri

function Set-Val($path, $name, $value, $type = 'String') {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    if ($name -eq '(default)') { Set-Item -Path $path -Value $value }
    else { New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $type -Force | Out-Null }
}

function Register-Class($clsid, $progId, $typeName, $isControl) {
    foreach ($root in $classes) {
        $k = "$root\$clsid"
        Set-Val $k '(default)' $progId
        foreach ($ip in @("$k\InprocServer32", "$k\InprocServer32\$version")) {
            if ($ip -like '*InprocServer32') {
                Set-Val $ip '(default)' 'mscoree.dll'
                Set-Val $ip 'ThreadingModel' 'Both'
            }
            Set-Val $ip 'Class' $typeName   # pełna nazwa typu .NET (nie ProgId!)
            Set-Val $ip 'Assembly' $asmName
            Set-Val $ip 'RuntimeVersion' 'v4.0.30319'
            Set-Val $ip 'CodeBase' $codeBase
        }
        Set-Val "$k\ProgId" '(default)' $progId
        Set-Val "$k\Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}" '(default)' ''
        if ($isControl) {
            # Kontrolka ActiveX dla panelu bocznego Office (Custom Task Pane)
            Set-Val "$k\Control" '(default)' ''
            Set-Val "$k\MiscStatus" '(default)' '0'
            Set-Val "$k\MiscStatus\1" '(default)' '131456'
            Set-Val "$k\Version" '(default)' '1.1'
            Set-Val "$k\Implemented Categories\{40FC6ED4-2438-11CF-A3DB-080036F12502}" '(default)' ''
        }
    }
    Set-Val "$progRoot\$progId" '(default)' $progId
    Set-Val "$progRoot\$progId\CLSID" '(default)' $clsid
}

Register-Class $addinClsid $addinProgId 'GpgLite.Connect' $false
# porządek po wersjach 1.x (panel boczny)
foreach ($root in @('HKCU:\Software\Classes\CLSID', 'HKCU:\Software\Classes\Wow6432Node\CLSID')) {
    $old = "$root\{A1D4E8C2-3B7F-4F6A-9E2D-5C8B1A7F3E90}"; if (Test-Path $old) { Remove-Item $old -Recurse -Force }
}
if (Test-Path 'HKCU:\Software\Classes\GpgLite.Pane') { Remove-Item 'HKCU:\Software\Classes\GpgLite.Pane' -Recurse -Force }

foreach ($root in $addinRoots) {
    $addin = "$root\$addinProgId"
    Set-Val $addin 'FriendlyName' 'GpgLite (OpenPGP)'
    Set-Val $addin 'Description' 'Szyfrowanie i podpisy OpenPGP przez GnuPG z Gpg4win. Autor: BW'
    Set-Val $addin 'LoadBehavior' 3 'DWord'
}
# Nie pozwól Outlookowi wyłączyć dodatku za "wolne uruchamianie" (klucz zawsze per użytkownik)
Set-Val 'HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList' $addinProgId 1 'DWord'

Write-Host "Zainstalowano w: $dest" -ForegroundColor Green

$gpgol = Get-ChildItem 'HKLM:\Software\Microsoft\Office\Outlook\Addins', 'HKLM:\Software\WOW6432Node\Microsoft\Office\Outlook\Addins', 'HKCU:\Software\Microsoft\Office\Outlook\Addins' -ErrorAction SilentlyContinue |
         Where-Object { $_.PSChildName -like '*GpgOL*' }
if ($gpgol) {
    $ans = Read-Host "Wykryto GpgOL. Wyłączyć go dla Twojego konta, żeby nie kolidował z GpgLite? (T/N)"
    if ($ans -match '^[TtYy]') {
        foreach ($g in ($gpgol | Select-Object -ExpandProperty PSChildName -Unique)) {
            Set-Val "HKCU:\Software\Microsoft\Office\Outlook\Addins\$g" 'LoadBehavior' 0 'DWord'
        }
        Write-Host "GpgOL wyłączony. Jeśli nadal się ładuje, odznacz go ręcznie: Plik > Opcje > Dodatki > Dodatki COM." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Gotowe. Uruchom Outlooka – na karcie Narzędzia główne pojawi się grupa 'GpgLite (OpenPGP)'." -ForegroundColor Cyan
