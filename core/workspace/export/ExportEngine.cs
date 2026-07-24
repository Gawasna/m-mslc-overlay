using System.Collections.Generic;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;

namespace MMslcOverlay.Core.Workspace.Export;

public interface IExporter
{
    string Export(IEnumerable<MergedSegment> segments);
}

public class ExportEngine
{
    private readonly SegmentRepository _segmentRepo;

    public ExportEngine(SegmentRepository segmentRepo)
    {
        _segmentRepo = segmentRepo;
    }

    public string RunExport(IExporter exporter)
    {
        var segments = _segmentRepo.GetMergedSegments();
        return exporter.Export(segments);
    }
}
