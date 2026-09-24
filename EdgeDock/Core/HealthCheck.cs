using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EdgeDock.Core;

/// <summary>
/// Раз в минуту проверяет, не стал ли виджет заметен по нагрузке, и пишет в лог, если:
/// процесс в среднем за минуту держит CPU выше 2% или его память больше 150 МБ.
/// Пишет один раз на каждый такой эпизод, на экран ничего не выводит.
/// </summary>
internal sealed class HealthCheck : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const double CpuLimitPercent = 2;
    private const long MemoryLimitBytes = 150L * 1024 * 1024;

    // Эпизод заканчивается, только когда значение опустилось заметно ниже порога,
    // иначе значение, колеблющееся около порога, писало бы запись каждую минуту.
    private const double CalmFactor = 0.9;

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

            if (percent > CpuLimitPercent && !_cpuHigh)
            {
                _cpuHigh = true;
                Log.Warn($"Нагрузка на CPU {percent:0.0}% в среднем за последнюю минуту.");
            }
            else if (percent < CpuLimitPercent * CalmFactor)
            {
                _cpuHigh = false;
            }

            long memory = PrivateMemory();
            if (memory > MemoryLimitBytes && !_memoryHigh)
            {
                _memoryHigh = true;
                Log.Warn($"Память процесса {memory / 1024 / 1024} МБ.");
            }
            else if (memory < MemoryLimitBytes * CalmFactor)
            {
                _memoryHigh = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Проверка нагрузки не удалась.", ex);
        }
    }

    /// <summary>
    /// Собственная память процесса в RAM — то же, что столбец «Память» в Диспетчере задач.
    /// Полный рабочий набор сюда не подходит: в нём общие системные библиотеки (драйвер видеокарты, WinRT, .NET),
    /// которые Windows засчитывает каждому процессу, — это ~100 МБ, которые виджет не занимает.
    /// </summary>
    private long PrivateMemory()
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        return GetProcessMemoryInfo(_process.Handle, ref counters, counters.cb)
            ? (long)counters.PrivateWorkingSetSize
            : _process.WorkingSet64;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    public void Dispose()
    {
        _timer.Dispose();
        _process.Dispose();
    }
}
