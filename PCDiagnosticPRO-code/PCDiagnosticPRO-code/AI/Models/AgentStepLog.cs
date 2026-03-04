using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PCDiagnosticPro.AI.Models
{
    public enum AgentStepStatus { Pending, Running, Completed, Failed }

    public class AgentStepLog : INotifyPropertyChanged
    {
        public string AgentName { get; set; } = string.Empty;

        private AgentStepStatus _status = AgentStepStatus.Pending;
        public AgentStepStatus Status
        {
            get => _status;
            set
            {
                if (_status == value)
                {
                    return;
                }

                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }

        public DateTime? StartedAt { get; set; }

        private DateTime? _completedAt;
        public DateTime? CompletedAt
        {
            get => _completedAt;
            set
            {
                if (_completedAt == value)
                {
                    return;
                }

                _completedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationDisplay));
            }
        }

        private string? _error;
        public string? Error
        {
            get => _error;
            set
            {
                if (string.Equals(_error, value, StringComparison.Ordinal))
                {
                    return;
                }

                _error = value;
                OnPropertyChanged();
            }
        }

        public string StatusDisplay => Status switch
        {
            AgentStepStatus.Pending => "En attente",
            AgentStepStatus.Running => "En cours",
            AgentStepStatus.Completed => "Termine",
            AgentStepStatus.Failed => "Echec",
            _ => "?"
        };

        public string? DurationDisplay
        {
            get
            {
                if (StartedAt == null || CompletedAt == null)
                {
                    return null;
                }

                return $"{(CompletedAt.Value - StartedAt.Value).TotalSeconds:F1}s";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
