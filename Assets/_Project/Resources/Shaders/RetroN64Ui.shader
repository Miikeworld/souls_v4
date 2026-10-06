// "Soft remaster" grade — displays the camera's low-res RenderTexture through a
// fullscreen RawImage: Bayer dither on the texel grid, gentle colour quantize,
// warm palette lift, chroma fringe, vignette, optional scanlines. Plain
// CG/uGUI so it renders reliably on a ScreenSpaceOverlay canvas under the HUD.
Shader "Hidden/NEA/RetroN64Ui"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Levels ("Colour Levels", Float) = 48
        _Dither ("Dither Amount", Range(0, 2)) = 0.2
        _Saturation ("Saturation", Range(0, 2)) = 1.15
        _Warmth ("Warmth", Range(0, 0.15)) = 0.05
        _Vignette ("Vignette", Range(0, 1)) = 0.3
        _Fringe ("Chroma Fringe", Range(0, 4)) = 0.5
        _Scanline ("Scanlines", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest Always Lighting Off
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Levels, _Dither, _Saturation, _Warmth;
            float _Vignette, _Fringe, _Scanline;

            static const float Bayer4x4[16] =
            {
                 0,  8,  2, 10,
                12,  4, 14,  6,
                 3, 11,  1,  9,
                15,  7, 13,  5
            };

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 res = _MainTex_TexelSize.zw;
                float2 lp = floor(i.uv * res); // dither cell = one RT texel

                // Chroma fringe — faint colour split like composite video bleed
                float2 ca = (i.uv - 0.5) * _Fringe * _MainTex_TexelSize.xy;
                fixed4 c = tex2D(_MainTex, i.uv);
                c.r = tex2D(_MainTex, i.uv + ca).r;
                c.b = tex2D(_MainTex, i.uv - ca).b;

                float d = Bayer4x4[fmod(lp.y, 4.0) * 4.0 + fmod(lp.x, 4.0)] / 16.0;
                c.rgb = saturate(floor(saturate(c.rgb) * _Levels + d * _Dither) / _Levels);

                float l = dot(c.rgb, fixed3(0.299, 0.587, 0.114));
                c.rgb = saturate(lerp(float3(l, l, l), c.rgb, _Saturation)
                    + float3(_Warmth, _Warmth * 0.55, 0));

                c.rgb *= 1 - _Scanline * (0.5 + 0.5 * sin(i.uv.y * res.y * 3.14159));
                c.rgb *= 1 - _Vignette * smoothstep(0.35, 0.95, length(i.uv - 0.5) * 1.4142);
                return fixed4(c.rgb, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
