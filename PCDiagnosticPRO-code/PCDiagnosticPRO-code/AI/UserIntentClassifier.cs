using System;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI
{
    public static class UserIntentClassifier
    {
        public static ConversationUserIntent Classify(string? userText)
        {
            var text = (userText ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text))
            {
                return ConversationUserIntent.General;
            }

            if (ContainsAny(text, "script", "autofix"))
            {
                return ConversationUserIntent.ScriptRequest;
            }

            if (ContainsAny(text, "le plus problematique", "top", "priorite", "priorité"))
            {
                return ConversationUserIntent.DiagnoseTop;
            }

            if (ContainsAny(text, "comment je", "comment", "how", "steps", "etapes", "étapes", "reparer", "réparer", "fix"))
            {
                return ConversationUserIntent.HowTo;
            }

            if (ContainsAny(text, "pourquoi", "cause", "why"))
            {
                return ConversationUserIntent.Why;
            }

            return ConversationUserIntent.General;
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
