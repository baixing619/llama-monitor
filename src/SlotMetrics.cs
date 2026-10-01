using System;
using System.Collections.Generic;
using System.Globalization;

namespace LlamaMonitor
{
    internal static class SlotMetrics
    {
        // In current llama.cpp n_prompt_tokens is the retained slot sequence, including
        // generated tokens. Adding n_decoded would count generated tokens twice.
        public static SlotRow Read(Dictionary<string, object> source)
        {
            var row = new SlotRow {
                Id = (int)Number(source, "id", 0),
                Task = Number(source, "id_task", -1),
                PromptTotal = Number(source, "n_prompt_tokens", -1),
                PromptProcessed = Number(source, "n_prompt_tokens_processed", -1),
                PromptCache = Number(source, "n_prompt_tokens_cache", -1),
                NCtx = Number(source, "n_ctx", 0)
            };
            object value;
            row.Processing = source.TryGetValue("is_processing", out value) && value is bool && (bool)value;
            row.NPast = Number(source, "n_past", row.PromptTotal);
            row.NDecoded = Number(source, "n_decoded", -1);
            if (row.NDecoded < 0 && source.TryGetValue("next_token", out value))
            {
                var next = value as Dictionary<string, object>;
                var array = value as object[];
                if (next == null && array != null && array.Length > 0)
                    next = array[0] as Dictionary<string, object>;
                row.NDecoded = Number(next, "n_decoded", -1);
            }
            row.ProgressText = Count(row.PromptProcessed);
            row.PromptText = Count(row.PromptCache);
            if (row.NPast >= 0)
            {
                row.ContextText = Count(row.NPast) + "/" + (row.NCtx > 0 ? Count(row.NCtx) : "—");
                if (row.NCtx > 0)
                    row.ContextText += " (" + (100.0 * row.NPast / row.NCtx).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
            }
            return row;
        }

        internal static long Number(Dictionary<string, object> source, string key, long fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null || value is bool) return fallback;
            try
            {
                long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return number >= 0 ? number : fallback;
            }
            catch { return fallback; }
        }

        internal static string Count(long count)
        {
            return count < 0 ? "—" : count.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
