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
        var mergedList = new List<MergedSegment>();

        foreach (var baseSeg in allBaseSegments)
        {
            var merged = new MergedSegment(baseSeg);
            mergedMap[merged.SegmentRef] = merged;
            mergedList.Add(merged);
        }

        // 3. Lấy tất cả patch events và apply tuần tự (Event Sourcing Replay)
        var events = UserDataRepo.GetAllPatchEvents();
        
        foreach (var evt in events)
        {
            // UNDO logic: ở mức repository đơn giản (replay all) thì cần logic UNDO/REDO phức tạp hơn
            // Hiện tại apply trực tiếp PATCH. 
            // Khi có UNDO, event UNDO sẽ mang value_new là giá trị value_old của event bị đảo ngược.
            
            if (mergedMap.TryGetValue(evt.SegmentRef, out var target))
            {
                ApplyFieldChange(target, evt.Field, evt.ValueNew);
            }
        }

        return mergedList;
    }

    private void ApplyFieldChange(MergedSegment segment, string field, string value)
    {
        switch (field)
        {
            case "text_src":
                segment.TextSrc = value;
                break;
            case "text_trs":
                segment.TextTrs = value;
                break;
            case "speaker_id":
                segment.SpeakerId = value;
                break;
        }
    }
}
