using System;
using System.Collections.ObjectModel;

namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Display model for a single agent card in the pipeline timeline UI.
    /// Bound to agent timeline rows showing status, elapsed time, and short log lines.
    /// </summary>
    public class AgentCardViewModel : ViewModelBase
    {
        private string _agentName = string.Empty;
        private string _agentIcon = "⬡";
        private AgentCardStatus _status = AgentCardStatus.Pending;
        private int _elapsedSeconds;
        private string _currentLog = string.Empty;
        private bool _isActive;
        private DateTime? _startedAt;

        public string AgentName
        {
            get => _agentName;
            set => SetProperty(ref _agentName, value);
        }

        public string AgentIcon
        {
            get => _agentIcon;
            set => SetProperty(ref _agentIcon, value);
        }

        public AgentCardStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(IsPending));
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(IsFailed));
                    OnPropertyChanged(nameof(StatusLabel));
                    IsActive = value == AgentCardStatus.Running;
                }
            }
        }

        public int ElapsedSeconds
        {
            get => _elapsedSeconds;
            set => SetProperty(ref _elapsedSeconds, value);
        }

        /// <summary>Latest status line for this agent (e.g. "Generating draft...", "3 corrections").</summary>
        public string CurrentLog
        {
            get => _currentLog;
            set => SetProperty(ref _currentLog, value);
        }

        /// <summary>True while this agent is actively running — drives spinner animation.</summary>
        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

        public bool IsPending => Status == AgentCardStatus.Pending;
        public bool IsRunning => Status == AgentCardStatus.Running;
        public bool IsCompleted => Status == AgentCardStatus.Completed;
        public bool IsFailed => Status == AgentCardStatus.Failed;

        public string StatusLabel => Status switch
        {
            AgentCardStatus.Running => "En cours...",
            AgentCardStatus.Completed => "Terminé",
            AgentCardStatus.Failed => "Échec",
            _ => "En attente"
        };

        /// <summary>Short log lines shown inside the card (max 10).</summary>
        public ObservableCollection<string> LogLines { get; } = new();

        public void MarkStarted()
        {
            _startedAt = DateTime.Now;
            Status = AgentCardStatus.Running;
            ElapsedSeconds = 0;
        }

        public void MarkCompleted(string? summary = null)
        {
            Status = AgentCardStatus.Completed;
            if (!string.IsNullOrWhiteSpace(summary))
                AddLog(summary);
        }

        public void MarkFailed(string? error = null)
        {
            Status = AgentCardStatus.Failed;
            if (!string.IsNullOrWhiteSpace(error))
                AddLog($"Erreur: {error}");
        }

        public void AddLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            CurrentLog = line.Length > 100 ? line[..97] + "..." : line;
            if (LogLines.Count >= 10)
                LogLines.RemoveAt(0);
            LogLines.Add(CurrentLog);
        }

        public void UpdateElapsed()
        {
            if (_startedAt.HasValue && Status == AgentCardStatus.Running)
                ElapsedSeconds = (int)(DateTime.Now - _startedAt.Value).TotalSeconds;
        }
    }

    public enum AgentCardStatus
    {
        Pending,
        Running,
        Completed,
        Failed
    }
}
