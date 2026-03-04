namespace PCDiagnosticPro.Models
{
    public class ResultField
    {
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Display value for UI. Null/empty means data was not collected.
        /// UI layer should show "Indisponible" when this is null or empty.
        /// Never store machine-typed data in this field — it is UI only.
        /// </summary>
        public string? Value { get; set; }

        /// <summary>True when Value is absent / not collected.</summary>
        public bool IsMissing => string.IsNullOrEmpty(Value);
    }
}
