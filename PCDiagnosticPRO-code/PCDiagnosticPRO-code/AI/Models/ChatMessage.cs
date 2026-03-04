using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace PCDiagnosticPro.AI.Models
{
    public enum ChatRole { User, Assistant, System }

    public class ChatMessage : INotifyPropertyChanged
    {
        private static readonly Regex WordRegex = new(@"\S+", RegexOptions.Compiled);

        public ChatRole Role { get; init; }

        private string _content = string.Empty;
        public string Content
        {
            get => _content;
            set
            {
                if (string.Equals(_content, value, StringComparison.Ordinal))
                {
                    return;
                }

                _content = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WordCount));
                OnPropertyChanged(nameof(IsLongResponse));
                OnPropertyChanged(nameof(DisplayContent));
                OnPropertyChanged(nameof(HasHiddenContent));
                OnPropertyChanged(nameof(ExpandActionLabel));
            }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayContent));
                OnPropertyChanged(nameof(HasHiddenContent));
                OnPropertyChanged(nameof(ExpandActionLabel));
            }
        }

        public DateTime Timestamp { get; init; } = DateTime.Now;

        public string RoleDisplay => Role switch
        {
            ChatRole.User => "USER",
            ChatRole.Assistant => "ASSISTANT",
            _ => "SYSTEM"
        };

        public bool IsUser => Role == ChatRole.User;
        public bool IsAssistant => Role == ChatRole.Assistant;
        public bool IsSystemMessage => Role == ChatRole.System;

        public int WordCount => CountWords(_content);
        public bool IsLongResponse => IsAssistant && WordCount > 500;
        public bool HasHiddenContent => IsLongResponse && !IsExpanded;
        public string ExpandActionLabel => IsExpanded ? "Reduire" : "Lire la suite";

        public string DisplayContent
        {
            get
            {
                if (!HasHiddenContent)
                {
                    return _content;
                }

                var words = WordRegex.Matches(_content).Select(m => m.Value).Take(500).ToArray();
                if (words.Length == 0)
                {
                    return _content;
                }

                var sb = new StringBuilder();
                sb.Append(string.Join(" ", words));
                sb.Append(" [...]");
                return sb.ToString();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static int CountWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return WordRegex.Matches(text).Count;
        }
    }
}

