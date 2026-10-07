/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 모바일 배치 렌더 경로의 전역 선택값과 공유 shader를 보관합니다.


using UnityEngine;
using UnityEngine.Rendering;


namespace Live2D.Cubism.Rendering
{
    public static class CubismBatchedRendering
    {
        public static bool Enabled = true;

        public static bool DrawToCameraTargetDirectly = false;

        /// true면 배치 모델이 전부 "over"만 쓰는 프레임에서도 direct 경로를 자동으로 탑니다.
        /// 그 경우엔 지킬 시맨틱 차이가 없습니다. "over"는 결합법칙이 성립해 버퍼를 거친 합성과
        /// 카메라 타깃 직접 합성의 픽셀이 같습니다. false면 DrawToCameraTargetDirectly로만 켜집니다.
        public static bool AutoDrawToCameraTargetDirectly = true;

        /// true면 월드 AABB가 카메라 절두체를 벗어난 모델은 마스크 아틀라스 패스도 배치도 기록하지 않습니다.
        /// AABB는 canvas 사각형이 아니라 Drawable의 현재 정점에서 합치므로,
        /// canvas를 벗어난 변형 파츠가 화면 가장자리에서 튀지 않습니다.
        /// false면 위치와 무관하게 모든 배치 모델을 제출합니다.
        public static bool CullOffscreenModels = true;

        public static bool UseTextureArray = true;

        public static int TextureArrayMinimumSystemMemoryMegabytes = 4096;

        public static float TextureArrayActivationDelaySeconds = 3.0f;

        internal static bool TextureArrayAllowed
        {
            get
            {
                return UseTextureArray
                       && (TextureArrayMinimumSystemMemoryMegabytes <= 0
                           || SystemInfo.systemMemorySize >= TextureArrayMinimumSystemMemoryMegabytes);
            }
        }

        internal const MeshUpdateFlags UpdateFlags =
            MeshUpdateFlags.DontValidateIndices
            | MeshUpdateFlags.DontNotifyMeshUsers
            | MeshUpdateFlags.DontRecalculateBounds
            | MeshUpdateFlags.DontResetBoneBounds;


        private static Shader _shader;

        public static Shader Shader
        {
            get
            {
                if (_shader == null)
                {
                    _shader = Resources.Load<Shader>("Live2D/Cubism/Shaders/BlendMode/UnlitBatched");
                }

                if (_shader == null)
                {
                    _shader = UnityEngine.Shader.Find("Live2D Cubism/Batched");
                }

                return _shader;
            }
        }
    }
}
