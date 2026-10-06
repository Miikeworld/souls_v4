Shader "CharacterPolish/Silhouette"
{
 Properties{_OutlineThickness("Outline Thickness",Range(0,4))=0.9 _OutlineColor("Outline Color",Color)=(0.025,0.021,0.035,1)}
 SubShader
 {
  Tags{"RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry+1"}
  Pass
  {
   Tags{"LightMode"="SRPDefaultUnlit"}
   Cull Front ZWrite Off
   HLSLPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   CBUFFER_START(UnityPerMaterial)
    float4 _OutlineColor;float _OutlineThickness;
   CBUFFER_END
   struct Attributes{float4 p:POSITION;float3 n:NORMAL;};
   float4 vert(Attributes i):SV_POSITION
   {
    float3 world=TransformObjectToWorld(i.p.xyz);float3 n=TransformObjectToWorldNormal(i.n);
    float4 clip=TransformWorldToHClip(world);float2 direction=TransformWorldToHClip(world+n).xy/max(0.001,TransformWorldToHClip(world+n).w)-clip.xy/max(0.001,clip.w);
    clip.xy+=normalize(direction+0.000001)*(_OutlineThickness*2/_ScaledScreenParams.xy)*clip.w;
    return clip;
   }
   half4 frag():SV_Target{return _OutlineColor;}
   ENDHLSL
  }
 }
}
