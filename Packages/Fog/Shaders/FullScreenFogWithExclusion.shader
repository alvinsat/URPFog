Shader "URPFog/FullScreenFogWithExclusion"
{
    Properties
    {
        [HDR] _Color ("Fog Color", Color) = (0.5, 0.5, 0.5, 1)
        _MainParams ("Main Params", Vector) = (0, 10, 0, 0)
        _NoiseParams ("Noise Params", Vector) = (0.5, 1, 1, 1)
        _NoiseTex ("Noise Texture", 2D) = "white" {}
        _ExclusionMask ("Exclusion Mask", 2D) = "white" {}
        _ExclusionSmoothing ("Exclusion Smoothing", Float) = 0.1
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType"="Opaque" 
            "RenderPipeline"="UniversalPipeline"
        }
        
        Pass
        {
            Name "FullScreenFog"
            
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #pragma multi_compile_local _ _MODE_DEPTH _MODE_DISTANCE _MODE_HEIGHT
            #pragma multi_compile_local _ _DENSITYMODE_LINEAR _DENSITYMODE_EXPONENTIAL _DENSITYMODE_EXPONENTIALSQUARED
            #pragma multi_compile_local _ _NOISEMODE_OFF _NOISEMODE_PROCEDURAL _NOISEMODE_TEXTURE
            #pragma multi_compile_local _ _EXCLUSION_ZONES
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            
            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            
            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            
            TEXTURE2D(_ExclusionMask);
            SAMPLER(sampler_ExclusionMask);
            
            TEXTURE2D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);
            
            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float4 _MainParams;
            float4 _NoiseParams;
            float4 _BlitScaleBias;
            float _ExclusionSmoothing;
            CBUFFER_END
            
            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                
                // Create full screen triangle
                float4 pos = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv = GetFullScreenTriangleTexCoord(input.vertexID);
                
                output.positionCS = pos;
                output.texcoord = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                
                return output;
            }
            
            float CalculateFogFactor(float2 uv, float3 worldPos)
            {
                float fogFactor = 0;
                
                #if defined(_MODE_DEPTH)
                    float depth = SampleSceneDepth(uv);
                    float linearDepth = LinearEyeDepth(depth, _ZBufferParams);
                    float fogDistance = linearDepth;
                #elif defined(_MODE_DISTANCE)
                    float depth = SampleSceneDepth(uv);
                    float3 worldPosition = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                    float fogDistance = distance(_WorldSpaceCameraPos, worldPosition);
                #elif defined(_MODE_HEIGHT)
                    float depth = SampleSceneDepth(uv);
                    float3 worldPosition = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                    float fogDistance = worldPosition.y;
                #else
                    float fogDistance = 0;
                #endif
                
                #if defined(_DENSITYMODE_LINEAR)
                    fogFactor = saturate((fogDistance - _MainParams.x) * _MainParams.y);
                #elif defined(_DENSITYMODE_EXPONENTIAL)
                    fogFactor = 1.0 - exp(-fogDistance * _MainParams.y);
                #elif defined(_DENSITYMODE_EXPONENTIALSQUARED)
                    float f = fogDistance * _MainParams.y;
                    fogFactor = 1.0 - exp(-f * f);
                #endif
                
                return saturate(fogFactor);
            }
            
            float SampleNoise(float2 uv, float3 worldPos)
            {
                float noise = 1.0;
                
                #if defined(_NOISEMODE_TEXTURE)
                    float2 noiseUV = uv * _NoiseParams.y + _Time.y * _NoiseParams.zw;
                    noise = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, noiseUV).r;
                    noise = lerp(1.0, noise, _NoiseParams.x);
                #elif defined(_NOISEMODE_PROCEDURAL)
                    // Simple procedural noise
                    float2 noiseUV = worldPos.xz * _NoiseParams.y + _Time.y * _NoiseParams.zw;
                    noise = frac(sin(dot(noiseUV, float2(12.9898, 78.233))) * 43758.5453);
                    noise = lerp(1.0, noise, _NoiseParams.x);
                #endif
                
                return noise;
            }
            
            float4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv);
                
                // Calculate fog
                float depth = SampleSceneDepth(uv);
                float3 worldPos = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                
                float fogFactor = CalculateFogFactor(uv, worldPos);
                float noise = SampleNoise(uv, worldPos);
                
                fogFactor *= noise;
                fogFactor *= _Color.a;
                
                #if defined(_EXCLUSION_ZONES)
                    // Sample exclusion mask
                    float exclusionMask = SAMPLE_TEXTURE2D(_ExclusionMask, sampler_ExclusionMask, uv).r;
                    
                    // Apply smoothing to exclusion mask edges
                    if (_ExclusionSmoothing > 0)
                    {
                        exclusionMask = smoothstep(0.5 - _ExclusionSmoothing * 0.5, 0.5 + _ExclusionSmoothing * 0.5, exclusionMask);
                    }
                    
                    // Reduce fog factor in excluded areas
                    fogFactor *= exclusionMask;
                #endif
                
                // Apply fog
                return lerp(color, _Color, fogFactor);
            }
            ENDHLSL
        }
    }
    
    Fallback Off
}