/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 모바일에서 Drawable별 draw를 한 번의 배치 경로로 합칠 때 필요한 활성·중단·복구 상태를 소유합니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering.URP.RenderingInterceptor;
using UnityEngine;


namespace Live2D.Cubism.Rendering
{
    public sealed partial class CubismRenderController
    {
        [SerializeField, HideInInspector]
        public bool ForceLegacyRendering;

        public bool IsBatchedRenderingActive { get; private set; }

        internal CubismBatchedModelRenderer BatchedRenderer { get; private set; }

        private bool _isBatchedRendererSuspended;


        /// 입력: 없음; 반환: 없음.
        private void TryActivateBatchedRendering()
        {
            IsBatchedRenderingActive = false;

            if (!Application.isPlaying
                || !CubismBatchedRendering.Enabled
                || ForceLegacyRendering
                || CubismBatchedRendering.Shader == null
                || !Model)
            {
                return;
            }

            // interceptor는 drawable별 draw event를 요구하므로 하나로 합친 배치 경로를 사용할 수 없습니다.
            if (GetComponent<ICubismRenderingInterceptor>() != null
                || CubismRenderingInterceptorsManager.GetInstance().Interceptors.Length > 0)
            {
                return;
            }

            if (!CubismBatchedModelRenderer.IsModelEligible(this))
            {
                return;
            }

            IsBatchedRenderingActive = true;
        }


        /// 입력: 없음; 반환: 없음.
        private void SuspendBatchedRenderer()
        {
            if (BatchedRenderer != null)
            {
                _isBatchedRendererSuspended = true;

                // 정지 중에는 코어 이벤트를 소비하지 않으므로 관리 사본 생성을 되살립니다.
                // 다시 켤 때 배치 경로가 아닐 수도 있고, 그러면 legacy 렌더러가 정지 시점 기하로 얼어붙습니다.
                BatchedRenderer.ReleaseManagedVertexCopySuppression();
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void OnDestroy()
        {
            DisposeBatchedRenderer();
        }


#if !UNITY_EDITOR
        private bool _pausedSinceVolatileRefresh;
#endif


        /// 입력: isPaused(bool); 반환: 없음.
        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused)
            {
#if !UNITY_EDITOR
                _pausedSinceVolatileRefresh = true;
#endif
                return;
            }

            RefreshVolatileGpuResourcesAfterGap();
        }


        /// 입력: hasFocus(bool); 반환: 없음.
        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                return;
            }

#if !UNITY_EDITOR
            if (!_pausedSinceVolatileRefresh)
            {
                return;
            }
#endif

            RefreshVolatileGpuResourcesAfterGap();
        }


        /// 입력: 없음; 반환: 없음.
        private void RefreshVolatileGpuResourcesAfterGap()
        {
#if !UNITY_EDITOR
            _pausedSinceVolatileRefresh = false;
#endif

            if (IsBatchedRenderingActive
                && BatchedRenderer != null
                && !_isBatchedRendererSuspended)
            {
                BatchedRenderer.RefreshVolatileGpuResources();
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void TryInitializeBatchedRenderer()
        {
            if (!IsBatchedRenderingActive)
            {
                // 중단 중 배치가 비활성화되면 남은 GPU 자원을 해제하고, legacy mesh 복구는 OnEnable에 맡깁니다.
                if (BatchedRenderer != null)
                {
                    DisposeBatchedRenderer();
                }

                return;
            }

            if (BatchedRenderer != null)
            {
                if (!_isBatchedRendererSuspended)
                {
                    if (BatchedRenderer.IsValid)
                    {
                        return;
                    }

                    // 실행 중 외부에서 GPU 자원이 파괴되면 배치는 draw를 기록하지 못하고 legacy mesh도 없으므로, legacy로 되돌려 mesh를 다시 만듭니다.
                    DisposeBatchedRenderer();

                    var legacyRenderers = Renderers;
                    for (var i = 0; i < legacyRenderers.Length; i++)
                    {
                        legacyRenderers[i].TryInitialize(this);
                    }

                    return;
                }

                // 자원이 살아 있는 재활성화이면 새로 만들지 않고 최신 모델 상태만 다시 읽습니다.
                _isBatchedRendererSuspended = false;

                if (BatchedRenderer.ResumeAfterDisable())
                {
                    return;
                }

                // 중단 사이 모델 topology가 바뀌었으면 기존 buffer를 버리고 처음부터 다시 만듭니다.
                BatchedRenderer.Dispose();
                BatchedRenderer = null;
            }

            if (CubismBatchedModelRenderer.AreRenderersEligible(this))
            {
                BatchedRenderer = new CubismBatchedModelRenderer(this);
            }

            if (BatchedRenderer == null || !BatchedRenderer.IsValid)
            {
                // 배치 초기화 실패 시 생략했던 drawable별 mesh를 legacy 경로용으로 복구합니다.
                BatchedRenderer?.Dispose();
                BatchedRenderer = null;
                IsBatchedRenderingActive = false;

                var renderers = Renderers;
                for (var i = 0; i < renderers.Length; i++)
                {
                    renderers[i].TryInitialize(this);
                }
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void DisposeBatchedRenderer()
        {
            _isBatchedRendererSuspended = false;

            if (BatchedRenderer != null)
            {
                // legacy로 돌아올 때 drawable별 renderer 상태가 배치 이전 상태와 일치하도록 복원합니다.
                if (Application.isPlaying)
                {
                    BatchedRenderer.RestoreLegacyRendererState();
                }

                BatchedRenderer.Dispose();
                BatchedRenderer = null;
            }

            IsBatchedRenderingActive = false;
        }


        /// 입력: 없음; 반환: 해제한 배치 렌더러 수.
        /// 도메인 리로드처럼 OnDestroy가 보장되지 않는 경계에서 살아 있는 모든 배치 렌더러의
        /// 네이티브 자원을 즉시 해제한다(에디터 안전장치 전용 — 정상 수명은 OnDestroy가 소유).
        public static int DisposeAllLiveBatchedRenderers()
        {
            var controllers = Object.FindObjectsByType<CubismRenderController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var disposed = 0;

            for (var i = 0; i < controllers.Length; i++)
            {
                var controller = controllers[i];

                if (controller == null || controller.BatchedRenderer == null)
                {
                    continue;
                }

                controller.DisposeBatchedRenderer();
                disposed++;
            }

            return disposed;
        }


        /// 입력: sender(CubismModel), data(CubismDynamicDrawableData[]); 반환: bool.
        private bool TryConsumeDynamicDataBatched(CubismModel sender, CubismDynamicDrawableData[] data)
        {
            if (!IsBatchedRenderingActive)
            {
                return false;
            }

            TryInitializeBatchedRenderer();

            if (!IsBatchedRenderingActive || BatchedRenderer == null)
            {
                return false;
            }

            BatchedRenderer.ConsumeDynamicData(data);

            // 배치가 dynamic data를 소비해도 공개 draw-order handler 호출 계약은 그대로 유지합니다.
            var drawOrderHandler = DrawOrderHandlerInterface;

            if (drawOrderHandler != null)
            {
                var drawables = sender.Drawables;

                for (var i = 0; i < data.Length; ++i)
                {
                    if (data[i].IsDrawOrderDirty)
                    {
                        drawOrderHandler.OnDrawOrderDidChange(this, drawables[i], data[i].DrawOrder);
                    }
                }
            }

            return true;
        }
    }
}
