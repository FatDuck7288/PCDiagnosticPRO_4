using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows.Threading;
using PCDiagnosticPro.AI.Models;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    public partial class ChatSupportView : UserControl
    {
        private ChatSupportViewModel? _viewModel;
        private readonly HashSet<ChatMessage> _trackedMessages = new();

        public ChatSupportView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (DataContext is ViewModels.ChatSupportViewModel vm &&
                    vm.SendMessageCommand.CanExecute(null))
                {
                    vm.SendMessageCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void MessagesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                DetachViewModel(_viewModel);
            }

            _viewModel = DataContext as ChatSupportViewModel;
            if (_viewModel != null)
            {
                AttachViewModel(_viewModel);
                ScheduleScrollToBottom();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                DetachViewModel(_viewModel);
                _viewModel = null;
            }
        }

        private void AttachViewModel(ChatSupportViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesCollectionChanged;
            foreach (var message in vm.Messages)
            {
                TrackMessage(message);
            }
        }

        private void DetachViewModel(ChatSupportViewModel vm)
        {
            vm.Messages.CollectionChanged -= OnMessagesCollectionChanged;
            foreach (var message in _trackedMessages)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }

            _trackedMessages.Clear();
        }

        private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is ChatMessage msg)
                    {
                        TrackMessage(msg);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is ChatMessage msg && _trackedMessages.Remove(msg))
                    {
                        msg.PropertyChanged -= OnMessagePropertyChanged;
                    }
                }
            }

            ScheduleScrollToBottom();
        }

        private void TrackMessage(ChatMessage message)
        {
            if (_trackedMessages.Add(message))
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }

        private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatMessage.Content))
            {
                ScheduleScrollToBottom();
            }
        }

        private void ScheduleScrollToBottom()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                MessagesScroll?.ScrollToEnd();
            }));
        }
    }
}
