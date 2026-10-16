using WallTrek.Services.Scheduling;

namespace WallTrek.Services
{
    /// <summary>
    /// App-resident, background ThreadPool scheduler, independent of the WinUI display timer.
    /// Windows startup registration resumes the app after user sign-in; after sleep, a
    /// 30-second wall-clock check catches missed due slots. Sleeping PCs are not woken.
    /// </summary>
    public sealed class AutoGenerateService
    {
        public static AutoGenerateService Instance { get; } = new();
        private readonly object _guard = new();
        private readonly DailyScheduleCoordinator _daily =
            new(new DailyScheduleJournalStore());
        private CancellationTokenSource? _workerCancellation;
        private Task? _worker;
        private Func<CancellationToken, Task<ScheduledRunOutcome>>? _execution;
        private bool _enabled;

        public event EventHandler? AutoGenerateTriggered;
        public event EventHandler<string>? NextGenerateTimeUpdated;

        private AutoGenerateService() { }
        public bool IsEnabled => _enabled;
        public DateTime? NextGenerateTime => Settings.Instance.NextAutoGenerateTime;
        public void SetExecutionHandler(Func<CancellationToken, Task<ScheduledRunOutcome>> handler)
            => _execution = handler ?? throw new ArgumentNullException(nameof(handler));

        public void RefreshFromSettings()
        {
            Stop();
            var settings = Settings.Instance;
            if (!settings.AutoGenerateEnabled)
            {
                // Preserve previous interval-mode semantics: disabling auto mode clears
                // an obsolete next-run timestamp so reenabling starts a fresh interval.
                if (settings.AutoGenerateMode == "interval" &&
                    settings.NextAutoGenerateTime.HasValue)
                {
                    settings.NextAutoGenerateTime = null;
                    settings.Save();
                }
                return;
            }
            if (settings.AutoGenerateMode == "daily")
            {
                // Validate before starting background tasks or touching journal files.
                var activation = settings.DailyAutoGenerateEnabledAtUtc ?? DateTimeOffset.UtcNow;
                var config = new DailyScheduleConfig(settings.DailyAutoGenerateTime,
                    settings.DailyAutoGenerateTimeZone, activation);
                _ = DailyWallClock.NextUtc(config, DateTimeOffset.UtcNow);
                if (!settings.DailyAutoGenerateEnabledAtUtc.HasValue)
                {
                    settings.DailyAutoGenerateEnabledAtUtc = activation;
                    settings.Save();
                }
            }
            else if (settings.AutoGenerateHours <= 0 ||
                     double.IsNaN(settings.AutoGenerateHours) ||
                     double.IsInfinity(settings.AutoGenerateHours)) return;
            else if (!settings.NextAutoGenerateTime.HasValue)
            {
                settings.NextAutoGenerateTime = DateTime.Now.AddHours(settings.AutoGenerateHours);
                settings.Save();
            }

            lock (_guard)
            {
                _enabled = true;
                _workerCancellation = new CancellationTokenSource();
                var token = _workerCancellation.Token;
                _worker = Task.Run(() => PollLoopAsync(token));
            }
            UpdateNextGenerateTime();
        }

        public void Start(double hours)
        {
            if (hours <= 0 || double.IsNaN(hours) || double.IsInfinity(hours)) return;
            Settings.Instance.AutoGenerateMode = "interval";
            Settings.Instance.AutoGenerateHours = hours;
            Settings.Instance.NextAutoGenerateTime = DateTime.Now.AddHours(hours);
            Settings.Instance.Save();
            RefreshFromSettings();
        }

        public void StartFromSavedTime() => RefreshFromSettings();

        public void Cancel()
        {
            Stop();
            Settings.Instance.NextAutoGenerateTime = null;
            Settings.Instance.Save();
        }

        public void Stop()
        {
            lock (_guard)
            {
                _enabled = false;
                _workerCancellation?.Cancel();
                // Existing task may still be completing a started generation; don't
                // dispose CTS while that callback is using it.
                _workerCancellation = null;
            }
            NextGenerateTimeUpdated?.Invoke(this, "");
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            try
            {
                // First check is immediate, then recurring even if the main window is hidden.
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Windows timezone and DST rules may change while the app runs.
                        TimeZoneInfo.ClearCachedData();
                        await CheckDueAsync(token);
                        UpdateNextGenerateTime();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // Do not catch up repeatedly after a corrupt state or unexpected
                        // failure. Retain prior wallpaper; retry only polling, not images.
                        System.Diagnostics.Debug.WriteLine("Auto-schedule check failed: " +
                            ex.GetType().Name);
                        NextGenerateTimeUpdated?.Invoke(this,
                            "Schedule unavailable; inspect local settings/journal.");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private async Task CheckDueAsync(CancellationToken token)
        {
            var s = Settings.Instance;
            if (!s.AutoGenerateEnabled || _execution is null) return;
            if (s.AutoGenerateMode == "daily")
            {
                var config = new DailyScheduleConfig(s.DailyAutoGenerateTime,
                    s.DailyAutoGenerateTimeZone,
                    s.DailyAutoGenerateEnabledAtUtc ?? DateTimeOffset.UtcNow);
                await _daily.CheckAsync(config, _execution, token);
                return;
            }

            var next = s.NextAutoGenerateTime;
            if (!next.HasValue) return;
            if (DateTime.Now < next.Value) return;
            // Consume previous due time before any async network call. After a crash,
            // an already-dispatched interval slot is not blindly dispatched again.
            var intervalHours = s.AutoGenerateHours;
            var now = DateTime.Now;
            s.NextAutoGenerateTime = now.AddHours(intervalHours);
            s.Save();
            await _execution(token);
        }

        private void UpdateNextGenerateTime()
        {
            var settings = Settings.Instance;
            if (!_enabled || !settings.AutoGenerateEnabled)
            {
                NextGenerateTimeUpdated?.Invoke(this, "");
                return;
            }
            if (settings.AutoGenerateMode == "daily")
            {
                var config = new DailyScheduleConfig(settings.DailyAutoGenerateTime,
                    settings.DailyAutoGenerateTimeZone,
                    settings.DailyAutoGenerateEnabledAtUtc ?? DateTimeOffset.UtcNow);
                var zone = DailyWallClock.ResolveZone(config.TimeZoneId);
                var next = DailyWallClock.NextUtc(config, DateTimeOffset.UtcNow);
                var wall = TimeZoneInfo.ConvertTime(next, zone);
                var journal = _daily.Snapshot();
                NextGenerateTimeUpdated?.Invoke(this,
                    $"Daily: {wall:ddd MMM d, HH:mm} ({zone.StandardName}) · {journal.LastStatus}");
            }
            else
                NextGenerateTimeUpdated?.Invoke(this,
                    settings.NextAutoGenerateTime is DateTime when
                        ? $"Every {settings.AutoGenerateHours:0.##}h · next {when:MMM d, h:mm tt}"
                        : "Interval schedule unavailable");
        }
    }
}
