# Собирает EdgeDock.exe — один файл со всем .NET внутри — в папку artifacts и открывает окно установки.
# Нужен .NET 10 SDK. Запуск:  powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'artifacts'

dotnet publish "$PSScriptRoot\EdgeDock\EdgeDock.csproj" -c Release -r win-x64 -o $out
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

Start-Process (Join-Path $out 'EdgeDock.exe')
Write-Host "Готово: $out\EdgeDock.exe"
