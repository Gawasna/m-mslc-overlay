using System;
using System.Collections.Generic;
using Avalonia.Controls.Documents;

namespace m_mslc_overlay.core
{
    public enum DiffOperation
    {
        Insert,
        Delete,
        Unchanged
    }

    public class TextDelta
    {
        public int StartIndex { get; set; }
        public int Length { get; set; }
        public string NewText { get; set; } = string.Empty;
        public DiffOperation Operation { get; set; }
    }

    public class DiffMatchPatchRenderer
    {
        private string _lastPartialText = string.Empty;

        public bool IsEnabled { get; set; } = false;

        public IReadOnlyList<TextDelta> ComputeDelta(string newText)
        {
            var deltas = new List<TextDelta>();
            
            if (_lastPartialText != newText)
            {
                deltas.Add(new TextDelta 
                { 
                    StartIndex = 0, 
                    Length = _lastPartialText.Length, 
                    NewText = newText, 
                    Operation = DiffOperation.Insert 
                });
            }

            _lastPartialText = newText ?? string.Empty;
            return deltas;
        }

        public void ApplyDeltaToInlineCollection(InlineCollection inlines, IReadOnlyList<TextDelta> deltas)
        {
            if (!IsEnabled) return;

            inlines.Clear();
            foreach (var delta in deltas)
            {
                if (delta.Operation == DiffOperation.Insert)
                {
                    inlines.Add(new Run { Text = delta.NewText });
                }
            }
        }
    }
}
