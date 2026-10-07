/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 한 Cubism draw object의 mesh·재질·정렬 상태를 Unity 렌더러에 반영하는 어댑터입니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Framework.Json;
using Live2D.Cubism.Rendering.Util;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;


namespace Live2D.Cubism.Rendering
{
    [ExecuteInEditMode, RequireComponent(typeof(MeshRenderer))]
    public sealed partial class CubismRenderer : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        private int _localSortingOrder;

        public int LocalSortingOrder
        {
            get
            {
                return _localSortingOrder;
            }
            set
            {
                // 값이 같으면 정렬 재계산과 MeshRenderer 쓰기를 생략합니다.
                if (value == _localSortingOrder)
                {
                    return;
                }


                // 새 로컬 정렬 오프셋을 보관합니다.
                _localSortingOrder = value;


                // 보관한 값으로 Unity 정렬 상태를 갱신합니다.
                ApplySorting();
            }
        }


        [SerializeField, HideInInspector]
        private Color _color = Color.white;

        public Color Color
        {
            get { return _color; }
            set
            {
                // 값이 같으면 정점 색상 갱신을 생략합니다.
                if (value == _color)
                {
                    return;
                }


                // 새 기본 tint를 보관합니다.
                _color = value;

                // 다음 mesh swap에 쓸 정점 색상을 다시 계산합니다.
                ApplyVertexColors();
            }
        }

        [FormerlySerializedAs("_isOverriddenDrawableMultiplyColors")] [SerializeField, HideInInspector]
        private bool isOverriddenDrawObjectMultiplyColors;

        public bool DrawObjectMultiplyColorEnabled
        {
            get { return isOverriddenDrawObjectMultiplyColors; }
            set { isOverriddenDrawObjectMultiplyColors = value; }
        }

        public bool LastIsUseUserMultiplyColor { get; set; }

        [FormerlySerializedAs("_isOverriddenDrawableScreenColors")] [SerializeField, HideInInspector]
        private bool _isOverriddenDrawObjectScreenColors;

        public bool DrawObjectScreenColorEnabled
        {
            get { return _isOverriddenDrawObjectScreenColors; }
            set { _isOverriddenDrawObjectScreenColors = value; }
        }

        public bool LastIsUseUserScreenColors { get; set; }

        [SerializeField, HideInInspector]
        private Color _multiplyColor = Color.white;

        public Color MultiplyColor
        {
            get
            {
                // 사용자 override가 켜졌으면 native 값 대신 직렬화 색상을 반환합니다.
                if (RenderController.MultiplyColorEnabled
                    || DrawObjectMultiplyColorEnabled)
                {
                    return _multiplyColor;
                }

                switch (DrawObjectType)
                {
                    case CubismModelTypes.DrawObjectType.Drawable:
                        return Drawable.MultiplyColor;
                    case CubismModelTypes.DrawObjectType.Offscreen:
                        return Offscreen.MultiplyColor;
                    default:
                        // 지원하지 않는 draw object이면 기본 흰색을 반환합니다.
                        return _multiplyColor;
                }
            }
            set
            {
                // 색이 같으면 shader 상태 갱신을 생략합니다.
                if (value == _multiplyColor)
                {
                    return;
                }


                // 새 사용자 Multiply 색을 보관합니다.
                _multiplyColor = (value != null)
                    ? value
                    : Color.white;
            }
        }

        public Color LastMultiplyColor { get; set; }

        [SerializeField, HideInInspector]
        private Color _screenColor = Color.clear;

        public Color ScreenColor
        {
            get
            {
                if (RenderController.ScreenColorEnabled
                    || DrawObjectScreenColorEnabled)
                {
                    return _screenColor;
                }

                switch (DrawObjectType)
                {
                    case CubismModelTypes.DrawObjectType.Drawable:
                        return Drawable.ScreenColor;
                    case CubismModelTypes.DrawObjectType.Offscreen:
                        return Offscreen.ScreenColor;
                    default:
                        // 지원하지 않는 draw object이면 기본 흰색을 반환합니다.
                        return _screenColor;
                }
            }
            set
            {
                // 색이 같으면 shader 상태 갱신을 생략합니다.
                if (value == _screenColor)
                {
                    return;
                }


                // 새 사용자 Screen 색을 보관합니다.
                _screenColor = (value != null)
                    ? value
                    : Color.black;
            }
        }

        public Color LastScreenColor { get; set; }


        public Material Material
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    if (!MeshRenderer.sharedMaterial)
                    {
                        MeshRenderer.sharedMaterial = SetMaterialFromPicker();
                    }

                    return MeshRenderer.sharedMaterial;
                }
#endif

                if (!MeshRenderer.material)
                {
                    MeshRenderer.material = SetMaterialFromPicker();
                }

                return MeshRenderer.material;
            }
            set
            {
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    MeshRenderer.sharedMaterial = value;

                    return;
                }
                #endif


                MeshRenderer.material = value;
            }
        }


        [SerializeField, HideInInspector]
        private Texture2D _mainTexture;

        public Texture2D MainTexture
        {
            get
            {
                if (_mainTexture == null && DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
                {
                    _mainTexture = new Texture2D(1, 1);
                    _mainTexture.SetPixel(1, 1, Color.clear);
                }

                return _mainTexture;
            }
            set
            {
                // 같은 유효 texture면 property block 갱신을 생략합니다.
                if (value == _mainTexture && _mainTexture != null)
                {
                    return;
                }

                if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
                {
                    _mainTexture = new Texture2D(1, 1);
                    _mainTexture.SetPixel(1, 1, Color.clear);
                }
                else
                {
                    // 선택한 texture를 직렬화 슬롯에 보관합니다.
                    _mainTexture = (value != null)
                        ? value
                        : Texture2D.whiteTexture;
                }


                // 새 texture를 현재 렌더 경로에 반영합니다.
                ApplyMainTexture();
            }
        }


        private Mesh[] Meshes { get; set; }

        private int FrontMesh { get; set; }

        private int BackMesh { get; set; }

        public Mesh Mesh
        {
            get
            {
                if (DrawObjectType == CubismModelTypes.DrawObjectType.Offscreen)
                {
                    return _offscreenMesh;
                }

                return FrontMesh < Meshes?.Length ? Meshes?[FrontMesh] : null;
            }
        }

        [NonSerialized]
        private MeshRenderer _meshRenderer;

        public MeshRenderer MeshRenderer
        {
            get
            {
                TryInitializeMeshRenderer();

                return _meshRenderer;
            }
        }

        [NonSerialized]
        private MeshFilter _meshFilter;

        public MeshFilter MeshFilter
        {
            get
            {
                TryInitializeMeshFilter();
                return _meshFilter;
            }

            set
            {
                if (value == _meshFilter || value == null)
                {
                    return;
                }

                _meshFilter = value;
            }
        }

        [SerializeField, HideInInspector]
        private Material _drawMaterial;

        public Material DrawMaterial
        {
            get
            {
                // picking은 Drawable만 하므로 그 경우에만 실제 draw 재질을 반환합니다.
                if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
                {
                    return null;
                }
                return _drawMaterial;
            }
            set { _drawMaterial = value; }
        }


        public CubismDrawable Drawable { get; set; }

        internal CubismRenderController RenderController { get; set; }


        #region Interface For CubismRenderController

        [SerializeField, HideInInspector]
        private CubismSortingMode _sortingMode;

        internal CubismSortingMode SortingMode
        {
            get
            {
                return _sortingMode;
            }
            set { _sortingMode = value; }
        }


        [SerializeField, HideInInspector]
        private int _sortingOrder;

        private int SortingOrder
        {
            get { return _sortingOrder; }
            set { _sortingOrder = value; }
        }


        [SerializeField, HideInInspector]
        private int _renderOrder;

        private int RenderOrder
        {
            get { return _renderOrder; }
            set { _renderOrder = value; }
        }


        [SerializeField, HideInInspector]
        private float _depthOffset = 0.00001f;

        private float DepthOffset
        {
            get { return _depthOffset; }
            set { _depthOffset = value; }
        }


        [SerializeField, HideInInspector]
        private float _opacity;

        internal float Opacity
        {
            get { return _opacity; }
            set { _opacity = value; }
        }


        private Color[] VertexColors { get; set; }


        private SwapInfo LastSwap { get; set; }

        private SwapInfo ThisSwap { get; set; }


        /// 입력: 없음; 반환: 없음.
        public void SwapMeshes()
        {
            // 배치 경로는 공유 mesh를 직접 소유하므로 per-Drawable front/back swap이 필요 없습니다.
            if (Meshes == null || Meshes.Length < 2)
            {
                return;
            }

            // 앞·뒤 mesh 버퍼와 해당 변경 플래그를 교환합니다.
            BackMesh = FrontMesh;
            FrontMesh = (FrontMesh == 0) ? 1 : 0;

            // 다음 렌더링에 노출할 mesh의 정점 색상을 갱신합니다.
            Meshes[BackMesh].colors = VertexColors;


            // 이번 프레임 변경 플래그를 직전 swap 상태로 넘깁니다.
            LastSwap = ThisSwap;


            ResetSwapInfoFlags();
#if UNITY_EDITOR
            SyncMeshFilterForPicking();
#endif
        }


        /// 입력: 없음; 반환: 없음.
        public void UpdateVisibility()
        {
            if (LastSwap.DidBecomeVisible)
            {
                MeshRenderer.enabled = true;
            }
            else if (LastSwap.DidBecomeInvisible)
            {
                MeshRenderer.enabled = false;
            }


            ResetVisibilityFlags();
        }


        /// 입력: 없음; 반환: 없음.
        public void UpdateRenderOrder()
        {
            if (LastSwap.NewRenderOrder)
            {
                ApplySorting();
            }


            ResetRenderOrderFlag();
        }

        /// 입력: newSortingLayer(int); 반환: 없음.
        internal void OnControllerSortingLayerDidChange(int newSortingLayer)
        {
            MeshRenderer.sortingLayerID = newSortingLayer;
        }

        /// 입력: newSortingMode(CubismSortingMode); 반환: 없음.
        internal void OnControllerSortingModeDidChange(CubismSortingMode newSortingMode)
        {
            SortingMode = newSortingMode;


            ApplySorting();
        }

        /// 입력: newSortingOrder(int); 반환: 없음.
        internal void OnControllerSortingOrderDidChange(int newSortingOrder)
        {
            SortingOrder = newSortingOrder;


            ApplySorting();
        }

        /// 입력: newDepthOffset(float); 반환: 없음.
        internal void OnControllerDepthOffsetDidChange(float newDepthOffset)
        {
            DepthOffset = newDepthOffset;


            ApplySorting();
        }


        /// 입력: newOpacity(float); 반환: 없음.
        internal void OnDrawableOpacityDidChange(float newOpacity)
        {
            Opacity = newOpacity;


            ApplyVertexColors();
        }

        /// 입력: newRenderOrder(int); 반환: 없음.
        internal void OnDrawableRenderOrderDidChange(int newRenderOrder)
        {
            if (RenderOrder == newRenderOrder) return;


            RenderOrder = newRenderOrder;


            SetNewRenderOrder();
        }

        /// 입력: newVertexPositions(Vector3[]); 반환: 없음.
        internal void OnDrawableVertexPositionsDidChange(Vector3[] newVertexPositions)
        {
            var mesh = Mesh;


            // 새 정점 위치를 기록하고 picking·culling용 bounds를 다시 계산합니다.
            mesh.vertices = newVertexPositions;


            mesh.RecalculateBounds();


            // 다음 swap에서 정점 위치 업로드가 일어나도록 표시합니다.
            SetNewVertexPositions();
        }

        /// 입력: newVisibility(bool); 반환: 없음.
        internal void OnDrawableVisiblityDidChange(bool newVisibility)
        {
            // 다음 swap에서 가시 상태 전환이 반영되도록 표시합니다.
            if (newVisibility)
            {
                BecomeVisible();
            }
            else
            {
                BecomeInvisible();
            }
        }


        /// 입력: newModelOpacity(float); 반환: 없음.
        internal void OnModelOpacityDidChange(float newModelOpacity)
        {
            // 배치 경로는 draw 기록 시 controller 불투명도를 직접 읽으므로 여기서 property block을 쓰지 않습니다.
            if (IsBatchedRenderingTarget())
            {
                return;
            }

            var property = PropertyBlock;
            _meshRenderer.GetPropertyBlock(property);


            // legacy 경로의 shader property block에 모델 불투명도를 기록합니다.
            property.SetFloat(CubismShaderVariables.ModelOpacity, newModelOpacity);

            MeshRenderer.SetPropertyBlock(property);
        }

        #endregion

        /// 입력: 없음; 반환: bool.
        private bool IsBatchedRenderingTarget()
        {
            return RenderController != null
                && RenderController.IsBatchedRenderingActive
                && DrawObjectType == CubismModelTypes.DrawObjectType.Drawable;
        }

        /// 입력: 없음; 반환: bool.
        private bool TryMarkBatchedColorDirty()
        {
            if (!IsBatchedRenderingTarget())
            {
                return false;
            }

            RenderController.BatchedRenderer?.MarkColorDirty(this);

            return true;
        }

        /// 입력: 없음; 반환: 없음.
        private void ApplyMainTexture()
        {
            if (IsBatchedRenderingTarget())
            {
                RenderController.BatchedRenderer?.MarkTexturesDirty();

                return;
            }

            var property = PropertyBlock;
            MeshRenderer.GetPropertyBlock(property);

            WriteMainTexture(property);

            MeshRenderer.SetPropertyBlock(property);
        }

        /// 입력: property(MaterialPropertyBlock); 반환: 없음.
        private void WriteMainTexture(MaterialPropertyBlock property)
        {
            property.SetTexture(CubismShaderVariables.MainTexture, MainTexture);
        }

        /// 입력: 없음; 반환: 없음.
        private void ApplySorting()
        {
            // controller 또는 모델이 없으면 정렬에 필요한 상태가 없으므로 종료합니다.
            if (!RenderController
                || !RenderController.Model)
            {
                return;
            }

            RenderController.DidChangeSorting = true;

            // order 모드에서는 controller 기준 순서와 drawable 순서를 합쳐 적용합니다.
            if (SortingMode.SortByOrder())
            {
                MeshRenderer.sortingOrder = SortingOrder + ((SortingMode == CubismSortingMode.BackToFrontOrder)
                    ? (RenderOrder + LocalSortingOrder)
                    : -(RenderOrder + LocalSortingOrder));


                transform.localPosition = Vector3.zero;


                return;
            }


            // depth 모드에서는 render order를 local Z 간격으로 변환합니다.
            var offset = (SortingMode == CubismSortingMode.BackToFrontZ)
                    ? -DepthOffset
                    : DepthOffset;


            MeshRenderer.sortingOrder = SortingOrder + LocalSortingOrder;

            transform.localPosition = new Vector3(0f, 0f, RenderOrder * offset);
        }

        /// 입력: 없음; 반환: 없음.
        public void ApplyVertexColors()
        {
            if (TryMarkBatchedColorDirty())
            {
                return;
            }

            var vertexColors = VertexColors;

            if (vertexColors == null)
            {
                return;
            }

            var color = Color;


            color.a *= Opacity;


            for (var i = 0; i < vertexColors.Length; ++i)
            {
                vertexColors[i] = color;
            }


            // 다음 swap에서 새 정점 색상이 업로드되도록 표시합니다.
            SetNewVertexColors();
        }

        /// 입력: 없음; 반환: 없음.
        public void ApplyMultiplyColor()
        {
            if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
            {
                return;
            }

            if (TryMarkBatchedColorDirty())
            {
                return;
            }

            var property = PropertyBlock;
            MeshRenderer.GetPropertyBlock(property);

            WriteMultiplyColor(property);

            MeshRenderer.SetPropertyBlock(property);
        }

        /// 입력: property(MaterialPropertyBlock); 반환: 없음.
        private void WriteMultiplyColor(MaterialPropertyBlock property)
        {
            property.SetColor(CubismShaderVariables.MultiplyColor, MultiplyColor);
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeMultiplyColor()
        {
            LastIsUseUserMultiplyColor = false;

            LastMultiplyColor = MultiplyColor;

            if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
            {
                return;
            }

            ApplyMultiplyColor();
        }

        /// 입력: 없음; 반환: 없음.
        public void ApplyScreenColor()
        {
            if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
            {
                return;
            }

            if (TryMarkBatchedColorDirty())
            {
                return;
            }

            var property = PropertyBlock;
            MeshRenderer.GetPropertyBlock(property);

            WriteScreenColor(property);

            MeshRenderer.SetPropertyBlock(property);
        }

        /// 입력: property(MaterialPropertyBlock); 반환: 없음.
        private void WriteScreenColor(MaterialPropertyBlock property)
        {
            property.SetColor(CubismShaderVariables.ScreenColor, ScreenColor);
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeScreenColor()
        {
            LastIsUseUserScreenColors = false;

            LastScreenColor = ScreenColor;

            if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
            {
                return;
            }

            ApplyScreenColor();
        }

        /// 입력: 없음; 반환: Material.
        public Material SetMaterialFromPicker()
        {
            Material material = null;

            switch (DrawObjectType)
            {
                case CubismModelTypes.DrawObjectType.Drawable:
                    if (!Drawable)
                    {
                        break;
                    }

                    material = CubismBuiltinPickers.DrawableMaterialPicker(null, Drawable);
                    break;
                case CubismModelTypes.DrawObjectType.Offscreen:
                    if (!Offscreen)
                    {
                        break;
                    }

                    material = CubismBuiltinPickers.OffscreenMaterialPicker(null, Offscreen);
                    break;
                default:
                    material = CubismBuiltinMaterials.GetBlendModeMaterial("UnlitBlendMode", BlendTypes.ColorBlend.Normal, BlendTypes.AlphaBlend.Over, false, false, true);
                    Debug.LogError("Unsupported DrawObjectType.");
                    break;
            }

            return material;
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeMeshRenderer()
        {
            if (!_meshRenderer)
            {
                _meshRenderer = GetComponent<MeshRenderer>();


                // 필요한 Unity MeshRenderer를 처음 접근할 때만 추가합니다.
                if (!_meshRenderer)
                {
                    _meshRenderer = gameObject.AddComponent<MeshRenderer>();
                    _meshRenderer.hideFlags = HideFlags.HideInInspector;
                    _meshRenderer.receiveShadows = false;
                    _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    _meshRenderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                }
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (!_meshRenderer.sharedMaterial)
                {
                    _meshRenderer.sharedMaterial = SetMaterialFromPicker();
                }

                return;
            }
#endif

            // 배치 경로는 MeshRenderer로 draw하지 않으므로 renderer별 material 복제를 만들지 않습니다.
            if (IsBatchedRenderingTarget())
            {
                return;
            }

            if (!_meshRenderer.material)
            {
                _meshRenderer.material = SetMaterialFromPicker();
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeMeshFilter()
        {
#if UNITY_EDITOR
            if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable
                || Application.isPlaying)
            {
                return;
            }

            if (_meshFilter != null)
            {
                return;
            }

            _meshFilter = GetComponent<MeshFilter>();

            // 편집기 picking용 MeshFilter가 없을 때만 추가합니다.
            if (_meshFilter == null)
            {
                _meshFilter = gameObject.AddComponent<MeshFilter>();
                _meshFilter.hideFlags = HideFlags.HideInInspector;
            }

            _meshFilter.sharedMesh = Mesh;
             SetupPickingMaterial();
#endif
        }

#if UNITY_EDITOR
        /// 입력: 없음; 반환: 없음.
        private void SetupPickingMaterial()
        {
            if (_meshRenderer == null)
            {
                return;
            }

            var currentMaterial = _meshRenderer.sharedMaterial;

            if (_drawMaterial == null)
            {
                // 현재 재질이 picking용 또는 비어 있으면 drawable 상태에 맞는 실제 재질을 다시 고릅니다.
                if (currentMaterial == null
                    || currentMaterial == CubismBuiltinMaterials.TransparentPicking
                    || (currentMaterial.shader != null && currentMaterial.shader.name == "Live2D Cubism/TransparentPicking"))
                {
                    _drawMaterial = SetMaterialFromPicker();
                }
                else
                {
                    _drawMaterial = currentMaterial;
                }
            }

            _meshRenderer.sharedMaterial = CubismBuiltinMaterials.TransparentPicking;

            // picking shader의 알파 테스트가 실제 질감을 읽도록 MainTexture를 연결합니다.
            if (MainTexture != null)
            {
                ApplyMainTexture();
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void SyncMeshFilterForPicking()
        {
            if (DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
            {
                return;
            }

            if (_meshFilter == null || Mesh == null)
            {
                return;
            }

            _meshFilter.sharedMesh = Mesh;
        }
#endif


        /// 입력: 없음; 반환: 없음.
        private void TryInitializeMesh()
        {
            // 배치 경로는 공유 mesh를 그리므로 per-Drawable double-buffer mesh 생성을 건너뜁니다.
            if (IsBatchedRenderingTarget())
            {
                return;
            }

            // 기존 mesh가 없거나 런타임 생성 후 정점이 비어 있을 때만 double-buffer mesh를 만듭니다.
            // Mesh 속성은 backing field가 없어 null 검사를 별도로 해야 하며, 빈 정점은 재생성 대상으로 봅니다.
            if ((Meshes != null && Meshes.Length == 2
                && Mesh != null && Mesh.vertexCount > 0
                && Drawable?.VertexPositions != null && Mesh.vertexCount == Drawable?.VertexPositions.Length)
                || (DrawObjectType == CubismModelTypes.DrawObjectType.Offscreen && _offscreenMesh))
            {
                return;
            }

            if (DrawObjectType == CubismModelTypes.DrawObjectType.Offscreen)
            {
                Meshes = new Mesh[1];
                _offscreenMesh = new Mesh
                {
                    vertices = OffscreenVertices,
                    uv = OffscreenUVs,
                    triangles = OffscreenTriangle
                };
                Meshes[0] = _offscreenMesh;
                return;
            }


            if (Meshes != null)
            {
                for (var i = 0; i < Meshes.Length; i++)
                {
                    DestroyImmediate(Meshes[i]);
                }
            }

            Meshes = new Mesh[2];

            for (var i = 0; i < 2; ++i)
            {
                var mesh = new Mesh();

                mesh.name = Drawable.name;
                mesh.vertices = Drawable.VertexPositions;
                mesh.uv = Drawable.VertexUvs;
                mesh.triangles = Drawable.Indices;

                mesh.MarkDynamic();
                mesh.RecalculateBounds();


                // 새 mesh를 front/back 슬롯에 보관합니다.
                Meshes[i] = mesh;
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeVertexColor()
        {
            if (Mesh == null)
            {
                return;
            }

            var mesh = Mesh;


            VertexColors = new Color[mesh.vertexCount];


            for (var i = 0; i < VertexColors.Length; ++i)
            {
                VertexColors[i] = Color;
                VertexColors[i].a *= Opacity;
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void TryInitializeMainTexture()
        {
            if (!MainTexture)
            {
                MainTexture = Texture2D.whiteTexture;
            }


            ApplyMainTexture();
        }

        /// 입력: renderController(CubismRenderController); 반환: 없음.
        public void TryInitialize(CubismRenderController renderController)
        {
            RenderController = renderController;

            if (!RenderController.Model)
            {
                return;
            }

            InitializeDrawObject();

            TryInitializeMeshRenderer();

            TryInitializeMesh();
            TryInitializeMeshFilter();
            TryInitializeVertexColor();
            TryInitializeMainTexture();
            TryInitializeMultiplyColor();
            TryInitializeScreenColor();
            _previousOffscreenUnmanagedIndex = -1;

            ApplySorting();
#if UNITY_EDITOR
            SyncMeshFilterForPicking();
#endif
        }

        #region Swap Info

        /// 입력: 없음; 반환: 없음.
        private void SetNewVertexPositions()
        {
            var swapInfo = ThisSwap;
            swapInfo.NewVertexPositions = true;
            ThisSwap = swapInfo;
        }


        /// 입력: 없음; 반환: 없음.
        private void SetNewVertexColors()
        {
            var swapInfo = ThisSwap;
            swapInfo.NewVertexColors = true;
            ThisSwap = swapInfo;
        }


        /// 입력: 없음; 반환: 없음.
        private void BecomeVisible()
        {
            var swapInfo = ThisSwap;
            swapInfo.DidBecomeVisible = true;
            ThisSwap = swapInfo;
        }


        /// 입력: 없음; 반환: 없음.
        private void BecomeInvisible()
        {
            var swapInfo = ThisSwap;
            swapInfo.DidBecomeInvisible = true;
            ThisSwap = swapInfo;
        }


        /// 입력: 없음; 반환: 없음.
        private void SetNewRenderOrder()
        {
            var swapInfo = ThisSwap;
            swapInfo.NewRenderOrder = true;
            ThisSwap = swapInfo;
        }


        /// 입력: 없음; 반환: 없음.
        private void ResetSwapInfoFlags()
        {
            ThisSwap = default;
        }


        /// 입력: 없음; 반환: 없음.
        private void ResetVisibilityFlags()
        {
            var swapInfo = LastSwap;
            swapInfo.DidBecomeVisible = false;
            swapInfo.DidBecomeInvisible = false;
            LastSwap = swapInfo;
        }


        /// 입력: 없음; 반환: 없음.
        private void ResetRenderOrderFlag()
        {
            var swapInfo = LastSwap;
            swapInfo.NewRenderOrder = false;
            LastSwap = swapInfo;
        }


        private struct SwapInfo
        {
            public bool NewVertexPositions { get; set; }

            public bool NewVertexColors { get; set; }

            public bool DidBecomeVisible { get; set; }

            public bool DidBecomeInvisible { get; set; }

            public bool NewRenderOrder { get; set; }
        }

        #endregion



        #region Unity Events Handling

        /// 입력: 없음; 반환: 없음.
        private void OnDestroy()
        {
            if (Meshes == null)
            {
                return;
            }


            for (var i = 0; i < Meshes.Length; i++)
            {
                DestroyImmediate(Meshes[i]);
            }
        }

        #endregion
    }
}
