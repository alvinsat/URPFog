using UnityEngine;

namespace Meryuhi.Rendering.Demo
{
    /// <summary>
    /// Simple demo script to help test fog exclusion zones.
    /// Add this to a GameObject with a collider to create an exclusion zone.
    /// </summary>
    public class FogExclusionZoneDemo : MonoBehaviour
    {
        [Header("Exclusion Zone Settings")]
        [Tooltip("Enable this exclusion zone")]
        public bool enableExclusion = true;
        
        [Header("Visualization")]
        [Tooltip("Show gizmos in scene view")]
        public bool showGizmos = true;
        
        [Tooltip("Color for the exclusion zone gizmo")]
        public Color gizmoColor = Color.red;

        private Collider _collider;

        void Start()
        {
            _collider = GetComponent<Collider>();
            if (_collider == null)
            {
                Debug.LogWarning("FogExclusionZoneDemo requires a Collider component. Adding a BoxCollider.");
                _collider = gameObject.AddComponent<BoxCollider>();
            }
        }

        void OnDrawGizmos()
        {
            if (!showGizmos) return;

            Gizmos.color = gizmoColor;
            Gizmos.matrix = transform.localToWorldMatrix;

            var collider = GetComponent<Collider>();
            if (collider != null)
            {
                if (collider is BoxCollider box)
                {
                    Gizmos.DrawWireCube(box.center, box.size);
                }
                else if (collider is SphereCollider sphere)
                {
                    Gizmos.DrawWireSphere(sphere.center, sphere.radius);
                }
                else
                {
                    // For other collider types, draw the bounds
                    Gizmos.DrawWireCube(collider.bounds.center - transform.position, collider.bounds.size);
                }
            }
        }

        void OnDrawGizmosSelected()
        {
            if (!showGizmos) return;

            Gizmos.color = gizmoColor * 0.3f;
            Gizmos.matrix = transform.localToWorldMatrix;

            var collider = GetComponent<Collider>();
            if (collider != null)
            {
                if (collider is BoxCollider box)
                {
                    Gizmos.DrawCube(box.center, box.size);
                }
                else if (collider is SphereCollider sphere)
                {
                    Gizmos.DrawSphere(sphere.center, sphere.radius);
                }
            }
        }

        /// <summary>
        /// Get the collider component for this exclusion zone.
        /// </summary>
        public Collider GetCollider()
        {
            if (_collider == null)
                _collider = GetComponent<Collider>();
            return _collider;
        }

        /// <summary>
        /// Check if this exclusion zone is active.
        /// </summary>
        public bool IsActive()
        {
            return enableExclusion && _collider != null && _collider.enabled && gameObject.activeInHierarchy;
        }
    }
}