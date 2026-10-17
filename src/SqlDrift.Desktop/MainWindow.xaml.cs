using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SqlDrift.Core.Backup;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Generator;
using SqlDrift.Core.Models;
using SqlDrift.Core.Planner;
using SqlDrift.Core.Reporting;

namespace SqlDrift.Desktop;

public partial class MainWindow : Window
{
    private readonly List<string> _backups = new();
    private SchemaDriftReport? _report;
    private DatabaseSchemaSnapshot? _sourceSnapshot;
    private DatabaseSchemaSnapshot? _targetSnapshot;
    private readonly List<DriftRow> _driftRows = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void AddBackups_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Attach .bak backup files",
            Filter = "SQL Backup (*.bak)|*.bak|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames)
            {
                if (!_backups.Contains(f))
                {
                    _backups.Add(f);
                }
            }
            RefreshBackupLists();
            StatusText.Text = $"Attached {_backups.Count} backup file(s).";
        }
    }

    private void ClearBackups_Click(object sender, RoutedEventArgs e)
    {
        _backups.Clear();
        RefreshBackupLists();
        StatusText.Text = "Cleared attached backups.";
    }

    private void RefreshBackupLists()
    {
        BackupsListBox.Items.Clear();
        foreach (var f in _backups)
            BackupsListBox.Items.Add(Path.GetFileName(f));

        SourceCombo.Items.Clear();
        TargetCombo.Items.Clear();
        foreach (var f in _backups)
        {
            SourceCombo.Items.Add(Path.GetFileName(f));
            TargetCombo.Items.Add(Path.GetFileName(f));
        }
        if (SourceCombo.Items.Count > 0) SourceCombo.SelectedIndex = 0;
        if (TargetCombo.Items.Count > 1) TargetCombo.SelectedIndex = 1;
        else if (TargetCombo.Items.Count > 0) TargetCombo.SelectedIndex = 0;
    }

    private string? GetSelectedBackupPath(ComboBox combo)
    {
        if (combo.SelectedIndex < 0 || combo.SelectedIndex >= _backups.Count)
            return null;
        return _backups[combo.SelectedIndex];
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var srcPath = GetSelectedBackupPath(SourceCombo);
        var tgtPath = GetSelectedBackupPath(TargetCombo);
        if (srcPath == null || tgtPath == null)
        {
            MessageBox.Show("Please attach and select source and target backups.", "Missing selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (srcPath.Equals(tgtPath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Source and target backups must be different files.", "Invalid selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CompareButton.IsEnabled = false;
        GenerateScriptButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        StatusText.Text = "Restoring backups and extracting schemas...";
        SummaryText.Text = "Working...";

        RestoredDatabase? r1 = null;
        RestoredDatabase? r2 = null;
        try
        {
            var server = ServerTextBox.Text;
            StatusText.Text = "Restoring source backup...";
            r1 = await BackupRestoreService.RestoreAsync(server, srcPath);
            StatusText.Text = "Restoring target backup...";
            r2 = await BackupRestoreService.RestoreAsync(server, tgtPath);

            StatusText.Text = "Extracting source schema and records...";
            _sourceSnapshot = await new UniversalSchemaExtractor().ExtractAsync(r1.ConnectionString);
            StatusText.Text = "Extracting target schema and records...";
            _targetSnapshot = await new UniversalSchemaExtractor().ExtractAsync(r2.ConnectionString);

            _report = new SchemaDiffer().Compare(_sourceSnapshot, _targetSnapshot, srcPath, tgtPath);

            _driftRows.Clear();
            FlattenDrifts(_report.Drifts, _driftRows);
            DriftsGrid.ItemsSource = null;
            DriftsGrid.ItemsSource = _driftRows;

            SummaryText.Text = $"Total: {_report.TotalDrifts} | Breaking: {_report.BreakingCount} | Medium: {_report.MediumCount} | Low: {_report.LowCount}";
            StatusText.Text = $"Comparison complete. {_report.TotalDrifts} drift(s) found.";
            GenerateScriptButton.IsEnabled = _report.HasDrift;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Error: " + ex.Message;
            SummaryText.Text = "Failed.";
        }
        finally
        {
            if (r2 != null) await r2.DisposeAsync();
            if (r1 != null) await r1.DisposeAsync();
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Collapsed;
            CompareButton.IsEnabled = true;
        }
    }

    private static void FlattenDrifts(List<ObjectDriftResult> drifts, List<DriftRow> rows)
    {
        foreach (var d in drifts)
        {
            rows.Add(new DriftRow
            {
                ObjectType = d.ObjectType.ToString(),
                FullName = d.FullName,
                DriftType = d.DriftType.ToString(),
                Severity = d.Severity.ToString(),
                Description = d.Description
            });
            if (d.Children != null)
                FlattenDrifts(d.Children, rows);
        }
    }

    private void GenerateScript_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null || _sourceSnapshot == null || _targetSnapshot == null)
            return;

        var dlg = new SaveFileDialog
        {
            Title = "Save migration script",
            Filter = "SQL Script (*.sql)|*.sql|All files (*.*)|*.*",
            FileName = "migration.sql"
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var planner = new MigrationPlanner();
            var plan = planner.BuildPlan(_report, _sourceSnapshot, _targetSnapshot);
            var generator = new MigrationScriptGenerator(new GeneratorOptions
            {
                AllowDataLoss = false,
                WrapInTransaction = true,
                TransactionName = "SqlDrift_Desktop_Migration"
            });
            var sql = generator.GenerateMigrationScript(plan, _report.SourceIdentifier, _report.TargetIdentifier);
            File.WriteAllText(dlg.FileName, sql);
            ScriptTextBox.Text = sql;
            StatusText.Text = $"Migration script saved to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class DriftRow
{
    public string ObjectType { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string DriftType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}