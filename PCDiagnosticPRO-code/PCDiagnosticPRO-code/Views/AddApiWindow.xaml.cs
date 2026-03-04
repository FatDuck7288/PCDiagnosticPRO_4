using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PCDiagnosticPro.AI;
using PCDiagnosticPro.AI.Models;
using PCDiagnosticPro.AI.Providers;

namespace PCDiagnosticPro.Views
{
    public partial class AddApiWindow : Window
    {
        private readonly ApiProviderSettings _existing;
        private readonly ApiSecretProtector _secretProtector = new();
        private CancellationTokenSource? _testCts;
        private bool _isBusy;

        public AddApiWindow(ApiProviderSettings existing)
        {
            InitializeComponent();
            _existing = existing ?? new ApiProviderSettings();

            ProviderCombo.ItemsSource = ApiProviderCatalog.SupportedProviders;
            ProviderCombo.SelectionChanged += ProviderCombo_SelectionChanged;

            var normalizedProvider = ApiProviderCatalog.NormalizeProviderName(_existing.Provider);
            ProviderCombo.SelectedItem = ApiProviderCatalog.SupportedProviders.Contains(normalizedProvider, StringComparer.OrdinalIgnoreCase)
                ? normalizedProvider
                : ApiProviderCatalog.OpenAi;

            ModelNameTextBox.Text = _existing.ModelName ?? string.Empty;
            BaseUrlTextBox.Text = _existing.BaseUrl ?? string.Empty;
            AdvancedUrlCheckBox.IsChecked = !string.IsNullOrWhiteSpace(_existing.BaseUrl);
            UpdateProviderUi();
        }

        public ApiProviderSettings Result { get; private set; } = new();

        /// <summary>
        /// Plaintext key entered in the modal. Persisted by caller through ApiSecretProtector.
        /// </summary>
        public string ApiKeyPlaintext { get; private set; } = string.Empty;

        private string SelectedProvider => ApiProviderCatalog.NormalizeProviderName(ProviderCombo.SelectedItem?.ToString());

        private void ProviderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateProviderUi();
        }

        private void AdvancedUrlCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateProviderUi();
        }

        private void UpdateProviderUi()
        {
            var provider = SelectedProvider;
            var requiresUrl = ApiProviderCatalog.RequiresBaseUrl(provider);
            var supportsOptionalUrl = ApiProviderCatalog.SupportsOptionalBaseUrl(provider);
            var advancedEnabled = supportsOptionalUrl;
            var showUrl = requiresUrl || (supportsOptionalUrl && AdvancedUrlCheckBox.IsChecked == true);

            AdvancedUrlCheckBox.Visibility = advancedEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (!advancedEnabled)
            {
                AdvancedUrlCheckBox.IsChecked = false;
            }

            BaseUrlLabel.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;
            BaseUrlTextBox.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;

            if (!showUrl && !requiresUrl)
            {
                BaseUrlTextBox.Text = string.Empty;
            }

            UrlRuleTextBlock.Text = provider switch
            {
                var p when p == ApiProviderCatalog.OpenAi => "URL hidden: OpenAI default endpoint is used.",
                var p when p == ApiProviderCatalog.Anthropic => "URL hidden: Anthropic default endpoint is used.",
                var p when p == ApiProviderCatalog.Gemini => "URL hidden: Google Gemini default endpoint is used.",
                var p when p == ApiProviderCatalog.Grok => "URL hidden: xAI Grok default endpoint is used.",
                var p when p == ApiProviderCatalog.OpenAiCompatible => "OpenAI-Compatible: URL is optional in Advanced mode.",
                _ => "Custom: Base URL is required."
            };
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _testCts?.Cancel();
            DialogResult = false;
            Close();
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorTextBlock.Text = string.Empty;
            if (!TryBuildDraftProfile(out var draftProfile, out var plaintextKey, out var error))
            {
                ErrorTextBlock.Text = error;
                return;
            }

            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            SetBusy(true);
            try
            {
                var ok = await TestConnectionAsync(draftProfile, plaintextKey, _testCts.Token).ConfigureAwait(true);
                ErrorTextBlock.Text = ok
                    ? "Connection test succeeded."
                    : "Connection test failed. Verify key, model, and provider.";
            }
            catch (OperationCanceledException)
            {
                ErrorTextBlock.Text = "Connection test timed out.";
            }
            catch (Exception ex)
            {
                ErrorTextBlock.Text = $"Connection test failed: {ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<bool> TestConnectionAsync(ApiProviderSettings draftProfile, string plaintextKey, CancellationToken ct)
        {
            var testSettings = AiSettings.CreateDefaultSafe();
            var protectedKey = draftProfile.EncryptedApiKey;
            if (!string.IsNullOrWhiteSpace(plaintextKey))
            {
                protectedKey = _secretProtector.Protect(plaintextKey.Trim(), out _);
            }

            draftProfile.EncryptedApiKey = protectedKey;
            testSettings.ApiProvider = draftProfile;
            testSettings.InferenceMode = AiSettings.InferenceModeApi;
            testSettings.Normalize();

            using var client = new OpenAiCompatibleClient(testSettings, _secretProtector);
            var validation = client.ValidateModelPath(string.Empty, computeChecksum: false);
            if (validation.Status != ModelStatus.Ready)
            {
                ErrorTextBlock.Text = validation.Message;
                return false;
            }

            var loaded = await client.TryLoadAsync(string.Empty, testSettings.ContextWindow, testSettings.Threads, testSettings.GpuLayers).ConfigureAwait(false);
            if (!loaded)
            {
                ErrorTextBlock.Text = client.StatusMessage;
                return false;
            }

            return await client.PingAsync(ct).ConfigureAwait(false);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorTextBlock.Text = string.Empty;
            if (!TryBuildDraftProfile(out var draftProfile, out var plaintextKey, out var error))
            {
                ErrorTextBlock.Text = error;
                return;
            }

            ApiKeyPlaintext = plaintextKey.Trim();
            Result = draftProfile;
            DialogResult = true;
            Close();
        }

        private bool TryBuildDraftProfile(out ApiProviderSettings draftProfile, out string plaintextApiKey, out string error)
        {
            draftProfile = new ApiProviderSettings();
            plaintextApiKey = (ApiKeyBox.Password ?? string.Empty).Trim();
            error = string.Empty;

            var provider = SelectedProvider;
            var model = (ModelNameTextBox.Text ?? string.Empty).Trim();
            var baseUrlRaw = (BaseUrlTextBox.Text ?? string.Empty).Trim();
            var requiresBaseUrl = ApiProviderCatalog.RequiresBaseUrl(provider);
            var optionalBaseUrl = ApiProviderCatalog.SupportsOptionalBaseUrl(provider);
            var useBaseUrl = requiresBaseUrl || (optionalBaseUrl && AdvancedUrlCheckBox.IsChecked == true);

            if (string.IsNullOrWhiteSpace(model))
            {
                error = "Model name is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(plaintextApiKey) && string.IsNullOrWhiteSpace(_existing.EncryptedApiKey))
            {
                error = "API key is required.";
                return false;
            }

            if (requiresBaseUrl && string.IsNullOrWhiteSpace(baseUrlRaw))
            {
                error = "Base URL is required for Custom provider.";
                return false;
            }

            if (useBaseUrl && !string.IsNullOrWhiteSpace(baseUrlRaw))
            {
                if (!Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out var url)
                    || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                {
                    error = "Base URL is invalid.";
                    return false;
                }
            }

            var normalizedBaseUrl = useBaseUrl ? baseUrlRaw.TrimEnd('/') : string.Empty;
            draftProfile = new ApiProviderSettings
            {
                Provider = provider,
                BaseUrl = normalizedBaseUrl,
                ModelName = model,
                ContextWindow = _existing.ContextWindow > 0 ? _existing.ContextWindow : 32768,
                MaxOutputTokens = _existing.MaxOutputTokens > 0 ? _existing.MaxOutputTokens : 800,
                Temperature = _existing.Temperature > 0 ? _existing.Temperature : 0.2f,
                EncryptedApiKey = _existing.EncryptedApiKey ?? string.Empty
            };

            return true;
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            IsEnabled = !_isBusy;
        }
    }
}
