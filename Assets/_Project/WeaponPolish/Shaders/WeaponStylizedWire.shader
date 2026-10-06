Shader "Universal Render Pipeline/Stylized Weapon Wire"
{
 Properties
 {
  [MainTexture] _BaseMap("Existing Base Color",2D)="white"{}
  [MainColor] _BaseColor("Texture Tint",Color)=(1,1,1,1)
  _WireStrength("Topology Strength",Range(0,1))=1
  _WireThickness("Topology Thickness (pixels)",Range(0,2))=0.55
  _WireTint("Wire Colour",Color)=(0,0,0,1)
  _CelThreshold("Cel Shadow Threshold",Range(0,1))=0.42
  _ShadowStrength("Shadow Strength",Range(0,1))=0.32
  _OutlineThickness("Outline Thickness (pixels)",Range(0,4))=0.9
  _OutlineColor("Outline Color",Color)=(0.025,0.021,0.035,1)
  _EmissionStrength("Arcane Emission Strength",Range(0,5))=1.2
  _EmissionMask("Painted Energy Mask",2D)="black"{}
  [HDR] _EmissionColor("Energy Colour",Color)=(1,1,1,1)
  _FaceWireStrength("Face Topology Multiplier",Range(0,1))=0
 }
 SubShader
 {
  Tags{"RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry"}
  Pass
  {
   Name "StylizedForward"
   Tags{"LightMode"="UniversalForward"}
   Cull Off ZWrite On
   HLSLPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.5
   #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
   #pragma multi_compile_fragment _ _SHADOWS_SOFT
   #pragma multi_compile_fog
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
   TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap); TEXTURE2D(_EmissionMask); SAMPLER(sampler_EmissionMask);
   CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST,_BaseColor,_WireTint,_OutlineColor,_EmissionColor;
    float _WireStrength,_WireThickness,_CelThreshold,_ShadowStrength,_OutlineThickness,_EmissionStrength,_FaceWireStrength;
   CBUFFER_END
   struct Attributes{float4 positionOS:POSITION;float3 normalOS:NORMAL;float2 uv:TEXCOORD0;float4 wireData:COLOR;};
   struct Varyings{float4 positionCS:SV_POSITION;float3 positionWS:TEXCOORD0;float3 normalWS:TEXCOORD1;float2 uv:TEXCOORD2;float3 bary:TEXCOORD3;float fog:TEXCOORD4;float4 polygonEdges:TEXCOORD5;};
   Varyings vert(Attributes i)
   {
    Varyings o;VertexPositionInputs p=GetVertexPositionInputs(i.positionOS.xyz);
    o.positionCS=p.positionCS;o.positionWS=p.positionWS;o.normalWS=TransformObjectToWorldNormal(i.normalOS);
    o.uv=TRANSFORM_TEX(i.uv,_BaseMap);
    o.bary=float3(i.wireData.rg,1-i.wireData.r-i.wireData.g);
    float bits=floor(i.wireData.b+0.5);
    o.polygonEdges=float4(fmod(bits,2),fmod(floor(bits*0.5),2),fmod(floor(bits*0.25),2),i.wireData.a);
    o.fog=ComputeFogFactor(p.positionCS.z);return o;
   }
   half4 frag(Varyings i,FRONT_FACE_TYPE facing:FRONT_FACE_SEMANTIC):SV_Target
   {
    half3 base=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).rgb*_BaseColor.rgb;
    half3 normal=normalize(i.normalWS)*IS_FRONT_VFACE(facing,1,-1);
    Light sun=GetMainLight(TransformWorldToShadowCoord(i.positionWS));
    half ndl=saturate(dot(normal,sun.direction));
    half band=ndl<_CelThreshold ? 1-_ShadowStrength : (ndl<_CelThreshold+0.30 ? 1-_ShadowStrength*0.40 : 1);
    half shadow=lerp(1-_ShadowStrength,1,sun.shadowAttenuation);
    half3 lighting=saturate(SampleSH(normal)*0.55+sun.color*0.80+0.20)*band*shadow;
    half energy=SAMPLE_TEXTURE2D(_EmissionMask,sampler_EmissionMask,i.uv).r;
    float3 derivative=max(fwidth(i.bary.xyz),float3(0.0001,0.0001,0.0001));
    float3 heights=1/derivative;
    float3 pixels=i.bary.xyz/derivative;
    // Keep dense, subpixel polygons from filling the entire character with ink.
    float3 thickness=min(float3(_WireThickness,_WireThickness,_WireThickness),heights*0.08);
    float3 feather=min(float3(0.35,0.35,0.35),heights*0.06);
    float3 edges=(1-smoothstep(thickness,thickness+feather,pixels))*i.polygonEdges.rgb*smoothstep(0.65,2.0,heights);
    float edge=max(edges.x,max(edges.y,edges.z));
    half strength=_WireStrength*lerp(_FaceWireStrength,1,saturate(i.polygonEdges.a))*step(-0.001,min(i.bary.x,min(i.bary.y,i.bary.z)));
    half3 surface=base*lighting+(base+_EmissionColor.rgb*0.6)*energy*_EmissionStrength;
    half3 col=lerp(surface,_WireTint.rgb,edge*strength);
    return half4(MixFog(col,i.fog),1);
   }
   ENDHLSL
  }
  UsePass "Universal Render Pipeline/Lit/ShadowCaster"
  UsePass "Universal Render Pipeline/Lit/DepthOnly"
 }
}

