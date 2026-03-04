using System;
using System.ComponentModel;
using System.Windows;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    public partial class UpdateApplyWindow : Window
    {
        private UpdateApplyViewModel? _viewModel;

        public UpdateApplyWindow()
        {
            InitializeComponent();
            Loaded += UpdateApplyWindow_Loaded;
            Closing += UpdateApplyWindow_Closing;
            DataContextChanged += UpdateApplyWindow_DataContextChanged;
        }

        private void UpdateApplyWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.CloseRequested -= ViewModel_CloseRequested;

            _viewModel = e.NewValue as UpdateApplyViewModel;
            if (_viewModel != null)
                _viewModel.CloseRequested += ViewModel_CloseRequested;
        }

        private void UpdateApplyWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is not UpdateApplyViewModel vm)
                return;

            Title = vm.WindowTitle;
            if (!vm.HasStarted && vm.StartCommand.CanExecute(null))
                vm.StartCommand.Execute(null);
        }

        private void UpdateApplyWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_viewModel?.IsRunning == true)
                e.Cancel = true;
        }

        private void ViewModel_CloseRequested()
        {
            Dispatcher.Invoke(Close);
        }
    }
}
