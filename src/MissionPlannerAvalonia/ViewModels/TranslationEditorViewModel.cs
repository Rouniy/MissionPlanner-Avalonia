using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public sealed partial class TranslationEditorRow : ObservableObject {
  private string _acceptedTranslation;

  internal TranslationEditorRow(ResxTranslationEntry entry) {
    RelativePath = entry.RelativePath;
    Key = entry.Key;
    SourceText = entry.SourceText;
    _translation = entry.Translation;
    _acceptedTranslation = entry.Translation;
    Comment = entry.Comment ?? "";
    HasExistingTranslation = entry.HasExistingTranslation;
  }

  public string RelativePath { get; }
  public string Key { get; }
  public string SourceText { get; }
  public string Comment { get; }
  public bool HasExistingTranslation { get; }

  [ObservableProperty]
  private string _translation;

  public bool IsMissing => !HasExistingTranslation
      && string.Equals(Translation, SourceText, StringComparison.Ordinal);

  public bool WillExport => !string.Equals(Translation, SourceText, StringComparison.Ordinal);

  public bool IsModified => !string.Equals(Translation, _acceptedTranslation, StringComparison.Ordinal);

  partial void OnTranslationChanged(string value) {
    OnPropertyChanged(nameof(IsMissing));
    OnPropertyChanged(nameof(WillExport));
    OnPropertyChanged(nameof(IsModified));
  }

  internal ResxTranslationEntry Snapshot() => new(
      RelativePath,
      Key,
      SourceText,
      Translation,
      string.IsNullOrEmpty(Comment) ? null : Comment,
      HasExistingTranslation);

  internal void AcceptChanges() {
    _acceptedTranslation = Translation;
    OnPropertyChanged(nameof(IsModified));
  }

  internal void Revert() => Translation = _acceptedTranslation;
}

public sealed partial class TranslationEditorViewModel : ViewModelBase, IDisposable {
  private readonly List<TranslationEditorRow> _allRows = [];
  private CancellationTokenSource? _operationCancellation;
  private Task? _activeOperation;
  private bool _disposed;
  private string _loadedCulture = "";

  public TranslationEditorViewModel() {
    SourceRoot = FindDefaultSourceRoot() ?? "";
    if (SourceRoot.Length != 0) {
      OutputRoot = Path.Combine(SourceRoot, "translation");
    }
    string currentCulture = CultureInfo.CurrentUICulture.Name;
    SelectedCulture = Cultures.FirstOrDefault(item =>
        string.Equals(item.Name, currentCulture, StringComparison.OrdinalIgnoreCase))
        ?? Cultures.FirstOrDefault(item => item.Name == "ru-RU")
        ?? Cultures.FirstOrDefault();
  }

  public IReadOnlyList<TranslationCulture> Cultures => ResxTranslationService.Cultures;
  public ObservableCollection<TranslationEditorRow> Rows { get; } = [];
  internal IReadOnlyList<TranslationEditorRow> AllRows => _allRows;

  [ObservableProperty]
  private string _sourceRoot = "";

  [ObservableProperty]
  private string _outputRoot = "";

  [ObservableProperty]
  private TranslationCulture? _selectedCulture;

  [ObservableProperty]
  private string _searchText = "";

  [ObservableProperty]
  private bool _missingOnly;

  [ObservableProperty]
  private bool _exportOnly;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private int _totalCount;

  [ObservableProperty]
  private int _visibleCount;

  [ObservableProperty]
  private int _missingCount;

  [ObservableProperty]
  private int _translatedCount;

  [ObservableProperty]
  private int _resourceFileCount;

  [ObservableProperty]
  private bool _hasUnsavedChanges;

  [ObservableProperty]
  private string _status =
      "Choose a Mission Planner source directory and target culture, then load neutral and existing localized RESX strings.";

  partial void OnSearchTextChanged(string value) => ApplyFilter();
  partial void OnMissingOnlyChanged(bool value) => ApplyFilter();
  partial void OnExportOnlyChanged(bool value) => ApplyFilter();

  partial void OnSelectedCultureChanged(TranslationCulture? value) {
    if (_allRows.Count == 0 || value == null
        || string.Equals(value.Name, _loadedCulture, StringComparison.OrdinalIgnoreCase)) {
      return;
    }
    Status = HasUnsavedChanges
        ? $"Unsaved {_loadedCulture} edits remain loaded. Save or revert them before loading {value.Name}."
        : $"The grid still contains {_loadedCulture}. Click Load to replace it with {value.Name}.";
  }

  [RelayCommand]
  private async Task BrowseSource() {
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    IReadOnlyList<IStorageFolder> folders = await Dialogs.Owner.StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions {
          Title = "Select Mission Planner source directory containing RESX files",
          AllowMultiple = false,
        });
    string? path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }
    SourceRoot = Path.GetFullPath(path);
    OutputRoot = Path.Combine(SourceRoot, "translation");
    Status = "Source selected. Choose the target culture and click Load.";
  }

  [RelayCommand]
  private async Task BrowseOutput() {
    if (IsBusy || Dialogs.Owner == null) {
      return;
    }
    IReadOnlyList<IStorageFolder> folders = await Dialogs.Owner.StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions {
          Title = "Select translation export directory",
          AllowMultiple = false,
        });
    string? path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      OutputRoot = Path.GetFullPath(path);
    }
  }

  [RelayCommand]
  private async Task Load() {
    if (IsBusy) {
      return;
    }
    if (SelectedCulture == null) {
      Status = "Select a target culture before loading resources.";
      return;
    }
    if (HasUnsavedChanges && !await Dialogs.Confirm(
            "Discard unsaved translations?",
            $"Loading {SelectedCulture.Name} will discard unsaved edits for {_loadedCulture}. Continue?")) {
      return;
    }
    await LoadDirectoryAsync(SourceRoot, SelectedCulture.Name);
  }

  internal async Task LoadDirectoryAsync(string sourceRoot, string culture) {
    ThrowIfDisposed();
    if (IsBusy) {
      return;
    }
    var cancellation = new CancellationTokenSource();
    _operationCancellation = cancellation;
    IsBusy = true;
    Status = "Scanning neutral and localized RESX resources…";
    Task<ResxTranslationProject> operation = Task.Run(
        () => ResxTranslationService.Load(sourceRoot, culture, cancellation.Token),
        cancellation.Token);
    _activeOperation = operation;
    try {
      ResxTranslationProject project = await operation;
      ReplaceRows(project);
      SourceRoot = project.SourceRoot;
      if (string.IsNullOrWhiteSpace(OutputRoot)
          || !Path.IsPathFullyQualified(OutputRoot)) {
        OutputRoot = Path.Combine(SourceRoot, "translation");
      }
      _loadedCulture = project.Culture;
      SelectedCulture = Cultures.FirstOrDefault(item =>
          string.Equals(item.Name, project.Culture, StringComparison.OrdinalIgnoreCase));
      string warning = project.Warnings.Count == 0
          ? ""
          : $" {project.Warnings.Count} unreadable/duplicate resource warning(s); first: {project.Warnings[0]}";
      Status = $"Loaded {project.Entries.Count:N0} strings from {project.ResourceFiles:N0} resource files "
          + $"for {project.Culture}.{warning}";
    } catch (OperationCanceledException) {
      Status = "Resource scan cancelled; the previous grid was retained.";
    } catch (Exception ex) {
      Status = "Resource scan failed: " + ex.Message;
    } finally {
      if (ReferenceEquals(_operationCancellation, cancellation)) {
        _operationCancellation = null;
        _activeOperation = null;
      }
      cancellation.Dispose();
      IsBusy = false;
    }
  }

  [RelayCommand]
  private async Task Save() {
    ThrowIfDisposed();
    if (IsBusy || _allRows.Count == 0) {
      return;
    }
    if (string.IsNullOrWhiteSpace(_loadedCulture)) {
      Status = "Load a target culture before exporting translations.";
      return;
    }
    if (string.IsNullOrWhiteSpace(OutputRoot)) {
      Status = "Choose an output directory before exporting translations.";
      return;
    }

    ResxTranslationEntry[] snapshot = _allRows.Select(row => row.Snapshot()).ToArray();
    var cancellation = new CancellationTokenSource();
    _operationCancellation = cancellation;
    IsBusy = true;
    Status = "Writing compatible localized RESX files and resume HTML…";
    Task<ResxTranslationExportResult> operation = Task.Run(
        () => ResxTranslationService.Export(OutputRoot, _loadedCulture, snapshot, cancellation.Token),
        cancellation.Token);
    _activeOperation = operation;
    try {
      ResxTranslationExportResult result = await operation;
      foreach (TranslationEditorRow row in _allRows) {
        row.AcceptChanges();
      }
      UpdateCounts();
      Status = $"Exported {result.TranslatedEntries:N0} translated strings to "
          + $"{result.ResourceFiles:N0} {_loadedCulture} RESX files. Resume table: {result.ResumeHtmlPath}"
          + (result.BackupDirectory == null
              ? ""
              : $" {result.OverwrittenFiles:N0} previous file(s) backed up to {result.BackupDirectory}.");
    } catch (OperationCanceledException) {
      Status = "Translation export cancelled. Files completed before cancellation remain valid.";
    } catch (Exception ex) {
      Status = "Translation export failed: " + ex.Message;
    } finally {
      if (ReferenceEquals(_operationCancellation, cancellation)) {
        _operationCancellation = null;
        _activeOperation = null;
      }
      cancellation.Dispose();
      IsBusy = false;
    }
  }

  [RelayCommand]
  private async Task ImportResume() {
    if (IsBusy || _allRows.Count == 0 || Dialogs.Owner == null) {
      return;
    }
    IReadOnlyList<IStorageFile> files = await Dialogs.Owner.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "Import Mission Planner translation output.html",
          AllowMultiple = false,
          FileTypeFilter = [
            new FilePickerFileType("Translation HTML") { Patterns = ["*.html", "*.htm"], },
            new FilePickerFileType("All files") { Patterns = ["*"], },
          ],
        });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      ImportResume(path);
    }
  }

  internal int ImportResume(string path) {
    IReadOnlyDictionary<TranslationIdentity, string> imported =
        ResxTranslationService.ImportResumeHtml(path);
    int modified = 0;
    var assigned = new HashSet<TranslationEditorRow>();
    foreach ((TranslationIdentity identity, string value) in imported) {
      TranslationEditorRow[] matches = _allRows
          .Where(row => string.Equals(row.Key, identity.Key, StringComparison.Ordinal)
              && ResxTranslationService.ResumeFileMatches(identity.RelativePath, row.RelativePath))
          .Take(2)
          .ToArray();
      if (matches.Length != 1 || !assigned.Add(matches[0])) {
        continue;
      }
      if (!string.Equals(matches[0].Translation, value, StringComparison.Ordinal)) {
        matches[0].Translation = value;
        modified++;
      }
    }
    ApplyFilter();
    Status = $"Imported {modified:N0} changed entries from {Path.GetFullPath(path)}.";
    return modified;
  }

  [RelayCommand]
  private async Task CopyCsv() {
    if (_allRows.Count == 0) {
      return;
    }
    var clipboard = Dialogs.Owner?.Clipboard;
    if (clipboard == null) {
      Status = "The system clipboard is unavailable.";
      return;
    }
    await clipboard.SetTextAsync(ResxTranslationService.BuildCsv(
        _allRows.Select(row => row.Snapshot())));
    Status = $"Copied {_allRows.Count:N0} RFC 4180 CSV rows to the clipboard.";
  }

  [RelayCommand]
  private async Task RevertAll() {
    if (!HasUnsavedChanges || !await Dialogs.Confirm(
            "Revert translation edits?",
            "Restore every edited value to the last loaded or exported state?")) {
      return;
    }
    foreach (TranslationEditorRow row in _allRows) {
      row.Revert();
    }
    ApplyFilter();
    Status = "Unsaved translation edits reverted.";
  }

  [RelayCommand]
  private void Cancel() {
    if (_operationCancellation == null) {
      return;
    }
    Status = "Cancellation requested…";
    _operationCancellation.Cancel();
  }

  internal async Task CancelAndWaitAsync() {
    _operationCancellation?.Cancel();
    Task? operation = _activeOperation;
    if (operation == null) {
      return;
    }
    try {
      await operation;
    } catch {
      // The public operation converts cancellation/failure to user-visible status.
    }
  }

  private void ReplaceRows(ResxTranslationProject project) {
    foreach (TranslationEditorRow row in _allRows) {
      row.PropertyChanged -= OnRowPropertyChanged;
    }
    _allRows.Clear();
    foreach (ResxTranslationEntry entry in project.Entries) {
      var row = new TranslationEditorRow(entry);
      row.PropertyChanged += OnRowPropertyChanged;
      _allRows.Add(row);
    }
    ResourceFileCount = project.ResourceFiles;
    ApplyFilter();
  }

  private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName is nameof(TranslationEditorRow.Translation)
        or nameof(TranslationEditorRow.IsMissing)
        or nameof(TranslationEditorRow.WillExport)
        or nameof(TranslationEditorRow.IsModified)) {
      UpdateCounts();
    }
  }

  private void ApplyFilter() {
    IEnumerable<TranslationEditorRow> filtered = _allRows;
    string search = SearchText.Trim();
    if (search.Length != 0) {
      filtered = filtered.Where(row =>
          row.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)
          || row.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
          || row.SourceText.Contains(search, StringComparison.OrdinalIgnoreCase)
          || row.Translation.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
    if (MissingOnly) {
      filtered = filtered.Where(row => row.IsMissing);
    }
    if (ExportOnly) {
      filtered = filtered.Where(row => row.WillExport);
    }
    Rows.Clear();
    foreach (TranslationEditorRow row in filtered) {
      Rows.Add(row);
    }
    UpdateCounts();
  }

  private void UpdateCounts() {
    TotalCount = _allRows.Count;
    VisibleCount = Rows.Count;
    MissingCount = _allRows.Count(row => row.IsMissing);
    TranslatedCount = _allRows.Count(row => row.WillExport);
    HasUnsavedChanges = _allRows.Any(row => row.IsModified);
  }

  internal static string? FindDefaultSourceRoot() {
    foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }) {
      var directory = new DirectoryInfo(Path.GetFullPath(start));
      while (directory != null) {
        string direct = Path.Combine(directory.FullName, "external", "MissionPlanner");
        if (File.Exists(Path.Combine(direct, "MissionPlanner.csproj"))) {
          return direct;
        }
        if (File.Exists(Path.Combine(directory.FullName, "MissionPlanner.csproj"))
            && File.Exists(Path.Combine(directory.FullName, "ResEdit.cs"))) {
          return directory.FullName;
        }
        directory = directory.Parent;
      }
    }
    return null;
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _operationCancellation?.Cancel();
    foreach (TranslationEditorRow row in _allRows) {
      row.PropertyChanged -= OnRowPropertyChanged;
    }
  }
}
