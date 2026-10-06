Shader "Souls/Afterimage"
{
    // Dodge afterimage for a baked player pose. Reads the same COLOR wire
    // metadata as CharacterPolish/Stylized Wire, so each ghost shows the
    // player's own polygon wire. Banded fresnel = ink silhouette + bright rim +
    // faint flat fill (the environment's ink-on-crease / banded-light language).
    // Fading ghosts break up in horizontal slices instead of a soft melt.
    Properties
    {
        _Tint ("Fill (a = fill opacity)", Color) = (0.55,0.21,1,0.16)
        _Rim ("Rim + wire", Color) = (0.85,0.7,1,1)
        _Ink ("Ink edge", Color) = (0.05,0.02,0.09,1)
        _Fade ("Fade", Range(0,1)) = 1
        _Dissolve ("Slice dissolve", Range(0,1)) = 0
        _Slices ("Slices per metre", Float) = 9
        _WireThickness ("Wire thickness (pixels)", Range(0,2)) = 0.6
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _Tint, _Rim, _Ink;
            half _Fade, _Dissolve, _Slices, _WireThickness;
        CBUFFER_END
        // > 0 keeps the fragment; each slice empties bottom-up as _Dissolve rises.
        float SliceMask(float3 positionWS) { return frac(positionWS.y * _Slices) - _Dissolve; }
        ENDHLSL

        // Depth-only pre-pass so the ghost resolves as ONE silhouette (limbs that
        // overlap the body don't double up their alpha).
        Pass
        {
            Name "AfterimageDepth"
            Tags { "LightMode"="SRPDefaultUnlit" }
            ZWrite On ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            Varyings Vert(Attributes i)
            {
                Varyings o; VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS; return o;
            }
            half4 Frag(Varyings i) : SV_Target { clip(SliceMask(i.positionWS)); return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "AfterimageColour"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off ZTest LEqual Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_fog
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 wire : COLOR; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1;
                float3 bary : TEXCOORD2; float4 edges : TEXCOORD3; float fog : TEXCOORD4;
            };
            Varyings Vert(Attributes i)
            {
                Varyings o; VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                // rg = barycentric corner, b = visible-edge bits, a = 1 off the face.
                o.bary = float3(i.wire.rg, 1 - i.wire.r - i.wire.g);
                float bits = floor(i.wire.b + 0.5);
                o.edges = float4(fmod(bits, 2), fmod(floor(bits * 0.5), 2), fmod(floor(bits * 0.25), 2), i.wire.a);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                clip(SliceMask(i.positionWS));
                float3 n = normalize(i.normalWS);
                float3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);
                half f = 1 - saturate(abs(dot(n, v)));
                half ink = step(0.84, f);
                half rim = step(0.55, f) * (1 - ink);
                // Polygon wire, same metric as the player shader (subpixel edges suppressed).
                float3 d = max(fwidth(i.bary), float3(0.0001, 0.0001, 0.0001));
                float3 heights = 1 / d;
                float3 px = i.bary / d;
                float3 thick = min(float3(_WireThickness, _WireThickness, _WireThickness), heights * 0.08);
                float3 e = (1 - smoothstep(thick, thick + 0.35, px)) * i.edges.rgb * smoothstep(0.65, 2.0, heights);
                half wire = saturate(max(e.x, max(e.y, e.z)) * saturate(i.edges.a)
                                     * step(-0.001, min(i.bary.x, min(i.bary.y, i.bary.z))));
                half3 col = _Tint.rgb; half a = _Tint.a;
                col = lerp(col, _Rim.rgb, wire); a = lerp(a, 0.7 * _Rim.a, wire);
                col = lerp(col, _Rim.rgb, rim);  a = lerp(a, _Rim.a, rim);
                col = lerp(col, _Ink.rgb, ink);  a = lerp(a, 0.85 * _Ink.a, ink);
                col = MixFog(col, i.fog);
                return half4(col, a * _Fade);
            }
            ENDHLSL
        }
    }
}
