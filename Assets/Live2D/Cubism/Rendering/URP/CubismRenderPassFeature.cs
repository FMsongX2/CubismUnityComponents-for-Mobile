/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 렌더 전후 interceptor가 현재 Cubism draw의 pass·buffer·정렬 정보를 읽도록 전달하는 값입니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering.URP.RenderingInterceptor;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Live2D.Cubism.Rendering.URP
{
    public struct CubismRenderedEventArgs
    {
        public CubismRenderPassFeature.CubismRenderPass.PassData PassData;
        public CommandBuffer CommandBuffer;
        public RenderTexture ColorBuffer;
        public TextureHandle DepthBuffer;
        public CubismSortingMode SortingMode;
        public int GroupSortingOrder;
        public int SortingOrder;
        public CubismDrawable Drawable;
        public float Distance;
        public float? PreviousDistance;
        public float? NextDistance;
        public Vector3 CameraPos;
        public Vector3 CameraForward;
    }

    public class CubismRenderPassFeature : ScriptableRendererFeature
    {
        internal static readonly int GEqual = 7;

        internal static readonly int LEqual = 4;

        public class CubismRenderPass : ScriptableRenderPass
        {
            private struct RendererGroupData
            {
                public int SortingIndex;

                public CubismRenderController[] RenderControllers;

                public CubismRenderer[] Renderers;
            }

            private static CommandBuffer _commandBuffer;

            private static RendererGroupData[] _sortedRendererGroupDataArray;

            private static Mesh _blitRenderTextureMesh;

            private static Material _blitRenderTextureMaterial;

            public class PassData
            {
                public CubismRenderController[] RenderControllers;

                public CubismRenderControllerGroup.RenderControllerGroupData[] RenderControllerGroupDaraArray;

                public UniversalCameraData CameraData;

                public UniversalResourceData ResourceData;

                public TextureHandle MaskTextureHandle;

                public TextureHandle CameraTextureHandle;

                public TextureHandle CameraDepthTextureHandle;

                public TextureHandle CommonRenderingTextureHandle;

                public TextureHandle CommonTemporaryTextureHandle;

                public bool AllControllersBatched;

                public bool DrawDirectlyToCameraTarget;
            }

            /// 입력: targetIndex(int), target(RendererGroupData), source(CubismRenderControllerGroup.RenderControllerGroupData), cameraPos(Vector3), data(PassData); 반환: 없음.
            private static void SetUpRendererGroup(int targetIndex, RendererGroupData target, CubismRenderControllerGroup.RenderControllerGroupData source, Vector3 cameraPos, PassData data)
            {
                var previousCount = 0;
                for (var renderControllerIndex = 0; renderControllerIndex < source.Controllers.Length; renderControllerIndex++)
                {
                    var controller = source.Controllers[renderControllerIndex];

                    controller.CurrentFrameBuffer = data.CommonRenderingTextureHandle;

                    if (!controller || controller.Renderers == null)
                    {
                        continue;
                    }

                    // controller 소유 렌더러를 이 sorting group의 연속 배열로 복사합니다.
                    for (var rendererIndex = 0; rendererIndex < controller.Renderers.Length; rendererIndex++)
                    {
                        target.Renderers[previousCount + rendererIndex] = controller.Renderers[rendererIndex];
                        target.Renderers[previousCount + rendererIndex].IsLastDrawObjectInModel = false;
                    }

                    // 다음 controller의 복사 시작 위치를 갱신합니다.
                    previousCount += controller.Renderers.Length;
                }

                // 채운 renderer 배열을 현재 정렬 규칙으로 정렬합니다.
                SortBySortingOrder(target.Renderers, cameraPos, data.CameraData.camera.transform.forward);

                // 완성한 group을 frame 간 재사용 배열의 대상 위치에 기록합니다.
                _sortedRendererGroupDataArray[targetIndex] = target;
            }

            /// 입력: data(PassData); 반환: 없음.
            private static void SortingRendererGroups(PassData data)
            {
                var didChangeSortingRenderControllerGroup = CubismRenderControllerGroup.GetInstance().DidChangeSortingRenderControllerGroup;
                if (data.RenderControllerGroupDaraArray == null
                    || data.RenderControllerGroupDaraArray.Length < 1)
                {
                    return;
                }

                // controller group마다 변경 여부와 renderer 수를 검사합니다.
                for (var groupsIndex = 0; groupsIndex < data.RenderControllerGroupDaraArray.Length; groupsIndex++)
                {
                    var group = data.RenderControllerGroupDaraArray[groupsIndex];
                    var rendererCount = 0;
                    var didChangeSortingOrder = false;

                    for (var i = 0; i < group.Controllers.Length; i++)
                    {
                        var controller = group.Controllers[i];

                        // controller가 없거나 renderer가 없으면 이 group에 넣지 않습니다.
                        if (!controller || controller.Renderers == null)
                        {
                            continue;
                        }

                        // 현재 카메라 위치로 depth 정렬 필요 상태를 갱신합니다.
                        controller.UpdateDidChangeSortingFromZ(data.CameraData.worldSpaceCameraPos);

                        // offscreen 계층 순서가 바뀌었으면 관련 정렬 상태를 갱신합니다.
                        if (controller.DidChangeDrawableRenderOrder)
                        {
                            for (var offscreenIndex = 0; offscreenIndex < controller.OffscreenRenderers?.Length; offscreenIndex++)
                            {
                                var offscreenRenderer = controller.OffscreenRenderers[offscreenIndex];
                                offscreenRenderer.SetDrawObjectRenderOrder(controller.Model.AllDrawObjectsRenderOrder[controller.DrawableRenderers.Length + offscreenRenderer.Offscreen.UnmanagedIndex]);
                            }
                        }

                        // 어떤 controller라도 native Drawable 순서를 바꿨는지 누적합니다.
                        didChangeSortingOrder |=
                            controller.DidChangeSorting
                             || controller.DidChangeDrawableRenderOrder
                             || didChangeSortingRenderControllerGroup;

                        rendererCount += controller.Renderers.Length;
                    }

                    // renderer 구성과 정렬 상태가 그대로면 기존 정렬 배열을 재사용합니다.
                    if (!didChangeSortingOrder)
                    {
                        continue;
                    }

                    // group 수가 달라졌을 때만 정렬 group 캐시 배열을 새로 만듭니다.
                    if (_sortedRendererGroupDataArray == null
                        || _sortedRendererGroupDataArray.Length > data.RenderControllerGroupDaraArray.Length)
                    {
                        _sortedRendererGroupDataArray = Array.Empty<RendererGroupData>();
                    }

                    var hasGroupFound = false;
                    for (var targetIndex = 0; targetIndex < _sortedRendererGroupDataArray.Length; targetIndex++)
                    {
                        var target = _sortedRendererGroupDataArray[targetIndex];

                        if (target.Renderers == null
                            || target.SortingIndex != group.SortingGroupIndex)
                        {
                            continue;
                        }

                        if (target.Renderers.Length != rendererCount)
                        {
                            // 합쳐진 renderer 수만큼 group 내부 배열을 확장합니다.
                            Array.Resize(ref target.Renderers, rendererCount);
                        }

                        var hasControllersNull = false;
                        for (var controllerIndex = 0; controllerIndex < target.RenderControllers.Length; controllerIndex++)
                        {
                            if (target.RenderControllers[controllerIndex])
                            {
                                continue;
                            }

                            hasControllersNull = true;
                            break;
                        }

                        if (hasControllersNull
                            || didChangeSortingRenderControllerGroup
                            || target.RenderControllers.Length != data.RenderControllerGroupDaraArray[groupsIndex].Controllers.Length)
                        {
                            target.RenderControllers = data.RenderControllerGroupDaraArray[groupsIndex].Controllers;
                        }

                        // 기존 group 슬롯에 controller·renderer 정렬 결과를 채웁니다.
                        SetUpRendererGroup(targetIndex, target, group, data.CameraData.worldSpaceCameraPos, data);

                        hasGroupFound = true;
                        break;
                    }

                    // 새 group 슬롯이면 controller 배열과 정렬 배열을 초기화합니다.
                    if (hasGroupFound)
                    {
                        continue;
                    }

                    // 새 sorting index를 담도록 group 캐시 배열을 확장합니다.
                    Array.Resize(ref _sortedRendererGroupDataArray, _sortedRendererGroupDataArray.Length + 1);
                    var newRendererGroup = new RendererGroupData
                    {
                        SortingIndex = group.SortingGroupIndex,
                        RenderControllers = data.RenderControllerGroupDaraArray[groupsIndex].Controllers,
                        Renderers = new CubismRenderer[rendererCount]
                    };

                    // 새 group 슬롯에 controller·renderer 정렬 결과를 기록합니다.
                    SetUpRendererGroup(_sortedRendererGroupDataArray.Length - 1, newRendererGroup, group, data.CameraData.worldSpaceCameraPos,data);
                }
            }

            /// 입력: renderers(CubismRenderer[]), cameraPosition(Vector3), cameraForward(Vector3); 반환: 없음.
            private static void SortBySortingOrder(CubismRenderer[] renderers, Vector3 cameraPosition, Vector3 cameraForward)
            {
                for (var index = 0; index < renderers.Length; index++)
                {
                    renderers[index].CalculateDistanceToCamera(cameraPosition, cameraForward);
                }

                Array.Sort(renderers, CompareBySortingOrder);
            }

            /// 입력: a(CubismRenderer), b(CubismRenderer); 반환: int.
            private static int CompareBySortingOrder(CubismRenderer a, CubismRenderer b)
            {
                // null renderer가 들어온 경우 정렬 비교가 깨지지 않도록 순서를 정합니다.
                if (!a || !b
                       || !a.MeshRenderer || !b.MeshRenderer
                       || !a.RenderController || !b.RenderController)
                {
                    return 0;
                }

                var result = a.MeshRenderer.sortingOrder - b.MeshRenderer.sortingOrder;

                if (result != 0
                    || !(a.SortingMode.SortByDepth() && b.SortingMode.SortByDepth()))
                {
                    return result;
                }

                // sorting order가 같으면 local Z로 앞뒤 draw 순서를 결정합니다.
                var sortValue = (b.DistanceToCamera / b.RenderController.DepthOffset) - (a.DistanceToCamera / a.RenderController.DepthOffset);
                result = sortValue >= 0.0f
                    ? Mathf.CeilToInt(sortValue)
                    : Mathf.FloorToInt(sortValue);

                return result;
            }

            /// 입력: 없음; 반환: 없음.
            private static void CheckRenderingSkip()
            {
                // 이전 pass의 SkipRendering 값을 먼저 모두 해제합니다.
                for (var groupIndex = 0; groupIndex < _sortedRendererGroupDataArray.Length; groupIndex++)
                {
                    var rendererGroup = _sortedRendererGroupDataArray[groupIndex];

                    for (var rendererIndex = 0; rendererIndex < rendererGroup.Renderers.Length; rendererIndex++)
                    {
                        rendererGroup.Renderers[rendererIndex].SkipRendering = false;
                    }
                }

                // offscreen 소유 관계와 가시 상태를 읽어 이번 pass에서 건너뛸 renderer를 표시합니다.
                for (var groupIndex = 0; groupIndex < _sortedRendererGroupDataArray.Length; groupIndex++)
                {
                    var rendererGroup = _sortedRendererGroupDataArray[groupIndex];

                    for (var rendererIndex = 0; rendererIndex < rendererGroup.Renderers.Length; rendererIndex++)
                    {
                        var renderer = rendererGroup.Renderers[rendererIndex];

                        if (!renderer
                            || renderer.SkipRendering)
                        {
                            continue;
                        }

                        renderer.SkipRendering |= !renderer.RenderController
                                     || !renderer.RenderController.gameObject.activeSelf
                                     || !renderer.RenderController.enabled
                                     || !renderer.gameObject.activeSelf
                                     || !renderer.MeshRenderer
                                     || !renderer.MeshRenderer.enabled;

                        switch (renderer.DrawObjectType)
                        {
                            case CubismModelTypes.DrawObjectType.Drawable:
                                renderer.SkipRendering |= renderer.Opacity <= 0.0f;
                                break;
                            case CubismModelTypes.DrawObjectType.Offscreen:
                                renderer.SkipRendering |= renderer.Offscreen.Opacity <= 0.0f;

                                if (!renderer.SkipRendering)
                                {
                                    break;
                                }

                                // 비활성 offscreen part의 모든 자식 Drawable·Offscreen도 함께 건너뜁니다.
                                var parts = renderer.RenderController?.Model?.Parts;
                                CubismPart part = null;
                                for (var partIndex = 0; partIndex < parts?.Length; partIndex++)
                                {
                                    if (parts[partIndex].UnmanagedIndex != renderer.Offscreen.OwnerIndex)
                                    {
                                        continue;
                                    }

                                    part = parts[partIndex];
                                    break;
                                }

                                if (!part)
                                {
                                    renderer.SkipRendering = true;
                                    break;
                                }

                                // 부모 offscreen이 현재 target이 아니면 그 하위 draw object도 건너뜁니다.
                                for (var drawablesIndex = 0; drawablesIndex < part.AllChildDrawables?.Length; drawablesIndex++)
                                {
                                    var childDrawable = part.AllChildDrawables[drawablesIndex];

                                    for (var targetRendererIndex = 0; targetRendererIndex < renderer.RenderController.Renderers.Length; targetRendererIndex++)
                                    {
                                        var targetRenderer = renderer.RenderController.Renderers[targetRendererIndex];

                                        if (targetRenderer.DrawObjectType != CubismModelTypes.DrawObjectType.Drawable
                                            || targetRenderer.Drawable.UnmanagedIndex != childDrawable.UnmanagedIndex)
                                        {
                                            continue;
                                        }

                                        targetRenderer.SkipRendering = true;
                                        break;
                                    }
                                }

                                for (var offscreensIndex = 0; offscreensIndex < part.AllChildOffscreens?.Length; offscreensIndex++)
                                {
                                    var childOffscreen = part.AllChildOffscreens[offscreensIndex];

                                    for (var j = 0; j < renderer.RenderController.Renderers.Length; j++)
                                    {
                                        var targetRenderer = renderer.RenderController.Renderers[j];

                                        if (targetRenderer.DrawObjectType != CubismModelTypes.DrawObjectType.Offscreen
                                            || targetRenderer.Offscreen.UnmanagedIndex != childOffscreen.UnmanagedIndex)
                                        {
                                            continue;
                                        }

                                        targetRenderer.SkipRendering = true;
                                        break;
                                    }
                                }
                                break;
                            default:
                                renderer.SkipRendering = true;
                                break;
                        }
                    }
                }

                for (var i= 0; i < _sortedRendererGroupDataArray.Length; i++)
                {
                    var target = _sortedRendererGroupDataArray[i];

                    // 이전 그룹의 마지막 draw object 표식을 모두 지웁니다.
                    for (var rendererIndex = 0; rendererIndex < target.Renderers.Length; rendererIndex++)
                    {
                        var targetRenderer = target.Renderers[rendererIndex];
                        targetRenderer.IsLastDrawObjectInModel = false;
                    }

                    for (var controllerIndex = 0; controllerIndex < target.RenderControllers.Length; controllerIndex++)
                    {
                        var controller = target.RenderControllers[controllerIndex];

                        if (!controller
                            || !controller.enabled
                            || !controller.gameObject.activeSelf)
                        {
                            continue;
                        }

                        var lastIndex = target.Renderers.Length - 1;
                        while (lastIndex >= 0)
                        {
                            var targetRenderer = target.Renderers[lastIndex];

                            // 정렬 배열의 마지막 실제 draw renderer를 찾습니다.
                            if (!targetRenderer.SkipRendering
                                && targetRenderer.RenderController == controller)
                            {
                                // 모델의 마지막 draw object임을 표시해 합성 종료 시점을 알립니다.
                                targetRenderer.IsLastDrawObjectInModel = true;
                                break;
                            }

                            lastIndex--;
                        }
                    }
                }
            }

            private static readonly System.Collections.Generic.List<CubismRenderController> _batchedControllers = new System.Collections.Generic.List<CubismRenderController>(8);

            private static Vector3 _batchedSortCameraPosition;
            private static Vector3 _batchedSortCameraForward;

            private static readonly Comparison<CubismRenderController> _batchedControllerComparison = CompareBatchedControllers;

            /// 화면 밖 모델 판정에 재사용하는 절두체 평면 버퍼.
            private static readonly Plane[] _batchedFrustumPlanes = new Plane[6];

            /// 입력: renderControllers(CubismRenderController[]); 반환: bool.
            internal static bool AreAllControllersBatched(CubismRenderController[] renderControllers)
            {
                if (renderControllers == null || renderControllers.Length < 1)
                {
                    return false;
                }

                for (var i = 0; i < renderControllers.Length; i++)
                {
                    var controller = renderControllers[i];

                    if (!controller)
                    {
                        continue;
                    }

                    if (!controller.IsBatchedRenderingActive
                        || controller.BatchedRenderer == null
                        || !controller.BatchedRenderer.IsValid)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// 배치 모델 중 블렌드에서 목적지를 읽는 게 하나도 없는지. 그러면 오프스크린 버퍼는
            /// "over"로만 합성되고 이는 결합법칙이 성립하므로 버퍼를 건너뛰어도 결과가 같습니다.
            /// 호출 전에 전 컨트롤러가 배치 상태임이 확인돼 있어야 합니다.
            internal static bool CanSkipBufferedComposition(CubismRenderController[] renderControllers)
            {
                for (var i = 0; i < renderControllers.Length; i++)
                {
                    var controller = renderControllers[i];

                    if (!controller)
                    {
                        continue;
                    }

                    if (controller.BatchedRenderer == null
                        || controller.BatchedRenderer.RequiresBufferedComposition)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// 입력: a(CubismRenderController), b(CubismRenderController); 반환: int.
            private static int CompareBatchedControllers(CubismRenderController a, CubismRenderController b)
            {
                if (!a || !b)
                {
                    return 0;
                }

                var result = a.SortingOrder.CompareTo(b.SortingOrder);

                if (result != 0)
                {
                    return result;
                }

                var distanceA = Vector3.Dot(a.transform.position - _batchedSortCameraPosition, _batchedSortCameraForward);
                var distanceB = Vector3.Dot(b.transform.position - _batchedSortCameraPosition, _batchedSortCameraForward);

                // 카메라에서 먼 controller를 먼저 그려 back-to-front 순서를 만듭니다.
                return distanceB.CompareTo(distanceA);
            }

            /// 입력: commandBuffer(CommandBuffer), data(PassData), controllers(CubismRenderController[]), drawToCameraTarget(bool); 반환: 없음.
            private static void DrawGroupBatched(CommandBuffer commandBuffer, PassData data, CubismRenderController[] controllers, bool drawToCameraTarget)
            {
                _batchedControllers.Clear();

                var cullOffscreen = CubismBatchedRendering.CullOffscreenModels;

                if (cullOffscreen)
                {
                    GeometryUtility.CalculateFrustumPlanes(data.CameraData.camera, _batchedFrustumPlanes);
                }

                for (var i = 0; i < controllers.Length; i++)
                {
                    var controller = controllers[i];

                    if (!controller
                        || !controller.enabled
                        || !controller.gameObject.activeInHierarchy
                        || controller.BatchedRenderer == null)
                    {
                        continue;
                    }

                    // 컨트롤러를 통째로 건너뛰면 mesh 업로드도 건너뜁니다.
                    // 더티 범위는 계속 병합되다가 다시 보일 때 한 번에 flush됩니다.
                    if (cullOffscreen && controller.BatchedRenderer.IsCulledBy(_batchedFrustumPlanes))
                    {
                        continue;
                    }

                    _batchedControllers.Add(controller);
                }

                if (_batchedControllers.Count < 1)
                {
                    return;
                }

                _batchedSortCameraPosition = data.CameraData.worldSpaceCameraPos;
                _batchedSortCameraForward = data.CameraData.camera.transform.forward;

                if (_batchedControllers.Count > 1)
                {
                    _batchedControllers.Sort(_batchedControllerComparison);
                }

                // dirty mesh를 먼저 GPU에 올리고 모든 mask atlas를 그린 뒤, main target은 group당 한 번만 바인딩합니다.
                for (var i = 0; i < _batchedControllers.Count; i++)
                {
                    var batchedRenderer = _batchedControllers[i].BatchedRenderer;

                    batchedRenderer.FlushMeshData();
                    batchedRenderer.RecordMaskPass(commandBuffer);
                }

                if (drawToCameraTarget)
                {
                    commandBuffer.SetRenderTarget(data.CameraTextureHandle, data.CameraDepthTextureHandle);
                }
                else
                {
                    commandBuffer.SetRenderTarget(data.CommonRenderingTextureHandle, data.CameraDepthTextureHandle);
                }

                for (var i = 0; i < _batchedControllers.Count; i++)
                {
                    _batchedControllers[i].BatchedRenderer.RecordMainDraws(commandBuffer);
                }
            }

            /// 입력: commandBuffer(CommandBuffer), data(PassData), rendererGroup(ref RendererGroupData); 반환: bool.
            private static bool TryDrawGroupBatched(CommandBuffer commandBuffer, PassData data, ref RendererGroupData rendererGroup)
            {
                var controllers = rendererGroup.RenderControllers;

                // Record 시 모든 controller가 배치 가능으로 확정됐으면 재검사하지 않습니다.
                // 이 frame에는 legacy fullscreen texture를 만들지 않았으므로 fallback 접근 자체가 잘못됩니다.
                if (controllers == null
                    || (!data.AllControllersBatched && !AreAllControllersBatched(controllers)))
                {
                    return false;
                }

                DrawGroupBatched(commandBuffer, data, controllers, false);

                // 배치 group 결과도 legacy와 같은 시점에 카메라 target으로 합성합니다.
                if (CubismRenderControllerGroup.GetInstance().IsCopiedToCameraTexture)
                {
                    _commandBuffer.SetRenderTarget(data.CameraTextureHandle, data.CameraDepthTextureHandle);

                    _blitRenderTextureMaterial.SetTexture(CubismShaderVariables.MainTexture, data.CommonRenderingTextureHandle);

                    var reversedZ = SystemInfo.usesReversedZBuffer ? GEqual : LEqual;
                    _blitRenderTextureMaterial.SetInt(CubismShaderVariables.ReversedZ, reversedZ);

                    _commandBuffer.DrawMesh(_blitRenderTextureMesh, Matrix4x4.identity, _blitRenderTextureMaterial);

                    _commandBuffer.SetRenderTarget(data.CommonRenderingTextureHandle);
                    _commandBuffer.ClearRenderTarget(true, true, Color.clear);
                }

                return true;
            }

            /// 입력: data(PassData), clearCommonAfter(bool); 반환: 없음.
            private static void BlitCommonToCameraTarget(PassData data, bool clearCommonAfter)
            {
                _commandBuffer.SetRenderTarget(data.CameraTextureHandle, data.CameraDepthTextureHandle);

                _blitRenderTextureMaterial.SetTexture(CubismShaderVariables.MainTexture, data.CommonRenderingTextureHandle);

                var reversedZ = SystemInfo.usesReversedZBuffer ? GEqual : LEqual;
                _blitRenderTextureMaterial.SetInt(CubismShaderVariables.ReversedZ, reversedZ);

                _commandBuffer.DrawMesh(_blitRenderTextureMesh, Matrix4x4.identity, _blitRenderTextureMaterial);

                if (clearCommonAfter)
                {
                    _commandBuffer.SetRenderTarget(data.CommonRenderingTextureHandle);
                    _commandBuffer.ClearRenderTarget(true, true, Color.clear);
                }
            }

            /// 입력: commandBuffer(CommandBuffer), data(PassData), drawDirectlyToCameraTarget(bool); 반환: 없음.
            private static void DrawObjects(CommandBuffer commandBuffer, PassData data, bool drawDirectlyToCameraTarget)
            {
                // 모든 모델이 배치 경로면 중간 legacy texture 없이 카메라 target에 바로 기록합니다.
                if (drawDirectlyToCameraTarget)
                {
                    var groups = data.RenderControllerGroupDaraArray;

                    for (var groupIndex = 0; groupIndex < groups?.Length; groupIndex++)
                    {
                        DrawGroupBatched(commandBuffer, data, groups[groupIndex].Controllers, true);
                    }

                    return;
                }

                // 이번 draw 시작에 풀의 offscreen texture 내용을 초기화합니다.
                CubismOffscreenRenderTextureManager.GetInstance().ClearRenderTextures(_commandBuffer);

                // 현재 offscreen 계층 기준으로 renderer별 SkipRendering 상태를 갱신합니다.
                CheckRenderingSkip();

                for (var groupIndex = 0; groupIndex < _sortedRendererGroupDataArray?.Length; groupIndex++)
                {
                    var rendererGroup = _sortedRendererGroupDataArray[groupIndex];

                    if (rendererGroup.Renderers == null)
                    {
                        continue;
                    }

                    // group의 모든 모델이 가능하면 공유 mesh 배치 경로를 기록합니다.
                    if (TryDrawGroupBatched(commandBuffer, data, ref _sortedRendererGroupDataArray[groupIndex]))
                    {
                        continue;
                    }

#if UNITY_EDITOR
                    // interceptor 이벤트에 줄 카메라 거리를 각 renderer에 계산합니다.
                    // 편집기에서는 Scene/Game 카메라 정보가 섞일 수 있어 매번 다시 계산합니다.
                    for (var rendererIndex = 0; rendererIndex < rendererGroup.Renderers.Length; rendererIndex++)
                    {
                        var target = rendererGroup.Renderers[rendererIndex];

                        if (!target)
                        {
                            continue;
                        }

                        target.CalculateDistanceToCamera(data.CameraData.worldSpaceCameraPos, data.CameraData.camera.transform.forward);
                    }
#endif

                    for (var rendererIndex = 0; rendererIndex < rendererGroup.Renderers.Length; rendererIndex++)
                    {
                        var renderer = rendererGroup.Renderers[rendererIndex];

                        // renderer가 없거나 비활성이면 command를 기록하지 않습니다.
                        if (!renderer
                            || renderer.SkipRendering)
                        {
                            continue;
                        }

                        var previousRenderer = rendererIndex > 0
                            ? rendererGroup.Renderers[rendererIndex - 1]
                            : null;
                        var nextRenderer = rendererIndex < rendererGroup.Renderers.Length - 1
                            ? rendererGroup.Renderers[rendererIndex + 1]
                            : null;

                        var args = new CubismRenderedEventArgs()
                        {
                            PassData = data,
                            CommandBuffer = commandBuffer,
                            ColorBuffer = renderer.RenderController.CurrentFrameBuffer,
                            DepthBuffer = data.CameraDepthTextureHandle,
                            SortingOrder = renderer.MeshRenderer.sortingOrder,
                            Drawable = renderer.Drawable,
                            SortingMode = renderer.RenderController.SortingMode,
                            GroupSortingOrder = rendererGroup.SortingIndex,
                            Distance = renderer.DistanceToCamera,
                            NextDistance = nextRenderer != null ? nextRenderer.DistanceToCamera : null,
                            PreviousDistance = previousRenderer != null ? previousRenderer.DistanceToCamera : null,
                            CameraPos = data.CameraData.worldSpaceCameraPos,
                            CameraForward = data.CameraData.camera.transform.forward
                        };

                        // 실제 draw 직전 interceptor가 command를 추가할 기회를 줍니다.
                        CubismRenderingInterceptorsManager.GetInstance().OnPreRendering(args);

                        // 현재 Drawable 또는 Offscreen의 draw command를 기록합니다.
                        renderer.DrawObject(commandBuffer, data);

                        // draw 직후 interceptor가 후처리 command를 추가할 기회를 줍니다.
                        CubismRenderingInterceptorsManager.GetInstance().OnPostRendering(args);

                        if (renderer.IsLastDrawObjectInModel)
                        {
                            renderer.RenderController.SubmitDrawOffscreen(commandBuffer, data);
                        }
                    }

                    // 공용 texture에 쌓은 결과가 있으면 카메라 color target으로 합성합니다.
                    if (CubismRenderControllerGroup.GetInstance().IsCopiedToCameraTexture)
                    {
                        _commandBuffer.SetRenderTarget(data.CameraTextureHandle, data.CameraDepthTextureHandle);

                        // fullscreen quad로 공용 texture를 카메라 texture에 그립니다.
                        _blitRenderTextureMaterial.SetTexture(CubismShaderVariables.MainTexture, data.CommonRenderingTextureHandle);

                        // 플랫폼의 reversed-Z 여부에 맞는 depth compare 값을 고릅니다.
                        var reversedZ = SystemInfo.usesReversedZBuffer ? GEqual : LEqual;
                        _blitRenderTextureMaterial.SetInt(CubismShaderVariables.ReversedZ, reversedZ);

                        // 선택한 depth compare로 fullscreen 합성 mesh를 그립니다.
                        _commandBuffer.DrawMesh(_blitRenderTextureMesh, Matrix4x4.identity, _blitRenderTextureMaterial);

                        // 다음 group 합성을 위해 공용 texture를 비웁니다.
                        _commandBuffer.SetRenderTarget(data.CommonRenderingTextureHandle);
                        _commandBuffer.ClearRenderTarget(true, true, Color.clear);
                    }
                }

                // frame 처리 뒤 controller의 순서 변경 플래그를 초기화합니다.
                for (var i = 0; i < data.RenderControllers?.Length; i++)
                {
                    var controller = data.RenderControllers[i];
                    if (!controller || !controller.enabled || !controller.gameObject.activeSelf)
                    {
                        continue;
                    }

                    controller.DidChangeSorting = false;
                    controller.DidChangeDrawableRenderOrder = false;
                }

                CubismRenderControllerGroup.GetInstance().DidChangeSortingRenderControllerGroup = false;
            }

            /// 입력: data(PassData), context(UnsafeGraphContext); 반환: 없음.
            private static void ExecutePass(PassData data, UnsafeGraphContext context)
            {
                // 처리할 Cubism controller가 없으면 RenderGraph 실행을 생략합니다.
                if (data.RenderControllers == null || data.RenderControllers.Length == 0)
                {
                    return;
                }

                if (_commandBuffer == null)
                {
                    _commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                }

                if (!_blitRenderTextureMesh)
                {
                    _blitRenderTextureMesh = new Mesh
                    {
                        vertices = CubismRenderer.OffscreenVertices,
                        uv = CubismRenderer.OffscreenUVs,
                        triangles = CubismRenderer.OffscreenTriangle
                    };

                    _blitRenderTextureMesh.RecalculateBounds();
                }

                if (!_blitRenderTextureMaterial)
                {
                    _blitRenderTextureMaterial = new Material(CubismBuiltinMaterials.UnlitBlit);
                }

                // 모든 모델이 배치 경로면 중간 texture·clear·blit 없이 카메라 target에 직접 그립니다.
                // Record 시 저장한 결정으로 실행 경로와 이 frame에 할당한 texture를 일치시킵니다.
                if (data.DrawDirectlyToCameraTarget)
                {
                    DrawObjects(_commandBuffer, data, true);

                    return;
                }

#if UNITY_EDITOR
                // 편집기 Scene View 카메라는 최신 texture 상태가 아닐 수 있어 별도 경로를 유지합니다.
                if (data.CameraData.isSceneViewCamera)
                {
                    _commandBuffer.Blit(data.CameraTextureHandle, data.CommonRenderingTextureHandle);
                }
#endif

                // 모든 모델이 배치 경로면 live controller group을 바로 그립니다.
                // 아래 legacy 정렬·skip 검사·플래그 초기화는 per-Drawable fallback 전용이므로 이 경로에서는 수행하지 않습니다.
                if (data.AllControllersBatched)
                {
                    // color는 아래에서 완전히 clear하므로 이전 transient 내용을 tile memory에 불러올 필요가 없습니다.
                    // camera depth는 이후 pass도 읽는 scene 상태이므로 load·store를 유지합니다.
                    _commandBuffer.SetRenderTarget(
                        data.CommonRenderingTextureHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                        data.CameraDepthTextureHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    _commandBuffer.ClearRenderTarget(false, true, Color.clear);

                    var groups = data.RenderControllerGroupDaraArray;
                    var copyPerGroup = CubismRenderControllerGroup.GetInstance().IsCopiedToCameraTexture;

                    for (var groupIndex = 0; groupIndex < groups?.Length; groupIndex++)
                    {
                        DrawGroupBatched(_commandBuffer, data, groups[groupIndex].Controllers, false);

                        if (copyPerGroup)
                        {
                            BlitCommonToCameraTarget(data, true);
                        }
                    }

                    if (!copyPerGroup)
                    {
                        // 공용 texture는 frame 한정 transient라 최종 합성 뒤 clear할 필요가 없습니다.
                        BlitCommonToCameraTarget(data, false);
                    }

                    return;
                }

                // legacy renderer를 sorting order 기준으로 다시 정렬합니다.
                SortingRendererGroups(data);

                // Unity 물체와 올바르게 depth test하도록 color·depth target을 함께 바인딩합니다.
                // color는 아래에서 clear하므로 이전 내용은 load하지 않습니다.
                _commandBuffer.SetRenderTarget(
                    data.CommonRenderingTextureHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                    data.CameraDepthTextureHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                _commandBuffer.ClearRenderTarget(false, true, Color.clear);

                // 정렬한 legacy draw object command를 기록합니다.
                DrawObjects(_commandBuffer, data, false);

                // 공용 texture 결과를 카메라 color target으로 합성합니다.
                if (!CubismRenderControllerGroup.GetInstance().IsCopiedToCameraTexture)
                {
                    _commandBuffer.SetRenderTarget(data.CameraTextureHandle, data.CameraDepthTextureHandle);

                    // fullscreen quad로 공용 texture를 카메라 texture에 그립니다.
                    _blitRenderTextureMaterial.SetTexture(CubismShaderVariables.MainTexture, data.CommonRenderingTextureHandle);

                    // reversed-Z 환경에 맞는 depth compare 값을 선택합니다.
                    var reversedZ = SystemInfo.usesReversedZBuffer? GEqual : LEqual;
                    _blitRenderTextureMaterial.SetInt(CubismShaderVariables.ReversedZ, reversedZ);

                    // 선택한 depth compare로 fullscreen 합성을 기록합니다.
                    _commandBuffer.DrawMesh(_blitRenderTextureMesh, Matrix4x4.identity, _blitRenderTextureMaterial);
                }

                // 다음 frame의 legacy 합성을 위해 공용 texture를 비웁니다.
                _commandBuffer.SetRenderTarget(data.CommonRenderingTextureHandle);
                _commandBuffer.ClearRenderTarget(true, true, Color.clear);
            }

            /// 입력: renderGraph(RenderGraph), frameData(ContextContainer); 반환: 없음.
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                const string renderCustomPass = "Cubism URP Render Pass";

                // 플레이·편집 모드 모두에서 현재 등록된 render controller를 읽습니다.
                var renderControllers = CubismRenderControllerGroup.GetInstance().RenderControllers;

                // 등록 controller가 없으면 pass를 RenderGraph에 추가하지 않습니다.
                if (renderControllers == null || renderControllers.Length == 0)
                {
                    return;
                }

                // ExecutePass에 전달할 데이터 타입과 이름을 지정해 raster pass를 등록합니다.
                using (var builder = renderGraph.AddUnsafePass<PassData>(renderCustomPass, out var passData))
                {
                    // controller·camera·URP resource를 pass 데이터에 기록합니다.
                    passData.RenderControllers = renderControllers;

                    var renderControllerGroups = CubismRenderControllerGroup.GetInstance().GroupDataArray;
                    passData.RenderControllerGroupDaraArray = renderControllerGroups;

                    var cameraData = frameData.Get<UniversalCameraData>();
                    var resourceData = frameData.Get<UniversalResourceData>();

                    passData.CameraData = cameraData;
                    passData.ResourceData = resourceData;

                    // Record 시 경로를 결정해 pass 데이터에 보관하고, Execute가 같은 texture 구성만 사용하게 합니다.
                    var allControllersBatched = AreAllControllersBatched(renderControllers);
                    var drawDirectlyToCameraTarget = allControllersBatched
                        && (CubismBatchedRendering.DrawToCameraTargetDirectly
                            || (CubismBatchedRendering.AutoDrawToCameraTargetDirectly
                                && CanSkipBufferedComposition(renderControllers)));

                    passData.AllControllersBatched = allControllersBatched;
                    passData.DrawDirectlyToCameraTarget = drawDirectlyToCameraTarget;

                    // 모델이 카메라 target 직행 대신 공용 texture에 그릴 때만 composite target을 만듭니다.
                    if (!drawDirectlyToCameraTarget)
                    {
                        var descriptor = resourceData.activeColorTexture.GetDescriptor(renderGraph);
                        descriptor.wrapMode = TextureWrapMode.Repeat;
                        descriptor.filterMode = FilterMode.Point;

                        descriptor.name = "CommonTexture";
                        passData.CommonRenderingTextureHandle = renderGraph.CreateTexture(descriptor);
                        builder.UseTexture(passData.CommonRenderingTextureHandle, AccessFlags.ReadWrite);
                    }

                    // 임시·mask fullscreen texture는 고정밀 마스크·blend 합성이 필요한 legacy 모델만 사용합니다.
                    // 배치 모델은 자체 mask atlas로 clipping합니다.
                    if (!allControllersBatched)
                    {
                        var descriptor = resourceData.activeColorTexture.GetDescriptor(renderGraph);
                        descriptor.wrapMode = TextureWrapMode.Repeat;
                        descriptor.filterMode = FilterMode.Point;

                        descriptor.name = "TempTexture";
                        passData.CommonTemporaryTextureHandle = renderGraph.CreateTexture(descriptor);
                        builder.UseTexture(passData.CommonTemporaryTextureHandle, AccessFlags.ReadWrite);

                        descriptor.name = "MaskTexture";
                        var maskTextureHandle = renderGraph.CreateTexture(descriptor);
                        builder.UseTexture(maskTextureHandle, AccessFlags.ReadWrite);
                        passData.MaskTextureHandle = maskTextureHandle;
                    }

                    // 이 pass의 color output을 현재 활성 카메라 color texture로 지정합니다.
                    passData.CameraTextureHandle = resourceData.activeColorTexture;
                    builder.UseTexture(passData.CameraTextureHandle, AccessFlags.ReadWrite);

                    // Unity 다른 물체와 depth test하도록 camera depth texture를 연결합니다.
                    passData.CameraDepthTextureHandle = resourceData.activeDepthTexture;
                    builder.UseTexture(passData.CameraDepthTextureHandle);

                    // RenderGraph가 pass 실행 때 호출할 ExecutePass delegate를 연결합니다.
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
                }
            }
        }

        private CubismRenderPass _mScriptablePass;

        /// 입력: 없음; 반환: 없음.
        public override void Create()
        {
            _mScriptablePass = new CubismRenderPass
            {
                // Cubism pass를 투명 오브젝트 이전 렌더 이벤트에 주입합니다.
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        /// 입력: renderer(ScriptableRenderer), renderingData(ref RenderingData); 반환: 없음.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            // 편집기 Scene View·Game View 카메라 중 중복 실행 대상에는 pass를 넣지 않습니다.
            if (!(renderingData.cameraData.cameraType == CameraType.Game
                || renderingData.cameraData.cameraType == CameraType.SceneView))
            {
                return;
            }
#endif

            renderer.EnqueuePass(_mScriptablePass);
        }
    }
}
