using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace PCDiagnosticPro.Controls
{
    public partial class ThemedSpinner : UserControl
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive),
                typeof(bool),
                typeof(ThemedSpinner),
                new PropertyMetadata(false, OnIsActiveChanged));

        private readonly DoubleAnimation _spinAnimation = new()
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.95),
            RepeatBehavior = RepeatBehavior.Forever
        };

        public ThemedSpinner()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ThemedSpinner spinner)
                return;

            spinner.UpdateAnimationState();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateAnimationState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
        }

        private void UpdateAnimationState()
        {
            if (SpinnerRotateTransform == null)
                return;

            if (IsActive)
            {
                SpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, _spinAnimation);
            }
            else
            {
                StopAnimation();
            }
        }

        private void StopAnimation()
        {
            if (SpinnerRotateTransform == null)
                return;

            SpinnerRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            SpinnerRotateTransform.Angle = 0;
        }
    }
}
