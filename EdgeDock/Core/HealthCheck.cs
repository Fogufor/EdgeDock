using System.Diagnostics;

namespace EdgeDock.Core;

/// <summary>
/// Раз в минуту проверяет, не стал ли виджет заметен по нагрузке, и пишет в лог, если:
/// процесс в среднем за минуту держит CPU выше 2% или рабочий набор памяти больше 150 МБ.
/// Пишет один раз на каждый такой эпизод, на экран ничего не выводит.
/// </summary>
internal sealed class HealthCheck : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const double CpuLimitPercent = 2;
    private const long MemoryLimitBytes = 150L * 1024 * 1024;

    private readonly Timer _timer;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastTime;
    private bool _cpuHigh, _memoryHigh;

    public HealthCheck()
    {
        _timer = new Timer(_ => Tick());
        Resume();
    }

    /// <summary>Полноэкранный режим: таймер тоже замирает.</summary>
    public void Suspend() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Resume()
    {
        _process.Refresh();
        _lastCpu = _process.TotalProcessorTime;
        _lastTime = DateTime.UtcNow;
        _timer.Change(Interval, Interval);
    }

    private void Tick()
    {
        try
        {
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            var now = DateTime.UtcNow;
            double percent = (cpu - _lastCpu).TotalMilliseconds / (now - _lastTime).TotalMilliseconds / Environment.ProcessorCount * 100;
            _lastCpu = cpu;
            _lastTime = now;

            if (percent > CpuLimitPercent && !_cpuHigh) Log.Warn($"Нагрузка на CPU {percent:0.0}% в среднем за последнюю минуту.");
            _cpuHigh = percent > CpuLimitPercent;

            long memory = _process.WorkingSet64;
            if (memory > MemoryLimitBytes && !_memoryHigh) Log.Warn($"Рабочий набор памяти {memory / 1024 / 1024} МБ.");
            _memoryHigh = memory > MemoryLimitBytes;
        }
        catch (Exception ex)
        {
            Log.Error("Проверка нагрузки не удалась.", ex);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _process.Dispose();
    }
}
