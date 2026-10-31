using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DriveMonitor.App.Services;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Export;
using Microsoft.Win32;

namespace DriveMonitor.App.ViewModels;

// Toolbar and row commands: clear, export, activity log, clipboard, File Explorer, quick filters, elevation.
public sealed partial class MainViewModel
{
    private bool _logBusy;
    private bool _logErrorReported;

    /// <summary>The last log ended on the thread pool; the shutdown waits for it.</summary>
    private Task? _logStopTask;

    /// <summary>True while every event is also written to a CSV log.</summary>
    public bool IsLogging => _engine.Log is not null;

    /// <summary>Opens the folder of a Live activity or Files row in File Explorer (renamed files: the new location).</summary>
    public void OpenContainingFolder(object? row)
    {
        string? path = row switch
        {
            ActivityRowViewModel activity => activity.TargetPath ?? activity.Path,
            FileRowViewModel file => (file.Lifecycle & FileLifecycle.Renamed) != 0 && file.RenamedTo is not null ? file.RenamedTo : file.Path,
            _ => null,
        };
        if (path is not null)
            _ = ShowInFolderAsync(path);
    }

    /// <summary>Shows only the activity of the row's process (by name).</summary>
    public void FilterToProcess(object? row)
    {
        string? name = row switch
        {
            ActivityRowViewModel activity => activity.ProcessName,
            ProcessRowViewModel process => process.ProcessName,
            _ => null,
        };
        if (!string.IsNullOrEmpty(name))
            SetSearchNow(name);
    }

    private void FilterToFile(object? row)
    {
        string? path = row switch
        {
            ActivityRowViewModel activity => activity.Path,
            FileRowViewModel file => file.Path,
            _ => null,
        };
        if (!string.IsNullOrEmpty(path))
            SetSearchNow(path);
    }

    private void Clear()
    {
        _engine.ClearStatistics();
        Activity.Clear();
        Files.Clear();
        Processes.Clear();
        foreach (DriveItemViewModel drive in Drives)
            drive.ResetActivity();
        CloseBanner(BannerKeys.PauseOverflow);
        UpdateCounts();
        UpdatePausedText();
    }

    private void CopyPaths(object? parameter)
    {
        List<string> paths = Rows(parameter)
            .Select(row => row switch
            {
                ActivityRowViewModel activity => activity.Path,
                FileRowViewModel file => file.Path,
                _ => null,
            })
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
        catch (ExternalException ex)
        {
            // Another program is holding the clipboard open.
            ShowBanner(BannerKeys.Clipboard, BannerKind.Error, "Could not copy to the clipboard: " + ex.Message,
                autoClose: TimeSpan.FromSeconds(10));
        }
    }

    private async Task ShowInFolderAsync(string path)
    {
        // Fire-and-forget callers: every failure ends up in a banner, never in an unobserved task.
        try
        {
            string? problem = await Task.Run(() => ShellActions.ShowInFolder(path));
            if (problem is not null)
                ShowBanner(BannerKeys.Explorer, BannerKind.Info, problem, autoClose: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKeys.Explorer, BannerKind.Error, "Could not open File Explorer: " + ex.Message, autoClose: TimeSpan.FromSeconds(10));
        }
    }

    private void ExportActivity()
    {
        string? path = AskForCsvPath("activity");
        if (path is null)
            return;

        // Copy on the UI thread: rows keep changing while the file is written.
        ActivityRecord[] rows = Activity.View.Cast<ActivityRowViewModel>().Select(row => row.ToRecord()).ToArray();
        _ = ExportAsync(path, "rows", () =>
        {
            CsvExport.WriteActivity(path, rows);
            return rows.Length;
        });
    }

    private void ExportFiles()
    {
        string? path = AskForCsvPath("files");
        if (path is null)
            return;

        _ = ExportAsync(path, "files", () =>
        {
            List<FileStatsSnapshot> files = _engine.Stats.GetAllFiles();
            Csv.ExportFileStats(path, files);
            return files.Count;
        });
    }

    private void ExportProcesses()
    {
        string? path = AskForCsvPath("processes");
        if (path is null)
            return;

        _ = ExportAsync(path, "processes", () =>
        {
            List<ProcessStatsSnapshot> processes = _engine.Stats.GetAllProcesses();
            CsvExport.WriteProcesses(path, processes);
            return processes.Count;
        });
    }

    private static string? AskForCsvPath(string what)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Export {what} as CSV",
            FileName = $"DriveMonitor-{what}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            DefaultExt = ".csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Runs <paramref name="write"/> on the thread pool and reports the outcome in a banner.</summary>
    private async Task ExportAsync(string path, string what, Func<int> write)
    {
        try
        {
            int count = await Task.Run(write);
            ShowBanner(BannerKeys.Export, BannerKind.Info, $"Exported {DisplayText.Number(count)} {what} to {path}",
                "Show file", () => _ = ShowInFolderAsync(path), autoClose: TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKeys.Export, BannerKind.Error, $"Could not export to {path}: {ex.Message}");
        }
    }

    private async void ToggleLog()
    {
        if (_logBusy)
            return;

        _logBusy = true;
        _toggleLogCommand.RaiseCanExecuteChanged();
        bool starting = _engine.Log is null;
        try
        {
            if (starting)
            {
                string path = Path.Combine(
                    SettingsStore.LogsDirectory,
                    $"activity-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv");
                await Task.Run(() => _engine.StartLog(path));
                _logErrorReported = false;
                ShowBanner(BannerKeys.Log, BannerKind.Info, "Recording every event to " + path,
                    "Open logs folder", OpenLogsFolder, autoClose: TimeSpan.FromSeconds(10));
            }
            else
            {
                // Waits for the writer to flush, which can take a moment on a slow disk.
                Task stop = Task.Run(_engine.StopLog);
                _logStopTask = stop;
                await stop;
                CloseBanner(BannerKeys.Log);
            }
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKeys.Log, BannerKind.Error, $"Could not {(starting ? "start" : "stop")} the activity log: {ex.Message}");
        }
        finally
        {
            _logBusy = false;
            _toggleLogCommand.RaiseCanExecuteChanged();

            // Also undoes the toggle button's own state change when starting the log failed.
            OnPropertyChanged(nameof(IsLogging));
        }
    }

    /// <summary>A log whose writer failed (disk full, drive removed) is closed and reported once.</summary>
    private void CheckLogHealth()
    {
        if (_logErrorReported || _logBusy || _engine.Log?.Error is not { } error)
            return;

        _logErrorReported = true;
        ShowBanner(BannerKeys.Log, BannerKind.Error, "The activity log stopped: " + error.Message);
        _ = StopFailedLogAsync();
    }

    private async Task StopFailedLogAsync()
    {
        _logBusy = true;
        _toggleLogCommand.RaiseCanExecuteChanged();
        try
        {
            Task stop = Task.Run(_engine.StopLog);
            _logStopTask = stop;
            await stop;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "stop failed log");
        }
        finally
        {
            _logBusy = false;
            _toggleLogCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsLogging));
        }
    }

    private void OpenLogsFolder() => _ = OpenLogsFolderAsync();

    private async Task OpenLogsFolderAsync()
    {
        try
        {
            await Task.Run(() => ShellActions.OpenFolder(SettingsStore.LogsDirectory));
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKeys.Explorer, BannerKind.Error, "Could not open the logs folder: " + ex.Message, autoClose: TimeSpan.FromSeconds(10));
        }
    }

    private void RestartAsAdministrator()
    {
        string executable = Environment.ProcessPath!;
        string arguments = SingleInstance.RestartedArgument;

        // Started as "dotnet DriveMonitor.dll": the host needs the app's path. Assembly.Location would be empty in
        // a single-file app, so the path is built from the folder the host loaded the app from.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string app = Path.Combine(AppContext.BaseDirectory, typeof(MainViewModel).Assembly.GetName().Name + ".dll");
            arguments = $"\"{app}\" {arguments}";
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        }
        catch (Win32Exception)
        {
            // The user answered No to the administrator prompt.
            ShowBanner(BannerKeys.Elevation, BannerKind.Info, "DriveMonitor was not restarted as administrator.",
                autoClose: TimeSpan.FromSeconds(10));
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The rows a context-menu command applies to: the grid's selected items, or a single row.</summary>
    private static List<object> Rows(object? parameter) => parameter switch
    {
        null => [],
        IList list => list.Cast<object>().ToList(),
        _ => [parameter],
    };

    private static object? FirstRow(object? parameter) => Rows(parameter).FirstOrDefault();
}
