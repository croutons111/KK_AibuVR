using System.Collections.Generic;

namespace KK_AibuVR
{
    // VRHandCtrl インスタンスごとに「現在選択中のアイテムIndex」を管理するクラス。
    // VRHandCtrl は Unity が管理するため Dictionary でのライフタイム管理で十分。
    internal static class AibuItemTracker
    {
        private static readonly Dictionary<object, ItemState> _states =
            new Dictionary<object, ItemState>();

        internal sealed class ItemState
        {
            public List<int> ItemIds { get; } = new List<int>();
            public int CurrentIndex { get; set; } = 0;
            public int CurrentId => ItemIds.Count > 0 ? ItemIds[CurrentIndex] : -1;
        }

        public static ItemState GetOrCreate(object vrHandCtrl)
        {
            if (!_states.TryGetValue(vrHandCtrl, out var state))
            {
                state = new ItemState();
                _states[vrHandCtrl] = state;
            }
            return state;
        }

        public static bool TryGet(object vrHandCtrl, out ItemState state)
            => _states.TryGetValue(vrHandCtrl, out state);

        public static void CycleNext(object vrHandCtrl)
        {
            var state = GetOrCreate(vrHandCtrl);
            if (state.ItemIds.Count <= 1) return;
            state.CurrentIndex = (state.CurrentIndex + 1) % state.ItemIds.Count;
        }

        public static void CyclePrev(object vrHandCtrl)
        {
            var state = GetOrCreate(vrHandCtrl);
            if (state.ItemIds.Count <= 1) return;
            state.CurrentIndex = (state.CurrentIndex - 1 + state.ItemIds.Count) % state.ItemIds.Count;
        }

        public static void Clear()
            => _states.Clear();
    }
}
