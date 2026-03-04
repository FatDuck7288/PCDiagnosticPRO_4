using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.AI;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Tests
{
    public static class ChatSupportViewModelTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_AnalyseRun_LoadsContext), Test_AnalyseRun_LoadsContext);

            return (Successes.Count, Failures.Count, Failures.ToList());
        }

        private static void Test_AnalyseRun_LoadsContext()
        {
            var vm = CreateViewModel("OK");
            var run = CreateRun();

            vm.AnalyseRunForTestAsync(run).GetAwaiter().GetResult();

            Assert(vm.HasContext, "Context must be loaded after Analyze selected run.");
            Assert(!vm.CanAutoFix, "AutoFix must remain disabled before 3-agent script generation.");
        }

        private static ChatSupportViewModel CreateViewModel(string response)
        {
            var fakeRuntime = new FakeRuntimeClient(response);
            var settings = AiSettings.CreateDefaultSafe();
            settings.EnableStreaming = false;
            settings.TimeoutSeconds = 8;

            return new ChatSupportViewModel(
                settings: settings,
                safety: null,
                contextBuilder: null,
                powerShellExecutor: null,
                llmClient: fakeRuntime,
                modelLoader: fakeRuntime,
                autoInitialize: false,
                autoLoadRuns: false,
                logSink: null,
                loadCombinedOverride: _ => new CombinedScanResult
                {
                    Metadata = new ScanMetadataExtract
                    {
                        RunId = "run-test",
                        Timestamp = DateTimeOffset.UtcNow.ToString("O")
                    },
                    DiagnosticsQuality = new DiagnosticQualityResult
                    {
                        CoverageScore = 96,
                        ReliabilityScore = 95
                    }
                });
        }

        private static ScanRunEntry CreateRun()
        {
            return new ScanRunEntry
            {
                DisplayName = "test run",
                CombinedJsonPath = "not-used.json",
                LastModified = DateTime.Now
            };
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Successes.Add(name);
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class FakeRuntimeClient : ILlmClient, ILlmModelLoader
        {
            private readonly string _response;

            public FakeRuntimeClient(string response)
            {
                _response = response;
                Status = ModelStatus.Ready;
                StatusMessage = "Ready";
            }

            public bool IsReady => true;
            public string StatusMessage { get; private set; }
            public ModelStatus Status { get; private set; }

            public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
            {
                return Task.FromResult(_response);
            }

            public async IAsyncEnumerable<string> StreamAsync(
                string systemPrompt,
                string userPrompt,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
                yield return _response;
            }

            public Task<bool> PingAsync(CancellationToken ct = default)
            {
                return Task.FromResult(true);
            }

            public void Unload()
            {
                Status = ModelStatus.NotInstalled;
                StatusMessage = "Model unloaded";
            }

            public ModelValidationResult ValidateModelPath(string path, bool computeChecksum = false)
            {
                return new ModelValidationResult
                {
                    Status = ModelStatus.Ready,
                    Message = "Ready",
                    NormalizedPath = path ?? string.Empty
                };
            }

            public Task<bool> TryLoadAsync(string modelPath, int contextWindow, int threads, int gpuLayers)
            {
                Status = ModelStatus.Ready;
                StatusMessage = "Ready";
                return Task.FromResult(true);
            }
        }
    }
}
