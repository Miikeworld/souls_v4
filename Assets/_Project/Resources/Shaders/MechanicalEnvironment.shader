Shader "Souls/Mechanical Environment"
{
 Properties
 {
  [MainTexture] _BaseMap("Surface",2D)="white"{}
  [MainColor] _BaseColor("Surface Tint",Color)=(1,1,1,1)
  _Ink("Structural Ink",Color)=(0.012,0.009,0.018,1)
  _LineStrength("Crease Strength",Range(0,1))=0.7
  _LineWidth("Crease Width (render pixels)",Range(0,2))=0.65
  _Saturation("Surface Saturation",Range(0,1.5))=0.7
  _EmissionMap("Authored Glow Mask",2D)="black"{}
  _EmissionColor("Authored Glow",Color)=(0,0,0,1)
  _Cull("Culling",Float)=2
  _Cutoff("Alpha Cutoff",Range(0,1))=0.5
 }
 SubShader
 {
  Tags{"RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry"}
  Pass
  {
   Name "Forward" Tags{"LightMode"="UniversalForward"}
   Cull [_Cull] ZWrite On
   HLSLPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
   #pragma multi_compile_fragment _ _SHADOWS_SOFT
   #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
   #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
   #pragma multi_compile_fog
   #pragma shader_feature_local_fragment _ALPHATEST_ON
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
   TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
   TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
   CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST,_BaseColor,_Ink,_EmissionColor;
    float _LineStrength,_LineWidth,_Saturation,_Cutoff,_Cull;
   CBUFFER_END
   struct A{float4 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD0;float3 bary:TEXCOORD3;float3 crease:TEXCOORD4;};
   struct V{float4 p:SV_POSITION;float3 world:TEXCOORD0;float3 normal:TEXCOORD1;float2 uv:TEXCOORD2;float3 bary:TEXCOORD3;float3 crease:TEXCOORD4;float fog:TEXCOORD5;half3 vertexLight:TEXCOORD6;};
   V vert(A i)
   {
    V o;VertexPositionInputs p=GetVertexPositionInputs(i.p.xyz);
    o.p=p.positionCS;o.world=p.positionWS;o.normal=TransformObjectToWorldNormal(i.n);
    o.uv=TRANSFORM_TEX(i.uv,_BaseMap);o.bary=i.bary;o.crease=i.crease;
    o.vertexLight=VertexLighting(o.world,o.normal);
    o.fog=ComputeFogFactor(p.positionCS.z);return o;
   }
   half4 frag(V i):SV_Target
   {
    half4 tex=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv);
    #ifdef _ALPHATEST_ON
     clip(tex.a*_BaseColor.a-_Cutoff);
    #endif
    half3 base=tex.rgb*_BaseColor.rgb;
    half grey=dot(base,half3(0.2126,0.7152,0.0722));
    base=lerp(grey.xxx,base,_Saturation);
    half3 n=normalize(i.normal);
    Light sun=GetMainLight(TransformWorldToShadowCoord(i.world));
    half ndl=saturate(dot(n,sun.direction))*sun.shadowAttenuation;
    // Broad bands keep neighbouring props in a shared lighting language.
    half band=ndl<0.18?0.12:(ndl<0.6?0.48:0.92);
    half3 ambient=max(SampleSH(n)*0.6,half3(0.075,0.065,0.095));
    half3 light=ambient+sun.color*band;
    #if defined(_ADDITIONAL_LIGHTS) || USE_CLUSTER_LIGHT_LOOP
     InputData inputData=(InputData)0;
     inputData.positionWS=i.world;
     inputData.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.p);
     uint count=GetAdditionalLightsCount();
     LIGHT_LOOP_BEGIN(count)
      Light l=GetAdditionalLight(lightIndex,i.world);
      half d=saturate(dot(n,l.direction));
      light+=l.color*l.distanceAttenuation*l.shadowAttenuation*(d<0.2?0:(d<0.65?0.4:0.8));
     LIGHT_LOOP_END
    #elif defined(_ADDITIONAL_LIGHTS_VERTEX)
     light+=i.vertexLight;
    #endif
    half3 col=base*light+SAMPLE_TEXTURE2D(_EmissionMap,sampler_EmissionMap,i.uv).rgb*_EmissionColor.rgb;
    float3 derivative=max(fwidth(i.bary),0.0001);
    float3 heights=1/derivative;
    float3 width=min(_LineWidth.xxx,heights*0.06);
    float3 edge=(1-smoothstep(width,width+0.45,i.bary/derivative))*i.crease*smoothstep(2,5,heights);
    float distanceFade=1-smoothstep(12,55,distance(_WorldSpaceCameraPos,i.world));
    col=lerp(col,_Ink.rgb,max(edge.x,max(edge.y,edge.z))*_LineStrength*distanceFade);
    return half4(MixFog(col,i.fog),1);
   }
   ENDHLSL
  }
  UsePass "Universal Render Pipeline/Lit/ShadowCaster"
  UsePass "Universal Render Pipeline/Lit/DepthOnly"
  UsePass "Universal Render Pipeline/Lit/DepthNormals"
 }
}
