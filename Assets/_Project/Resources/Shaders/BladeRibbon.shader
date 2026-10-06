Shader "NEA/BladeRibbon"
{
 SubShader {
 Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
 Pass {
 Blend SrcAlpha OneMinusSrcAlpha
 ZWrite Off
 Cull Off
 HLSLPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 struct Attributes { float4 positionOS:POSITION; half4 color:COLOR; };
 struct Varyings { float4 positionCS:SV_POSITION; half4 color:COLOR; };
 Varyings vert(Attributes v) { Varyings o; o.positionCS=TransformObjectToHClip(v.positionOS.xyz); o.color=v.color; return o; }
 half4 frag(Varyings i):SV_Target { return half4(i.color.rgb, min(i.color.a, 0.55h)); }
 ENDHLSL
 }
 }
}
