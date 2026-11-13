using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed class SettingsCatalogHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource OldEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OldRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RetryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RetryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _retryCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var old = request.Headers.Authorization?.Parameter == "old-test-key";
            if (old) { OldEntered.TrySetResult(); await OldRelease.Task; }
            var retry = request.Headers.Authorization?.Parameter == "retry-test-key";
            if (retry && Interlocked.Increment(ref _retryCalls) == 1) { RetryEntered.TrySetResult(); await RetryRelease.Task; }
            var id = old ? "gpt-old-fixture" : retry ? "gpt-retry-fixture" : "gpt-new-fixture";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"" + id + "\"}]}", Encoding.UTF8, "application/json") };
        }
    }

    private static async Task CheckAiSettingsLoginAsync(MainWindow owner)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        using var handler = new SettingsCatalogHandler();
        using var connection = new AiConnection(handler);
        using var settings = new AiSettingsPanel(connection);
        var initial = new AiCredentials { ActiveClient = "client-a", Accounts = [
            new AiAccount { ClientId = "client-a", Subject = "a", Email = "a@example.invalid" },
            new AiAccount { ClientId = "client-b", Subject = "b", Email = "b@example.invalid" }
        ] };
        initial.Save();
        var host = new Window { Owner = owner, Width = 760, Height = 680, Content = settings, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout();
        try
        {
            await Task.Delay(100);
            var options = (StackPanel)settings.FindName("ConnectionOptions");
            var cancel = (Button)settings.FindName("CancelButton");
            var accounts = (ComboBox)settings.FindName("Accounts");
            var status = (TextBlock)settings.FindName("StatusText");
            Task Run(Func<CancellationToken, Task> work) => (Task)Invoke(settings, "RunOperationAsync", work)!;
            async Task Until(Func<bool> condition)
            {
                for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
                Check(condition(), "Timed out waiting for settings operation");
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = Run(async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            await started.Task;
            Check(options.IsEnabled && accounts.IsEnabled && cancel.IsEnabled, "An in-progress login disabled the settings page");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Check(options.IsEnabled && !cancel.IsEnabled && status.Text.Contains("取消"), "Cancelling login did not restore the settings page");

            await Run(_ => Task.FromException(new InvalidOperationException("授权失败测试")));
            Check(options.IsEnabled && !cancel.IsEnabled && status.Text == "授权失败测试", "Failed login did not restore the settings page");
            var retried = false;
            await Run(_ => { retried = true; return Task.CompletedTask; });
            Check(retried && options.IsEnabled, "Settings could not retry after login failure");

            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = Run(async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            await started.Task;
            accounts.SelectedItem = accounts.Items.Cast<AiAccount>().Single(account => account.ClientId == "client-b");
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => !settings.IsBusy && AiCredentials.Load().ActiveClient == "client-b");
            Check(options.IsEnabled, "Switching an existing account left settings disabled");

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = Run(async token => { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { await release.Task; throw; } });
            await started.Task;
            var obsoleteStarted = false; var newestStarted = false;
            var obsolete = Run(_ => { obsoleteStarted = true; return Task.CompletedTask; });
            var newest = Run(_ => { newestStarted = true; return Task.CompletedTask; });
            Check(options.IsEnabled, "Waiting to replace login disabled the settings page");
            release.TrySetResult(); await Task.WhenAll(waiting, obsolete, newest).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!obsoleteStarted && newestStarted, "Rapid replacement operations did not retain the latest request");

            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = Run(async token => { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { await release.Task; throw; } });
            await started.Task;
            var cancelledReplacementStarted = false;
            var cancelledReplacement = Run(_ => { cancelledReplacementStarted = true; return Task.CompletedTask; });
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            release.TrySetResult(); await Task.WhenAll(waiting, cancelledReplacement).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!cancelledReplacementStarted && options.IsEnabled && !cancel.IsEnabled, "Cancelling also needs to cancel a queued replacement");

            var credentials = AiCredentials.Load(); credentials.UseApiKey = true; credentials.ApiKey = "old-test-key"; credentials.Save();
            var oldLoad = settings.LoadModelsAsync(CancellationToken.None);
            await handler.OldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            credentials = AiCredentials.Load(); credentials.ApiKey = "new-test-key"; credentials.Save();
            var newLoad = settings.LoadModelsAsync(CancellationToken.None);
            await newLoad.WaitAsync(TimeSpan.FromSeconds(5));
            handler.OldRelease.TrySetResult();
            try { await oldLoad; } catch (OperationCanceledException) { }
            Check(settings.SelectedModel?.Id == "gpt-new-fixture" && AiCredentials.Load().Model == "gpt-new-fixture",
                "A stale model response replaced the selected account's catalog");
            credentials = AiCredentials.Load(); credentials.ApiKey = "retry-test-key"; credentials.Save();
            using var retryCancellation = new CancellationTokenSource();
            var cancelledLoad = settings.LoadModelsAsync(retryCancellation.Token);
            await handler.RetryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retryCancellation.Cancel();
            var retriedLoad = settings.LoadModelsAsync(CancellationToken.None);
            await retriedLoad.WaitAsync(TimeSpan.FromSeconds(5));
            handler.RetryRelease.TrySetResult();
            try { await cancelledLoad; } catch (OperationCanceledException) { }
            Check(settings.SelectedModel?.Id == "gpt-retry-fixture", "Retry reused a cancelled catalog request");
            Console.WriteLine("PASS: settings stay usable during login, cancellation, failure, retry, account switch and stale catalog.");
        }
        finally { handler.OldRelease.TrySetResult(); handler.RetryRelease.TrySetResult(); host.Close(); }
    }
}
