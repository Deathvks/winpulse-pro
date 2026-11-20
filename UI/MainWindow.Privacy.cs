using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WinPulseOptimizer;

public partial class MainWindow
{
    private static readonly UsageReportingSettings? UsageReporting = UsageReportingSettings.Current;
    private static readonly HttpClient? UsageClient = UsageReporting == null ? null :
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(7) };
    private readonly UsageStatistics _usageStatistics = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulseOptimizer",
            UsageReporting == null ? "usage_statistics.json" : "usage_statistics_sharing.json"),
        typeof(MainWindow).Assembly.GetName().Version!.ToString(3), UsageReporting?.ConsentScope);
    private bool _showingPrivacy;
    private bool _privacyNoticeShown;
    private CancellationTokenSource? _usageUpload;

    private async Task ShowForegroundNoticesAsync()
    {
        if (!_automationTimer.IsEnabled || !CanCheckForUpdate) return;
        if (_usageStatistics.NeedsDecision && !_privacyNoticeShown) ShowUsagePrivacy(initialNotice: true);
        await CheckForUpdateAsync();
        await TryUploadUsageAsync();
    }

    private async Task TryUploadUsageAsync()
    {
        if (UsageReporting == null || UsageClient == null || _usageUpload != null || !CanCheckForUpdate ||
            _usageStatistics.PrepareUpload(DateTimeOffset.UtcNow) is not { } batch) return;

        using var cancellation = new CancellationTokenSource();
        _usageUpload = cancellation;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, UsageReporting.Endpoint) { Content = JsonContent.Create(batch) };
            using var response = await UsageClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            if (response.StatusCode == HttpStatusCode.NoContent && !cancellation.IsCancellationRequested)
                _usageStatistics.ConfirmUpload(batch.BatchId);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        finally { _usageUpload = null; }
    }

    private void BtnPrivacy_Click(object sender, RoutedEventArgs e) => ShowUsagePrivacy(initialNotice: false);

    private void ShowUsagePrivacy(bool initialNotice)
    {
        if (_showingPrivacy) return;
        _usageUpload?.Cancel();
        _showingPrivacy = true;
        _privacyNoticeShown = true;
        try
        {
            var window = new PrivacyWindow(_usageStatistics, initialNotice, UsageReporting) { Owner = this };
            window.ShowDialog();
            if (_usageStatistics.NeedsDecision) _usageStatistics.SetConsent(false);
            else _usageStatistics.Flush();
        }
        finally { _showingPrivacy = false; }
    }
}
