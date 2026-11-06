using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlugelKranz.Diagnostics;
using System.Globalization;

namespace FlugelKranz.ViewModels;

/// <summary>"Save diagnostic logs": list what would go in, then write the redacted zip on confirmation.</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<DiagnosticFile> diagnosticPlan = [];
    private (string Key, object?[] Args)? diagnosticMessage;
    [ObservableProperty] private bool isDiagnosticPlanVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseDiagnostics))]
    private bool isSavingDiagnostics;
    public bool CanUseDiagnostics => !IsSavingDiagnostics;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticArchive))]
    private string? diagnosticArchivePath;

    /// <summary>Folders to read from; tests replace it.</summary>
    internal Func<DiagnosticSources> DiagnosticSourcesFactory { get; set; } = null!;
    /// <summary>Archive location for a collection time; tests replace it.</summary>
    internal Func<DateTime, string> DiagnosticArchivePathFactory { get; set; } = DiagnosticLogCollector.DefaultArchivePath;

    public bool HasDiagnosticArchive => DiagnosticArchivePath is not null && OperatingSystem.IsWindows();

    public string DiagnosticStatus => diagnosticMessage is { } m ? Strings.Format(m.Key, m.Args) : "";

    /// <summary>The planned files, one per line: archive name, size and last write time.</summary>
    public string DiagnosticFileList => diagnosticPlan.Count == 0
        ? Strings["settings.diagnostics.empty"]
        : string.Join("\n", diagnosticPlan.Select(f => string.Format(CultureInfo.InvariantCulture,
            "{0}   {1}   {2:yyyy-MM-dd HH:mm}", f.ArchivePath.Replace('/', '\\'), Size(f.Length), f.LastWriteTime)));

    public string DiagnosticPlanHeader => Strings.Format("settings.diagnostics.planHeader", diagnosticPlan.Count);

    private void SetDiagnosticMessage(string? key, params object?[] args)
    {
        diagnosticMessage = key is null ? null : (key, args);
        OnPropertyChanged(nameof(DiagnosticStatus));
    }

    [RelayCommand]
    private void PrepareDiagnosticLogs()
    {
        if (closing || IsSavingDiagnostics) return;
        try { diagnosticPlan = DiagnosticLogCollector.Plan(DiagnosticSourcesFactory()); }
        catch (Exception exception)
        {
            diagnosticPlan = [];
            SetDiagnosticMessage("settings.diagnostics.failed", exception.Message);
            return;
        }
        OnPropertyChanged(nameof(DiagnosticFileList));
        OnPropertyChanged(nameof(DiagnosticPlanHeader));
        SetDiagnosticMessage(null);
        IsDiagnosticPlanVisible = true;
    }

    [RelayCommand] private void CancelDiagnosticLogs() => IsDiagnosticPlanVisible = false;

    [RelayCommand]
    private async Task SaveDiagnosticLogs()
    {
        if (closing || IsSavingDiagnostics || !IsDiagnosticPlanVisible) return;
        IsSavingDiagnostics = true;
        SetDiagnosticMessage("settings.diagnostics.saving");
        var files = diagnosticPlan;
        DateTime now = DateTime.Now;
        string path = DiagnosticArchivePathFactory(now);
        try
        {
            await Task.Run(() => DiagnosticLogCollector.WriteArchive(path, files, DiagnosticRedactor.ForThisPc(), now));
            DiagnosticArchivePath = path;
            IsDiagnosticPlanVisible = false;
            SetDiagnosticMessage("settings.diagnostics.saved", path);
        }
        catch (Exception exception)
        {
            SetDiagnosticMessage("settings.diagnostics.failed", exception.Message);
        }
        finally { IsSavingDiagnostics = false; }
    }

    [RelayCommand]
    private void OpenDiagnosticFolder()
    {
        if (DiagnosticArchivePath is not { } path || !OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false
            });
        }
        catch (Exception exception) { SetDiagnosticMessage("settings.diagnostics.failed", exception.Message); }
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
}
