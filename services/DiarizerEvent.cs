using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MMslcOverlay.Services
{
    public record DiarizerConfig(
        int DeviceIndex,
        float Threshold = 0.5f,
        int ExpectedSpeakers = 0,
        float MinSpeechDuration = 1.2f,
        string DbPath = "models/voice_profiles.db",
        int LcPort = 47832,
        bool Debug = false
    );

    // Diarizer Segment
    public record SegmentInfo(
        [property: JsonPropertyName("start")] float Start,
        [property: JsonPropertyName("end")] float End,
        [property: JsonPropertyName("uid")] string Uid,
        [property: JsonPropertyName("identity")] string Identity,
        [property: JsonPropertyName("seg_count")] int SegCount
    );

    // Base abstract class for events
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(ReadyEvent), "ready")]
    [JsonDerivedType(typeof(TimelineUpdateEvent), "timeline_update")]
    [JsonDerivedType(typeof(RecognitionEvent), "recognition")]
    [JsonDerivedType(typeof(VolLevelEvent), "vol_level")]
    [JsonDerivedType(typeof(SessionFlushedEvent), "session_flushed")]
    [JsonDerivedType(typeof(ErrorEvent), "error")]
    [JsonDerivedType(typeof(StoppedEvent), "stopped")]
    [JsonDerivedType(typeof(LcStateEvent), "lc_state")]
    public abstract record DiarizerEvent;

    public record ReadyEvent() : DiarizerEvent;

    public record TimelineUpdateEvent(
        [property: JsonPropertyName("segments")] List<SegmentInfo> Segments,
        [property: JsonPropertyName("speaker_count")] int SpeakerCount
    ) : DiarizerEvent;

    public record RecognitionEvent(
        [property: JsonPropertyName("uid")] string Uid,
        [property: JsonPropertyName("profile_id")] string ProfileId,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("dist")] float Dist,
        [property: JsonPropertyName("method")] string Method
    ) : DiarizerEvent;

    public record VolLevelEvent(
        [property: JsonPropertyName("rms")] float Rms
    ) : DiarizerEvent;

    public record SessionFlushedEvent(
        [property: JsonPropertyName("uid_map")] Dictionary<string, string> UidMap
    ) : DiarizerEvent;

    public record LcStateEvent(
        [property: JsonPropertyName("state")] string State
    ) : DiarizerEvent;

    public record ErrorEvent(
        [property: JsonPropertyName("message")] string Message
    ) : DiarizerEvent;

    public record StoppedEvent() : DiarizerEvent;
}
