using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m_mslc_overlay.core;

namespace m_mslc_overlay.services
{
    public class LiveCaptionUdpSender : IDisposable
    {
        private readonly UdpClient _udpClient;
        private readonly int _port;
        private string _currentState = "IDLE";
        private DateTime _lastSyncTime = DateTime.MinValue;

        public LiveCaptionUdpSender(int port = 47832)
        {
            _port = port;
            _udpClient = new UdpClient();
        }

        public void SetState(string state)
        {
            _currentState = state;
        }

        public void SendStateHeartbeat()
        {
            try
            {
                var payload = new
                {
                    type = "state",
                    lc_state = _currentState,
                    wall_clock_ms = Environment.TickCount64
                };
                Send(payload);
            }
            catch { }
        }

        public void SendCommit(CommitMetadata meta)
        {
            try
            {
                var payload = new
                {
                    type = "commit",
                    reason = meta.Reason,
                    acoustic_end_ms = meta.AcousticEndMs,
                    utterance_offset = meta.UtteranceOffset,
                    wall_clock_ms = Environment.TickCount64,
                    is_dangling = meta.IsDangling,
                    was_merged = meta.WasMerged,
                    word_count = meta.WordCount
                };
                Send(payload);
                _currentState = "COMMITTED";
            }
            catch { }
        }

        public void SendSyncIfNeeded(ulong utteranceOffset)
        {
            // Send sync at most once every 5 minutes
            if ((DateTime.Now - _lastSyncTime).TotalMinutes >= 5)
            {
                try
                {
                    var payload = new
                    {
                        type = "sync",
                        utterance_offset_ticks = utteranceOffset,
                        wall_clock_ms = Environment.TickCount64,
                        sample_rate = 16000
                    };
                    Send(payload);
                    _lastSyncTime = DateTime.Now;
                }
                catch { }
            }
        }

        private void Send(object payload)
        {
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            _udpClient.SendAsync(bytes, bytes.Length, "127.0.0.1", _port);
        }

        public void Dispose()
        {
            _udpClient?.Dispose();
        }
    }
}
