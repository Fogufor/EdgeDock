# Собирает рабочую копию EdgeDock в %LocalAppData%\Programs\EdgeDock.
# Ярлык автозапуска указывает туда (отладочные сборки из bin\ автозапуск не трогают).
# Запуск:  powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\EdgeDock'

# Запущенный виджет держит exe — закрываем его перед заменой.
Get-Process EdgeDock -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500

dotnet publish "$PSScriptRoot\EdgeDock\EdgeDock.csproj" -c Release -r win-x64 --self-contained false -o $target
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

Start-Process (Join-Path $target 'EdgeDock.exe')
Write-Host "Готово: $target"
