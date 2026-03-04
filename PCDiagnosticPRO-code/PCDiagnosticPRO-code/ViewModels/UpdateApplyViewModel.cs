using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PCDiagnosticPro.ViewModels
{
    public enum UpdateItemKind
    {
        App,
        Driver
    }

    public enum UpdateItemState
    {
        Queued,
        Running,
        Success,
        Failed,
        Skipped
    }

    public sealed class UpdateItemViewModel : INotifyPropertyChanged
    {
        private string _displayName;
        private UpdateItemKind _kind;
        private UpdateItemState _state;
        private int _percent;
        private string _message;
        private DateTime? _startedAt;
        private DateTime? _endedAt;

        public UpdateItemViewModel(string id, string displayName, UpdateItemKind kind, string? message = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            _displayName = displayName ?? string.Empty;
            _kind = kind;
            _state = UpdateItemState.Queued;
            _percent = 0;
            _message = message ?? "En attente";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (string.Equals(_displayName, value, StringComparison.Ordinal))
                    return;

                _displayName = value ?? string.Empty;
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public UpdateItemKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value)
                    return;

                _kind = value;
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(KindDisplay));
            }
        }

        public string KindDisplay => Kind == UpdateItemKind.App ? "App" : "Pilote";

        public UpdateItemState State
        {
            get => _state;
            set
            {
                if (_state == value)
                    return;

                _state = value;
                if (_state == UpdateItemState.Running && !_startedAt.HasValue)
                    _startedAt = DateTime.Now;

                if (IsTerminalState(_state))
                    _endedAt = DateTime.Now;

                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StateDisplay));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(IsTerminal));
                OnPropertyChanged(nameof(StartedAt));
                OnPropertyChanged(nameof(EndedAt));
            }
        }

        public string StateDisplay => State switch
        {
            UpdateItemState.Queued => "En attente",
            UpdateItemState.Running => "En cours",
            UpdateItemState.Success => "Reussie",
            UpdateItemState.Failed => "Echouee",
            UpdateItemState.Skipped => "Ignoree",
            _ => "En attente"
        };

        public Brush StateBrush => State switch
        {
            UpdateItemState.Success => new SolidColorBrush(Color.FromRgb(46, 213, 115)),
            UpdateItemState.Failed => new SolidColorBrush(Color.FromRgb(255, 71, 87)),
            UpdateItemState.Skipped => new SolidColorBrush(Color.FromRgb(255, 165, 2)),
            UpdateItemState.Running => new SolidColorBrush(Color.FromRgb(88, 166, 255)),
            _ => new SolidColorBrush(Color.FromRgb(139, 148, 158))
        };

        public int Percent
        {
            get => _percent;
            set
            {
                var bounded = Math.Max(0, Math.Min(100, value));
                if (_percent == bounded)
                    return;

                _percent = bounded;
                OnPropertyChanged(nameof(Percent));
            }
        }

        public string Message
        {
            get => _message;
            set
            {
                if (string.Equals(_message, value, StringComparison.Ordinal))
                    return;

                _message = value ?? string.Empty;
                OnPropertyChanged(nameof(Message));
            }
        }

        public DateTime? StartedAt
        {
            get => _startedAt;
            set
            {
                if (_startedAt == value)
                    return;

                _startedAt = value;
                OnPropertyChanged(nameof(StartedAt));
            }
        }

        public DateTime? EndedAt
        {
            get => _endedAt;
            set
            {
                if (_endedAt == value)
                    return;

                _endedAt = value;
                OnPropertyChanged(nameof(EndedAt));
            }
        }

        public bool IsTerminal => IsTerminalState(State);

        private static bool IsTerminalState(UpdateItemState state)
        {
            return state == UpdateItemState.Success ||
                   state == UpdateItemState.Failed ||
                   state == UpdateItemState.Skipped;
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class UpdateApplyViewModel : INotifyPropertyChanged
    {
        private readonly Func<UpdateApplyViewModel, CancellationToken, Task> _startDelegate;
        private CancellationTokenSource? _runCts;
        private bool _isRunning;
        private bool _hasStarted;
        private bool _isLogExpanded;
        private double _globalPercent;
        private string _headerStatus = "En attente...";

        public UpdateApplyViewModel(
            IEnumerable<UpdateItemViewModel> items,
            Func<UpdateApplyViewModel, CancellationToken, Task> startDelegate,
            string? title = null)
        {
            _startDelegate = startDelegate ?? throw new ArgumentNullException(nameof(startDelegate));
            WindowTitle = string.IsNullOrWhiteSpace(title) ? "Application des mises a jour" : title;

            foreach (var item in items ?? Enumerable.Empty<UpdateItemViewModel>())
            {
                item.PropertyChanged += Item_PropertyChanged;
                Items.Add(item);
            }

            StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning && !HasStarted);
            CancelCommand = new RelayCommand(Cancel, () => CanCancel);
            CloseCommand = new RelayCommand(RequestClose, () => CanClose);

            RecalculateSummary();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? CloseRequested;

        public ObservableCollection<UpdateItemViewModel> Items { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();

        public string WindowTitle { get; }
        public ICommand StartCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand CloseCommand { get; }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                    return;

                _isRunning = value;
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanClose));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasStarted
        {
            get => _hasStarted;
            private set
            {
                if (_hasStarted == value)
                    return;

                _hasStarted = value;
                OnPropertyChanged(nameof(HasStarted));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsLogExpanded
        {
            get => _isLogExpanded;
            set
            {
                if (_isLogExpanded == value)
                    return;

                _isLogExpanded = value;
                OnPropertyChanged(nameof(IsLogExpanded));
            }
        }

        public string HeaderStatus
        {
            get => _headerStatus;
            private set
            {
                if (string.Equals(_headerStatus, value, StringComparison.Ordinal))
                    return;

                _headerStatus = value;
                OnPropertyChanged(nameof(HeaderStatus));
            }
        }

        public double GlobalPercent
        {
            get => _globalPercent;
            private set
            {
                if (Math.Abs(_globalPercent - value) < 0.001)
                    return;

                _globalPercent = value;
                OnPropertyChanged(nameof(GlobalPercent));
            }
        }

        public int TotalCount => Items.Count;
        public int QueuedCount => Items.Count(i => i.State == UpdateItemState.Queued);
        public int RunningCount => Items.Count(i => i.State == UpdateItemState.Running);
        public int SuccessCount => Items.Count(i => i.State == UpdateItemState.Success);
        public int FailedCount => Items.Count(i => i.State == UpdateItemState.Failed);
        public int SkippedCount => Items.Count(i => i.State == UpdateItemState.Skipped);
        public int CompletedCount => SuccessCount + FailedCount + SkippedCount;
        public bool CanCancel => IsRunning && _runCts != null && !_runCts.IsCancellationRequested;
        public bool CanClose => !IsRunning;

        public async Task StartAsync()
        {
            if (IsRunning || HasStarted)
                return;

            HasStarted = true;
            IsRunning = true;
            _runCts = new CancellationTokenSource();
            SetHeaderStatus("Execution en cours...");

            try
            {
                await _startDelegate(this, _runCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Operation annulee.");
                MarkUnfinishedAs(UpdateItemState.Skipped, "Annulee par l'utilisateur.");
                SetHeaderStatus("Operation annulee.");
            }
            catch (Exception ex)
            {
                AppendLog($"Erreur: {ex.Message}");
                MarkUnfinishedAs(UpdateItemState.Failed, "Erreur inattendue pendant l'application.");
                SetHeaderStatus("Termine avec erreurs.");
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                IsRunning = false;
                if (RunningCount == 0)
                    SetHeaderStatus("Execution terminee.");
                RecalculateSummary();
            }
        }

        public void Cancel()
        {
            if (!CanCancel)
                return;

            _runCts?.Cancel();
            SetHeaderStatus("Annulation en cours...");
            AppendLog("Demande d'annulation envoyee.");
        }

        public void SetHeaderStatus(string text)
        {
            ExecuteOnUi(() => HeaderStatus = string.IsNullOrWhiteSpace(text) ? "En attente..." : text);
        }

        public void AppendLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            ExecuteOnUi(() =>
            {
                var stamped = $"[{DateTime.Now:HH:mm:ss}] {text.Trim()}";
                LogLines.Add(stamped);
                while (LogLines.Count > 500)
                    LogLines.RemoveAt(0);
            });
        }

        public UpdateItemViewModel EnsureItem(string id, string displayName, UpdateItemKind kind)
        {
            var normalizedId = NormalizeId(id);
            var existing = Items.FirstOrDefault(i => string.Equals(NormalizeId(i.Id), normalizedId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            var created = new UpdateItemViewModel(normalizedId, displayName, kind);
            ExecuteOnUi(() =>
            {
                created.PropertyChanged += Item_PropertyChanged;
                Items.Add(created);
                RecalculateSummary();
            });
            return created;
        }

        public UpdateItemViewModel? FindById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var normalized = NormalizeId(id);
            return Items.FirstOrDefault(i => string.Equals(NormalizeId(i.Id), normalized, StringComparison.OrdinalIgnoreCase));
        }

        public UpdateItemViewModel? FindByDisplayName(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return null;

            var normalized = displayName.Trim();
            return Items.FirstOrDefault(i => string.Equals(i.DisplayName, normalized, StringComparison.OrdinalIgnoreCase)) ??
                   Items.FirstOrDefault(i => i.DisplayName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             normalized.IndexOf(i.DisplayName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public void MarkItemQueued(string id, string? displayName = null, string? message = null, UpdateItemKind kind = UpdateItemKind.App)
        {
            SetItemStateInternal(id, displayName, kind, UpdateItemState.Queued, 0, message ?? "En attente");
        }

        public void MarkItemRunning(string id, string? displayName = null, string? message = null, int percent = 50, UpdateItemKind kind = UpdateItemKind.App)
        {
            SetItemStateInternal(id, displayName, kind, UpdateItemState.Running, percent, message ?? "En cours");
        }

        public void MarkItemSuccess(string id, string? displayName = null, string? message = null, UpdateItemKind kind = UpdateItemKind.App)
        {
            SetItemStateInternal(id, displayName, kind, UpdateItemState.Success, 100, message ?? "Mise a jour appliquee");
        }

        public void MarkItemFailed(string id, string? displayName = null, string? message = null, UpdateItemKind kind = UpdateItemKind.App)
        {
            SetItemStateInternal(id, displayName, kind, UpdateItemState.Failed, 100, message ?? "Echec");
        }

        public void MarkItemSkipped(string id, string? displayName = null, string? message = null, UpdateItemKind kind = UpdateItemKind.App)
        {
            SetItemStateInternal(id, displayName, kind, UpdateItemState.Skipped, 100, message ?? "Ignoree");
        }

        public void UpdateItemProgress(string id, int percent, string? message = null, string? displayName = null, UpdateItemKind kind = UpdateItemKind.App)
        {
            ExecuteOnUi(() =>
            {
                var item = EnsureItem(id, displayName ?? id, kind);
                if (item.State == UpdateItemState.Queued)
                    item.State = UpdateItemState.Running;
                item.Percent = percent;
                if (!string.IsNullOrWhiteSpace(message))
                    item.Message = message!;
                RecalculateSummary();
            });
        }

        public void MarkUnfinishedAs(UpdateItemState state, string message)
        {
            ExecuteOnUi(() =>
            {
                foreach (var item in Items.Where(i => !i.IsTerminal))
                {
                    item.State = state;
                    item.Percent = 100;
                    item.Message = message;
                }
                RecalculateSummary();
            });
        }

        private void SetItemStateInternal(
            string id,
            string? displayName,
            UpdateItemKind kind,
            UpdateItemState state,
            int percent,
            string message)
        {
            ExecuteOnUi(() =>
            {
                var name = string.IsNullOrWhiteSpace(displayName) ? id : displayName!;
                var item = EnsureItem(id, name, kind);
                if (!string.IsNullOrWhiteSpace(displayName))
                    item.DisplayName = displayName!;
                item.Kind = kind;
                item.State = state;
                item.Percent = percent;
                item.Message = message;
                RecalculateSummary();
            });
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UpdateItemViewModel.State) ||
                e.PropertyName == nameof(UpdateItemViewModel.Percent))
            {
                RecalculateSummary();
            }
        }

        private void RecalculateSummary()
        {
            ExecuteOnUi(() =>
            {
                var total = Math.Max(1, TotalCount);
                GlobalPercent = Math.Round((double)CompletedCount / total * 100.0, 1);

                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(QueuedCount));
                OnPropertyChanged(nameof(RunningCount));
                OnPropertyChanged(nameof(SuccessCount));
                OnPropertyChanged(nameof(FailedCount));
                OnPropertyChanged(nameof(SkippedCount));
                OnPropertyChanged(nameof(CompletedCount));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanClose));
            });
        }

        private void RequestClose()
        {
            if (!CanClose)
                return;

            CloseRequested?.Invoke();
        }

        private static string NormalizeId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Guid.NewGuid().ToString("N");

            return raw.Trim();
        }

        private static void ExecuteOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
