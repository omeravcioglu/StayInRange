// Worn by a level object while it stands between the camera and the player: it dissolves into
// blotches with a glowing edge until it is gone, and comes back the same way once the view is clear.
// CameraOccluderFade puts it on (copying the object's own texture, colour, normal map and alpha
// clipping) and drives _Dissolve. Lit like URP's Simple Lit, so the switch itself does not flash; the
// shadow stays. _OCCLUDER_TRIPLANAR maps the colour texture the way ToonScapes' Stone does - projected
// along the object's own axes instead of read at the mesh's UV - so a faded rock keeps its stone's scale.
//
// The normal-map, alpha-clip and triplanar keywords are multi_compile, not shader_feature: every
// material wearing this is made at run time, so no material in a build asks for those variants, and as
// shader_features they would be stripped from the player - a normal-mapped wall would turn flat, a
// cut-out banner solid and a rock's stone change scale, the moment it started to fade (only ever in a
// build, never in the editor).
Shader "CollarCali/Occluder Dissolve"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Cutoff("Alpha Clipping", Range(0.0, 1.0)) = 0.5
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.3
        _SpecColor("Specular Color", Color) = (0.2, 0.2, 0.2, 0.5)
        [NoScaleOffset] _BumpMap("Normal Map", 2D) = "bump" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0)
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "white" {}

        _TriplanarScale("Triplanar Scale (per object unit)", Float) = 1
        _TriplanarFalloff("Triplanar Falloff", Float) = 2

        _Dissolve("Dissolve", Range(0, 1)) = 0
        _NoiseScale("Noise Scale (per metre)", Float) = 2.5
        [HDR] _EdgeColor("Edge Color", Color) = (0.6, 1.4, 2.0, 1)
        _EdgeWidth("Edge Width", Range(0, 0.3)) = 0.08

        [HideInInspector] _Cull("__cull", Float) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "SimpleLit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex LitPassVertexSimple
            #pragma fragment OccluderForwardFragment

            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile_local_fragment _ _OCCLUDER_TRIPLANAR

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #define BUMP_SCALE_NOT_SUPPORTED 1

            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitForwardPass.hlsl"
            #include "OccluderDissolve.hlsl"

            // URP 17's InitializeSimpleLitSurfaceData (SimpleLitInput.hlsl), but for where the colour
            // texture is read: with _OCCLUDER_TRIPLANAR it is projected along the object's own axes
            // (OccluderTriplanar), at the object-space point and normal of the pixel, instead of at the
            // mesh's UV. The material's own alpha clipping happens here too (_ALPHATEST_ON, _Cutoff).
            void OccluderSurfaceData(Varyings input, out SurfaceData outSurfaceData)
            {
                outSurfaceData = (SurfaceData)0;

                #if defined(_OCCLUDER_TRIPLANAR)
                    half4 albedoAlpha = OccluderTriplanar(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap),
                        TransformWorldToObject(input.positionWS), TransformWorldToObjectNormal(input.normalWS.xyz));
                #else
                    half4 albedoAlpha = SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
                #endif
                outSurfaceData.alpha = albedoAlpha.a * _BaseColor.a;
                outSurfaceData.alpha = AlphaDiscard(outSurfaceData.alpha, _Cutoff);

                outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
                outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

                half4 specularSmoothness = SampleSpecularSmoothness(input.uv, outSurfaceData.alpha, _SpecColor, TEXTURE2D_ARGS(_SpecGlossMap, sampler_SpecGlossMap));
                outSurfaceData.metallic = 0.0; // unused
                outSurfaceData.specular = specularSmoothness.rgb;
                outSurfaceData.smoothness = specularSmoothness.a;
                outSurfaceData.normalTS = SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));
                outSurfaceData.occlusion = 1.0;
                outSurfaceData.emission = SampleEmission(input.uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));
            }

            // URP 17's LitPassFragmentSimple (SimpleLitForwardPass.hlsl) - the same lighting, fog, shadows
            // and rendering layers - with the dissolve's clip first, the surface from OccluderSurfaceData,
            // and the dissolving edge's glow added last, over the fog.
            void OccluderForwardFragment(
                Varyings input
                , out half4 outColor : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out float4 outRenderingLayers : SV_Target1
            #endif
            )
            {
                half edge = OccluderDissolveClip(input.positionWS);

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                SurfaceData surfaceData;
                OccluderSurfaceData(input, surfaceData);

            #ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(input.positionCS);
            #endif

                InputData inputData;
                InitializeInputData(input, surfaceData.normalTS, inputData);
                SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

            #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
            #endif

                InitializeBakedGIData(input, inputData);

                half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));

                outColor = color;
                outColor.rgb += _EdgeColor.rgb * edge;

            #ifdef _WRITE_RENDERING_LAYERS
                uint renderingLayers = GetMeshRenderingLayer();
                outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
            #endif
            }
            ENDHLSL
        }

        // The shadow stays while the object is faded: the light on the scene does not change. A cut-out
        // object's shadow keeps its holes (URP's shadow pass clips at _Cutoff under _ALPHATEST_ON).
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_local _ _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex OccluderDepthVertex
            #pragma fragment OccluderDepthFragment
            #pragma multi_compile_local _ _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "OccluderDissolve.hlsl"

            struct OccluderDepthAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct OccluderDepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                #if defined(_ALPHATEST_ON)
                    float2 uv : TEXCOORD2;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            OccluderDepthVaryings OccluderDepthVertex(OccluderDepthAttributes input)
            {
                OccluderDepthVaryings output = (OccluderDepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = (half3)TransformObjectToWorldNormal(input.normalOS);
                #if defined(_ALPHATEST_ON)
                    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                #endif
                return output;
            }

            half OccluderDepthFragment(OccluderDepthVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                OccluderDissolveClip(input.positionWS);
                #if defined(_ALPHATEST_ON)
                    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                #endif
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex OccluderDepthNormalsVertex
            #pragma fragment OccluderDepthNormalsFragment
            #pragma multi_compile_local _ _ALPHATEST_ON
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "OccluderDissolve.hlsl"

            struct OccluderDepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct OccluderDepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                #if defined(_ALPHATEST_ON)
                    float2 uv : TEXCOORD2;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            OccluderDepthNormalsVaryings OccluderDepthNormalsVertex(OccluderDepthNormalsAttributes input)
            {
                OccluderDepthNormalsVaryings output = (OccluderDepthNormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = (half3)NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
                #if defined(_ALPHATEST_ON)
                    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                #endif
                return output;
            }

            half4 OccluderDepthNormalsFragment(OccluderDepthNormalsVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                OccluderDissolveClip(input.positionWS);
                #if defined(_ALPHATEST_ON)
                    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                #endif
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
