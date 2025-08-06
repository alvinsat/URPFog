using UnityEngine;
using UnityEngine.Rendering;

namespace Meryuhi.Rendering
{
    /// <summary>
    /// Utility script to automatically manage fog exclusion zones.
    /// Add this to a GameObject in your scene to automatically collect exclusion zone colliders.
    /// </summary>
    public class FogExclusionZoneManager : MonoBehaviour
    {
        [Header("Auto-Collection Settings")]
        [Tooltip("Automatically collect all FogExclusionZoneDemo colliders in the scene")]
        public bool autoCollectExclusionZones = true;
        
        [Tooltip("Update exclusion zones every frame (useful for moving colliders)")]
        public bool updateEveryFrame = false;
        
        [Header("Manual Colliders")]
        [Tooltip("Manually assigned colliders for exclusion zones")]
        public Collider[] manualExclusionColliders = new Collider[0];

        private FullScreenFog _fogVolume;
        private Volume _volume;

        void Start()
        {
            // Find the fog volume in the scene
            _volume = FindObjectOfType<Volume>();
            if (_volume != null)
            {
                _fogVolume = _volume.profile?.Get<FullScreenFog>();
            }

            if (_fogVolume == null)
            {
                Debug.LogWarning("FogExclusionZoneManager: No FullScreenFog volume found in the scene. Please add a Volume with FullScreenFog override.");
                return;
            }

            UpdateExclusionZones();
        }

        void Update()
        {
            if (updateEveryFrame)
            {
                UpdateExclusionZones();
            }
        }

        /// <summary>
        /// Update the exclusion zones in the fog volume.
        /// </summary>
        public void UpdateExclusionZones()
        {
            if (_fogVolume == null) return;

            var colliders = CollectExclusionZoneColliders();
            
            if (colliders.Length > 0)
            {
                _fogVolume.enableExclusionZones.value = true;
                _fogVolume.exclusionZoneColliders.value = colliders;
                _fogVolume.enableExclusionZones.overrideState = true;
                _fogVolume.exclusionZoneColliders.overrideState = true;
            }
            else
            {
                _fogVolume.enableExclusionZones.value = false;
                _fogVolume.enableExclusionZones.overrideState = true;
            }
        }

        /// <summary>
        /// Collect all active exclusion zone colliders from the scene.
        /// </summary>
        private Collider[] CollectExclusionZoneColliders()
        {
            var collidersList = new System.Collections.Generic.List<Collider>();

            // Add manual colliders
            foreach (var collider in manualExclusionColliders)
            {
                if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy)
                {
                    collidersList.Add(collider);
                }
            }

            // Auto-collect from FogExclusionZoneDemo components
            if (autoCollectExclusionZones)
            {
                var exclusionZones = FindObjectsOfType<Demo.FogExclusionZoneDemo>();
                foreach (var zone in exclusionZones)
                {
                    if (zone.IsActive())
                    {
                        var collider = zone.GetCollider();
                        if (collider != null && !collidersList.Contains(collider))
                        {
                            collidersList.Add(collider);
                        }
                    }
                }
            }

            return collidersList.ToArray();
        }

        void OnValidate()
        {
            // Update exclusion zones when values change in the inspector
            if (Application.isPlaying && _fogVolume != null)
            {
                UpdateExclusionZones();
            }
        }
    }
}