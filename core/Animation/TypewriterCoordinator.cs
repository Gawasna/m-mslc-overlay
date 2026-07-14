using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using m_mslc_overlay.services;

namespace m_mslc_overlay.core.Animation
{
    public class TypewriterCoordinator : IDisposable
    {
        private readonly RevisionWindowService _revisionService;
        private readonly DispatcherTimer _typewriterTimer;
        private readonly ConcurrentQueue<string> _sentenceQueue;
        
        public bool UseTypewriter { get; set; } = true;

        public TypewriterCoordinator(RevisionWindowService revisionService, DispatcherTimer typewriterTimer, ConcurrentQueue<string> sentenceQueue)
        {
            _revisionService = revisionService ?? throw new ArgumentNullException(nameof(revisionService));
            _typewriterTimer = typewriterTimer ?? throw new ArgumentNullException(nameof(typewriterTimer));
            _sentenceQueue = sentenceQueue ?? throw new ArgumentNullException(nameof(sentenceQueue));

            _revisionService.OnRevise += OnRevisionTriggered;
        }

        public void OnRevisionTriggered(string oldText, string newText)
        {
            if (!UseTypewriter) return;

            _typewriterTimer.Stop();
            
            DequeueOldText(oldText);
            EnqueueMergedText(newText);
            
            ResumeTypewriter();
        }

        public void DequeueOldText(string oldText)
        {
            var items = _sentenceQueue.ToList();
            while (_sentenceQueue.TryDequeue(out _)) { }

            bool removed = false;
            foreach (var item in items)
            {
                if (!removed && item.Trim() == oldText.Trim())
                {
                    removed = true;
                    continue;
                }
                _sentenceQueue.Enqueue(item);
            }
        }

        public void EnqueueMergedText(string newText)
        {
            var items = _sentenceQueue.ToList();
            while (_sentenceQueue.TryDequeue(out _)) { }

            _sentenceQueue.Enqueue(newText);
            foreach (var item in items)
            {
                _sentenceQueue.Enqueue(item);
            }
        }

        public void ResumeTypewriter()
        {
            // Reset state if needed, then start
            _typewriterTimer.Start();
        }

        public void Dispose()
        {
            _revisionService.OnRevise -= OnRevisionTriggered;
        }
    }
}
