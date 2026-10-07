/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 모델의 Drawable 렌더러 캐시와 공통 불투명도·색·정렬 상태를 소유합니다.
// 동적 모델 값을 읽어 각 렌더러 또는 배치 렌더 경로에 반영합니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Rendering.URP.RenderingInterceptor;
using System;
using UnityEngine;
using Object = UnityEngine.Object;


namespace Live2D.Cubism.Rendering
{
    [ExecuteInEditMode, CubismDontMoveOnReimport]
    public sealed partial class CubismRenderController : MonoBehaviour, ICubismUpdatable
    {
        [SerializeField, HideInInspector]
        public float Opacity = 1f;

        [SerializeField, HideInInspector]
        private float _lastOpacity;

        private float LastOpacity
        {
            get { return _lastOpacity; }
            set { _lastOpacity = value; }
        }

        [SerializeField, HideInInspector]
        private bool _isOverriddenModelMultiplyColors;

        public bool MultiplyColorEnabled
        {
            get { return _isOverriddenModelMultiplyColors; }
            set { _isOverriddenModelMultiplyColors = value; }
        }

        [SerializeField, HideInInspector]
        private bool _isOverriddenModelScreenColors;

        public bool ScreenColorEnabled
        {
            get { return _isOverriddenModelScreenColors; }
            set { _isOverriddenModelScreenColors = value; }
        }

        [SerializeField, HideInInspector]
        private Color _modelMultiplyColor;

        public Color ModelMultiplyColor
        {
            get { return _modelMultiplyColor; }
            set { _modelMultiplyColor = value; }
        }

        [SerializeField, HideInInspector]
        private Color _modelScreenColor;

        public Color ModelScreenColor
        {
            get { return _modelScreenColor; }
            set { _modelScreenColor = value; }
        }

        public string SortingLayer
        {
            get
            {
                return UnityEngine.SortingLayer.IDToName(SortingLayerId);
            }
            set
            {
                SortingLayerId = UnityEngine.SortingLayer.NameToID(value);
            }
        }

        [SerializeField, HideInInspector]
        private int _sortingLayerId;

        public int SortingLayerId
        {
            get
            {
                return _sortingLayerId;
            }
            set
            {
                if (value == _sortingLayerId)
                {
                    return;
                }


                _sortingLayerId = value;


                // 새 sorting layer를 모든 Drawable renderer에 반영합니다.
                var renderers = Renderers;


                for (var i = 0; i < renderers.Length; ++i)
                {
                    renderers[i].OnControllerSortingLayerDidChange(_sortingLayerId);
                }

                SortRenderers();
            }
        }


        [SerializeField, HideInInspector]
        private CubismSortingMode _sortingMode;

        public CubismSortingMode SortingMode
        {
            get
            {
                return _sortingMode;
            }
            set
            {
                // 값이 같으면 sorting mode와 renderer를 다시 쓰지 않습니다.
                if (value == _sortingMode)
                {
                    return;
                }


                _sortingMode = value;


                // 음수 scale 축에 맞춰 front/back sorting 방향을 뒤집습니다.
                var renderers = Renderers;


                for (var i = 0; i < renderers.Length; ++i)
                {
                    renderers[i].OnControllerSortingModeDidChange(_sortingMode);
                }

                SortRenderers();
            }
        }


        [SerializeField, HideInInspector]
        private int _sortingOrder;

        public int SortingOrder
        {
            get
            {
                return _sortingOrder;
            }
            set
            {
                // 값이 같으면 sorting order를 다시 계산하지 않습니다.
                if (value == _sortingOrder)
                {
                    return;
                }


                _sortingOrder = value;


                // 계산한 sorting order를 하위 renderer에 반영합니다.
                var renderers = Renderers;


                for (var i = 0; i < renderers.Length; ++i)
                {
                    renderers[i].OnControllerSortingOrderDidChange(SortingOrder);
                }

                SortRenderers();
            }
        }


        [SerializeField]
        public Camera CameraToFace;



        [SerializeField, HideInInspector]
        private Object _drawOrderHandler;

        public Object DrawOrderHandler
        {
            get { return _drawOrderHandler; }
            set { _drawOrderHandler = value.ToNullUnlessImplementsInterface<ICubismDrawOrderHandler>(); }
        }


        [NonSerialized]
        private ICubismDrawOrderHandler _drawOrderHandlerInterface;

        private ICubismDrawOrderHandler DrawOrderHandlerInterface
        {
            get
            {
                if (_drawOrderHandlerInterface == null)
                {
                    _drawOrderHandlerInterface = DrawOrderHandler.GetInterface<ICubismDrawOrderHandler>();
                }


                return _drawOrderHandlerInterface;
            }
        }


        [SerializeField, HideInInspector]
        private Object _opacityHandler;

        public Object OpacityHandler
        {
            get { return _opacityHandler; }
            set { _opacityHandler = value.ToNullUnlessImplementsInterface<ICubismOpacityHandler>(); }
        }


        private ICubismOpacityHandler _opacityHandlerInterface;

        private ICubismOpacityHandler OpacityHandlerInterface
        {
            get
            {
                if (_opacityHandlerInterface == null)
                {
                    _opacityHandlerInterface = OpacityHandler.GetInterface<ICubismOpacityHandler>();
                }


                return _opacityHandlerInterface;
            }
        }


        [SerializeField, HideInInspector]
        private Object _multiplyColorHandler;

        public Object MultiplyColorHandler
        {
            get { return _multiplyColorHandler; }
            set { _multiplyColorHandler = value.ToNullUnlessImplementsInterface<ICubismBlendColorHandler>(); }
        }


        private ICubismBlendColorHandler _multiplyColorHandlerInterface;

        private ICubismBlendColorHandler MultiplyColorHandlerInterface
        {
            get
            {
                if (_multiplyColorHandlerInterface == null)
                {
                    _multiplyColorHandlerInterface = MultiplyColorHandler?.GetInterface<ICubismBlendColorHandler>();
                }


                return _multiplyColorHandlerInterface;
            }
        }

        [SerializeField, HideInInspector]
        private Object _screenColorHandler;

        public Object ScreenColorHandler
        {
            get { return _screenColorHandler; }
            set { _screenColorHandler = value.ToNullUnlessImplementsInterface<ICubismBlendColorHandler>(); }
        }


        private ICubismBlendColorHandler _screenColorHandlerInterface;

        private ICubismBlendColorHandler ScreenColorHandlerInterface
        {
            get
            {
                if (_screenColorHandlerInterface == null)
                {
                    _screenColorHandlerInterface = ScreenColorHandler?.GetInterface<ICubismBlendColorHandler>();
                }


                return _screenColorHandlerInterface;
            }
        }

        [SerializeField, HideInInspector]
        private float _depthOffset = 0.00001f;

        public float DepthOffset
        {
            get { return _depthOffset; }
            set
            {
                // depth offset이 같으면 renderer 갱신을 생략합니다.
                if (Mathf.Abs(value - _depthOffset) < Mathf.Epsilon)
                {
                    return;
                }


                // 새 depth offset을 controller 상태에 저장합니다.
                _depthOffset = value;


                // 저장한 depth offset을 legacy renderer에 반영합니다.
                var renderers = Renderers;


                for (var i = 0; i < renderers.Length; ++i)
                {
                    renderers[i].OnControllerDepthOffsetDidChange(_depthOffset);
                }
            }
        }

        [NonSerialized]
        private CubismModel _cubismModel;

        public CubismModel Model
        {
            get
            {
                if (_cubismModel == null)
                {
                    _cubismModel = this.FindCubismModel();
                }

                return _cubismModel;
            }
        }


        private Transform _drawablesRootTransform;

        private Transform DrawablesRootTransform
        {
            get
            {
                if (_drawablesRootTransform == null)
                {
                    _drawablesRootTransform = Model.Drawables[0].transform.parent;
                }


                return _drawablesRootTransform;
            }
        }

        [SerializeField]
        private CubismRenderer[] _renderers;

        public CubismRenderer[] Renderers
        {
            get
            {
                if (_renderers == null)
                {
                    TryInitialize();
                }

                return _renderers;
            }
            private set { _renderers = value; }
        }


        private Color[] _newMultiplyColors;

        private Color[] _newScreenColors;


        [HideInInspector]
        public bool HasUpdateController { get; set; }

        private bool _isInitialized = false;

        [HideInInspector]
        public bool IsInitialized
        {
            get
            {
                return _isInitialized;
            }
            private set
            {
                _isInitialized = value;
            }
        }

        /// 입력: 없음; 반환: 없음.
        public void TryInitialize()
        {
            // 모델 reload로 Drawable 수가 달라지면 기존 renderer cache는 인덱스 계약이 깨집니다.
            // 다음 초기화에서 새 Drawable 수에 맞게 만들도록 cache를 버립니다.
            if (_renderers != null && Model && Model.Drawables != null)
            {
                var newCount = Model.Drawables.Length;

                if (Model.Offscreens != null)
                {
                    newCount += Model.Offscreens.Length;
                }

                if (_renderers.Length != newCount)
                {
                    _renderers = null;
                }
            }

            // 모델에 이미 붙은 Drawable renderer를 찾아 cache 후보로 읽습니다.
            var renderers = _renderers;
            TryInitializeRenderers(renderers);

            if (_renderers == null
                || _renderers.Length < 1)
            {
                return;
            }

            // 모든 Drawable renderer가 mesh·material 상태를 갖도록 초기화합니다.
            for (var i = 0; i < Renderers.Length; ++i)
            {
                var targetRenderer = Renderers[i];
                targetRenderer.TryInitialize(this);
                if (!HasRootPartOffscreen)
                {
                    continue;
                }

                HasRootPartOffscreen = CheckHasRootPartOffscreen(targetRenderer);
            }

            // 첫 renderer에서 sorting layer를 읽어 controller 초기 상태로 설정합니다.
            // setter가 다시 renderer에 쓰지 않도록 backing field에 직접 저장합니다.
            _sortingLayerId = Renderers[0]
                .MeshRenderer
                .sortingLayerID;

            OnAfterRenderersInitialize(Renderers);

            IsInitialized = true;
        }

        /// 입력: 없음; 반환: 없음.
        private void UpdateOpacity()
        {
            // opacity가 같으면 변경 이벤트와 renderer 갱신을 생략합니다.
            if (Mathf.Abs(Opacity - LastOpacity) < Mathf.Epsilon)
            {
                return;
            }


            // 새 opacity를 controller 상태에 저장합니다.
            Opacity = Mathf.Clamp(Opacity, 0f, 1f);
            LastOpacity = Opacity;


            // legacy renderer에는 opacity를 즉시 반영합니다.
            // batched path는 draw 기록 시 controller opacity를 직접 읽습니다.
            var applyOpacityToRenderers = !IsBatchedRenderingActive
                && (OpacityHandlerInterface == null || Opacity > (1f - Mathf.Epsilon));


            if (applyOpacityToRenderers && Renderers != null)
            {
                var renderers = Renderers;


                for (var i = 0; i < renderers.Length; ++i)
                {
                    renderers[i].OnModelOpacityDidChange(Opacity);
                }
            }


            // 등록된 opacity handler에 확정된 값을 알립니다.
            if (OpacityHandlerInterface != null)
            {
                OpacityHandlerInterface.OnOpacityDidChange(this, Opacity);
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void UpdateDrawableBlendColors()
        {
            if (Renderers == null
                || !IsInitialized)
            {
                return;
            }

            var isMultiplyColorUpdated = false;
            var isScreenColorUpdated = false;

            if ((_newMultiplyColors?.Length ?? 0) != Renderers.Length)
            {
                _newMultiplyColors = new Color[Renderers.Length];
            }

            if ((_newScreenColors?.Length ?? 0) != Renderers.Length)
            {
                _newScreenColors = new Color[Renderers.Length];
            }

            for (var i = 0; i < Renderers.Length; i++)
            {
                if (!Renderers[i])
                {
                    continue;
                }

                var isUseUserMultiplyColor = (Renderers[i].DrawObjectMultiplyColorEnabled ||
                                              MultiplyColorEnabled);

                if (isUseUserMultiplyColor)
                {
                    // 모델 색을 쓰던 설정으로 돌아오면 보관한 모델 multiply 색을 복원합니다.
                    if (!Renderers[i].LastIsUseUserMultiplyColor)
                    {
                        Renderers[i].MultiplyColor = Renderers[i].LastMultiplyColor;
                        Renderers[i].ApplyMultiplyColor();
                        isMultiplyColorUpdated = true;
                    }
                    else if (Renderers[i].LastMultiplyColor != Renderers[i].MultiplyColor)
                    {
                        Renderers[i].ApplyMultiplyColor();
                        isMultiplyColorUpdated = true;
                    }

                    Renderers[i].LastMultiplyColor = Renderers[i].MultiplyColor;
                }
                else if (Renderers[i].LastIsUseUserMultiplyColor)
                {
                    Renderers[i].MultiplyColor = Renderers[i].LastMultiplyColor;
                    Renderers[i].ApplyMultiplyColor();
                    isMultiplyColorUpdated = true;
                }

                _newMultiplyColors[i] = Renderers[i].MultiplyColor;
                Renderers[i].LastIsUseUserMultiplyColor = isUseUserMultiplyColor;

                var isUseUserScreenColor = (Renderers[i].DrawObjectScreenColorEnabled ||
                                            ScreenColorEnabled);

                if (isUseUserScreenColor)
                {
                    // 모델 색을 쓰던 설정으로 돌아오면 보관한 모델 screen 색을 복원합니다.
                    if (!Renderers[i].LastIsUseUserScreenColors)
                    {
                        Renderers[i].ScreenColor = Renderers[i].LastScreenColor;
                        Renderers[i].ApplyScreenColor();
                        isScreenColorUpdated = true;
                    }
                    else if (Renderers[i].LastScreenColor != Renderers[i].ScreenColor)
                    {
                        Renderers[i].ApplyScreenColor();
                        isScreenColorUpdated = true;
                    }

                    Renderers[i].LastScreenColor = Renderers[i].ScreenColor;
                }
                else if (Renderers[i].LastIsUseUserScreenColors)
                {
                    Renderers[i].ScreenColor = Renderers[i].LastScreenColor;
                    Renderers[i].ApplyScreenColor();
                    isScreenColorUpdated = true;
                }

                _newScreenColors[i] = Renderers[i].ScreenColor;
                Renderers[i].LastIsUseUserScreenColors = isUseUserScreenColor;
            }

            if (MultiplyColorHandler != null && isMultiplyColorUpdated)
            {
                MultiplyColorHandlerInterface.OnBlendColorDidChange(this, _newMultiplyColors);
            }

            if (ScreenColorHandler != null && isScreenColorUpdated)
            {
                ScreenColorHandlerInterface.OnBlendColorDidChange(this, _newScreenColors);
            }
        }

        /// 입력: cameraPosition(Vector3); 반환: 없음.
        internal void UpdateDidChangeSortingFromZ(Vector3 cameraPosition)
        {
            // depth 정렬 모드가 아니면 카메라 방향 변화는 정렬에 영향을 주지 않습니다.
            if (!SortingMode.SortByDepth())
            {
                return;
            }

            for (var i = 0; i < Renderers?.Length; i++)
            {
                var cubismRenderer = Renderers[i];

                if (!cubismRenderer)
                {
                    continue;
                }

                // 마지막 정렬 때의 카메라 방향과 달라졌는지 확인합니다.
                DidChangeSorting |= cubismRenderer.DidUpdateDirectionFromLastSorted(cameraPosition);
            }
        }

        public int ExecutionOrder
        {
            get { return CubismUpdateExecutionOrder.CubismRenderController; }
        }

        public bool NeedsUpdateOnEditing
        {
            get { return true; }
        }

        /// 입력: 없음; 반환: 없음.
        public void OnLateUpdate()
        {
            // 비활성 controller는 모델 상태를 갱신하지 않고 종료합니다.
            if (!enabled)
            {
                return;
            }

            // 필요하면 controller opacity를 legacy renderer 상태에 반영합니다.
            UpdateOpacity();

            // 필요하면 모델 multiply·screen 색을 renderer에 반영합니다.
            UpdateDrawableBlendColors();

            // 바라볼 카메라가 없으면 방향·정렬 갱신을 생략합니다.
            if (CameraToFace == null)
            {
                return;
            }

            // 현재 카메라 위치를 기준으로 depth 정렬 변화를 계산합니다.
            DrawablesRootTransform.rotation = (Quaternion.LookRotation(CameraToFace.transform.forward, Vector3.up));
        }

        #region Unity Event Handling

        /// 입력: 없음; 반환: 없음.
        private void Start()
        {
            // update 순서를 소유하는 CubismUpdateController를 찾습니다.
            HasUpdateController = (GetComponent<CubismUpdateController>() != null);
        }

        /// 입력: 없음; 반환: 없음.
        private void OnEnable()
        {
            // 모델이 아직 없으면 등록·초기화를 하지 않고 종료합니다.
            if (!Model)
            {
                return;
            }

            CurrentOffscreenUnmanagedIndex = -1;

            // renderer 초기화 전에 batch 사용 여부를 확정해야 개별 mesh 생성을 건너뛸 수 있습니다.
            Model.Revive();
            TryActivateBatchedRendering();

            // batch 여부와 관계없이 renderer 참조 배열은 준비합니다.
            if (!IsInitialized)
            {
                TryInitialize();
            }
            else if (!IsBatchedRenderingActive)
            {
                // 재활성화 때 batch가 불가하면 이전 batch 세션에서 mesh 생성을 건너뛴 renderer를 복구합니다.
                var renderers = Renderers;

                for (var i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null && renderers[i].Mesh == null)
                    {
                        renderers[i].TryInitialize(this);
                    }
                }
            }

            TryInitializeBatchedRenderer();


            // core의 동적 Drawable 데이터 변경을 받을 listener를 등록합니다.
            Model.OnDynamicDrawableData += OnDynamicDrawableData;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Model.ForceUpdateNow();

                for (var drawableIndex = 0; drawableIndex < DrawableRenderers.Length; drawableIndex++)
                {
                    DrawableRenderers[drawableIndex].SwapMeshes();
                }
            }
#endif

            if (GetComponent<ICubismRenderingInterceptor>() != null)
            {
                // interceptor가 직접 draw 순서를 소유하므로 공용 controller group에는 중복 등록하지 않습니다.
                return;
            }

            // 공용 render controller group에 등록해 URP pass가 이 모델을 찾게 합니다.
            CubismRenderControllerGroup.GetInstance().AddRenderController(this);
        }

        /// 입력: 없음; 반환: 없음.
        private void OnDisable()
        {
            // 아바타 전력 관리의 disable/enable 사이에는 batch GPU 자원을 유지해 재개 끊김을 피합니다.
            // 실제 해제는 OnDestroy가 담당합니다.
            SuspendBatchedRenderer();

            // 모델이 이미 해제되었으면 event·group 해제를 더 하지 않습니다.
            if (!Model)
            {
                return;
            }

            // core 동적 데이터 listener를 해제해 비활성 controller가 갱신되지 않게 합니다.
            Model.OnDynamicDrawableData -= OnDynamicDrawableData;

            // 공용 controller group에서 제거해 URP pass가 더 이상 draw하지 않게 합니다.
            CubismRenderControllerGroup.GetInstance().RemoveRenderController(this);
        }

#endregion

        #region Cubism Event Handling

        /// 입력: 없음; 반환: 없음.
        private void LateUpdate()
        {
            if (!HasUpdateController)
            {
                OnLateUpdate();
            }
        }

        /// 입력: renderers(CubismRenderer[]), unmanagedIndex(int); 반환: int.
        private static int IndexOfDrawable(CubismRenderer[] renderers, int unmanagedIndex)
        {
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].Drawable.UnmanagedIndex == unmanagedIndex)
                {
                    return i;
                }
            }

            return -1;
        }


        /// 입력: sender(CubismModel), data(CubismDynamicDrawableData[]); 반환: 없음.
        private void OnDynamicDrawableData(CubismModel sender, CubismDynamicDrawableData[] data)
        {
            // batch 경로가 활성화되면 하나의 합친 mesh가 동적 데이터를 소비하므로 개별 mesh 갱신을 건너뜁니다.
            if (TryConsumeDynamicDataBatched(sender, data))
            {
                return;
            }

            // 변경 index를 기존 renderer에 연결할 Drawable·renderer 배열을 읽습니다.
            var drawables = sender.Drawables;
            var renderers = DrawableRenderers;


            // core가 전달한 Drawable별 변경 플래그를 순서대로 반영합니다.
            for (var dataIndex = 0; dataIndex < data.Length; ++dataIndex)
            {
                var rendererIndex = (dataIndex < renderers.Length && renderers[dataIndex].Drawable.UnmanagedIndex == dataIndex)
                    ? dataIndex
                    : IndexOfDrawable(renderers, dataIndex);

                // index에 맞는 renderer가 없으면 해당 변경은 반영할 대상이 없습니다.
                if (rendererIndex < 0) {
                    continue;
                }

                // 하나라도 mesh에 반영되면 마지막에 front/back buffer를 교체합니다.
                var swapMeshes = false;

                // 이전 SwapInfo가 남긴 표시 상태를 먼저 반영합니다.
                renderers[rendererIndex].UpdateVisibility();

                // 이전 SwapInfo가 남긴 render order를 먼저 반영합니다.
                renderers[rendererIndex].UpdateRenderOrder();

                // 어떤 상태도 바뀌지 않은 Drawable은 mesh 작업을 건너뜁니다.
                if (!data[dataIndex].IsAnyDirty)
                {
                    continue;
                }


                // 새 표시 여부를 renderer 상태에 반영합니다.
                if (data[dataIndex].IsVisibilityDirty)
                {
                    renderers[rendererIndex].OnDrawableVisiblityDidChange(data[dataIndex].IsVisible);

                    swapMeshes = true;
                }


                // 새 render order와 group 정렬 필요 상태를 갱신합니다.
                if (data[dataIndex].IsRenderOrderDirty)
                {
                    renderers[rendererIndex].OnDrawableRenderOrderDidChange(data[dataIndex].RenderOrder);
                    DidChangeDrawableRenderOrder = true;
                    swapMeshes = true;
                }


                // 새 opacity를 renderer 색상 상태에 반영합니다.
                if (data[dataIndex].IsOpacityDirty)
                {
                    renderers[rendererIndex].OnDrawableOpacityDidChange(data[dataIndex].Opacity);


                    swapMeshes = true;
                }


                // core가 준 새 정점을 renderer mesh back buffer에 씁니다.
                if (data[dataIndex].AreVertexPositionsDirty)
                {
                    renderers[rendererIndex].OnDrawableVertexPositionsDidChange(data[dataIndex].VertexPositions);


                    swapMeshes = true;
                }


                // 변경한 back buffer를 front로 교체해 이번 frame draw가 최신 mesh를 읽게 합니다.
                // 일부 mesh만 교체하면 frame 안에서 형태가 어긋날 수 있어 현재는 사용하지 않습니다.
                if (swapMeshes)
                {
                    renderers[rendererIndex].SwapMeshes();
                }
            }


            // draw order handler가 있으면 변경된 core 순서를 외부 소유자에게 전달합니다.
            var drawOrderHandler = DrawOrderHandlerInterface;


            if (drawOrderHandler != null)
            {
                for (var i = 0; i < data.Length; ++i)
                {
                    if (data[i].IsDrawOrderDirty)
                    {
                        drawOrderHandler.OnDrawOrderDidChange(this, drawables[i], data[i].DrawOrder);
                    }
                }
            }

            var isMultiplyColorUpdated = false;
            var isScreenColorUpdated = false;
            _newMultiplyColors ??= new Color[renderers.Length];
            _newScreenColors ??= new Color[renderers.Length];
            var newMultiplyColors = _newMultiplyColors;
            var newScreenColors = _newScreenColors;

            for (var dataIndex = 0; dataIndex < data.Length; ++dataIndex)
            {
                var rendererIndex = (dataIndex < renderers.Length && renderers[dataIndex].Drawable.UnmanagedIndex == dataIndex)
                    ? dataIndex
                    : IndexOfDrawable(renderers, dataIndex);

                // index에 맞는 renderer가 없으면 multiply 색 처리를 건너뜁니다.
                if (rendererIndex < 0)
                {
                    continue;
                }

                var isUseModelMultiplyColor = !(renderers[rendererIndex].DrawObjectMultiplyColorEnabled ||
                                                MultiplyColorEnabled);

                // object 또는 controller override 색을 쓰면 모델 색 변경을 덮어쓰지 않습니다.
                if (data[dataIndex].IsBlendColorDirty && isUseModelMultiplyColor)
                {
                    renderers[rendererIndex].ApplyMultiplyColor();
                    isMultiplyColorUpdated = true;
                }

                newMultiplyColors[rendererIndex] = renderers[rendererIndex].MultiplyColor;
            }

            for (var dataIndex = 0; dataIndex < data.Length; ++dataIndex)
            {
                var rendererIndex = (dataIndex < renderers.Length && renderers[dataIndex].Drawable.UnmanagedIndex == dataIndex)
                    ? dataIndex
                    : IndexOfDrawable(renderers, dataIndex);

                // index에 맞는 renderer가 없으면 screen 색 처리를 건너뜁니다.
                if (rendererIndex < 0)
                {
                    continue;
                }

                var isUseModelScreenColor = !(renderers[rendererIndex].DrawObjectScreenColorEnabled ||
                                              ScreenColorEnabled);

                // object 또는 controller override 색을 쓰면 모델 색 변경을 덮어쓰지 않습니다.
                if (data[dataIndex].IsBlendColorDirty && isUseModelScreenColor)
                {
                    renderers[rendererIndex].ApplyScreenColor();
                    isScreenColorUpdated = true;
                }

                newScreenColors[rendererIndex] = renderers[rendererIndex].ScreenColor;
            }

            // 변경된 blend 색이 있으면 각 외부 handler에 한 번 전달합니다.
            var multiplyColorHandlerInterface = MultiplyColorHandlerInterface;
            var screenColorHandlerInterface = ScreenColorHandlerInterface;

            if (MultiplyColorHandler != null && isMultiplyColorUpdated)
            {
                multiplyColorHandlerInterface.OnBlendColorDidChange(this, newMultiplyColors);
            }

            if (ScreenColorHandler != null && isScreenColorUpdated)
            {
                screenColorHandlerInterface.OnBlendColorDidChange(this, newScreenColors);
            }
        }

        #endregion
    }
}
