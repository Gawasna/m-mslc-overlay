using System;

namespace m_mslc_overlay.core
{
    /// <summary>
    /// Quick verification that SegmentDisplayModel and SegmentDisplayItem work correctly.
    /// This is a standalone verification file that can be run to test the implementation.
    /// </summary>
    public static class SegmentDisplayModelVerification
    {
        public static void VerifyImplementation()
        {
            Console.WriteLine("=== SegmentDisplayModel Verification ===\n");

            // Create a display model
            var model = new SegmentDisplayModel();
            Console.WriteLine("✓ Created SegmentDisplayModel");

            // Create test segments
            var commit1 = CommitMetadata.From("Hello world", "HardCommit", isDangling: false);
            var record1 = new SegmentRecord { Commit = commit1 };
            
            var commit2 = CommitMetadata.From("How are", "SoftCommit", isDangling: true);
            var record2 = new SegmentRecord { Commit = commit2 };

            // Add segments
            model.AddSegment(record1);
            model.AddSegment(record2);
            Console.WriteLine($"✓ Added 2 segments, count: {model.Count}");

            // Verify segments
            var segments = model.GetAllSegments();
            Console.WriteLine($"✓ Retrieved {segments.Count} segments");

            // Verify segment 1 (Committed, not dangling)
            var seg1 = segments[0];
            Console.WriteLine($"\nSegment 1: '{seg1.Text}'");
            Console.WriteLine($"  State: {seg1.State}");
            Console.WriteLine($"  Opacity: {seg1.Opacity} (expected: 0.5)");
            Console.WriteLine($"  IsItalic: {seg1.IsItalic} (expected: true)");
            Console.WriteLine($"  IsUnderlined: {seg1.IsUnderlined} (expected: false)");
            Console.WriteLine($"  TextColor: {seg1.TextColor} (expected: null)");

            // Verify segment 2 (Committed, dangling)
            var seg2 = segments[1];
            Console.WriteLine($"\nSegment 2: '{seg2.Text}'");
            Console.WriteLine($"  State: {seg2.State}");
            Console.WriteLine($"  Opacity: {seg2.Opacity} (expected: 0.5)");
            Console.WriteLine($"  IsItalic: {seg2.IsItalic} (expected: true)");
            Console.WriteLine($"  IsUnderlined: {seg2.IsUnderlined} (expected: true)");
            Console.WriteLine($"  IsDangling: {seg2.IsDangling} (expected: true)");
            Console.WriteLine($"  TextColor: {seg2.TextColor} (expected: yellow #FFAA00)");

            // Update segment state
            model.UpdateSegmentState(seg1.SegmentId, SegmentState.Translated);
            Console.WriteLine($"\n✓ Updated segment 1 to Translated state");

            var updatedSeg1 = model.GetAllSegments()[0];
            Console.WriteLine($"  Updated opacity: {updatedSeg1.Opacity} (expected: 1.0)");
            Console.WriteLine($"  Updated IsItalic: {updatedSeg1.IsItalic} (expected: false)");

            // Update to stale
            model.UpdateSegmentState(seg2.SegmentId, SegmentState.Stale);
            Console.WriteLine($"\n✓ Updated segment 2 to Stale state");

            var updatedSeg2 = model.GetAllSegments()[1];
            Console.WriteLine($"  Updated opacity: {updatedSeg2.Opacity} (expected: 0.3)");
            Console.WriteLine($"  TextColor: {updatedSeg2.TextColor} (expected: gray #808080)");

            // Remove segment
            model.RemoveSegment(seg1.SegmentId);
            Console.WriteLine($"\n✓ Removed segment 1, count: {model.Count} (expected: 1)");

            // Clear all
            model.ClearAllSegments();
            Console.WriteLine($"✓ Cleared all segments, count: {model.Count} (expected: 0)");

            Console.WriteLine("\n=== Verification Complete ===");
        }
    }
}
