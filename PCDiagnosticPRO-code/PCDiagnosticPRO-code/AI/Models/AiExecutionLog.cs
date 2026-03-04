using System;
using System.Collections.Generic;

namespace PCDiagnosticPro.AI.Models
{
    public sealed class AiExecutionLog
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Level { get; set; } = "info";
        public string Message { get; set; } = string.Empty;
    }

    public sealed class PowerShellExecutionResult
    {
        public bool Started { get; set; }
        public bool Completed { get; set; }
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public bool RebootRequired { get; set; }
        public bool Elevated { get; set; }
        public int ExitCode { get; set; } = -1;

        public string WorkingDirectory { get; set; } = string.Empty;
        public string ScriptPath { get; set; } = string.Empty;
        public string ExecutionLogPath { get; set; } = string.Empty;
        public string TranscriptPath { get; set; } = string.Empty;
        public string StdOut { get; set; } = string.Empty;
        public string StdErr { get; set; } = string.Empty;

        public List<AiExecutionLog> Logs { get; set; } = new();
    }
}
