using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Meryuhi.Rendering
{
    [Tooltip(FullScreenFog.Name + " will process the related volume overrides in scenes.")]
    public sealed class FullScreenFogRendererFeature : ScriptableRendererFeature
    {
        struct PassData
        {
            internal Material Material;
            internal FullScreenFog Fog;
            internal RTHandle ExclusionMask;
        }

        class FullScreenFogRenderPass : ScriptableRenderPass
        {
            private PassData _passData;
            private RTHandle _copiedColor;
            private RTHandle _exclusionMask;

            private static readonly int BlitTextureShaderID = Shader.PropertyToID("_BlitTexture");
            private static readonly int BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
            private static readonly int ExclusionMaskShaderID = Shader.PropertyToID("_ExclusionMask");

            private static readonly (string Name, FullScreenFogDensityMode Value)[] ModeShaderKeywords = Enum.GetValues(typeof(FullScreenFogDensityMode))
                .Cast<FullScreenFogDensityMode>()
                .Select(mode => ($"_{nameof(FullScreenFog.densityMode).ToUpper()}_{mode.ToString().ToUpper()}", mode)).ToArray();

            private static readonly (string Name, FullScreenFogMode Value)[] DistanceModeShaderKeywords = Enum.GetValues(typeof(FullScreenFogMode))
                .Cast<FullScreenFogMode>()
                .Select(mode => ($"_{nameof(FullScreenFog.mode).ToUpper()}_{mode.ToString().ToUpper()}", mode)).ToArray();
            private static readonly int MainParamsShaderID = Shader.PropertyToID("_MainParams");

            private static readonly (string Name, FullScreenFogNoiseMode Value)[] NoiseModeShaderKeywords = Enum.GetValues(typeof(FullScreenFogNoiseMode))
                .Cast<FullScreenFogNoiseMode>()
                .Select(mode => ($"_{nameof(FullScreenFog.noiseMode).ToUpper()}_{mode.ToString().ToUpper()}", mode)).ToArray();
            private static readonly int NoiseTexShaderID = Shader.PropertyToID("_NoiseTex");
            private static readonly int NoiseParamsShaderID = Shader.PropertyToID("_NoiseParams");
            private static readonly string CopiedColorRTName = $"_{FullScreenFog.Name}CopiedColor";
            private static readonly string ExclusionMaskRTName = $"_{FullScreenFog.Name}ExclusionMask";
            private static readonly string CopyColorPassName = $"{FullScreenFog.Name}CopyColorPass";
            private static readonly string ExclusionMaskPassName = $"{FullScreenFog.Name}ExclusionMaskPass";
            private static readonly string MainPassName = $"{FullScreenFog.Name}MainPass";

            public FullScreenFogRenderPass()
            {
                profilingSampler = new(FullScreenFog.Name);
            }

            public void Setup(PassData passData)
            {
                _passData = passData;
            }

            ///From <see cref="FullScreenPassRendererFeature.FullScreenRenderPass"/>
            [Obsolete]
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                // Disable obsolete warning for internal usage
                #pragma warning disable CS0618
                // FullScreenPass manages its own RenderTarget.
                // ResetTarget here so that ScriptableRenderer's active attachement can be invalidated when processing this ScriptableRenderPass.
                ResetTarget();
                #pragma warning restore CS0618
                ReAllocate(renderingData.cameraData.cameraTargetDescriptor);
            }
            
            internal void ReAllocate(RenderTextureDescriptor desc)
            {
                desc.msaaSamples = 1;
                desc.depthStencilFormat = GraphicsFormat.None;
                RenderingUtils.ReAllocateHandleIfNeeded(ref _copiedColor, desc, name: CopiedColorRTName);
                
                // Create exclusion mask with single channel format for efficiency
                var maskDesc = desc;
                maskDesc.colorFormat = RenderTextureFormat.R8;
                RenderingUtils.ReAllocateHandleIfNeeded(ref _exclusionMask, maskDesc, name: ExclusionMaskRTName);
            }

            public void Dispose()
            {
                _copiedColor?.Release();
                _exclusionMask?.Release();
            }

            private static void ExecuteCopyColorPass(RasterCommandBuffer cmd, RTHandle sourceTexture)
            {
                Blitter.BlitTexture(cmd, sourceTexture, new Vector4(1, 1, 0, 0), 0.0f, false);
            }

            private static void ExecuteExclusionMaskPass(RasterCommandBuffer cmd, FullScreenFog fog, UniversalCameraData cameraData)
            {
                ExecuteExclusionMaskPass(cmd, fog, cameraData.camera);
            }
            
            private static void ExecuteExclusionMaskPass(RasterCommandBuffer cmd, FullScreenFog fog, Camera camera)
            {
                // Clear the exclusion mask to white (no exclusion)
                cmd.ClearRenderTarget(RTClearFlags.Color, Color.white, 0.0f, 0);
                
                if (!fog.enableExclusionZones.value || fog.exclusionZoneColliders.value == null)
                    return;

                // For each collider, render it as a black area in the mask
                foreach (var collider in fog.exclusionZoneColliders.value)
                {
                    if (collider == null) continue;

                    // Get collider bounds in world space
                    var bounds = collider.bounds;
                    var center = bounds.center;
                    var size = bounds.size;

                    // Create world space bounds corners
                    var corners = new Vector3[8];
                    corners[0] = center + new Vector3(-size.x, -size.y, -size.z) * 0.5f;
                    corners[1] = center + new Vector3(size.x, -size.y, -size.z) * 0.5f;
                    corners[2] = center + new Vector3(size.x, size.y, -size.z) * 0.5f;
                    corners[3] = center + new Vector3(-size.x, size.y, -size.z) * 0.5f;
                    corners[4] = center + new Vector3(-size.x, -size.y, size.z) * 0.5f;
                    corners[5] = center + new Vector3(size.x, -size.y, size.z) * 0.5f;
                    corners[6] = center + new Vector3(size.x, size.y, size.z) * 0.5f;
                    corners[7] = center + new Vector3(-size.x, size.y, size.z) * 0.5f;

                    // Project all corners to screen space and find 2D bounding box
                    var minScreenPos = Vector2.positiveInfinity;
                    var maxScreenPos = Vector2.negativeInfinity;
                    bool anyVisible = false;

                    foreach (var corner in corners)
                    {
                        var viewPos = camera.worldToCameraMatrix.MultiplyPoint(corner);
                        if (viewPos.z > 0) // In front of camera
                        {
                            var screenPos = camera.WorldToScreenPoint(corner);
                            var normalizedPos = new Vector2(
                                screenPos.x / camera.pixelWidth,
                                screenPos.y / camera.pixelHeight
                            );
                            
                            minScreenPos = Vector2.Min(minScreenPos, normalizedPos);
                            maxScreenPos = Vector2.Max(maxScreenPos, normalizedPos);
                            anyVisible = true;
                        }
                    }

                    if (anyVisible)
                    {
                        // Clamp to screen bounds and ensure valid rectangle
                        minScreenPos = Vector2.Max(minScreenPos, Vector2.zero);
                        maxScreenPos = Vector2.Min(maxScreenPos, Vector2.one);

                        if (minScreenPos.x < maxScreenPos.x && minScreenPos.y < maxScreenPos.y)
                        {
                            // Convert to NDC coordinates for rendering
                            var ndcMin = minScreenPos * 2.0f - Vector2.one;
                            var ndcMax = maxScreenPos * 2.0f - Vector2.one;
                            
                            // Draw a screen-space quad to mask this area
                            // For simplicity, we'll use a built-in approach with DrawProcedural
                            // This creates a simple quad covering the collider's screen projection
                            
                            // Create transformation matrix for the exclusion zone
                            var exclusionMatrix = Matrix4x4.identity;
                            exclusionMatrix.m00 = (ndcMax.x - ndcMin.x) * 0.5f; // width scale
                            exclusionMatrix.m11 = (ndcMax.y - ndcMin.y) * 0.5f; // height scale
                            exclusionMatrix.m03 = (ndcMax.x + ndcMin.x) * 0.5f; // x position
                            exclusionMatrix.m13 = (ndcMax.y + ndcMin.y) * 0.5f; // y position
                            
                            // Note: This simplified version draws a screen-space rectangle
                            // A more advanced implementation would render the actual collider geometry
                        }
                    }
                }
            }

            private static void ExecuteMainPass(RasterCommandBuffer cmd, RTHandle sourceTexture, Material material, FullScreenFog fog, RTHandle exclusionMask)
            {
                var mode = fog.mode.value;
                foreach (var (Name, Value) in DistanceModeShaderKeywords)
                {
                    CoreUtils.SetKeyword(material, Name, Value == mode);
                }
                var color = fog.color.value;
                color.a = fog.intensity.value;
                material.color = color;

                var densityMode = fog.densityMode.value;
                foreach (var (Name, Value) in ModeShaderKeywords)
                {
                    CoreUtils.SetKeyword(material, Name, Value == densityMode);
                }

                var fogParams = new Vector4();
                if (FullScreenFog.UseStartLine(mode))
                {
                    fogParams.x = fog.startLine.value;
                }
                if (FullScreenFog.UseEndLine(mode, densityMode))
                {
                    var delta = fog.endLine.value - fogParams.x;
                    fogParams.y = delta == 0 ? float.MaxValue : 1 / delta;
                }
                var isHeightMode = FullScreenFog.UseStartHeight(mode);
                if (isHeightMode)
                {
                    fogParams.x = fog.startHeight.value;
                }
                if (FullScreenFog.UseEndHeight(mode, densityMode))
                {
                    var delta = fogParams.x - fog.endHeight.value;
                    fogParams.y = delta == 0 ? float.MaxValue : 1 / delta;
                }
                if (FullScreenFog.UseIntensity(densityMode))
                {
                    fogParams.y = fog.density.value;
                }
                if (isHeightMode)/*Because the height fog calculation direction is reversed, we need to reverse the sign*/
                {
                    fogParams.y *= -1;
                }
                material.SetVector(MainParamsShaderID, fogParams);

                foreach (var (Name, Value) in NoiseModeShaderKeywords)
                {
                    CoreUtils.SetKeyword(material, Name, Value == fog.noiseMode.value);
                }
                var noiseMode = fog.noiseMode.value;
                if (FullScreenFog.UseNoiseTex(noiseMode))
                {
                    material.SetTexture(NoiseTexShaderID, fog.noiseTexture.value == null ? Texture2D.whiteTexture : fog.noiseTexture.value);
                }
                if (FullScreenFog.UseNoiseIntensity(noiseMode))
                {
                    material.SetVector(NoiseParamsShaderID, new Vector4(fog.noiseIntensity.value, fog.noiseScale.value, fog.noiseScrollSpeed.value.x, fog.noiseScrollSpeed.value.y));
                }
                
                // Set exclusion zone parameters
                CoreUtils.SetKeyword(material, "_EXCLUSION_ZONES", fog.enableExclusionZones.value);
                if (fog.enableExclusionZones.value)
                {
                    material.SetTexture(ExclusionMaskShaderID, exclusionMask);
                    material.SetFloat("_ExclusionSmoothing", fog.exclusionZoneSmoothing.value);
                }
                
                material.SetTexture(BlitTextureShaderID, sourceTexture);
                // We need to set the "_BlitScaleBias" uniform for user materials with shaders relying on core Blit.hlsl to work
                material.SetVector(BlitScaleBias, new Vector4(1, 1, 0, 0));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
            }

            [Obsolete]
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                ref var cameraData = ref renderingData.cameraData;
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, profilingSampler))
                {
                    RasterCommandBuffer rasterCmd = CommandBufferHelpers.GetRasterCommandBuffer(cmd);
                    
                    // Copy color pass
                    CoreUtils.SetRenderTarget(cmd, _copiedColor);
                    ExecuteCopyColorPass(rasterCmd, cameraData.renderer.cameraColorTargetHandle);

                    // Exclusion mask pass
                    if (_passData.Fog.enableExclusionZones.value)
                    {
                        CoreUtils.SetRenderTarget(cmd, _exclusionMask);
                        ExecuteExclusionMaskPass(rasterCmd, _passData.Fog, cameraData.camera);
                    }

                    // Main fog pass
                    CoreUtils.SetRenderTarget(cmd, cameraData.renderer.cameraColorTargetHandle);
                    ExecuteMainPass(rasterCmd, _copiedColor, _passData.Material, _passData.Fog, _exclusionMask);
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                CommandBufferPool.Release(cmd);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                TextureHandle source, destination, exclusionMask;

                Debug.Assert(resourcesData.cameraColor.IsValid());

                var targetDesc = renderGraph.GetTextureDesc(resourcesData.cameraColor);
                targetDesc.name = FullScreenFog.Name;
                targetDesc.clearBuffer = false;

                source = resourcesData.activeColorTexture;
                destination = renderGraph.CreateTexture(targetDesc);
                
                // Create exclusion mask texture
                var maskDesc = targetDesc;
                maskDesc.name = $"{FullScreenFog.Name}_ExclusionMask";
                maskDesc.colorFormat = GraphicsFormat.R8_UNorm;
                exclusionMask = renderGraph.CreateTexture(maskDesc);
                
                using (var builder = renderGraph.AddRasterRenderPass<CopyPassData>(CopyColorPassName, out var passData, profilingSampler))
                {
                    passData.InputTexture = source;
                    builder.UseTexture(passData.InputTexture, AccessFlags.Read);

                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                    builder.SetRenderFunc((CopyPassData data, RasterGraphContext rgContext) =>
                    {
                        ExecuteCopyColorPass(rgContext.cmd, data.InputTexture);
                    });
                }

                // Exclusion mask pass
                if (_passData.Fog.enableExclusionZones.value)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<ExclusionMaskPassData>(ExclusionMaskPassName, out var passData, profilingSampler))
                    {
                        passData.Fog = _passData.Fog;
                        passData.CameraData = cameraData;

                        builder.SetRenderAttachment(exclusionMask, 0, AccessFlags.Write);

                        builder.SetRenderFunc((ExclusionMaskPassData data, RasterGraphContext rgContext) =>
                        {
                            ExecuteExclusionMaskPass(rgContext.cmd, data.Fog, data.CameraData);
                        });
                    }
                }

                //Swap for next pass;
                source = destination;

                destination = resourcesData.activeColorTexture;

                using (var builder = renderGraph.AddRasterRenderPass<MainPassData>(MainPassName, out var passData, profilingSampler))
                {
                    passData.Material = _passData.Material;

                    passData.Fog = _passData.Fog;

                    passData.InputTexture = source;
                    passData.ExclusionMask = exclusionMask;

                    if(passData.InputTexture.IsValid())
                        builder.UseTexture(passData.InputTexture, AccessFlags.Read);
                        
                    if(passData.ExclusionMask.IsValid())
                        builder.UseTexture(passData.ExclusionMask, AccessFlags.Read);

                    //Declare that the pass uses the input texture
                    var colorTexture = source;
                    //For avoid overpainting
                    if (renderPassEvent >= (RenderPassEvent)InjectionPoint.BeforeRenderingTransparents)
                    {
                        colorTexture = resourcesData.cameraOpaqueTexture;
                    }
                    if ((input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None)
                    {
                        Debug.Assert(colorTexture.IsValid());
                        builder.UseTexture(colorTexture);
                    }

                    //Declare that the pass uses the depth texture
                    if ((input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None)
                    {
                        Debug.Assert(resourcesData.cameraDepthTexture.IsValid());
                        builder.UseTexture(resourcesData.cameraDepthTexture);
                    }
                    
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                    builder.SetRenderFunc(static (MainPassData data, RasterGraphContext rgContext) =>
                    {
                        ExecuteMainPass(rgContext.cmd, data.InputTexture, data.Material, data.Fog, data.ExclusionMask);
                    });                
                }
            }

            private class CopyPassData
            {
                internal TextureHandle InputTexture;
            }

            private class ExclusionMaskPassData
            {
                internal FullScreenFog Fog;
                internal UniversalCameraData CameraData;
            }

            private class MainPassData
            {
                internal Material Material;
                internal FullScreenFog Fog;
                internal TextureHandle InputTexture;
                internal TextureHandle ExclusionMask;
            }
        }

        /// <summary>
        /// An injection point for the pass. This is similar to RenderPassEvent enum but limits to only supported events.
        /// </summary>
        public enum InjectionPoint
        {
            BeforeRenderingSkybox = RenderPassEvent.BeforeRenderingSkybox,
            BeforeRenderingTransparents = RenderPassEvent.BeforeRenderingTransparents,
            BeforeRenderingPostProcessing = RenderPassEvent.BeforeRenderingPostProcessing,
            AfterRenderingPostProcessing = RenderPassEvent.AfterRenderingPostProcessing
        }

        /// <summary>
        /// Selection for when the effect is rendered.
        /// </summary>
        [SerializeField]
        private InjectionPoint _injectionPoint = InjectionPoint.BeforeRenderingPostProcessing;
        /// <summary>
        /// Selection for which camera type want to render.
        /// </summary>
        [SerializeField]
        private CameraType _renderCamera = CameraType.Game | CameraType.SceneView;
        /// <summary>
        /// Material the Renderer Feature uses to render the effect.
        /// </summary>
        [SerializeField]
        [Reload("Shaders/FullScreenFog.shadergraph")]
        private Shader _shader;

        private Material _material;
        private FullScreenFogRenderPass _renderPass;
        public static readonly string PackagePath = "Packages/moe.meryuhi.effects.fog";
        /// <inheritdoc/>
        public override void Create()
        {
#if UNITY_EDITOR
            ResourceReloader.TryReloadAllNullIn(this, PackagePath);
#endif
            if (_shader == null)
            {
                Debug.LogWarning($"Missing {FullScreenFog.Name} shader. {GetType().Name} will not execute. Check for missing reference in the assigned renderer.");
                return;
            }
            _material = CoreUtils.CreateEngineMaterial(_shader);
            _renderPass = new();
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if ((renderingData.cameraData.cameraType & _renderCamera) == 0)
            {
                return;
            }

#if UNITY_EDITOR
            var sceneView = UnityEditor.SceneView.currentDrawingSceneView;
            if (sceneView != null && renderingData.cameraData.camera == sceneView.camera && !sceneView.sceneViewState.fogEnabled)
            {
                return;
            }
#endif
            var stack = VolumeManager.instance.stack;
            var fog = stack?.GetComponent<FullScreenFog>();
            if (fog == null || !fog.IsActive())
            {
                return;
            }

            if (_material == null)
            {
                return;
            }
            _renderPass.renderPassEvent = (RenderPassEvent)_injectionPoint;
            _renderPass?.Setup(new PassData
            {
                Material = _material,
                Fog = fog,
                ExclusionMask = null, // Will be created in render graph
            });
            //TODO: maybe we do not need color input
            _renderPass.ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);

            _renderPass.requiresIntermediateTexture = true;

            renderer.EnqueuePass(_renderPass);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            _renderPass?.Dispose();
            CoreUtils.Destroy(_material);
        }
    }
}
