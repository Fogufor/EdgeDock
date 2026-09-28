using System.Net.Http;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Setup;

/// <summary>
/// Когда проверять обновление и когда ставить (сама проверка — Updater). Проверка — один раз через 2 минуты после
/// запуска и по пункту трея; постоянного опроса нет. Ставить — только когда док «тих»: панель свернута, ничего не на
/// весь экран; иначе повторить попытку через 30 с.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ApplyRetry = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly DispatcherTimer _timer = new();
    private readonly Func<bool> _canRestart;
    private readonly Action<string, string> _notify;
    private bool _checking;
    private UpdateCheck? _ready;

    public UpdateService(Func<bool> canRestart, Action<string, string> notify)
    {
        _canRestart = canRestart;
        _notify = notify;
        _timer.Tick += (_, _) => OnTimer();
    }

    public void ScheduleFirstCheck()
    {
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    /// <summary>Проверить сейчас. manual — по пункту трея: ответить уведомлением в любом случае.</summary>
    public async void CheckNow(bool manual)
    {
        if (_ready != null)
        {
            TryApply();
            return;
        }
        if (_checking) return;
        _checking = true;
        try
        {
            Updater.DeleteDownloads(AppPaths.Updates);
            var result = await Updater.FetchNewerAsync(Http, Updater.LatestRelease, Version.Parse(Installer.Version), AppPaths.Updates);
            switch (result.Status)
            {
                case UpdateStatus.UpToDate:
                    if (manual) _notify("EdgeDock", $"Установлена последняя версия {Installer.Version}.");
                    break;
                case UpdateStatus.Failed:
                    if (!result.Offline) Log.Warn($"Обновление: {result.Error}");
                    if (manual) _notify("Не удалось проверить обновления", result.Error ?? "");
                    break;
                case UpdateStatus.Ready:
                    _ready = result;
                    TryApply();
                    break;
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private void OnTimer()
    {
        _timer.Stop();
        if (_ready != null) TryApply();
        else CheckNow(manual: false);
    }

    private void TryApply()
    {
        if (_ready?.Path is not string path) return;
        if (!_canRestart())
        {
            _timer.Interval = ApplyRetry;
            _timer.Start();
            return;
        }
        _notify("EdgeDock обновляется", $"Версия {_ready.Version}: виджет перезапустится на пару секунд.");
        try
        {
            Updater.Apply(path);
        }
        catch (Exception ex)
        {
            Log.Error("Обновление: не удалось запустить установку.", ex);
            _ready = null;
        }
    }

    public void Dispose() => _timer.Stop();
}
