using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanLite.Core.Data;
using PlanLite.Core.Services;
using PlanLite.Core.Utilities;

namespace PlanLite.ViewModels;

public sealed partial class ReportViewModel : ObservableObject
{
    private readonly PlanRepository _repository;
    private readonly ReportService _reportService;
    private readonly Func<string, string, string, string?> _pickSaveFilePath;
    private readonly Action<string> _showToast;

    public ReportViewModel(
        PlanRepository repository,
        ReportService reportService,
        Func<string, string, string, string?> pickSaveFilePath,
        Action<string> showToast)
    {
        _repository = repository;
        _reportService = reportService;
        _pickSaveFilePath = pickSaveFilePath;
        _showToast = showToast;
        _selectedDate = DateOnly.FromDateTime(DateTime.Now);
    }

    [ObservableProperty]
    private bool _isDailyReport = true;

    [ObservableProperty]
    private DateOnly _selectedDate;

    [ObservableProperty]
    private string _periodLabel = string.Empty;

    [ObservableProperty]
    private string _reportMarkdown = string.Empty;

    partial void OnIsDailyReportChanged(bool value)
    {
        RefreshReport();
    }

    public void RefreshReport()
    {
        if (IsDailyReport)
        {
            PeriodLabel = WeekHelper.FormatChineseDateWithDay(SelectedDate);
            var tasks = _repository.GetDailyTasks(SelectedDate);
            ReportMarkdown = _reportService.GenerateDailyReport(SelectedDate, tasks);
        }
        else
        {
            var weekStart = WeekHelper.GetWeekStart(SelectedDate);
            PeriodLabel = WeekHelper.FormatWeekLabel(weekStart);
            var weeklyTasks = _repository.GetWeeklyTasks(weekStart);
            var dailyTasks = _repository.GetDailyTasksByWeek(weekStart);
            ReportMarkdown = _reportService.GenerateWeeklyReport(weekStart, weeklyTasks, dailyTasks);
        }
    }

    [RelayCommand]
    private void SwitchReportKind(string kind)
    {
        IsDailyReport = string.Equals(kind, "Daily", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void PreviousPeriod()
    {
        SelectedDate = IsDailyReport ? SelectedDate.AddDays(-1) : SelectedDate.AddDays(-7);
        RefreshReport();
    }

    [RelayCommand]
    private void NextPeriod()
    {
        SelectedDate = IsDailyReport ? SelectedDate.AddDays(1) : SelectedDate.AddDays(7);
        RefreshReport();
    }

    [RelayCommand]
    private void GoToCurrentPeriod()
    {
        SelectedDate = DateOnly.FromDateTime(DateTime.Now);
        RefreshReport();
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (string.IsNullOrEmpty(ReportMarkdown))
        {
            return;
        }

        try
        {
            Clipboard.SetText(ReportMarkdown);
            _showToast("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            _showToast($"复制失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ExportMarkdown()
    {
        var defaultName = IsDailyReport
            ? $"工作日报-{SelectedDate:yyyy-MM-dd}.md"
            : $"工作周报-{WeekHelper.GetIsoWeekInfo(SelectedDate).IsoYear}-W{WeekHelper.GetIsoWeekInfo(SelectedDate).WeekNumber:00}.md";

        var path = _pickSaveFilePath(defaultName, "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*", "导出 Markdown 报告");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, ReportMarkdown);
            _showToast($"已导出至 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _showToast($"导出失败: {ex.Message}");
        }
    }
}
