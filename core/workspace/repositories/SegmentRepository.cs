using System.Collections.Generic;
using System.Linq;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Repositories;

/// <summary>
/// SegmentRepository kết hợp Machine Truth và Human Truth (Apply patch_events lên segments)
/// </summary>
public class SegmentRepository
{
    // Cần mở rộng để hỗ trợ đọc nhiều BaseSegmentRepository (active.db, seg_001.db, v.v.)
    // Ở bản đơn giản, truyền vào danh sách base repos.
    private readonly List<BaseSegmentRepository> _baseRepos;
    public UserDataRepository UserDataRepo { get; }

    public SegmentRepository(List<BaseSegmentRepository> baseRepos, UserDataRepository userDataRepo)
    {
        _baseRepos = baseRepos;
        UserDataRepo = userDataRepo;
    }

    public List<MergedSegment> GetMergedSegments()
    {
        // 1. Thu thập tất cả base segments từ các chunks
        var allBaseSegments = new List<Segment>();
        foreach (var repo in _baseRepos)
        {
            allBaseSegments.AddRange(repo.GetActiveSegments());
        }

        // 2. Sắp xếp theo thứ tự timecode ASC
        allBaseSegments.Sort((a, b) => a.TsStartMs.CompareTo(b.TsStartMs));

        // Tạo map để apply patches
        var mergedMap = new Dictionary<string, MergedSegment>();
        var byIdMap = new Dictionary<string, MergedSegment>();
        var mergedList = new List<MergedSegment>();

        foreach (var baseSeg in allBaseSegments)
        {
            var merged = new MergedSegment(baseSeg);
            mergedMap[merged.SegmentRef] = merged;
            byIdMap[baseSeg.Id.ToString()] = merged;
            mergedList.Add(merged);
        }

        // 3. Lấy tất cả patch events và apply tuần tự (Event Sourcing Replay)
        var events = UserDataRepo.GetAllPatchEvents();
        
        foreach (var evt in events)
        {
            // UNDO logic: ở mức repository đơn giản (replay all) thì cần logic UNDO/REDO phức tạp hơn
            // Hiện tại apply trực tiếp PATCH. 
            // Khi có UNDO, event UNDO sẽ mang value_new là giá trị value_old của event bị đảo ngược.
            
            // Support both full SegmentRef (chunkId:id) and legacy/raw ID format (id)
            string lookupKey = evt.SegmentRef;
            if (mergedMap.TryGetValue(lookupKey, out var target) || byIdMap.TryGetValue(lookupKey, out target))
            {
                ApplyFieldChange(target, evt.Field, evt.ValueNew);
            }
            else if (lookupKey.Contains(':'))
            {
                var parts = lookupKey.Split(':');
                if (parts.Length == 2 && byIdMap.TryGetValue(parts[1], out target))
                {
                    ApplyFieldChange(target, evt.Field, evt.ValueNew);
                }
            }
        }

        return mergedList;
    }

    private void ApplyFieldChange(MergedSegment segment, string field, string value)
    {
        switch (field)
        {
            // Support both PascalCase (from ViewModel) and snake_case (from tests/legacy)
            case "text_src":
            case "TextSrc":
                segment.TextSrc = value;
                break;
            case "text_trs":
            case "TextTrs":
                segment.TextTrs = value;
                break;
            case "speaker_id":
            case "SpeakerId":
                segment.SpeakerId = value;
                break;
        }
    }
}
