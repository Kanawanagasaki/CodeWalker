using System.Collections.Generic;

namespace CodeWalker.World
{
    /// <summary>
    /// Static class holding facial bone swap mappings for debug purposes.
    /// This allows swapping which skeleton bone receives animation data
    /// for facial expression tracks (24, 25, 26) without modifying the
    /// underlying data structures. Only the renderer's bone lookup is
    /// redirected through this mapping.
    /// 
    /// Usage: Set FacialBoneSwapMap[boneIdA] = boneIdB to make the renderer
    /// apply animation data intended for boneIdA to boneIdB instead.
    /// </summary>
    public static class FacialBoneSwapMap
    {
        private static readonly object _lock = new object();
        private static Dictionary<ushort, ushort> _swapMap = new Dictionary<ushort, ushort>();
        private static Dictionary<ushort, ushort> _reverseMap = new Dictionary<ushort, ushort>();

        /// <summary>
        /// Whether the swap map is active and should be consulted by the renderer.
        /// </summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// Get the current swap mapping (read-only copy).
        /// Key = original bone ID, Value = replacement bone ID.
        /// </summary>
        public static Dictionary<ushort, ushort> GetSwapMap()
        {
            lock (_lock)
            {
                return new Dictionary<ushort, ushort>(_swapMap);
            }
        }

        /// <summary>
        /// Add or update a bone swap mapping.
        /// After this, animation data for originalBoneId will be applied to targetBoneId.
        /// </summary>
        public static void SetSwap(ushort originalBoneId, ushort targetBoneId)
        {
            lock (_lock)
            {
                _swapMap[originalBoneId] = targetBoneId;
                _reverseMap[targetBoneId] = originalBoneId;
            }
        }

        /// <summary>
        /// Swap two bones bidirectionally.
        /// After this, boneIdA's animation goes to boneIdB and vice versa.
        /// </summary>
        public static void SwapBones(ushort boneIdA, ushort boneIdB)
        {
            lock (_lock)
            {
                // Remove any existing swaps involving these bones first
                RemoveSwapInternal(boneIdA);
                RemoveSwapInternal(boneIdB);

                // Apply bidirectional swap
                _swapMap[boneIdA] = boneIdB;
                _swapMap[boneIdB] = boneIdA;
                _reverseMap[boneIdB] = boneIdA;
                _reverseMap[boneIdA] = boneIdB;
            }
        }

        /// <summary>
        /// Remove a swap mapping for a specific bone.
        /// </summary>
        public static void RemoveSwap(ushort boneId)
        {
            lock (_lock)
            {
                RemoveSwapInternal(boneId);
            }
        }

        private static void RemoveSwapInternal(ushort boneId)
        {
            // Note: called within lock
            if (_swapMap.TryGetValue(boneId, out var target))
            {
                _reverseMap.Remove(target);
                _swapMap.Remove(boneId);
            }
            if (_reverseMap.TryGetValue(boneId, out var source))
            {
                _swapMap.Remove(source);
                _reverseMap.Remove(boneId);
            }
        }

        /// <summary>
        /// Clear all swap mappings.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _swapMap.Clear();
                _reverseMap.Clear();
            }
        }

        /// <summary>
        /// Look up a bone ID through the swap map.
        /// If the bone has been swapped, returns the replacement bone ID.
        /// Otherwise returns the original bone ID.
        /// </summary>
        public static ushort RemapBoneId(ushort boneId)
        {
            if (!Enabled) return boneId;
            lock (_lock)
            {
                if (_swapMap.TryGetValue(boneId, out var remapped))
                    return remapped;
                return boneId;
            }
        }

        /// <summary>
        /// Get the number of active swap mappings.
        /// </summary>
        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _swapMap.Count;
                }
            }
        }
    }
}
