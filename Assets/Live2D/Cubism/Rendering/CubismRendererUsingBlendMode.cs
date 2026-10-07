/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// Cubism 5.3 이후의 마스크·오프스크린·복합 blend 렌더 단계를 구현합니다.
// 각 Drawable의 중간 RenderTexture와 shader property 상태 수명을 관리합니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering.URP;
using Live2D.Cubism.Rendering.Util;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;


namespace Live2D.Cubism.Rendering
{
    public partial class CubismRenderer
    {
        #region Values

        private static Vector4 HighPrecisionMaskTile = new Vector4(
            0, // R 채널
            0, // atlas 열
            0, // atlas 행
            1 // tile 크기
        );

        internal static Vector3[] OffscreenVertices = new Vector3[]
        {
            new Vector3(-1, -1, 0),
            new Vector3(1, -1, 0),
            new Vector3(1, 1, 0),
            new Vector3(-1, 1, 0)
        };

        internal static Vector2[] OffscreenUVs = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };

        internal static int[] OffscreenTriangle = new int[] { 0, 1, 2, 0, 2, 3 };

        [SerializeField, HideInInspector]
        private CubismRenderer[] _masks;

        private Vector4 _maskTransform;

        private Bounds _maskBounds;

#if UNITY_EDITOR
        private bool _haveMasksNull;
#endif

        public int DrawObjectUnmanagedIndex { get; set; }

        public CubismOffscreen Offscreen { get; set; }


        [SerializeField]
        public BlendTypes.ColorBlend ColorBlendType;

        [SerializeField]
        public BlendTypes.AlphaBlend AlphaBlendType;

        [SerializeField]
        public CubismModelTypes.DrawObjectType DrawObjectType;

        internal bool IsLastDrawObjectInModel;

        private RenderTexture _offscreenFrameBuffer;

        public RenderTexture OffscreenFrameBuffer
        {
            get
            {
                if (!_offscreenFrameBuffer
                    && RenderController.CurrentFrameBuffer
                    && (RenderController.OffscreenRenderers?.Length ?? 0) > 0)
                {
                    _offscreenFrameBuffer = CubismOffscreenRenderTextureManager.GetInstance().GetOffscreenRenderTexture(
                        RenderController.CurrentFrameBuffer);
                }

                return _offscreenFrameBuffer;
            }

            set
            {
                if (_offscreenFrameBuffer != null)
                {
                    CubismOffscreenRenderTextureManager.GetInstance().StopUsingRenderTexture(RenderController, _offscreenFrameBuffer);
                    _offscreenFrameBuffer = null;
                }
                _offscreenFrameBuffer = value;
            }
        }

        [SerializeField, HideInInspector]
        private Vector4 _offsetScale = new Vector4(0, 0, 1, 1);

        [SerializeField, HideInInspector]
        private Vector4 _quaternion = Vector4.zero;

        [SerializeField, HideInInspector]
        private float _zOffset = 0.0f;

        private Mesh _offscreenMesh;

        private int _previousOffscreenUnmanagedIndex;

        public bool SkipRendering
        {
            get;
            set;
        }

        #endregion

        #region Interface For CubismRenderController

        /// 입력: newRenderOrder(int); 반환: 없음.
        internal void SetDrawObjectRenderOrder(int newRenderOrder)
        {
            if (RenderOrder == newRenderOrder) return;

            RenderOrder = newRenderOrder;

            ApplySorting();
        }

        #endregion

        internal Vector3 LastDirection;

        /// 입력: cameraPosition(Vector3); 반환: bool.
        internal bool DidUpdateDirectionFromLastSorted(Vector3 cameraPosition)
        {
            return LastDirection != (transform.position - cameraPosition);
        }

        internal float DistanceToCamera;

        /// 입력: cameraPosition(Vector3), cameraForward(Vector3); 반환: 없음.
        internal void CalculateDistanceToCamera(Vector3 cameraPosition, Vector3 cameraForward)
        {
            // 카메라에서 이 renderer 월드 위치로 향하는 벡터입니다.
            var directionToRenderer = transform.position - cameraPosition;

            LastDirection = directionToRenderer;

            // 카메라 forward 축에 투영해 깊이 정렬에 쓸 성분만 남깁니다.
            var projection = Vector3.Project(directionToRenderer, cameraForward);

            // 투영 벡터 길이가 카메라 forward 축을 따라 떨어진 깊이 거리입니다.
            // 투영은 directionToRenderer와 cameraForward의 내적을 forward에 곱해 구합니다.
            // 따라서 거리는 그 내적의 절댓값과 같습니다.
            DistanceToCamera = projection.magnitude;
        }

        private MaterialPropertyBlock _propertyBlock;

        private MaterialPropertyBlock PropertyBlock
        {
            get
            {
                // 처음 필요한 시점에만 block을 만들어 이후 frame에는 재사용합니다.
                if (_propertyBlock == null)
                {
                    _propertyBlock = new MaterialPropertyBlock();
                }


                return _propertyBlock;
            }
        }

        /// 입력: passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        private void ApplyBlendedRenderTexture(CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            if (!RenderController?.CurrentFrameBuffer)
            {
                return;
            }

            var property = PropertyBlock;

            MeshRenderer.GetPropertyBlock(property);

            WriteBlendedRenderTexture(property, passData);

            MeshRenderer.SetPropertyBlock(property);
        }

        /// 입력: property(MaterialPropertyBlock), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        private void WriteBlendedRenderTexture(MaterialPropertyBlock property, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            if (!RenderController?.CurrentFrameBuffer)
            {
                return;
            }

            property.SetTexture(CubismShaderVariables.RenderTexture, passData.CommonTemporaryTextureHandle);
        }

        /// 입력: buffer(CommandBuffer), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        public void DrawObject(CommandBuffer buffer, CubismRenderPassFeature.CubismRenderPass.PassData passData)// 이전 RenderTexture 인자는 사용하지 않습니다.
        {
            if (!MeshRenderer)
            {
                return;
            }

            switch (DrawObjectType)
            {
                case CubismModelTypes.DrawObjectType.Offscreen:
                    // 이 offscreen part가 다음 Drawable을 쌓을 framebuffer를 준비합니다.
                    SetOffscreen(buffer, passData);
                    break;
                case CubismModelTypes.DrawObjectType.Drawable:
                    // 일반 Drawable의 mask·blend 경로 draw를 기록합니다.
                    DrawDrawable(buffer, passData);
                    break;
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void ApplyTransform()
        {
            var property = PropertyBlock;
            MeshRenderer.GetPropertyBlock(property);

            WriteTransform(property);

            MeshRenderer.SetPropertyBlock(property);
        }

        /// 입력: property(MaterialPropertyBlock); 반환: 없음.
        private void WriteTransform(MaterialPropertyBlock property)
        {
            // 모델은 AvatarRig 같은 부모가 위치·scale을 소유할 수 있으므로 local 값만 쓰면 씬 루트가 아닐 때 틀립니다.
            // 배치 경로 RecordMainDraws와 같은 월드 합성 기준을 써야 Edit legacy와 Play batch의 위치·크기가 일치합니다.
            var controllerTransform = RenderController.transform;
            var worldPosition = controllerTransform.position;
            var worldScale = controllerTransform.lossyScale;

            // controller 월드 위치·scale과 Drawable local 보정을 합쳐 shader offset·scale을 만듭니다.
            var offsetScale = _offsetScale;

            offsetScale.Set(worldPosition.x + transform.localPosition.x, worldPosition.y + transform.localPosition.y,
                worldScale.x * transform.localScale.x, worldScale.y * transform.localScale.y);
            _offsetScale = offsetScale;
            // 계산한 offset·scale을 이번 draw의 shader property에 기록합니다.
            property.SetVector(CubismShaderVariables.OffsetScale, _offsetScale);

            // controller 월드 회전과 Drawable local 회전을 합친 회전을 만듭니다.
            var combinedRotation = controllerTransform.rotation * transform.localRotation;
            _quaternion.Set(combinedRotation.x, combinedRotation.y, combinedRotation.z, combinedRotation.w);

            // 합성한 quaternion을 이번 draw의 shader property에 기록합니다.
            property.SetVector(CubismShaderVariables.RotationQuaternion, _quaternion);

            // 월드 Z와 Drawable local Z를 합쳐 depth 보정값을 만듭니다.
            _zOffset = worldPosition.z + transform.localPosition.z;
            // 계산한 Z 보정값을 이번 draw의 shader property에 기록합니다.
            property.SetFloat(CubismShaderVariables.ZOffset, _zOffset);
        }

        /// 입력: buffer(CommandBuffer), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        private void SetOffscreen(CommandBuffer buffer, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            var currentOffscreenUnmanagedIndex = RenderController.CurrentOffscreenUnmanagedIndex;
            SubmitDrawToParentOffscreen(ref passData, ref currentOffscreenUnmanagedIndex,
                buffer, this);

            // 풀에서 빌린 texture를 이 offscreen part의 framebuffer로 연결합니다.
            OffscreenFrameBuffer = CubismOffscreenRenderTextureManager.GetInstance().GetOffscreenRenderTexture(passData.CommonRenderingTextureHandle);

            // 이후 command가 이 offscreen framebuffer에 쓰도록 target을 바꿉니다.
            buffer.SetRenderTarget(OffscreenFrameBuffer);
            // 이전 part의 색이 섞이지 않게 offscreen color를 지웁니다.
            buffer.ClearRenderTarget(false, true, Color.clear);

            // controller의 현재 framebuffer와 offscreen index를 새 계층으로 갱신합니다.
            RenderController.CurrentFrameBuffer = OffscreenFrameBuffer;
            RenderController.CurrentOffscreenUnmanagedIndex = Offscreen.UnmanagedIndex;
        }

        /// 입력: 없음; 반환: Bounds.
        private Bounds GetMaskBounds()
        {
            // mask가 없으면 합칠 기하가 없으므로 빈 Bounds를 반환합니다.
            if (_masks == null
                || _masks.Length < 1)
            {
                return new Bounds();
            }

            var min = _masks[0]?.Mesh?.bounds.min ?? Vector3.zero;
            var max = _masks[0]?.Mesh?.bounds.max ?? Vector3.zero;


            for (var i = 1; i < _masks.Length; ++i)
            {
                // 아직 생성되지 않은 mask는 Bounds 합산에서 제외합니다.
                // 배치 렌더링 중에는 Mesh도 null이므로 위 시드값과 같은 방식으로 막습니다.
                var maskMesh = _masks[i] ? _masks[i].Mesh : null;

                if (maskMesh == null)
                {
                    continue;
                }

                var boundsI = maskMesh.bounds;


                if (boundsI.min.x < min.x)
                {
                    min.x = boundsI.min.x;
                }

                if (boundsI.max.x > max.x)
                {
                    max.x = boundsI.max.x;
                }


                if (boundsI.min.y < min.y)
                {
                    min.y = boundsI.min.y;
                }

                if (boundsI.max.y > max.y)
                {
                    max.y = boundsI.max.y;
                }
            }

            var bounds = _maskBounds;
            bounds.SetMinMax(min, max);
            _maskBounds = bounds;

            return _maskBounds;
        }

        /// 입력: 없음; 반환: 없음.
        private void CalcMaskTransform()
        {
            // 모든 mask Bounds와 긴 축 길이를 읽어 정규화 scale을 구합니다.
            var bounds = GetMaskBounds();
            var scale = (bounds.size.x > bounds.size.y)
                ? bounds.size.x
                : bounds.size.y;

            // Bounds 중심과 scale을 shader mask 좌표 변환으로 기록합니다.
            var maskTransform = _maskTransform;
            maskTransform.Set(
                bounds.center.x, // 중심 X 오프셋
                bounds.center.y, // 중심 Y 오프셋
                1.0f / scale, // 정규화 scale
                0 // 사용하지 않는 예약 성분
                  );
            _maskTransform = maskTransform;
        }

        /// 입력: buffer(CommandBuffer), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        internal void DrawMasks(CommandBuffer buffer, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            // 모델에 mask가 없으면 atlas target을 건드리지 않습니다.
            if (!RenderController.HasMask)
            {
                return;
            }

#if UNITY_EDITOR
            // Editor에서 누락된 mask 참조가 있으면 다시 찾아 캐시를 복구합니다.
            if (_haveMasksNull)
            {
                TryInitializeMasks();
                _haveMasksNull = false;
            }
#endif

            if (_masks?.Length > 0)
            {
                var maskTexture = passData.MaskTextureHandle;

                buffer.SetRenderTarget(maskTexture);
                buffer.ClearRenderTarget(true, true, Color.clear);

                // 현재 mask 기하에 맞는 atlas 좌표 변환을 계산합니다.
                CalcMaskTransform();

                // 각 mask mesh를 mask target에 기록합니다.
                for (var maskIndex = 0; maskIndex < _masks.Length; maskIndex++)
                {
                    var mask = _masks[maskIndex];

                    if (!mask)
                    {
#if UNITY_EDITOR
                        _haveMasksNull = true;
#endif
                        continue;
                    }

                    switch (DrawObjectType)
                    {
                        case CubismModelTypes.DrawObjectType.Drawable:
                            mask.PropertyBlock.SetTexture(CubismShaderVariables.MainTexture, mask.MainTexture);
                            mask.PropertyBlock.SetVector(CubismShaderVariables.MaskTile, HighPrecisionMaskTile);
                            mask.PropertyBlock.SetVector(CubismShaderVariables.MaskTransform, _maskTransform);

                            // Drawable mask material로 mesh draw를 기록합니다.
                            buffer.DrawMesh(
                                mask.Mesh,
                                Matrix4x4.identity,
                                mask.Drawable.IsDoubleSided
                                    ? CubismBuiltinMaterials.Mask
                                    : CubismBuiltinMaterials.MaskCulling,
                                0,
                                0,
                                mask.PropertyBlock);
                            break;
                        case CubismModelTypes.DrawObjectType.Offscreen:
                            mask.PropertyBlock.SetTexture(CubismShaderVariables.MainTexture, mask.MainTexture);
                            mask.ApplyTransform();

                            // offscreen mask material로 월드 변환된 mesh draw를 기록합니다.
                            buffer.DrawMesh(
                                mask.Mesh,
                                Matrix4x4.identity,
                                CubismBuiltinMaterials.OffscreenMask,
                                0,
                                0,
                                mask.PropertyBlock);
                            break;
                        default:
                            Debug.LogWarning("Unknown DrawObjectType.");
                            break;
                    }
                }

                ApplyMask(maskTexture);
            }
        }

        /// 입력: passData(ref CubismRenderPassFeature.CubismRenderPass.PassData), currentOffscreenUnmanagedIndex(ref int), buffer(CommandBuffer), targetRenderer(CubismRenderer); 반환: 없음.
        private void SubmitDrawToParentOffscreen(ref CubismRenderPassFeature.CubismRenderPass.PassData passData, ref int currentOffscreenUnmanagedIndex,
            CommandBuffer buffer, CubismRenderer targetRenderer)
        {
            if (passData == null
                || RenderController.CurrentFrameBuffer == (RenderTexture)passData.CommonRenderingTextureHandle
                || currentOffscreenUnmanagedIndex == -1)
            {
                return;
            }

            // 현재 offscreen unmanaged index를 소유한 renderer를 찾습니다.
            CubismRenderer offscreenRenderer = null;
            for (var offscreenRendererIndex = 0; offscreenRendererIndex < RenderController.OffscreenRenderers.Length; offscreenRendererIndex++)
            {
                if (RenderController.OffscreenRenderers[offscreenRendererIndex].Offscreen.UnmanagedIndex !=
                    currentOffscreenUnmanagedIndex)
                {
                    continue;
                }

                offscreenRenderer = RenderController.OffscreenRenderers[offscreenRendererIndex];
                break;
            }
            var currentOwnerIndex = offscreenRenderer?.Offscreen.OwnerIndex ?? -1;

            if (currentOwnerIndex == -1)
            {
                // 해제 중이거나 캐시가 없으면 합성을 기록하지 않고 종료합니다.
                return;
            }

            var targetParentIndex = -1;
            RenderTexture previousOffscreen = null;
            switch (targetRenderer.DrawObjectType)
            {
                case CubismModelTypes.DrawObjectType.Drawable:
                    targetParentIndex = targetRenderer.Drawable.ParentPartIndex;
                    break;
                case CubismModelTypes.DrawObjectType.Offscreen:
                    // 대상 offscreen의 owner part 부모를 다음 비교 기준으로 읽습니다.
                    targetParentIndex = RenderController.Model.Parts[targetRenderer.Offscreen.OwnerIndex].UnmanagedParentIndex;
                    break;
                default:
                    // 지원하지 않는 draw object는 parent 합성 경로가 없으므로 종료합니다.
                    return;
            }

            // 대상이 현재 offscreen owner의 자식인지 part 부모 체인을 따라 확인합니다.
            while (targetParentIndex != -1)
            {
                // 현재 owner 아래 대상이면 framebuffer를 닫지 않고 그대로 계속 draw합니다.
                if (targetParentIndex == RenderController.Model.Parts[currentOwnerIndex].UnmanagedIndex)
                {
                    return;
                }

                targetParentIndex = RenderController.Model.Parts[targetParentIndex].UnmanagedParentIndex;
            }
            var parentIndex = RenderController.Model.Parts[currentOwnerIndex].UnmanagedParentIndex;

            // 상위 part 체인에서 이전 framebuffer를 줄 offscreen renderer를 찾습니다.
            while (parentIndex != -1)
            {
                var part = RenderController.Model.Parts[parentIndex];
                if (part.OffscreenIndex != -1)
                {
                    for (var offscreenRendererIndex = 0; offscreenRendererIndex < RenderController.OffscreenRenderers.Length; offscreenRendererIndex++)
                    {
                        var element = RenderController.OffscreenRenderers[offscreenRendererIndex];

                        if (!element.isActiveAndEnabled
                            || !element.MeshRenderer.enabled
                            || element.Offscreen.UnmanagedIndex != part.OffscreenIndex)
                        {
                            continue;
                        }

                        previousOffscreen = RenderController.OffscreenRenderers[offscreenRendererIndex].OffscreenFrameBuffer;
                        _previousOffscreenUnmanagedIndex = part.OffscreenIndex;
                        break;
                    }
                }

                parentIndex = RenderController.Model.Parts[parentIndex].UnmanagedParentIndex;

                if (!previousOffscreen)
                {
                    continue;
                }

                break;
            }

            // 상위 offscreen이 없으면 root part offscreen을 이전 framebuffer 후보로 찾습니다.
            if (!previousOffscreen && RenderController.HasRootPartOffscreen)
            {
                var offscreenRenderers = RenderController.OffscreenRenderers;

                for (var offscreenIndex = 0; offscreenIndex < offscreenRenderers.Length; offscreenIndex++)
                {
                    if (offscreenRenderers[offscreenIndex].Offscreen.UnmanagedIndex != 0)
                    {
                        continue;
                    }

                    previousOffscreen = offscreenRenderers[offscreenIndex].OffscreenFrameBuffer;
                    break;
                }
            }

            // 상위 framebuffer가 없으면 공용 렌더 texture로 되돌립니다.
            if (!previousOffscreen)
            {
                previousOffscreen = passData.CommonRenderingTextureHandle;
            }

            if (previousOffscreen == offscreenRenderer?.OffscreenFrameBuffer)
            {
                return;
            }

            // 이전 framebuffer 내용을 현재 offscreen으로 합성해 부모 결과를 이어받습니다.
            DrawOffscreen(buffer, previousOffscreen, offscreenRenderer, passData);

            offscreenRenderer.OffscreenFrameBuffer = null;
            RenderController.CurrentFrameBuffer = previousOffscreen;
            currentOffscreenUnmanagedIndex = _previousOffscreenUnmanagedIndex;

            // 현재 offscreen이 대상의 부모이면 부모 framebuffer를 다음 draw target으로 유지합니다.
            SubmitDrawToParentOffscreen(ref passData, ref currentOffscreenUnmanagedIndex,
                buffer, targetRenderer);
        }

        /// 입력: buffer(CommandBuffer), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        private void DrawDrawable(CommandBuffer buffer, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            if (!RenderController)
            {
                return;
            }

            if (RenderController.CurrentOffscreenUnmanagedIndex != -1)
            {
                var currentOffscreenOwnerUnmanagedIndex = RenderController.CurrentOffscreenUnmanagedIndex;
                SubmitDrawToParentOffscreen(ref passData, ref currentOffscreenOwnerUnmanagedIndex,
                    buffer, this);

                RenderController.CurrentOffscreenUnmanagedIndex = currentOffscreenOwnerUnmanagedIndex;
            }

            // high precision mask 결과를 현재 Drawable shader에 연결합니다.
            DrawMasks(buffer, passData);

            // PropertyBlock Get/Set을 한 번만 수행해 Drawable당 최대 다섯 번의 왕복을 피합니다.
            // DrawObject는 여기로 Drawable만 보내므로 Drawable 전용 property를 안전하게 함께 기록할 수 있습니다.
            var property = PropertyBlock;
            MeshRenderer.GetPropertyBlock(property);
            WriteMainTexture(property);
            WriteBlendedRenderTexture(property, passData);
            WriteScreenColor(property);
            WriteMultiplyColor(property);
            WriteTransform(property);
            MeshRenderer.SetPropertyBlock(property);

            // 정점 색은 property block이 아니라 mesh vertex stream에서 갱신합니다.
            ApplyVertexColors();

            // Cubism 5.2 이전 색 blend 호환 경로를 적용합니다.
            if ((ColorBlendType == BlendTypes.ColorBlend.Normal
                && AlphaBlendType == BlendTypes.AlphaBlend.Over)
                || ColorBlendType == BlendTypes.ColorBlend.Add
                || ColorBlendType == BlendTypes.ColorBlend.Multiply)
            {
                // 현재 framebuffer가 공용 texture와 다르면 shader 입력을 해당 framebuffer로 갱신합니다.
                // offscreen으로 그릴 경우 크기는 SetOffscreen에서 이미 맞았다는 전제입니다.
                if (!RenderController.CurrentFrameBuffer
                    || RenderController.CurrentFrameBuffer.width != ((RenderTexture)passData.CameraDepthTextureHandle).width
                    || RenderController.CurrentFrameBuffer.height != ((RenderTexture)passData.CameraDepthTextureHandle).height)
                {
                    RenderController.CurrentFrameBuffer = passData.CommonRenderingTextureHandle;
                }

                // Unity 물체와 정확히 depth test하도록 color·depth target을 함께 설정합니다.
                buffer.SetRenderTarget(RenderController.CurrentFrameBuffer, passData.CameraDepthTextureHandle);

                // 준비한 material과 property block으로 mesh draw를 기록합니다.
                buffer.DrawMesh(Mesh, Matrix4x4.identity, DrawMaterial ?? Material, 0, 0, PropertyBlock);

                return;
            }

            // blend 전에 현재 결과를 임시 texture로 복사합니다.
            buffer.Blit(RenderController.CurrentFrameBuffer, passData.CommonTemporaryTextureHandle);

            // 다음 blend 결과가 쌓일 임시 target을 설정합니다.
            buffer.SetRenderTarget(RenderController.CurrentFrameBuffer, passData.CameraDepthTextureHandle);

            // blend material로 Drawable mesh를 임시 target에 그립니다.
            buffer.DrawMesh(Mesh, Matrix4x4.identity, DrawMaterial ?? Material, 0, 0, PropertyBlock);
        }

        /// 입력: buffer(CommandBuffer), previousOffscreen(RenderTexture), currentOffscreenRenderer(CubismRenderer), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        internal void DrawOffscreen(CommandBuffer buffer, RenderTexture previousOffscreen, CubismRenderer currentOffscreenRenderer, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            if (previousOffscreen == null
                || currentOffscreenRenderer == null)
            {
                return;
            }

            // 합성 material로 mesh draw를 기록합니다.
            currentOffscreenRenderer.DrawOffscreenMesh(buffer, previousOffscreen, passData);
        }

        /// 입력: buffer(CommandBuffer), previousOffscreen(RenderTexture), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        internal void DrawOffscreenMesh(CommandBuffer buffer, RenderTexture previousOffscreen, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            // 이 Drawable에 mask texture·transform property를 적용합니다.
            DrawMasks(buffer, passData);

            ApplyPropertyForOffscreen(previousOffscreen);

            // Cubism 5.2 이전 색 blend 호환 경로를 적용합니다.
            if ((ColorBlendType == BlendTypes.ColorBlend.Normal
                && AlphaBlendType == BlendTypes.AlphaBlend.Over)
                || ColorBlendType == BlendTypes.ColorBlend.Add
                || ColorBlendType == BlendTypes.ColorBlend.Multiply)
            {
                buffer.SetRenderTarget(previousOffscreen, passData.CameraDepthTextureHandle);

                // 현재 framebuffer에 기존 blend material draw를 기록합니다.
                buffer.DrawMesh(Mesh, Matrix4x4.identity, DrawMaterial ?? Material, 0, 0, PropertyBlock);

                return;
            }

            // 중간 합성 결과를 받을 임시 target을 설정합니다.
            buffer.SetRenderTarget(passData.CommonTemporaryTextureHandle, passData.CameraDepthTextureHandle);
            // 중간 target의 이전 색을 지워 새 결과만 남깁니다.
            buffer.ClearRenderTarget(false, true, Color.clear);

            // 새 Drawable을 임시 target에 그립니다.
            buffer.DrawMesh(Mesh, Matrix4x4.identity, DrawMaterial ?? Material, 0, 0, PropertyBlock);

            // 임시 합성 결과를 이전 offscreen framebuffer로 되돌려 복사합니다.
            buffer.Blit(passData.CommonTemporaryTextureHandle, previousOffscreen);
        }

        /// 입력: previousOffscreen(RenderTexture); 반환: 없음.
        private void ApplyPropertyForOffscreen(RenderTexture previousOffscreen)
        {
            var property = PropertyBlock;
            MeshRenderer.GetPropertyBlock(property);

            // 갱신한 mask property를 renderer에 한 번 기록합니다.
            property.SetTexture(CubismShaderVariables.MainTexture, OffscreenFrameBuffer);
            property.SetTexture(CubismShaderVariables.RenderTexture, previousOffscreen);
            property.SetColor(CubismShaderVariables.MultiplyColor, MultiplyColor);
            property.SetColor(CubismShaderVariables.ScreenColor, ScreenColor);
            property.SetFloat(CubismShaderVariables.OffscreenOpacity, Offscreen.Opacity);

            var reversedZ = SystemInfo.usesReversedZBuffer ? CubismRenderPassFeature.GEqual : CubismRenderPassFeature.LEqual;
            property.SetInt(CubismShaderVariables.ReversedZ, reversedZ);

            MeshRenderer.SetPropertyBlock(property);
        }

        /// 입력: maskTextureHandle(TextureHandle); 반환: 없음.
        private void ApplyMask(TextureHandle maskTextureHandle)
        {
            MeshRenderer.GetPropertyBlock(PropertyBlock);

            // 갱신한 blend property를 renderer에 한 번 기록합니다.
            PropertyBlock.SetTexture(CubismShaderVariables.MaskTexture, maskTextureHandle);
            if (DrawObjectType == CubismModelTypes.DrawObjectType.Drawable)
            {
                PropertyBlock.SetVector(CubismShaderVariables.MaskTile, HighPrecisionMaskTile);
                PropertyBlock.SetVector(CubismShaderVariables.MaskTransform, _maskTransform);
            }

            MeshRenderer.SetPropertyBlock(PropertyBlock);
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeMasks()
        {
            if (!RenderController.HasMask)
            {
                return;
            }

            CubismDrawable[] maskDrawables = null;
            switch (DrawObjectType)
            {
                case CubismModelTypes.DrawObjectType.Drawable:
                    _masks = new CubismRenderer[Drawable.Masks.Length];
                    maskDrawables = Drawable.Masks;
                    break;
                case CubismModelTypes.DrawObjectType.Offscreen:
                    _masks = new CubismRenderer[Offscreen.Masks.Length];
                    maskDrawables = Offscreen.Masks;
                    break;
                default:
                    Debug.LogWarning($"{name} has unknown DrawObjectType.");
                    return;
            }

            for (var maskIndex = 0; maskIndex < maskDrawables.Length; maskIndex++)
            {
                for (var drawableRendererIndex = 0; drawableRendererIndex < RenderController.DrawableRenderers.Length; drawableRendererIndex++)
                {
                    if (RenderController.DrawableRenderers[drawableRendererIndex].Drawable.UnmanagedIndex != maskDrawables[maskIndex].UnmanagedIndex)
                    {
                        continue;
                    }

                    _masks[maskIndex] = RenderController.DrawableRenderers[drawableRendererIndex];
                    break;
                }
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void InitializeDrawObject()
        {
            switch (DrawObjectType)
            {
                case CubismModelTypes.DrawObjectType.Drawable:
                {
                    Drawable = GetComponent<CubismDrawable>();
                    DrawObjectUnmanagedIndex = Drawable.UnmanagedIndex;
                    break;
                }
                case CubismModelTypes.DrawObjectType.Offscreen:
                {
                    Offscreen = GetComponent<CubismOffscreen>();
                    DrawObjectUnmanagedIndex = Offscreen.UnmanagedIndex;
                    break;
                }
                default:
                    DrawObjectUnmanagedIndex = -1;
                    break;
            }
        }

        /// 입력: 없음; 반환: 없음.
        public void OnAfterAllRendererInitialize()
        {
            TryInitializeMasks();
        }
    }
}
