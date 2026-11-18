# GpgLite – deinstalacja (usuwa wpisy użytkownika; wpisy -AllUsers tylko gdy uruchomiono jako administrator)
$ids = @('{7F3C2A9E-5B1D-4E8A-9C6F-2D4B8A1E3F57}', '{A1D4E8C2-3B7F-4F6A-9E2D-5C8B1A7F3E90}')
$progIds = @('GpgLite.Connect', 'GpgLite.Pane')

while (Get-Process OUTLOOK -ErrorAction SilentlyContinue) {
    Write-Host "Zamknij Outlooka i naciśnij Enter..." -ForegroundColor Yellow
    [void](Read-Host)
}

$paths = @()
foreach ($hive in @('HKCU:', 'HKLM:')) {
    foreach ($id in $ids) { $paths += "$hive\Software\Classes\CLSID\$id"; $paths += "$hive\Software\Classes\Wow6432Node\CLSID\$id" }
    foreach ($p in $progIds) { $paths += "$hive\Software\Classes\$p" }
    $paths += "$hive\Software\Microsoft\Office\Outlook\Addins\GpgLite.Connect"
    $paths += "$hive\Software\WOW6432Node\Microsoft\Office\Outlook\Addins\GpgLite.Connect"
}
foreach ($p in $paths) {
    if (Test-Path $p) { try { Remove-Item $p -Recurse -Force } catch { Write-Host "Pominięto (brak uprawnień): $p" -ForegroundColor Yellow } }
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList' -Name 'GpgLite.Connect' -ErrorAction SilentlyContinue

foreach ($bin in @((Join-Path $env:LOCALAPPDATA 'GpgLite\bin'), (Join-Path $env:ProgramFiles 'GpgLite'))) {
    if (Test-Path $bin) { try { Remove-Item $bin -Recurse -Force } catch { Write-Host "Nie można usunąć $bin (uruchom jako administrator)" -ForegroundColor Yellow } }
}

$ans = Read-Host "Przywrócić GpgOL (jeśli był wyłączony instalatorem)? (T/N)"
if ($ans -match '^[TtYy]') {
    Get-ChildItem 'HKCU:\Software\Microsoft\Office\Outlook\Addins' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like '*GpgOL*' } | ForEach-Object { Remove-Item $_.PSPath -Recurse -Force }
}
Write-Host "GpgLite odinstalowany. Ustawienia, listy adresów i log zostały w $env:LOCALAPPDATA\GpgLite (możesz je usunąć)." -ForegroundColor Green
