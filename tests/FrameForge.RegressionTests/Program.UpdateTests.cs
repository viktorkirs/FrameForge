using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;
using FrameForge.UI;

internal static partial class Program
{
    static string UpdateManifest(string version, string? url = null) => JsonSerializer.Serialize(new
    {
        version, downloadUrl = url ?? UpdateChecker.InstallerUri.AbsoluteUri
    });

    sealed class UpdateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    static HttpClient UpdateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new UpdateHttpHandler(send));
    static HttpResponseMessage UpdateResponse(string version) => new(HttpStatusCode.OK) { Content = new StringContent(UpdateManifest(version)) };
    static T UpdateResult<T>(Task<T> task)
    {
        if (Application.Current != null) Wait(task);
        return task.GetAwaiter().GetResult();
    }

    static void RunUpdateCoreTests()
    {
        Test("updates compare numeric application versions and ignore equal or older builds", () =>
        {
            var installed = new Version(1, 3, 0, 0);
            Check(UpdateChecker.ParseManifest(UpdateManifest("1.3.0"), installed) == null, "Equal three-part version notified a four-part installation");
            Check(UpdateChecker.ParseManifest(UpdateManifest("1.2.9"), installed) == null, "An older installer was offered");
            Check(UpdateChecker.ParseManifest(UpdateManifest("1.10.0"), new Version(1, 9, 9))?.Version == new Version(1, 10, 0), "Versions were compared as text");
            Check(UpdateChecker.ParseManifest(UpdateManifest("1.3.1"), installed)?.DownloadUri == UpdateChecker.InstallerUri, "New patch installer was not offered");
        });
        Test("updates reject malformed manifests and unexpected installer locations", () =>
        {
            foreach (string json in new[] { "{}", "not json", UpdateManifest("1.4"), UpdateManifest("1.4.0-beta"),
                UpdateManifest("1.4.0", "http://github.com/viktorkirs/FrameForge/releases/download/latest/FrameForge-Setup-x64.exe"),
                UpdateManifest("1.4.0", "https://example.com/setup.exe"),
                UpdateManifest("1.4.0", "https://github.com/another/repository/releases/download/latest/setup.exe") })
            {
                bool rejected = false;
                try { UpdateChecker.ParseManifest(json, new Version(1, 3, 0)); }
                catch (Exception ex) when (ex is JsonException or System.IO.InvalidDataException or InvalidOperationException) { rejected = true; }
                Check(rejected, "Accepted an invalid update manifest");
            }
        });
        Test("update HTTP checks request only the manifest and include cache and version headers", () =>
        {
            int calls = 0;
            using HttpClient client = UpdateClient((request, _) =>
            {
                calls++;
                Check(request.Method == HttpMethod.Get && request.RequestUri == UpdateChecker.ManifestUri && request.Content == null, "Update request uploaded data or requested an installer");
                Check(request.Headers.UserAgent.ToString() == "FrameForge/1.3.0" && request.Headers.CacheControl?.NoCache == true, "Update headers are missing");
                return Task.FromResult(UpdateResponse("1.4.0"));
            });
            AppUpdate? update = UpdateResult(new UpdateChecker(client).CheckAsync(new Version(1, 3, 0, 0)));
            Check(calls == 1 && update?.Version == new Version(1, 4, 0), "Published version was not read");
        });
        Test("update HTTP errors, oversized responses and cancellation do not become version notices", () =>
        {
            using HttpClient missing = UpdateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
            bool failed = false;
            try { UpdateResult(new UpdateChecker(missing).CheckAsync(new Version(1, 3, 0))); }
            catch (HttpRequestException) { failed = true; }
            Check(failed, "Missing manifest was reported as up to date");
            using HttpClient large = UpdateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 20_000)) }));
            failed = false;
            try { UpdateResult(new UpdateChecker(large).CheckAsync(new Version(1, 3, 0))); }
            catch (System.IO.InvalidDataException) { failed = true; }
            Check(failed, "Unbounded update response was accepted");
            using HttpClient slow = UpdateClient(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return UpdateResponse("1.4.0"); });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            bool canceled = false;
            try { UpdateResult(new UpdateChecker(slow).CheckAsync(new Version(1, 3, 0), cancellation.Token)); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Update request ignored cancellation");
        });
        Test("automatic update preference survives cloning and persistence", () =>
        {
            var settings = new AppSettings { CheckUpdatesOnStartup = false, OutputDirectory = _output };
            Check(!settings.Clone().CheckUpdatesOnStartup && new AppSettings().CheckUpdatesOnStartup, "Update preference clone/default is wrong");
            SettingsStore.Save(settings);
            Check(!SettingsStore.Load().CheckUpdatesOnStartup, "Update preference did not persist");
        });
    }

    static void RunUpdateUiTests()
    {
        RunUpdateCoreTests();
        Test("startup updates can be disabled, notify once and only download on click", () =>
        {
            int calls = 0;
            Uri? opened = null;
            using HttpClient client = UpdateClient((_, _) => { calls++; return Task.FromResult(UpdateResponse("1.4.0")); });
            using var model = new AppUpdateViewModel(new UpdateChecker(client), uri => opened = uri, new Version(1, 3, 0));
            Wait(model.StartAsync(false));
            Check(calls == 0 && !model.ShowNotice, "Disabled startup check sent a request");
            Wait(model.StartAsync(true));
            Wait(model.StartAsync(true));
            Check(calls == 1 && model.ShowNotice && model.HasUpdate && opened == null, "Startup check repeated or downloaded without a click");
            model.DownloadCommand.Execute(null);
            Check(opened == UpdateChecker.InstallerUri, "Download action did not open the published installer");
            model.DismissCommand.Execute(null);
            Check(!model.ShowNotice, "Update notice did not dismiss");
            Wait(model.CheckAsync());
            Check(calls == 2 && model.ShowNotice && model.Status.Contains("1.4.0"), "Manual check did not restore the version notice");
        });
        Test("current and offline automatic checks are quiet while manual checks show a result", () =>
        {
            using HttpClient current = UpdateClient((_, _) => Task.FromResult(UpdateResponse("1.3.0")));
            using var currentModel = new AppUpdateViewModel(new UpdateChecker(current), installed: new Version(1, 3, 0));
            Wait(currentModel.StartAsync(true));
            Check(!currentModel.ShowNotice && !currentModel.HasUpdate, "Current installation showed a startup notice");
            Wait(currentModel.CheckAsync());
            Check(currentModel.ShowNotice && currentModel.Status.Contains("1.3.0"), "Manual check did not show the installed version");
            using HttpClient offline = UpdateClient((_, _) => throw new HttpRequestException("offline"));
            using var offlineModel = new AppUpdateViewModel(new UpdateChecker(offline), installed: new Version(1, 3, 0));
            Wait(offlineModel.StartAsync(true));
            Check(!offlineModel.ShowNotice && !offlineModel.HasUpdate, "Offline startup interrupted the user");
            Wait(offlineModel.CheckAsync());
            Check(offlineModel.ShowNotice && offlineModel.Status == Loc.Get("UpdateCheckFailed") && offlineModel.CheckCommand.CanExecute(null), "Manual offline result is missing or cannot retry");
        });
        Test("closing cancels an update request and duplicate checks share the pending task", () =>
        {
            int calls = 0;
            bool canceled = false;
            using HttpClient slow = UpdateClient(async (_, token) =>
            {
                calls++;
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled = true; throw; }
                return UpdateResponse("1.4.0");
            });
            using var model = new AppUpdateViewModel(new UpdateChecker(slow), installed: new Version(1, 3, 0));
            Task pending = model.CheckAsync();
            Check(ReferenceEquals(pending, model.CheckAsync()) && calls == 1 && !model.CheckCommand.CanExecute(null), "Concurrent checks sent duplicate requests");
            model.Dispose();
            Wait(pending);
            Check(canceled && !model.HasUpdate, "Closing retained the update request or produced a stale notice");
        });
        Test("update banner and About download controls fit all interface languages", () =>
        {
            using HttpClient client = UpdateClient((_, _) => Task.FromResult(UpdateResponse("1.4.0")));
            using var model = new AppUpdateViewModel(new UpdateChecker(client), _ => { }, new Version(1, 3, 0));
            Wait(model.CheckAsync());
            SettingsStore.Save(new AppSettings { Language = AppLanguage.Russian, CheckUpdatesOnStartup = false, SystemAudio = false, OutputDirectory = _output });
            var main = new MainWindow { Width = 680, Height = 600 };
            var notice = (Border)main.FindName("UpdateNotice");
            notice.DataContext = model;
            main.Show();
            try
            {
                foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
                {
                    Loc.Set(language);
                    Wait(Task.Delay(70));
                    main.UpdateLayout();
                    Check(notice.IsVisible && notice.ActualHeight > 20 && model.Status.Contains("1.4.0"), "New-version banner is hidden or untranslated");
                    SaveWindow(main, $"update-notice-{language}.png");
                    var about = new AboutWindow(model);
                    about.Show();
                    about.UpdateLayout();
                    Check(((Button)about.FindName("CheckUpdatesButton")).IsEnabled && about.ActualHeight < 800, "About update controls are unavailable or overflow");
                    SaveWindow(about, $"update-about-{language}.png");
                    about.Close();
                }
            }
            finally
            {
                main.Close();
                for (int attempt = 0; attempt < 50 && main.IsVisible; attempt++) Wait(Task.Delay(30));
                Loc.Set(AppLanguage.Russian);
            }
        });
    }
}
