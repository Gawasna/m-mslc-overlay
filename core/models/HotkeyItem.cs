using System;

namespace m_mslc_overlay.core.models
{
    public class HotkeyItem
    {
        public string ActionId { get; set; } = string.Empty;
        public string ActionName { get; set; } = string.Empty;
        public string KeyGesture { get; set; } = string.Empty; 
        public bool IsGlobal { get; set; } = false;

        public HotkeyItem() { }

        public HotkeyItem(string actionId, string actionName, string keyGesture, bool isGlobal)
        {
            ActionId = actionId;
            ActionName = actionName;
            KeyGesture = keyGesture;
            IsGlobal = isGlobal;
        }
    }
}
