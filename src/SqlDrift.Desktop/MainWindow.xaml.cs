using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
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
    private readonly List<ObjectDriftResult> _flatDrifts = new();
    private RestoredDatabase? _restoredSource;
    private RestoredDatabase? _restoredTarget;

    private HashSet<int> _srcDiffRows = new();
    private HashSet<int> _tgtDiffRows = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _restoredSource?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _restoredTarget?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                    _backups.Add(f);
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
            BackupsListBox.Items.Add(System.IO.Path.GetFileName(f));

        SourceCombo.Items.Clear();
        TargetCombo.Items.Clear();
        foreach (var f in _backups)
        {
            SourceCombo.Items.Add(System.IO.Path.GetFileName(f));
            TargetCombo.Items.Add(System.IO.Path.GetFileName(f));
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
        StatusText.Text = "Preparing...";

        try
        {
            // Dispose previous restored DBs
            if (_restoredSource != null) await _restoredSource.DisposeAsync();
            if (_restoredTarget != null) await _restoredTarget.DisposeAsync();
            _restoredSource = null;
            _restoredTarget = null;

            var server = ServerTextBox.Text;

            StatusText.Text = "Restoring source backup...";
            _restoredSource = await BackupRestoreService.RestoreAsync(server, srcPath);

            StatusText.Text = "Restoring target backup...";
            _restoredTarget = await BackupRestoreService.RestoreAsync(server, tgtPath);

            StatusText.Text = "Extracting source schema and records...";
            _sourceSnapshot = await new UniversalSchemaExtractor().ExtractAsync(_restoredSource.ConnectionString);

            StatusText.Text = "Extracting target schema and records...";
            _targetSnapshot = await new UniversalSchemaExtractor().ExtractAsync(_restoredTarget.ConnectionString);

            _report = new SchemaDiffer().Compare(_sourceSnapshot, _targetSnapshot, srcPath, tgtPath);

            _driftRows.Clear();
            _flatDrifts.Clear();
            FlattenDrifts(_report.Drifts, _driftRows, _flatDrifts);
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
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Collapsed;
            CompareButton.IsEnabled = true;
        }
    }

    private static void FlattenDrifts(List<ObjectDriftResult> drifts, List<DriftRow> rows, List<ObjectDriftResult> flat)
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
            flat.Add(d);
            if (d.Children != null)
                FlattenDrifts(d.Children, rows, flat);
        }
    }

    private void DriftsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriftsGrid.SelectedIndex < 0 || DriftsGrid.SelectedIndex >= _flatDrifts.Count)
            return;
        var drift = _flatDrifts[DriftsGrid.SelectedIndex];
        ShowDetails(drift);
    }

    private void ShowDetails(ObjectDriftResult drift)
    {
        DetailTitle.Text = $"{drift.ObjectType} {drift.FullName} — {drift.Description}";
        _srcDiffRows.Clear();
        _tgtDiffRows.Clear();

        var isTableLike = drift.ObjectType == DatabaseObjectType.Table || drift.ObjectType == DatabaseObjectType.Data || drift.ObjectType == DatabaseObjectType.Column;
        var isTextObject = drift.ObjectType == DatabaseObjectType.View || drift.ObjectType == DatabaseObjectType.StoredProcedure ||
                           drift.ObjectType == DatabaseObjectType.UserDefinedFunction || drift.ObjectType == DatabaseObjectType.Trigger;

        if (isTableLike)
        {
            TextDiffGrid.Visibility = Visibility.Collapsed;
            TableDetailPanel.Visibility = Visibility.Visible;

            if (_restoredSource == null || _restoredTarget == null)
            {
                DetailTitle.Text += " (No restored databases available for data preview.)";
                SourceDataGrid.ItemsSource = null;
                TargetDataGrid.ItemsSource = null;
                return;
            }

            try
            {
                var srcTable = GetTableData(_restoredSource.ConnectionString, drift.Schema, drift.Name);
                var tgtTable = GetTableData(_restoredTarget.ConnectionString, drift.Schema, drift.Name);

                SourceDataGrid.ItemsSource = srcTable.DefaultView;
                TargetDataGrid.ItemsSource = tgtTable.DefaultView;

                // Compute differing row indices
                var max = Math.Max(srcTable.Rows.Count, tgtTable.Rows.Count);
                for (int i = 0; i < max; i++)
                {
                    var s = i < srcTable.Rows.Count ? RowToString(srcTable.Rows[i]) : null;
                    var t = i < tgtTable.Rows.Count ? RowToString(tgtTable.Rows[i]) : null;
                    if (!string.Equals(s, t, StringComparison.Ordinal))
                    {
                        if (i < srcTable.Rows.Count) _srcDiffRows.Add(i);
                        if (i < tgtTable.Rows.Count) _tgtDiffRows.Add(i);
                    }
                }
                SourceDataGrid.Items.Refresh();
                TargetDataGrid.Items.Refresh();
            }
            catch (Exception ex)
            {
                DetailTitle.Text += $" (Data preview error: {ex.Message})";
            }
        }
        else if (isTextObject)
        {
            TableDetailPanel.Visibility = Visibility.Collapsed;
            TextDiffGrid.Visibility = Visibility.Visible;

            var srcLines = (drift.SourceDefinition ?? "").Split('\n');
            var tgtLines = (drift.TargetDefinition ?? "").Split('\n');
            var max = Math.Max(srcLines.Length, tgtLines.Length);
            var rows = new List<TextDiffRow>();
            for (int i = 0; i < max; i++)
            {
                var s = i < srcLines.Length ? srcLines[i] : "";
                var t = i < tgtLines.Length ? tgtLines[i] : "";
                string status;
                if (i >= srcLines.Length) status = "Added";
                else if (i >= tgtLines.Length) status = "Deleted";
                else if (s.Trim() == t.Trim()) status = "Same";
                else status = "Modified";
                rows.Add(new TextDiffRow { LineNumber = i + 1, SourceLine = s, TargetLine = t, Status = status });
            }
            TextDiffGrid.ItemsSource = rows;
        }
        else
        {
            TableDetailPanel.Visibility = Visibility.Collapsed;
            TextDiffGrid.Visibility = Visibility.Collapsed;
        }
    }

    private DataTable GetTableData(string connectionString, string schema, string table)
    {
        var dt = new DataTable();
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand($"SELECT * FROM [{schema}].[{table}]", conn);
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);
        return dt;
    }

    private static string RowToString(DataRow row)
    {
        return string.Join("|", row.ItemArray.Select(v => v == DBNull.Value ? "<NULL>" : v?.ToString()));
    }

    private void SourceDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        var idx = e.Row.GetIndex();
        e.Row.Background = _srcDiffRows.Contains(idx)
            ? new SolidColorBrush(Color.FromRgb(254, 249, 195))
            : Brushes.White;
    }

    private void TargetDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        var idx = e.Row.GetIndex();
        e.Row.Background = _tgtDiffRows.Contains(idx)
            ? new SolidColorBrush(Color.FromRgb(254, 249, 195))
            : Brushes.White;
    }

    private void TextDiffGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is TextDiffRow row)
        {
            e.Row.Background = row.Status switch
            {
                "Modified" => new SolidColorBrush(Color.FromRgb(254, 249, 195)),
                "Added" => new SolidColorBrush(Color.FromRgb(220, 252, 231)),
                "Deleted" => new SolidColorBrush(Color.FromRgb(254, 226, 226)),
                _ => Brushes.White
            };
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
            System.IO.File.WriteAllText(dlg.FileName, sql);
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

public class TextDiffRow
{
    public int LineNumber { get; set; }
    public string SourceLine { get; set; } = string.Empty;
    public string TargetLine { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}