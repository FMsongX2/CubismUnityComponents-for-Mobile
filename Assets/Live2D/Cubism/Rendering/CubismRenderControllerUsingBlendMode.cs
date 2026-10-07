/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// Cubism 5.3 이후 Drawable·Offscreen 혼합 렌더의 정렬, 계층 frame buffer, draw 제출을 담당합니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Rendering.URP;
using System;
using System.Collections.Generic;
using Live2D.Cubism.Rendering.URP.RenderingInterceptor;
using UnityEngine;
using UnityEngine.Rendering;


namespace Live2D.Cubism.Rendering
{
    public partial class CubismRenderController
    {
        #region Values

        private MaterialPropertyBlock _properties;

        internal bool HasRootPartOffscreen = true;

        [SerializeField, HideInInspector]
        private bool _hasMask;

        public bool HasMask
        {
            get
            {
                return _hasMask;
            }
            set
            {
                _hasMask = value;
            }
        }

        public RenderTexture CurrentFrameBuffer { get; set; }

        public int CurrentOffscreenUnmanagedIndex { get; set; }

        #region Sorting

        [SerializeField, HideInInspector]
        private int _groupedSortingIndex;

        public int GroupedSortingIndex
        {
            get
            {
                return _groupedSortingIndex;
            }

            set
            {
                // 이전 정렬 인덱스로 등록된 공용 render group에서 먼저 제거합니다.
                CubismRenderControllerGroup.GetInstance().RemoveRenderControllerFromGroups(this, true);

                _groupedSortingIndex = value;

                // 새 정렬 인덱스를 기준으로 공용 render group에 다시 등록합니다.
                CubismRenderControllerGroup.GetInstance().AddRenderControllerGroups(this);
            }
        }

        internal bool DidChangeDrawableRenderOrder;

        internal bool DidChangeSorting;

        [NonSerialized]
        private CubismRenderer[] _sortedRenderers;

        public CubismRenderer[] SortedRenderers
        {
            get
            {
                if (_sortedRenderers == null)
                {
                    SortRenderers();
                }

                return _sortedRenderers;
            }
            private set { _sortedRenderers = value; }
        }

        /// 입력: 없음; 반환: 없음.
        private void SortRenderers()
        {
            if (Renderers == null)
            {
                return;
            }

            for (var i = 0; i < Renderers.Length; i++)
            {
                if (Renderers[i].DrawObjectType != CubismModelTypes.DrawObjectType.Offscreen)
                {
                    continue;
                }

                Renderers[i].SetDrawObjectRenderOrder(
                    Model.AllDrawObjectsRenderOrder[
                        DrawableRenderers.Length + Renderers[i].Offscreen.UnmanagedIndex]);
            }

            // sort dirty 프레임마다 실행되므로 작업 List와 결과 배열을 재사용해 지속적인 GC 할당을 막습니다.
            _sortScratch ??= new List<CubismRenderer>(Renderers.Length);
            _sortScratch.Clear();
            _sortScratch.AddRange(Renderers);

            SortBySortingOrder(_sortScratch);

            if (_sortedRenderers == null || _sortedRenderers.Length != _sortScratch.Count)
            {
                _sortedRenderers = new CubismRenderer[_sortScratch.Count];
            }

            _sortScratch.CopyTo(_sortedRenderers);
        }

        private List<CubismRenderer> _sortScratch;

        /// 입력: renderers(List<CubismRenderer>); 반환: 없음.
        private void SortBySortingOrder(List<CubismRenderer> renderers)
        {
            renderers.Sort(CompareBySortingOrder);
        }

        /// 입력: a(CubismRenderer), b(CubismRenderer); 반환: int.
        private int CompareBySortingOrder(CubismRenderer a, CubismRenderer b)
        {
            return a.MeshRenderer.sortingOrder - b.MeshRenderer.sortingOrder;
        }

        #endregion

        [NonSerialized]
        private CubismRenderer[] _drawableRenderers;

        public CubismRenderer[] DrawableRenderers
        {
            get
            {
                if (_drawableRenderers == null)
                {
                    _drawableRenderers = Model.Drawables.GetComponentsMany<CubismRenderer>();
                }
                return _drawableRenderers;
            }
            private set { _drawableRenderers = value; }
        }

        private CubismRenderer[] _offscreenRenderers;

        public CubismRenderer[] OffscreenRenderers
        {
            get
            {
                if (_offscreenRenderers == null && Model?.Offscreens != null)
                {
                    _offscreenRenderers = Model.Offscreens.GetComponentsMany<CubismRenderer>();
                }
                return _offscreenRenderers;
            }
            private set { _offscreenRenderers = value; }
        }

        #endregion

        /// 입력: renderers(CubismRenderer[]); 반환: 없음.
        private void TryInitializeRenderers(CubismRenderer[] renderers)
        {
            // 호출자가 넘긴 배열이 이미 채워졌으면 중복 component와 cache를 만들지 않습니다.
            if (renderers != null && renderers.Length != 0)
            {
                return;
            }

            // Drawable·Offscreen renderer를 합칠 빈 배열에서 초기화를 시작합니다.
            renderers = Array.Empty<CubismRenderer>();

            // 모든 Drawable에 renderer를 붙이고 종류와 원본 Drawable 참조를 연결합니다.
            var drawables = Model.Drawables;

            var drawableRenderers = drawables.AddComponentEach<CubismRenderer>();
            Array.Resize(ref renderers, drawableRenderers.Length);
            Array.Copy(drawableRenderers, renderers, drawableRenderers.Length);

            for (var index = 0; index < renderers.Length; index++)
            {
                var targetRenderer = renderers[index];
                targetRenderer.DrawObjectType = CubismModelTypes.DrawObjectType.Drawable;
                targetRenderer.Drawable = drawables[index];

                if (!HasRootPartOffscreen)
                {
                    continue;
                }
                HasRootPartOffscreen = CheckHasRootPartOffscreen(targetRenderer);
            }

            // 이후 mask·정렬 경로가 다시 찾지 않도록 Drawable renderer 배열을 저장합니다.
            DrawableRenderers = drawableRenderers;

            var offscreens = Model.Offscreens;

            if (offscreens != null)
            {
                var offscreenRenderers = offscreens.AddComponentEach<CubismRenderer>();
                Array.Resize(ref renderers, renderers.Length + offscreenRenderers.Length);
                Array.Copy(offscreenRenderers, 0, renderers, renderers.Length - offscreenRenderers.Length, offscreenRenderers.Length);

                for (var index = drawableRenderers.Length; index < renderers.Length; ++index)
                {
                    var targetRenderer = renderers[index];
                    targetRenderer.DrawObjectType = CubismModelTypes.DrawObjectType.Offscreen;
                    targetRenderer.Offscreen = offscreens[index - drawableRenderers.Length];

                    if (!HasRootPartOffscreen)
                    {
                        continue;
                    }
                    HasRootPartOffscreen = CheckHasRootPartOffscreen(targetRenderer);
                }

                // 이후 계층 합성 경로가 다시 찾지 않도록 Offscreen renderer 배열을 저장합니다.
                OffscreenRenderers = offscreenRenderers;
            }

            // Drawable과 Offscreen을 합친 전체 renderer 배열을 controller에 저장합니다.
            Renderers = renderers;
        }

        /// 입력: renderers(CubismRenderer[]); 반환: 없음.
        private void OnAfterRenderersInitialize(CubismRenderer[] renderers)
        {
            // 각 renderer에 모델 draw order를 쓰고 전체 renderer 준비 뒤 필요한 후처리를 호출합니다.
            for (var i = 0; i < renderers.Length; i++)
            {
                var initRenderer = renderers[i];
                switch (initRenderer.DrawObjectType)
                {
                    case CubismModelTypes.DrawObjectType.Drawable:
                        initRenderer.SetDrawObjectRenderOrder(Model.AllDrawObjectsRenderOrder[initRenderer.Drawable.UnmanagedIndex]);
                        break;
                    case CubismModelTypes.DrawObjectType.Offscreen:
                        initRenderer.SetDrawObjectRenderOrder(Model.AllDrawObjectsRenderOrder[DrawableRenderers.Length + initRenderer.Offscreen.UnmanagedIndex]);
                        break;
                    default:
                        Debug.LogWarning($"Unknown draw object type: {initRenderer.DrawObjectType} for renderer: {initRenderer.name}");
                        break;
                }
                initRenderer.OnAfterAllRendererInitialize();
            }
        }

        /// 입력: targetRenderer(CubismRenderer); 반환: bool.
        private bool CheckHasRootPartOffscreen(CubismRenderer targetRenderer)
        {
            var parentPartIndex = -1;
            switch (targetRenderer.DrawObjectType)
            {
                case CubismModelTypes.DrawObjectType.Drawable:
                    parentPartIndex = targetRenderer.Drawable.ParentPartIndex;
                    break;
                case CubismModelTypes.DrawObjectType.Offscreen:
                    parentPartIndex = Model.Parts[targetRenderer.Offscreen.OwnerIndex].UnmanagedParentIndex;
                    break;
                default:
                    Debug.LogWarning($"Unknown draw object type: {targetRenderer.DrawObjectType} for renderer: {targetRenderer.name}");
                    break;
            }

            CubismPart part = null;
            while (parentPartIndex > 0)
            {
                for (var partIndex = 0; partIndex < Model.Parts.Length; partIndex++)
                {
                    if (Model.Parts[partIndex].UnmanagedIndex != parentPartIndex)
                    {
                        continue;
                    }

                    // unmanaged parent 번호에 해당하는 실제 part를 찾았으므로 상위 계층 탐색을 이어갑니다.
                    part = Model.Parts[partIndex];
                    break;
                }

                parentPartIndex = part?.UnmanagedParentIndex ?? -1;
            }

            return parentPartIndex == 0 && Model.Parts[parentPartIndex].OffscreenIndex > -1;
        }

        /// 입력: commandBuffer(CommandBuffer), passData(CubismRenderPassFeature.CubismRenderPass.PassData); 반환: 없음.
        internal void SubmitDrawOffscreen(CommandBuffer commandBuffer, CubismRenderPassFeature.CubismRenderPass.PassData passData)
        {
            if (!IsInitialized
                || !Model
                || OffscreenRenderers == null)
            {
                return;
            }

            while (CurrentFrameBuffer != (RenderTexture)passData.CommonRenderingTextureHandle)
            {
                CubismRenderer offscreenRenderer = null;
                if (CurrentOffscreenUnmanagedIndex == -1 && HasRootPartOffscreen)
                {
                    for (var offscreenIndex = 0; offscreenIndex < OffscreenRenderers.Length; offscreenIndex++)
                    {
                        if (OffscreenRenderers[offscreenIndex].Offscreen.UnmanagedIndex != 0)
                        {
                            continue;
                        }

                        offscreenRenderer = OffscreenRenderers[offscreenIndex];

                        break;
                    }

                    if (!offscreenRenderer)
                    {
                        break;
                    }

                    offscreenRenderer.DrawOffscreen(commandBuffer, passData.CommonRenderingTextureHandle, offscreenRenderer, passData);
                    offscreenRenderer.OffscreenFrameBuffer = null;
                    CurrentFrameBuffer = passData.CommonRenderingTextureHandle;
                    CurrentOffscreenUnmanagedIndex = -1;
                    break;
                }

                for (var offscreenRendererIndex = 0; offscreenRendererIndex < OffscreenRenderers.Length; offscreenRendererIndex++)
                {
                    if (OffscreenRenderers[offscreenRendererIndex].Offscreen.UnmanagedIndex != CurrentOffscreenUnmanagedIndex)
                    {
                        continue;
                    }

                    offscreenRenderer = OffscreenRenderers[offscreenRendererIndex];
                    break;
                }

                RenderTexture previousOffscreen = null;
                var currentOwnerIndex = offscreenRenderer?.Offscreen?.OwnerIndex ?? -1;

                var parentIndex = -1;
                if (currentOwnerIndex != -1)
                {
                    parentIndex = Model.Parts[currentOwnerIndex]?.UnmanagedParentIndex ?? -1;
                }

                var previousIndex = -1;
                // 현재 owner의 부모 계층에서 가장 가까운 활성 offscreen renderer와 frame buffer를 찾습니다.
                while (parentIndex != -1)
                {
                    var part = Model.Parts[parentIndex];
                    if (part && part.OffscreenIndex != -1)
                    {
                        for (var offscreenRendererIndex = 0; offscreenRendererIndex < OffscreenRenderers.Length; offscreenRendererIndex++)
                        {
                            var element = OffscreenRenderers[offscreenRendererIndex];

                            if (!element.isActiveAndEnabled
                                || !element.MeshRenderer.enabled)
                            {
                                continue;
                            }

                            if (element.Offscreen.UnmanagedIndex != part?.OffscreenIndex)
                            {
                                continue;
                            }

                            previousOffscreen = OffscreenRenderers[offscreenRendererIndex].OffscreenFrameBuffer;
                            previousIndex = part.OffscreenIndex;
                            break;
                        }
                    }
                    parentIndex = Model.Parts[parentIndex]?.UnmanagedParentIndex ?? -1;

                    if (!previousOffscreen)
                    {
                        continue;
                    }

                    break;
                }

                // 가까운 부모 offscreen이 없으면 root part의 offscreen frame buffer를 사용합니다.
                if (!previousOffscreen && HasRootPartOffscreen)
                {
                    for (var offscreenIndex = 0; offscreenIndex < OffscreenRenderers.Length; offscreenIndex++)
                    {
                        if (OffscreenRenderers[offscreenIndex].Offscreen.UnmanagedIndex != 0)
                        {
                            continue;
                        }

                        previousOffscreen = OffscreenRenderers[offscreenIndex].OffscreenFrameBuffer;

                        break;
                    }
                }

                // 부모가 없거나 자기 frame buffer를 가리키면 읽기·쓰기 충돌을 피해 공용 texture로 돌아갑니다.
                if (!previousOffscreen
                    || previousOffscreen == offscreenRenderer?.OffscreenFrameBuffer)
                {
                    previousOffscreen = passData.CommonRenderingTextureHandle;
                }

                // 결정한 부모 또는 공용 texture를 입력으로 현재 offscreen 합성 draw를 기록합니다.
                offscreenRenderer?.DrawOffscreen(commandBuffer, previousOffscreen, offscreenRenderer, passData);

                if (offscreenRenderer)
                {
                    offscreenRenderer.OffscreenFrameBuffer = null;
                }

                CurrentFrameBuffer = previousOffscreen;
                CurrentOffscreenUnmanagedIndex = previousIndex;
            }
        }
    }
}
