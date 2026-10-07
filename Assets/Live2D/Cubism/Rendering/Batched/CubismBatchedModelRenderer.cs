/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// Live2D 모델 전체를 동적 mesh 하나로 합치고 같은 상태의 Drawable과 mask를 묶어 draw call을 줄입니다.
// 변경된 정점 범위만 GPU에 쓰고 mask atlas는 내용이 달라질 때만 다시 그리는 모바일 렌더 경로입니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering.Util;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;


namespace Live2D.Cubism.Rendering
{
    public sealed class CubismBatchedModelRenderer : IDisposable
    {
        public const int MaxMaskGroups = 64;

        public static int MaskAtlasSize = 1024;

        /// MaskAtlasFullSizeMinimumSystemMemoryMegabytes 미만인 기기에서 쓰는 해상도.
        /// 아틀라스는 모델당 1장 ARGB32라 1024면 약 4MB고 변을 절반으로 줄이면 1/4이 됩니다.
        /// 대가는 마스크 에지 품질입니다. 타일 하한이 아틀라스의 1/4이라 1024에서 256px,
        /// 512에서 128px이 되는데, 머리카락·눈 같은 작은 클립 영역은 256px 미만에서 눈에 띄게 앨리어싱됩니다.
        /// MaskAtlasSize와 같게 두면 티어링이 꺼집니다.
        public static int MaskAtlasSizeLowMemory = 512;

        /// 풀사이즈 마스크 아틀라스를 쓰기 위한 최소 SystemInfo.systemMemorySize(MB). 0이면 검사 안 함.
        public static int MaskAtlasFullSizeMinimumSystemMemoryMegabytes = 3072;

        /// 기기 메모리 티어를 반영한 마스크 아틀라스 변 길이.
        internal static int EffectiveMaskAtlasSize
        {
            get
            {
                return (MaskAtlasFullSizeMinimumSystemMemoryMegabytes <= 0
                        || SystemInfo.systemMemorySize >= MaskAtlasFullSizeMinimumSystemMemoryMegabytes)
                    ? MaskAtlasSize
                    : MaskAtlasSizeLowMemory;
            }
        }


        #region Static Shader Property IDs

        private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTextureArrayId = Shader.PropertyToID("_MainTexArray");
        private static readonly int ModelOpacityId = Shader.PropertyToID("cubism_ModelOpacity");
        private static readonly int MaskTextureId = Shader.PropertyToID("cubism_MaskTexture");
        private static readonly int MaskTileId = Shader.PropertyToID("cubism_MaskTile");
        private static readonly int MaskTransformId = Shader.PropertyToID("cubism_MaskTransform");
        private static readonly int MaskTilesArrayId = Shader.PropertyToID("_CubismMaskTiles");
        private static readonly int MaskTransformsArrayId = Shader.PropertyToID("_CubismMaskTransforms");
        private static readonly int SrcColorId = Shader.PropertyToID("_SrcColor");
        private static readonly int DstColorId = Shader.PropertyToID("_DstColor");
        private static readonly int SrcAlphaId = Shader.PropertyToID("_SrcAlpha");
        private static readonly int DstAlphaId = Shader.PropertyToID("_DstAlpha");
        private static readonly int CullId = Shader.PropertyToID("_Cull");

        #endregion


        #region Types

        private struct Stream1Data
        {
            public Color32 Color;

            public Color32 MultiplyAndGroup;

            public Color32 ScreenAndInvert;
        }

        private struct Batch
        {
            public int TextureSlot;
            public BlendTypes.ColorBlend ColorBlend;
            public bool IsDoubleSided;
            public int IndexStart;
            public int IndexCount;
        }

        private struct MaskSection
        {
            public int GroupIndex;
            public int SubMeshIndex;
            public Material Material;
            public MaterialPropertyBlock Properties;
        }

        #endregion


        #region Fields

        private CubismRenderController _controller;
        private CubismRenderer[] _renderersByDrawable;

        // 정점을 Drawable에서 직접 읽으므로, 이 렌더러가 모델을 소유하는 동안에는
        // 코어가 CubismDynamicDrawableData로 뜨는 관리 배열 복사를 억제합니다.
        // 배열 참조를 들고 있어야 코어 이벤트를 더 기다리지 않고 억제를 풀 수 있습니다.
        private CubismDrawable[] _drawablesByIndex;
        private CubismDynamicDrawableData[] _suppressedDynamicData;

        // 정렬 깊이는 위치 스트림의 z에 있고, 렌더 순서나 깊이 설정이 바뀔 때만 다시 씁니다.
        private float _appliedDepthOffset;
        private bool _appliedSortZ;

        // 목적지를 읽는 블렌드(Add, Multiply)를 쓰는 Drawable이 하나라도 있으면 true.
        // 그런 모델은 legacy 시맨틱 유지를 위해 오프스크린 버퍼를 거쳐야 합니다.
        // "over"만 쓰는 모델은 결합법칙이 성립해 어느 쪽으로 합성하든 결과가 같습니다.
        private bool _hasDestinationDependentBlend;

        // Drawable별 AABB. 정점을 읽는 같은 루프에서 갱신하고, 이를 합쳐 모델 AABB를 만듭니다.
        // mesh bounds는 canvas 사각형이라 변형된 파츠가 자주 벗어나므로 컬링 기준으로 쓸 수 없습니다.
        // 그걸로 컬링하면 화면 가장자리에서 파츠가 튑니다.
        private Vector2[] _drawableMinimum;
        private Vector2[] _drawableMaximum;
        private Bounds _localModelBounds;
        private bool _localModelBoundsDirty = true;

        private int _drawableCount;
        private int _totalVertexCount;
        private int _totalIndexCount;
        private int _maskIndexCount;

        private int[] _vertexBase;
        private int[] _vertexCount;
        private int[] _indexBase;
        private int[] _indexCount;
        private BlendTypes.ColorBlend[] _colorBlend;
        private bool[] _isDoubleSided;
        private byte[] _maskGroup;
        private bool[] _isInverted;
        private int[] _textureSlot;

        private float[] _opacities;
        private bool[] _visible;
        private int[] _renderOrders;
        private int[] _orderedDrawables;

        private NativeArray<Vector3> _positions;
        private NativeArray<Stream1Data> _stream1;
        private NativeArray<Vector3> _uvs;
        private NativeArray<ushort> _bakedIndices16;
        private NativeArray<uint> _bakedIndices32;
        private NativeArray<ushort> _indexBuffer16;
        private NativeArray<uint> _indexBuffer32;
        private bool _use32BitIndices;

        private Mesh _mesh;

        private readonly List<Batch> _batches = new List<Batch>(32);
        private int _mainIndexCount;

        private Texture[] _textures;
        private Texture2DArray _textureArray;
        private bool _useTextureArray;
        private readonly Dictionary<long, Material> _materials = new Dictionary<long, Material>();
        private MaterialPropertyBlock _modelProperties;

        private int _maskGroupCount;
        private int[][] _maskGroupMembers;
        private Vector4[] _maskTiles;
        private Vector4[] _maskTransforms;
        private readonly List<MaskSection> _maskSections = new List<MaskSection>(32);
        private readonly List<SubMeshDescriptor> _maskSubMeshes = new List<SubMeshDescriptor>(32);
        private RenderTexture _maskAtlas;

        private readonly List<SubMeshDescriptor> _subMeshScratch = new List<SubMeshDescriptor>(64);

        private bool _positionsDirty;
        private bool _stream1Dirty;
        private bool _indicesDirty;
        private bool _texturesDirty;

        private int _positionsDirtyMin;
        private int _positionsDirtyMax;
        private int _stream1DirtyMin;
        private int _stream1DirtyMax;

        private bool _maskContentDirty = true;

        private bool _maskAtlasContentValid;

        private bool[] _isMaskGroupMember;

        private Comparison<int> _renderOrderComparison;

        private bool _textureArrayPending;

        private float _textureArrayActivationTime;
        private bool _receivedFirstData;
        private int _lastFlushedFrame = -1;
        private int _lastMaskUpdateFrame = -1;

        private bool _isDisposed;
        private bool _isBroken;

        private bool _convertBlendColorsToLinear;

        #endregion


        public bool IsValid
        {
            get { return !_isDisposed && !_isBroken && _mesh != null; }
        }


        /// legacy 블렌드 시맨틱을 지키려면 오프스크린 버퍼를 거쳐야 하는 모델인지.
        /// 목적지를 읽는 건 Add와 Multiply뿐이고 "over"는 결합법칙이 성립하므로,
        /// 둘 다 없으면 버퍼를 거치든 카메라 타깃에 바로 그리든 결과가 같습니다.
        internal bool RequiresBufferedComposition
        {
            get { return _hasDestinationDependentBlend; }
        }


        /// Drawable의 bind pose AABB를 기록해 첫 코어 이벤트 전에도 모델 AABB가 완전하게 합니다.
        private void SeedDrawableExtent(int drawableIndex, Vector3[] positions)
        {
            if (positions == null || positions.Length < 1)
            {
                _drawableMinimum[drawableIndex] = Vector2.zero;
                _drawableMaximum[drawableIndex] = Vector2.zero;

                return;
            }

            var minimum = new Vector2(float.MaxValue, float.MaxValue);
            var maximum = new Vector2(float.MinValue, float.MinValue);

            for (var v = 0; v < positions.Length; v++)
            {
                minimum = Vector2.Min(minimum, positions[v]);
                maximum = Vector2.Max(maximum, positions[v]);
            }

            _drawableMinimum[drawableIndex] = minimum;
            _drawableMaximum[drawableIndex] = maximum;
            _localModelBoundsDirty = true;
        }


        /// Drawable별 AABB를 합쳐 모델 AABB를 만듭니다. z는 위치 스트림이 싣는 정렬 깊이만큼 넓힙니다.
        private void RefreshLocalModelBounds()
        {
            _localModelBoundsDirty = false;

            var minimum = new Vector2(float.MaxValue, float.MaxValue);
            var maximum = new Vector2(float.MinValue, float.MinValue);
            var any = false;

            for (var i = 0; i < _drawableCount; i++)
            {
                if (_vertexCount[i] < 1)
                {
                    continue;
                }

                minimum = Vector2.Min(minimum, _drawableMinimum[i]);
                maximum = Vector2.Max(maximum, _drawableMaximum[i]);
                any = true;
            }

            if (!any)
            {
                _localModelBounds = new Bounds();

                return;
            }

            var depthSpan = _appliedSortZ
                ? Mathf.Abs(_appliedDepthOffset) * _drawableCount
                : 0.0f;

            var bounds = new Bounds();
            bounds.SetMinMax(
                new Vector3(minimum.x, minimum.y, -depthSpan),
                new Vector3(maximum.x, maximum.y, depthSpan));

            _localModelBounds = bounds;
        }


        /// 모델의 월드 AABB가 절두체 밖이면 true. CommandBuffer.DrawMesh는 컬링을 하지 않으므로
        /// 화면 밖 모델도 마스크 아틀라스 패스와 전 배치를 그대로 제출하게 됩니다.
        internal bool IsCulledBy(Plane[] frustumPlanes)
        {
            if (frustumPlanes == null || _controller == null || _totalVertexCount < 1)
            {
                return false;
            }

            if (_localModelBoundsDirty)
            {
                RefreshLocalModelBounds();
            }

            if (_localModelBounds.size == Vector3.zero)
            {
                return false;
            }

            var matrix = _controller.transform.localToWorldMatrix;
            var center = matrix.MultiplyPoint3x4(_localModelBounds.center);
            var extents = _localModelBounds.extents;

            var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0.0f, 0.0f));
            var axisY = matrix.MultiplyVector(new Vector3(0.0f, extents.y, 0.0f));
            var axisZ = matrix.MultiplyVector(new Vector3(0.0f, 0.0f, extents.z));

            var worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

            return !GeometryUtility.TestPlanesAABB(frustumPlanes, new Bounds(center, worldExtents * 2.0f));
        }


        /// 입력: baseVertex(int), count(int); 반환: 없음.
        private void MarkPositionsDirty(int baseVertex, int count)
        {
            if (!_positionsDirty)
            {
                _positionsDirtyMin = baseVertex;
                _positionsDirtyMax = baseVertex + count;
                _positionsDirty = true;
                return;
            }

            if (baseVertex < _positionsDirtyMin) { _positionsDirtyMin = baseVertex; }
            if (baseVertex + count > _positionsDirtyMax) { _positionsDirtyMax = baseVertex + count; }
        }


        /// 입력: baseVertex(int), count(int); 반환: 없음.
        private void MarkStream1Dirty(int baseVertex, int count)
        {
            if (!_stream1Dirty)
            {
                _stream1DirtyMin = baseVertex;
                _stream1DirtyMax = baseVertex + count;
                _stream1Dirty = true;
                return;
            }

            if (baseVertex < _stream1DirtyMin) { _stream1DirtyMin = baseVertex; }
            if (baseVertex + count > _stream1DirtyMax) { _stream1DirtyMax = baseVertex + count; }
        }


        #region Initialization

        /// 입력: controller(CubismRenderController); 반환: bool.
        public static bool IsModelEligible(CubismRenderController controller)
        {
            var model = controller.Model;

            if (model == null || model.Drawables == null || model.Drawables.Length < 1)
            {
                return false;
            }

            // blend 색상 handler는 legacy 경로의 drawable별 변경 event를 전제로 하므로 배치를 허용하지 않습니다.
            if (controller.MultiplyColorHandler != null || controller.ScreenColorHandler != null)
            {
                return false;
            }

            // Part offscreen은 중간 frame buffer가 필요한 legacy pipeline으로만 처리합니다.
            if (model.Offscreens != null && model.Offscreens.Length > 0)
            {
                return false;
            }

            // core render order와 실제 draw 순서가 같은 정렬 방식만 한 mesh 배치로 합칠 수 있습니다.
            if (controller.SortingMode != CubismSortingMode.BackToFrontZ
                && controller.SortingMode != CubismSortingMode.BackToFrontOrder)
            {
                return false;
            }

            var maskGroupKeys = new HashSet<string>();
            var drawables = model.Drawables;

            for (var i = 0; i < drawables.Length; i++)
            {
                var drawable = drawables[i];

                // GPU 고정 기능으로 표현 가능한 blend mode만 배치 대상이 됩니다.
                switch (drawable.ColorBlend)
                {
                    case BlendTypes.ColorBlend.Normal:
                        if (drawable.AlphaBlend != BlendTypes.AlphaBlend.Over)
                        {
                            return false;
                        }
                        break;
                    case BlendTypes.ColorBlend.Add:
                    case BlendTypes.ColorBlend.Multiply:
                        break;
                    default:
                        return false;
                }

                if (drawable.IsMasked)
                {
                    maskGroupKeys.Add(MaskGroupKey(drawable.Masks));
                }
            }

            // shader mask parameter 배열에 들어가야 하며 slot 0은 예약되어 있습니다.
            if (maskGroupKeys.Count > MaxMaskGroups - 1)
            {
                return false;
            }

            return true;
        }


        /// 입력: controller(CubismRenderController); 반환: bool.
        public static bool AreRenderersEligible(CubismRenderController controller)
        {
            // drawable별 local sorting order는 core render order를 바꾸므로 배치 순서를 보장할 수 없습니다.
            var renderers = controller.Renderers;

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].LocalSortingOrder != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// 입력: masks(CubismDrawable[]); 반환: string.
        private static string MaskGroupKey(CubismDrawable[] masks)
        {
            var indices = new int[masks.Length];
            for (var i = 0; i < masks.Length; i++)
            {
                indices[i] = masks[i] != null ? masks[i].UnmanagedIndex : -1;
            }
            Array.Sort(indices);
            return string.Join(",", indices);
        }


        /// 입력: controller(CubismRenderController); 반환: 없음.
        public CubismBatchedModelRenderer(CubismRenderController controller)
        {
            _controller = controller;

            try
            {
                Build();
            }
            catch (Exception e)
            {
                Debug.LogError($"[CubismBatchedModelRenderer] Initialization failed, falling back to legacy rendering: {e}");
                _isBroken = true;
                DisposeResources();
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void Build()
        {
            var model = _controller.Model;
            var drawables = model.Drawables;
            _drawableCount = drawables.Length;

            _convertBlendColorsToLinear = QualitySettings.activeColorSpace == ColorSpace.Linear;

            // native drawable index로 renderer를 바로 찾을 수 있게 매핑합니다.
            var drawableRenderers = _controller.DrawableRenderers;
            _renderersByDrawable = new CubismRenderer[_drawableCount];
            for (var i = 0; i < drawableRenderers.Length; i++)
            {
                _renderersByDrawable[drawableRenderers[i].Drawable.UnmanagedIndex] = drawableRenderers[i];

                // 편집기 picking용 MeshFilter가 Play에서 오래된 mesh를 일반 pipeline으로 그리지 않게 비활성화합니다.
                var meshFilter = drawableRenderers[i].GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    meshFilter.sharedMesh = null;
                }
            }

            // drawable마다 변하지 않는 정점·색상·재질 테이블을 구성합니다.
            _vertexBase = new int[_drawableCount];
            _vertexCount = new int[_drawableCount];
            _indexBase = new int[_drawableCount];
            _indexCount = new int[_drawableCount];
            _colorBlend = new BlendTypes.ColorBlend[_drawableCount];
            _isDoubleSided = new bool[_drawableCount];
            _maskGroup = new byte[_drawableCount];
            _isInverted = new bool[_drawableCount];
            _textureSlot = new int[_drawableCount];
            _opacities = new float[_drawableCount];
            _visible = new bool[_drawableCount];
            _renderOrders = new int[_drawableCount];
            _orderedDrawables = new int[_drawableCount];

            _totalVertexCount = 0;
            _totalIndexCount = 0;

            var vertexUvs = new Vector2[_drawableCount][];
            var localIndices = new int[_drawableCount][];
            var initialPositions = new Vector3[_drawableCount][];
            var renderOrders = model.AllDrawObjectsRenderOrder;

            _drawablesByIndex = new CubismDrawable[_drawableCount];
            _drawableMinimum = new Vector2[_drawableCount];
            _drawableMaximum = new Vector2[_drawableCount];

            for (var i = 0; i < _drawableCount; i++)
            {
                var drawable = drawables[i];
                var unmanagedIndex = drawable.UnmanagedIndex;

                _drawablesByIndex[unmanagedIndex] = drawable;
                vertexUvs[unmanagedIndex] = drawable.VertexUvs;
                localIndices[unmanagedIndex] = drawable.Indices;
                initialPositions[unmanagedIndex] = drawable.VertexPositions;

                SeedDrawableExtent(unmanagedIndex, initialPositions[unmanagedIndex]);

                _vertexBase[unmanagedIndex] = 0; // 아래 두 번째 반복에서 drawable index 순서의 실제 시작점을 채웁니다.
                _vertexCount[unmanagedIndex] = vertexUvs[unmanagedIndex].Length;
                _indexCount[unmanagedIndex] = localIndices[unmanagedIndex].Length;
                _colorBlend[unmanagedIndex] = drawable.ColorBlend;

                if (drawable.ColorBlend != BlendTypes.ColorBlend.Normal)
                {
                    _hasDestinationDependentBlend = true;
                }

                _isDoubleSided[unmanagedIndex] = drawable.IsDoubleSided;
                _isInverted[unmanagedIndex] = drawable.IsInverted;
                _renderOrders[unmanagedIndex] = renderOrders[unmanagedIndex];
                _opacities[unmanagedIndex] = 1.0f;
                _visible[unmanagedIndex] = true;
            }

            for (var i = 0; i < _drawableCount; i++)
            {
                _vertexBase[i] = _totalVertexCount;
                _indexBase[i] = _totalIndexCount;
                _totalVertexCount += _vertexCount[i];
                _totalIndexCount += _indexCount[i];
            }

            _use32BitIndices = _totalVertexCount > ushort.MaxValue;

            // mask 관계를 읽어 atlas tile과 shader mask 그룹을 구성합니다.
            BuildMaskGroups(drawables);

            // 텍스처 표와 재질·slot을 만들되, 비동기 업로드가 끝난 뒤 texture array를 복사하도록 snapshot은 미룹니다.
            _textureArrayActivationTime = Time.realtimeSinceStartup
                + CubismBatchedRendering.TextureArrayActivationDelaySeconds;
            BuildTextures();

            // 모든 drawable을 합칠 정점·인덱스 저장 공간을 할당합니다.
            _positions = new NativeArray<Vector3>(_totalVertexCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _stream1 = new NativeArray<Stream1Data>(_totalVertexCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _uvs = new NativeArray<Vector3>(_totalVertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            for (var i = 0; i < _drawableCount; i++)
            {
                var uvs = vertexUvs[i];
                var slice = _useTextureArray ? _textureSlot[i] : 0;
                var baseVertex = _vertexBase[i];

                for (var v = 0; v < uvs.Length; v++)
                {
                    _uvs[baseVertex + v] = new Vector3(uvs[v].x, uvs[v].y, slice);
                }

                // core는 움직인 drawable만 dirty로 주므로, 한 번도 움직이지 않는 drawable도 여기서 초기 정점을 채웁니다.
                var positions = initialPositions[i];

                if (positions != null)
                {
                    var count = Mathf.Min(positions.Length, _vertexCount[i]);

                    for (var v = 0; v < count; v++)
                    {
                        _positions[baseVertex + v] = positions[v];
                    }
                }
            }

            RebuildOrder();

            if (_controller.SortingMode == CubismSortingMode.BackToFrontZ)
            {
                RefreshSortZ(_controller.DepthOffset);
            }

            // drawable local index에 각 drawable의 base vertex를 더해 하나의 mesh 인덱스로 굽습니다.
            _maskIndexCount = ComputeMaskIndexCount();
            var indexBufferCapacity = _maskIndexCount + _totalIndexCount;

            if (_use32BitIndices)
            {
                _bakedIndices32 = new NativeArray<uint>(_totalIndexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _indexBuffer32 = new NativeArray<uint>(indexBufferCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

                for (var i = 0; i < _drawableCount; i++)
                {
                    var indices = localIndices[i];
                    var baseVertex = (uint)_vertexBase[i];
                    var indexBase = _indexBase[i];
                    for (var n = 0; n < indices.Length; n++)
                    {
                        _bakedIndices32[indexBase + n] = baseVertex + (uint)indices[n];
                    }
                }
            }
            else
            {
                _bakedIndices16 = new NativeArray<ushort>(_totalIndexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _indexBuffer16 = new NativeArray<ushort>(indexBufferCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

                for (var i = 0; i < _drawableCount; i++)
                {
                    var indices = localIndices[i];
                    var baseVertex = _vertexBase[i];
                    var indexBase = _indexBase[i];
                    for (var n = 0; n < indices.Length; n++)
                    {
                        _bakedIndices16[indexBase + n] = (ushort)(baseVertex + indices[n]);
                    }
                }
            }

            // 합쳐진 정점·인덱스로 GPU mesh를 생성합니다.
            _mesh = new Mesh
            {
                name = model.name + " (Batched)",
                hideFlags = HideFlags.HideAndDontSave
            };
            _mesh.MarkDynamic();

            _mesh.SetVertexBufferParams(
                _totalVertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.UNorm8, 4, 1));

            _mesh.SetIndexBufferParams(indexBufferCapacity, _use32BitIndices ? IndexFormat.UInt32 : IndexFormat.UInt16);

            // 변하지 않는 정점 stream과 submesh descriptor를 GPU에 올립니다.
            _mesh.SetVertexBufferData(_uvs, 0, 0, _totalVertexCount, 2, CubismBatchedRendering.UpdateFlags);

            // 배치 draw는 CommandBuffer.DrawMesh로 culling하지 않으므로 모델 전체 bounds는 유효한 값이면 충분합니다.
            var canvas = model.CanvasInformation;
            var size = new Vector3(canvas.CanvasWidth / canvas.PixelsPerUnit, canvas.CanvasHeight / canvas.PixelsPerUnit, 1.0f);
            _mesh.bounds = new Bounds(Vector3.zero, size * 2.0f);

            // mask atlas·정적 mask index 영역·texture별 mask section을 만듭니다.
            BuildMaskSections();

            _modelProperties = new MaterialPropertyBlock();

            MarkPositionsDirty(0, _totalVertexCount);
            MarkStream1Dirty(0, _totalVertexCount);
            _indicesDirty = true;
        }


        /// 입력: drawables(CubismDrawable[]); 반환: 없음.
        private void BuildMaskGroups(CubismDrawable[] drawables)
        {
            var groupByKey = new Dictionary<string, int>();
            var members = new List<int[]>();
            var extents = new List<float>();

            _isMaskGroupMember = new bool[_drawableCount];

            for (var i = 0; i < drawables.Length; i++)
            {
                var drawable = drawables[i];
                var unmanagedIndex = drawable.UnmanagedIndex;

                if (!drawable.IsMasked || drawable.Masks.Length < 1)
                {
                    _maskGroup[unmanagedIndex] = 0;
                    continue;
                }

                var key = MaskGroupKey(drawable.Masks);

                if (!groupByKey.TryGetValue(key, out var group))
                {
                    var masks = drawable.Masks;
                    var maskIndices = new int[masks.Length];

                    // bind pose mask 크기로 tile 크기를 정해 큰 clip 영역에는 더 높은 atlas 해상도를 배정합니다.
                    var min = new Vector2(float.MaxValue, float.MaxValue);
                    var max = new Vector2(float.MinValue, float.MinValue);

                    for (var m = 0; m < masks.Length; m++)
                    {
                        maskIndices[m] = masks[m].UnmanagedIndex;
                        _isMaskGroupMember[masks[m].UnmanagedIndex] = true;

                        var maskPositions = masks[m].VertexPositions;
                        for (var v = 0; maskPositions != null && v < maskPositions.Length; v++)
                        {
                            var position = maskPositions[v];
                            if (position.x < min.x) { min.x = position.x; }
                            if (position.y < min.y) { min.y = position.y; }
                            if (position.x > max.x) { max.x = position.x; }
                            if (position.y > max.y) { max.y = position.y; }
                        }
                    }

                    group = members.Count + 1; // 0번 슬롯은 마스크를 쓰지 않는 drawable을 나타내므로 그룹은 1부터 시작합니다.
                    groupByKey.Add(key, group);
                    members.Add(maskIndices);
                    extents.Add(min.x <= max.x ? Mathf.Max(max.x - min.x, max.y - min.y) : 0.0f);
                }

                _maskGroup[unmanagedIndex] = (byte)group;
            }

            _maskGroupCount = members.Count;
            _maskGroupMembers = members.ToArray();

            _maskTiles = new Vector4[MaxMaskGroups];
            _maskTransforms = new Vector4[MaxMaskGroups];

            // 채널 가중치 0과 invert 1은 shader mask factor를 정확히 1로 만들어 unmasked drawable을 표시합니다.
            _maskTiles[0] = new Vector4(-1.0f, 0.0f, 0.0f, 1.0f);
            _maskTransforms[0] = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);

            if (_maskGroupCount < 1)
            {
                return;
            }

            if (!TryLayoutMaskTilesByExtent(extents))
            {
                LayoutMaskTilesUniform();
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void LayoutMaskTilesUniform()
        {
            var tileCount = (_maskGroupCount + 3) / 4;
            var tilesPerAxis = Mathf.CeilToInt(Mathf.Sqrt(tileCount));
            var tileSize = 1.0f / tilesPerAxis;

            for (var group = 0; group < _maskGroupCount; group++)
            {
                var channel = group & 3;
                var tileIndex = group >> 2;
                var column = tileIndex % tilesPerAxis;
                var row = tileIndex / tilesPerAxis;

                _maskTiles[group + 1] = new Vector4(channel, column, row, tileSize);
            }
        }


        /// 입력: extents(List<float>); 반환: bool.
        private bool TryLayoutMaskTilesByExtent(List<float> extents)
        {
            var tileCount = (_maskGroupCount + 3) / 4;

            // mask 그룹을 크기 내림차순·그룹 index 안정 순서로 정렬합니다.
            var ranked = new int[_maskGroupCount];
            for (var i = 0; i < _maskGroupCount; i++) { ranked[i] = i; }
            System.Array.Sort(ranked, (a, b) =>
            {
                var byExtent = extents[b].CompareTo(extents[a]);
                return byExtent != 0 ? byExtent : a.CompareTo(b);
            });

            var maxExtent = extents[ranked[0]];
            if (maxExtent <= 0.0f)
            {
                return false;
            }

            // 크기에 비례한 2의 거듭제곱 tile을 [1/4, cap]으로 제한합니다. 작은 머리카락·눈 mask가 aliasing되지 않도록 최소 1/4 tile을 보장합니다.
            const float minTileSize = 0.25f;
            var cap = tileCount == 1 ? 1.0f : 0.5f;
            var sizes = new float[tileCount];
            var totalArea = 0.0f;

            for (var t = 0; t < tileCount; t++)
            {
                var tileExtent = extents[ranked[t * 4]];
                var size = cap;
                while (size * 0.5f >= minTileSize && size * maxExtent > cap * tileExtent * 2.0f - 1e-6f)
                {
                    size *= 0.5f;
                }
                sizes[t] = size;
                totalArea += size * size;
            }

            // 모든 tile이 단위 정사각형에 들어갈 때까지 가장 작은 tile부터 줄입니다.
            var guard = 256;
            while (totalArea > 1.0f + 1e-6f && guard-- > 0)
            {
                var shrunk = false;
                for (var t = tileCount - 1; t >= 0; t--)
                {
                    if (sizes[t] * 0.5f >= minTileSize)
                    {
                        totalArea -= sizes[t] * sizes[t] * 0.75f;
                        sizes[t] *= 0.5f;
                        shrunk = true;
                        break;
                    }
                }
                if (!shrunk)
                {
                    return false;
                }
            }
            if (totalArea > 1.0f + 1e-6f)
            {
                return false;
            }

            // 이미 큰 순서인 tile을 quadtree에 큰 것부터 배치합니다.
            var freeNodes = new List<Vector3>(64) { new Vector3(0.0f, 0.0f, 1.0f) }; // 각 원소는 빈 영역의 x, y 시작점과 정사각형 크기를 보관합니다.
            var placements = new Vector2[tileCount];

            for (var t = 0; t < tileCount; t++)
            {
                var size = sizes[t];

                // tile을 담을 수 있는 빈 node 중 가장 작은 것을 골라 공간 낭비를 줄입니다.
                var best = -1;
                for (var n = 0; n < freeNodes.Count; n++)
                {
                    if (freeNodes[n].z >= size - 1e-6f && (best < 0 || freeNodes[n].z < freeNodes[best].z))
                    {
                        best = n;
                    }
                }
                if (best < 0)
                {
                    return false;
                }

                var node = freeNodes[best];
                freeNodes.RemoveAt(best);

                // 선택 node를 요청 tile 크기까지 4분할하며 남은 quarter를 빈 공간으로 유지합니다.
                while (node.z > size + 1e-6f)
                {
                    var half = node.z * 0.5f;
                    freeNodes.Add(new Vector3(node.x + half, node.y, half));
                    freeNodes.Add(new Vector3(node.x, node.y + half, half));
                    freeNodes.Add(new Vector3(node.x + half, node.y + half, half));
                    node.z = half;
                }

                placements[t] = new Vector2(node.x, node.y);
            }

            // tile 크기 단위의 분수 column·row를 shader용 tile vector로 기록합니다.
            for (var r = 0; r < _maskGroupCount; r++)
            {
                var t = r / 4;
                var channel = r & 3;
                var size = sizes[t];
                var group = ranked[r];

                _maskTiles[group + 1] = new Vector4(
                    channel,
                    placements[t].x / size,
                    placements[t].y / size,
                    size);
            }

            return true;
        }


        /// 입력: 없음; 반환: int.
        private int ComputeMaskIndexCount()
        {
            var total = 0;

            for (var group = 0; group < _maskGroupCount; group++)
            {
                var groupMembers = _maskGroupMembers[group];
                for (var m = 0; m < groupMembers.Length; m++)
                {
                    total += _indexCount[groupMembers[m]];
                }
            }

            return total;
        }


        /// 입력: 없음; 반환: 없음.
        private void BuildMaskSections()
        {
            _maskSections.Clear();
            _maskSubMeshes.Clear();

            if (_maskGroupCount < 1)
            {
                return;
            }

            if (_maskAtlas == null)
            {
                var atlasSize = EffectiveMaskAtlasSize;

                _maskAtlas = new RenderTexture(atlasSize, atlasSize, 0, RenderTextureFormat.ARGB32)
                {
                    name = _controller.Model.name + " MaskAtlas",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false
                };

                // 메모리가 부족한 기기에서는 할당이 실패할 수 있습니다. 이 처리가 없으면
                // 생성되지 않은 텍스처에 매 프레임 마스크를 기록하며 빠져나올 길이 없습니다.
                if (!_maskAtlas.Create())
                {
                    Debug.LogWarning("[CubismBatchedModelRenderer] Mask atlas allocation failed, falling back to legacy rendering.");

                    _maskAtlas.Release();
                    UnityEngine.Object.DestroyImmediate(_maskAtlas);
                    _maskAtlas = null;
                    _isBroken = true;

                    return;
                }
            }

            var cursor = 0;
            var subMeshIndex = 0;

            for (var group = 0; group < _maskGroupCount; group++)
            {
                var groupMembers = _maskGroupMembers[group];

                // 같은 mask 그룹 안의 drawable을 texture·cull 조합별 section으로 나눕니다.
                var sectionOf = new Dictionary<long, int>();
                var sectionMembers = new List<List<int>>();
                var sectionTexture = new List<Texture>();
                var sectionDoubleSided = new List<bool>();

                for (var m = 0; m < groupMembers.Length; m++)
                {
                    var drawableIndex = groupMembers[m];
                    var renderer = _renderersByDrawable[drawableIndex];
                    var texture = renderer != null ? (Texture)renderer.MainTexture : Texture2D.whiteTexture;
                    var doubleSided = _isDoubleSided[drawableIndex];

                    var sectionKey = ((long)texture.GetInstanceID() << 1) | (doubleSided ? 1L : 0L);

                    if (!sectionOf.TryGetValue(sectionKey, out var section))
                    {
                        section = sectionMembers.Count;
                        sectionOf.Add(sectionKey, section);
                        sectionMembers.Add(new List<int>(groupMembers.Length));
                        sectionTexture.Add(texture);
                        sectionDoubleSided.Add(doubleSided);
                    }

                    sectionMembers[section].Add(drawableIndex);
                }

                for (var section = 0; section < sectionMembers.Count; section++)
                {
                    var start = cursor;

                    for (var m = 0; m < sectionMembers[section].Count; m++)
                    {
                        var drawableIndex = sectionMembers[section][m];
                        CopyBakedIndices(drawableIndex, ref cursor);
                    }

                    var properties = new MaterialPropertyBlock();
                    properties.SetTexture(MainTextureId, sectionTexture[section]);
                    properties.SetVector(MaskTileId, _maskTiles[group + 1]);

                    _maskSections.Add(new MaskSection
                    {
                        GroupIndex = group,
                        SubMeshIndex = subMeshIndex,
                        Material = sectionDoubleSided[section] ? CubismBuiltinMaterials.Mask : CubismBuiltinMaterials.MaskCulling,
                        Properties = properties
                    });

                    _maskSubMeshes.Add(MakeSubMeshDescriptor(start, cursor - start));
                    subMeshIndex++;
                }
            }

            // 변하지 않는 mask index 영역을 GPU index buffer에 업로드합니다.
            if (_use32BitIndices)
            {
                _mesh.SetIndexBufferData(_indexBuffer32, 0, 0, _maskIndexCount, CubismBatchedRendering.UpdateFlags);
            }
            else
            {
                _mesh.SetIndexBufferData(_indexBuffer16, 0, 0, _maskIndexCount, CubismBatchedRendering.UpdateFlags);
            }

            // 새 section 구성은 기존 atlas 내용과 맞지 않으므로 다시 그리게 표시합니다.
            _maskContentDirty = true;
            _maskAtlasContentValid = false;
        }


        /// 입력: drawableIndex(int), cursor(ref int); 반환: 없음.
        private void CopyBakedIndices(int drawableIndex, ref int cursor)
        {
            var count = _indexCount[drawableIndex];
            var indexBase = _indexBase[drawableIndex];

            if (_use32BitIndices)
            {
                NativeArray<uint>.Copy(_bakedIndices32, indexBase, _indexBuffer32, cursor, count);
            }
            else
            {
                NativeArray<ushort>.Copy(_bakedIndices16, indexBase, _indexBuffer16, cursor, count);
            }

            cursor += count;
        }


        /// 입력: indexStart(int), indexCount(int); 반환: SubMeshDescriptor.
        private SubMeshDescriptor MakeSubMeshDescriptor(int indexStart, int indexCount)
        {
            return new SubMeshDescriptor(indexStart, indexCount)
            {
                bounds = _mesh.bounds,
                firstVertex = 0,
                vertexCount = _totalVertexCount
            };
        }


        /// 입력: 없음; 반환: 없음.
        private void BuildTextures()
        {
            var distinct = new List<Texture>(8);

            for (var i = 0; i < _drawableCount; i++)
            {
                var renderer = _renderersByDrawable[i];
                var texture = renderer != null ? (Texture)renderer.MainTexture : Texture2D.whiteTexture;

                var slot = distinct.IndexOf(texture);
                if (slot < 0)
                {
                    slot = distinct.Count;
                    distinct.Add(texture);
                }

                _textureSlot[i] = slot;
            }

            _textures = distinct.ToArray();
            _useTextureArray = false;
            _textureArrayPending = false;

            if (CubismBatchedRendering.TextureArrayAllowed && _textures.Length > 1)
            {
                if (Time.realtimeSinceStartup >= _textureArrayActivationTime)
                {
                    TryBuildTextureArray();
                }
                else
                {
                    // source texture가 비동기 업로드 중이면 지금 복사한 placeholder가 array에 고정되므로, settle window 뒤 재시도합니다.
                    _textureArrayPending = true;
                }
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void TryBuildTextureArray()
        {
            var first = _textures[0] as Texture2D;

            if (first == null)
            {
                return;
            }

            for (var i = 1; i < _textures.Length; i++)
            {
                var texture = _textures[i] as Texture2D;

                if (texture == null
                    || texture.width != first.width
                    || texture.height != first.height
                    || texture.graphicsFormat != first.graphicsFormat
                    || texture.mipmapCount != first.mipmapCount)
                {
                    return;
                }
            }

            try
            {
                var array = new Texture2DArray(
                    first.width, first.height, _textures.Length,
                    first.graphicsFormat,
                    first.mipmapCount > 1 ? UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain : UnityEngine.Experimental.Rendering.TextureCreationFlags.None,
                    first.mipmapCount)
                {
                    name = _controller.Model.name + " TextureArray",
                    filterMode = first.filterMode,
                    wrapMode = first.wrapMode,
                    anisoLevel = first.anisoLevel,
                    // _MainTexArray는 keyword HLSL 선언으로만 참조되어 scene load의 UnloadUnusedAssets가 material 연결을 못 보고 해제할 수 있습니다. disk backing 없는 runtime array가 null이면 아바타가 회색으로 렌더되므로 HideAndDontSave의 DontUnloadUnusedAsset로 상주시킵니다.
                    hideFlags = HideFlags.HideAndDontSave
                };

                if (array.mipmapCount != first.mipmapCount)
                {
                    UnityEngine.Object.DestroyImmediate(array);
                    return;
                }

                for (var i = 0; i < _textures.Length; i++)
                {
                    Graphics.CopyTexture(_textures[i], 0, array, i);
                }

                _textureArray = array;
                _useTextureArray = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CubismBatchedModelRenderer] Texture array unavailable, batching per texture instead: {e.Message}");

                if (_textureArray != null)
                {
                    UnityEngine.Object.DestroyImmediate(_textureArray);
                    _textureArray = null;
                }

                _useTextureArray = false;
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void RefreshTextureArrayContent()
        {
            if (!_useTextureArray || _textures == null)
            {
                return;
            }

            // graphics context 유실이나 guard 전 unload로 texture array 객체가 사라졌으면 다음 flush에서 array와 연결 재질을 함께 다시 만듭니다.
            if (_textureArray == null)
            {
                _texturesDirty = true;
                return;
            }

            try
            {
                var slices = Mathf.Min(_textures.Length, _textureArray.depth);

                for (var i = 0; i < slices; i++)
                {
                    if (_textures[i] != null)
                    {
                        Graphics.CopyTexture(_textures[i], 0, _textureArray, i);
                    }
                }
            }
            catch (Exception e)
            {
                // source texture의 크기·형식이 달라지면 slice 호환성이 없으므로 전체 texture 상태를 다시 만듭니다.
                Debug.LogWarning($"[CubismBatchedModelRenderer] Texture array refresh failed, rebuilding: {e.Message}");
                _texturesDirty = true;
                return;
            }

            // 캐시 재질이 교체·파괴된 array를 계속 가리킬 수 있으므로 현재 array binding을 다시 기록해 회색 렌더를 막습니다.
            foreach (var material in _materials.Values)
            {
                if (material != null && material.IsKeywordEnabled("CUBISM_TEXTURE_ARRAY"))
                {
                    material.SetTexture(MainTextureArrayId, _textureArray);
                }
            }
        }


        /// 입력: 없음; 반환: 없음.
        public void RefreshVolatileGpuResources()
        {
            if (!IsValid)
            {
                return;
            }

            RefreshTextureArrayContent();

            // mask atlas는 dirty일 때만 다시 그리지만 context 유실 뒤 내용은 신뢰할 수 없으므로 갱신 대상으로 표시합니다.
            _maskContentDirty = true;
            _maskAtlasContentValid = false;
        }


        /// 입력: textureSlot(int), colorBlend(BlendTypes.ColorBlend), isDoubleSided(bool); 반환: Material.
        private Material GetBatchMaterial(int textureSlot, BlendTypes.ColorBlend colorBlend, bool isDoubleSided)
        {
            var key = ((long)(_useTextureArray ? 0 : textureSlot) << 8)
                      | ((long)colorBlend << 2)
                      | (isDoubleSided ? 0L : 2L)
                      | (_useTextureArray ? 1L : 0L);

            if (_materials.TryGetValue(key, out var material) && material != null)
            {
                // cached 재질을 draw에 쓸 때마다 texture array binding을 다시 기록합니다. 객체가 살아 있어도 binding이 null이면 shader가 회색을 sample하므로 frame 사이 누가 지웠어도 유효한 참조를 보장합니다.
                if (_useTextureArray && _textureArray != null)
                {
                    material.SetTexture(MainTextureArrayId, _textureArray);
                }

                return material;
            }

            material = new Material(CubismBatchedRendering.Shader)
            {
                name = $"Cubism Batched ({colorBlend}{(isDoubleSided ? string.Empty : ", Cull")}{(_useTextureArray ? ", Array" : $", Tex{textureSlot}")})",
                hideFlags = HideFlags.HideAndDontSave
            };

            switch (colorBlend)
            {
                case BlendTypes.ColorBlend.Add:
                    material.SetInt(SrcColorId, (int)UnityEngine.Rendering.BlendMode.One);
                    material.SetInt(DstColorId, (int)UnityEngine.Rendering.BlendMode.One);
                    material.SetInt(SrcAlphaId, (int)UnityEngine.Rendering.BlendMode.Zero);
                    material.SetInt(DstAlphaId, (int)UnityEngine.Rendering.BlendMode.One);
                    break;
                case BlendTypes.ColorBlend.Multiply:
                    material.SetInt(SrcColorId, (int)UnityEngine.Rendering.BlendMode.DstColor);
                    material.SetInt(DstColorId, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    material.SetInt(SrcAlphaId, (int)UnityEngine.Rendering.BlendMode.Zero);
                    material.SetInt(DstAlphaId, (int)UnityEngine.Rendering.BlendMode.One);
                    break;
                default:
                    material.SetInt(SrcColorId, (int)UnityEngine.Rendering.BlendMode.One);
                    material.SetInt(DstColorId, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    material.SetInt(SrcAlphaId, (int)UnityEngine.Rendering.BlendMode.One);
                    material.SetInt(DstAlphaId, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    break;
            }

            material.SetInt(CullId, isDoubleSided ? (int)CullMode.Off : (int)CullMode.Back);

            if (_useTextureArray)
            {
                material.EnableKeyword("CUBISM_TEXTURE_ARRAY");
                material.SetTexture(MainTextureArrayId, _textureArray);
            }
            else
            {
                material.SetTexture(MainTextureId, _textures[textureSlot]);
            }

            _materials[key] = material;

            return material;
        }

        #endregion


        #region Per-Frame Update (main thread, from OnDynamicDrawableData)

        /// 입력: data(CubismDynamicDrawableData[]); 반환: 없음.
        public unsafe void ConsumeDynamicData(CubismDynamicDrawableData[] data)
        {
            if (!IsValid || data == null || data.Length != _drawableCount)
            {
                return;
            }

            var fullRefresh = !_receivedFirstData;
            _receivedFirstData = true;

            // 이제부터 정점은 Drawable에서 오므로 코어는 관리 사본을 채울 필요가 없습니다.
            // 모델을 다시 만들면 배열 인스턴스가 바뀌므로 이벤트마다 다시 확정합니다.
            SuppressManagedVertexCopy(data);

            var orderDirty = false;
            var visibilityDirty = false;
            var applySortZ = _controller.SortingMode == CubismSortingMode.BackToFrontZ;
            var depthOffset = _controller.DepthOffset;

            var positions = (Vector3*)_positions.GetUnsafePtr();

            for (var i = 0; i < _drawableCount; i++)
            {
                var drawableData = data[i];

                // core는 dirty drawable만 dynamic 정점을 채우므로 dirty일 때만 읽고, 나머지는 Build의 초기 정점을 유지해야 0 좌표를 덮어쓰지 않습니다.
                // Drawable에서 정점 스트림으로 바로 읽어 관리 사본을 건너뛰고, z에 든 정렬 깊이를 보존하며,
                // 실제로 움직였는지도 함께 받습니다. 코어는 재평가한 Drawable에 더티를 세우지
                // 모양이 바뀌었을 때 세우는 게 아닙니다.
                if (drawableData.AreVertexPositionsDirty)
                {
                    var drawable = _drawablesByIndex[i];
                    var baseVertex = _vertexBase[i];
                    var count = _vertexCount[i];
                    var changed = false;

                    if (drawable != null && count > 0)
                    {
                        var read = drawable.ReadVertexPositionsInto(positions + baseVertex, count, out changed, out var extentMinimum, out var extentMaximum);

                        // 모양이 어긋나면 모델이 바뀐 것입니다. 조용히 넘기면 mesh가 마지막 포즈로 얼어붙으므로,
                        // 다른 중도 무효화와 같은 방식으로 무효 처리해 컨트롤러가 폴백·재빌드하게 합니다.
                        if (read != count)
                        {
                            _isBroken = true;

                            return;
                        }

                        if (changed)
                        {
                            _drawableMinimum[i] = extentMinimum;
                            _drawableMaximum[i] = extentMaximum;
                            _localModelBoundsDirty = true;
                        }
                    }

                    if (changed || fullRefresh)
                    {
                        MarkPositionsDirty(baseVertex, count);

                        // mask mesh가 움직이면 기존 atlas mask 그림이 맞지 않으므로 다시 그리게 표시합니다.
                        if (_isMaskGroupMember[i])
                        {
                            _maskContentDirty = true;
                        }
                    }
                }

                if (fullRefresh || drawableData.IsRenderOrderDirty)
                {
                    if (_renderOrders[i] != drawableData.RenderOrder)
                    {
                        _renderOrders[i] = drawableData.RenderOrder;
                        orderDirty = true;
                    }
                }

                // visibility는 one-shot event가 아닌 매 update의 절대 IsVisible 값으로 반영합니다. 비활성화 중 event를 놓쳐 pose가 숨긴 파츠가 계속 사라지는 문제를 피하고, 비교로 재구성 비용만 제한합니다.
                {
                    var isVisible = drawableData.IsVisible;
                    var visibleChanged = _visible[i] != isVisible;

                    if (visibleChanged)
                    {
                        _visible[i] = isVisible;
                        visibilityDirty = true;
                    }

                    if (visibleChanged || fullRefresh)
                    {
                        // mesh 없는 MeshRenderer도 raycast·사용자 코드의 가시성 신호이므로 enabled 값을 현재 visibility와 동기화합니다.
                        var renderer = _renderersByDrawable[i];
                        if (renderer != null && renderer.MeshRenderer.enabled != isVisible)
                        {
                            renderer.MeshRenderer.enabled = isVisible;
                        }
                    }
                }

                // opacity도 누락된 one-shot event에 의존하지 않도록 현재 절대값으로 동기화합니다.
                if (fullRefresh || drawableData.IsOpacityDirty || _opacities[i] != drawableData.Opacity)
                {
                    _opacities[i] = drawableData.Opacity;

                    var renderer = _renderersByDrawable[i];
                    if (renderer != null)
                    {
                        renderer.Opacity = drawableData.Opacity;
                    }

                    RecomputeColorRow(i);
                }
                else if (fullRefresh || drawableData.IsBlendColorDirty)
                {
                    RecomputeColorRow(i);
                }
            }

            if (orderDirty)
            {
                RebuildOrder();
            }

            if (orderDirty || visibilityDirty)
            {
                _indicesDirty = true;
            }

            // 정렬 깊이를 더는 매 프레임 dirty drawable마다 다시 쓰지 않습니다. Drawable 직접 읽기가 z를 보존합니다.
            // 렌더 순서가 바뀌거나, 깊이 설정이 바뀌거나, z 정렬을 벗어날 때(스트림을 0으로 되돌려야 함)만 갱신합니다.
            var sortZConfigurationChanged = applySortZ != _appliedSortZ
                || (applySortZ && !Mathf.Approximately(depthOffset, _appliedDepthOffset));

            if (sortZConfigurationChanged || (orderDirty && applySortZ) || fullRefresh)
            {
                RefreshSortZ(applySortZ ? depthOffset : 0.0f);

                _appliedSortZ = applySortZ;
                _appliedDepthOffset = depthOffset;

                // 모델 AABB가 정렬 깊이만큼 z를 넓히므로 다시 계산해야 합니다.
                _localModelBoundsDirty = true;
            }
        }


        /// 이 모델의 정점 관리 사본 생성을 코어에서 멈추게 하고, 나중에 억제를 풀 수 있도록 배열을 기억합니다.
        private void SuppressManagedVertexCopy(CubismDynamicDrawableData[] data)
        {
            if (ReferenceEquals(_suppressedDynamicData, data))
            {
                return;
            }

            ReleaseManagedVertexCopySuppression();

            for (var i = 0; i < data.Length; i++)
            {
                data[i].SuppressManagedVertexCopy = true;
            }

            _suppressedDynamicData = data;
        }


        /// 코어의 정점 관리 사본을 되살립니다. legacy 개별 렌더러가 다시 그릴 수 있는 시점마다 반드시 호출해야
        /// 억제 직전 기하로 얼어붙지 않습니다.
        internal void ReleaseManagedVertexCopySuppression()
        {
            var data = _suppressedDynamicData;

            if (data == null)
            {
                return;
            }

            _suppressedDynamicData = null;

            for (var i = 0; i < data.Length; i++)
            {
                data[i].SuppressManagedVertexCopy = false;
            }
        }


        /// 입력: depthOffset(float); 반환: 없음.
        private unsafe void RefreshSortZ(float depthOffset)
        {
            var positions = (Vector3*)_positions.GetUnsafePtr();

            for (var i = 0; i < _drawableCount; i++)
            {
                var z = _renderOrders[i] * -depthOffset;
                var baseVertex = _vertexBase[i];
                var count = _vertexCount[i];

                for (var v = 0; v < count; v++)
                {
                    positions[baseVertex + v].z = z;
                }
            }

            MarkPositionsDirty(0, _totalVertexCount);
        }


        /// 입력: 없음; 반환: 없음.
        private void RebuildOrder()
        {
            // 정상 render order는 [0, drawableCount) 범위의 순열입니다.
            var isPermutation = true;

            for (var i = 0; i < _drawableCount; i++)
            {
                _orderedDrawables[i] = -1;
            }

            for (var i = 0; i < _drawableCount; i++)
            {
                var order = _renderOrders[i];

                if (order < 0 || order >= _drawableCount || _orderedDrawables[order] != -1)
                {
                    isPermutation = false;
                    break;
                }

                _orderedDrawables[order] = i;
            }

            if (isPermutation)
            {
                return;
            }

            // native 순열이 깨진 경우에도 일관된 draw를 위해 render order 안정 정렬로 복구합니다.
            for (var i = 0; i < _drawableCount; i++)
            {
                _orderedDrawables[i] = i;
            }

            _renderOrderComparison ??= CompareByRenderOrder;
            Array.Sort(_orderedDrawables, _renderOrderComparison);
        }


        /// 입력: a(int), b(int); 반환: int.
        private int CompareByRenderOrder(int a, int b)
        {
            var byOrder = _renderOrders[a].CompareTo(_renderOrders[b]);
            return byOrder != 0 ? byOrder : a.CompareTo(b);
        }


        /// 입력: drawableIndex(int); 반환: 없음.
        public void RecomputeColorRow(int drawableIndex)
        {
            if (_isDisposed || _isBroken)
            {
                return;
            }

            var renderer = _renderersByDrawable[drawableIndex];

            if (renderer == null)
            {
                return;
            }

            var tint = renderer.Color;
            tint.a *= _opacities[drawableIndex];

            var multiply = renderer.MultiplyColor;
            var screen = renderer.ScreenColor;

            if (_convertBlendColorsToLinear)
            {
                multiply = multiply.linear;
                screen = screen.linear;
            }

            var row = new Stream1Data
            {
                Color = tint,
                MultiplyAndGroup = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(multiply.r * 255.0f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(multiply.g * 255.0f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(multiply.b * 255.0f), 0, 255),
                    _maskGroup[drawableIndex]),
                ScreenAndInvert = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(screen.r * 255.0f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(screen.g * 255.0f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(screen.b * 255.0f), 0, 255),
                    // unmasked sentinel은 mask factor가 1이 되도록 invert를 1로 기록합니다.
                    (byte)(_maskGroup[drawableIndex] == 0 ? 255 : (_isInverted[drawableIndex] ? 255 : 0)))
            };

            var baseVertex = _vertexBase[drawableIndex];
            var count = _vertexCount[drawableIndex];

            for (var v = 0; v < count; v++)
            {
                _stream1[baseVertex + v] = row;
            }

            MarkStream1Dirty(baseVertex, count);
        }


        /// 입력: renderer(CubismRenderer); 반환: 없음.
        public void MarkColorDirty(CubismRenderer renderer)
        {
            if (renderer == null || renderer.Drawable == null)
            {
                return;
            }

            RecomputeColorRow(renderer.Drawable.UnmanagedIndex);
        }


        /// 입력: 없음; 반환: 없음.
        public void MarkTexturesDirty()
        {
            _texturesDirty = true;

            // 새 texture 내용은 비동기 업로드될 수 있으므로 settle window를 다시 시작합니다.
            _textureArrayActivationTime = Time.realtimeSinceStartup
                + CubismBatchedRendering.TextureArrayActivationDelaySeconds;
        }

        #endregion


        #region Rendering (main thread, from the URP render pass)

        /// 입력: 없음; 반환: 없음.
        public void FlushMeshData()
        {
            if (!IsValid)
            {
                return;
            }

            if (_textureArrayPending && Time.realtimeSinceStartup >= _textureArrayActivationTime)
            {
                // settle window가 끝나면 일반 texture 재구성 경로로 array의 UV slice와 batch key를 다시 굽습니다.
                _textureArrayPending = false;
                _texturesDirty = true;
            }

            if (_lastFlushedFrame == Time.frameCount && !_texturesDirty)
            {
                return;
            }

            _lastFlushedFrame = Time.frameCount;

            if (_texturesDirty)
            {
                RebuildTexturesAndUvs();
                _texturesDirty = false;
                _indicesDirty = true;
            }

            if (_positionsDirty)
            {
                var start = Mathf.Clamp(_positionsDirtyMin, 0, _totalVertexCount);
                var count = Mathf.Clamp(_positionsDirtyMax, start, _totalVertexCount) - start;

                if (count > 0)
                {
                    _mesh.SetVertexBufferData(_positions, start, start, count, 0, CubismBatchedRendering.UpdateFlags);
                }

                _positionsDirty = false;
            }

            if (_stream1Dirty)
            {
                var start = Mathf.Clamp(_stream1DirtyMin, 0, _totalVertexCount);
                var count = Mathf.Clamp(_stream1DirtyMax, start, _totalVertexCount) - start;

                if (count > 0)
                {
                    _mesh.SetVertexBufferData(_stream1, start, start, count, 1, CubismBatchedRendering.UpdateFlags);
                }

                _stream1Dirty = false;
            }

            if (_indicesDirty)
            {
                RebuildBatches();
                _indicesDirty = false;
            }
        }


        /// 입력: 없음; 반환: 없음.
        private void RebuildTexturesAndUvs()
        {
            // 이전 texture set을 참조하는 재질을 해제해 GPU 참조가 남지 않게 합니다.
            foreach (var material in _materials.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }
            _materials.Clear();

            if (_textureArray != null)
            {
                UnityEngine.Object.DestroyImmediate(_textureArray);
                _textureArray = null;
            }

            BuildTextures();

            // 정적 UV stream의 texture slice 인덱스를 새 array 구성에 맞춰 갱신합니다.
            for (var i = 0; i < _drawableCount; i++)
            {
                var slice = _useTextureArray ? _textureSlot[i] : 0;
                var baseVertex = _vertexBase[i];
                var count = _vertexCount[i];

                for (var v = 0; v < count; v++)
                {
                    var uv = _uvs[baseVertex + v];
                    uv.z = slice;
                    _uvs[baseVertex + v] = uv;
                }
            }

            _mesh.SetVertexBufferData(_uvs, 0, 0, _totalVertexCount, 2, CubismBatchedRendering.UpdateFlags);

            // mask section도 texture별로 나뉘므로 texture 변경 뒤 layout을 다시 계산합니다.
            BuildMaskSections();
        }


        /// 입력: 없음; 반환: 없음.
        private void RebuildBatches()
        {
            _batches.Clear();

            var cursor = _maskIndexCount;
            var batchStart = cursor;
            var hasOpenBatch = false;
            var currentTexture = -1;
            var currentBlend = BlendTypes.ColorBlend.Normal;
            var currentDoubleSided = false;

            for (var order = 0; order < _drawableCount; order++)
            {
                var drawableIndex = _orderedDrawables[order];

                if (drawableIndex < 0
                    || !_visible[drawableIndex]
                    || _indexCount[drawableIndex] < 1)
                {
                    continue;
                }

                var textureSlot = _useTextureArray ? 0 : _textureSlot[drawableIndex];
                var blend = _colorBlend[drawableIndex];
                var doubleSided = _isDoubleSided[drawableIndex];

                if (!hasOpenBatch
                    || textureSlot != currentTexture
                    || blend != currentBlend
                    || doubleSided != currentDoubleSided)
                {
                    if (hasOpenBatch)
                    {
                        _batches.Add(new Batch
                        {
                            TextureSlot = currentTexture,
                            ColorBlend = currentBlend,
                            IsDoubleSided = currentDoubleSided,
                            IndexStart = batchStart,
                            IndexCount = cursor - batchStart
                        });
                    }

                    batchStart = cursor;
                    currentTexture = textureSlot;
                    currentBlend = blend;
                    currentDoubleSided = doubleSided;
                    hasOpenBatch = true;
                }

                CopyBakedIndices(drawableIndex, ref cursor);
            }

            if (hasOpenBatch && cursor > batchStart)
            {
                _batches.Add(new Batch
                {
                    TextureSlot = currentTexture,
                    ColorBlend = currentBlend,
                    IsDoubleSided = currentDoubleSided,
                    IndexStart = batchStart,
                    IndexCount = cursor - batchStart
                });
            }

            _mainIndexCount = cursor - _maskIndexCount;

            // 다시 만든 일반 draw index 영역을 GPU mesh에 업로드합니다.
            if (_mainIndexCount > 0)
            {
                if (_use32BitIndices)
                {
                    _mesh.SetIndexBufferData(_indexBuffer32, _maskIndexCount, _maskIndexCount, _mainIndexCount, CubismBatchedRendering.UpdateFlags);
                }
                else
                {
                    _mesh.SetIndexBufferData(_indexBuffer16, _maskIndexCount, _maskIndexCount, _mainIndexCount, CubismBatchedRendering.UpdateFlags);
                }
            }

            // mask section 뒤 batch 순서의 전체 submesh 표를 한 번에 적용해, 중간 기본 descriptor 때문에 Unity 경고가 반복되는 것을 막습니다.
            _subMeshScratch.Clear();
            _subMeshScratch.AddRange(_maskSubMeshes);

            for (var batch = 0; batch < _batches.Count; batch++)
            {
                _subMeshScratch.Add(MakeSubMeshDescriptor(_batches[batch].IndexStart, _batches[batch].IndexCount));
            }

            _mesh.SetSubMeshes(_subMeshScratch, CubismBatchedRendering.UpdateFlags);
        }


        /// 입력: buffer(CommandBuffer); 반환: 없음.
        public void RecordMaskPass(CommandBuffer buffer)
        {
            if (!IsValid || _maskSections.Count < 1)
            {
                return;
            }

            // 완전히 투명한 모델은 draw하지 않되, 다시 나타날 때 atlas를 갱신할 수 있도록 대기 mask update는 dirty로 남깁니다.
            if (_controller.Opacity <= 0.0f)
            {
                return;
            }

            // atlas는 상주하므로 mask mesh가 안 움직이고 내용이 유효하면 재렌더를 건너뜁니다. context 유실로 다시 만든 RT는 비어 있으므로 항상 다시 그립니다.
            if (_maskAtlas == null || !_maskAtlas.IsCreated())
            {
                _maskAtlasContentValid = false;
            }

            if (!_maskContentDirty && _maskAtlasContentValid)
            {
                return;
            }

            _maskContentDirty = false;
            _maskAtlasContentValid = true;

            UpdateMaskTransforms();

            // 이전 atlas 내용은 완전히 덮어쓰므로 DontCare로 지정해 tiler가 이전 tile 메모리를 읽지 않게 합니다.
            buffer.SetRenderTarget(_maskAtlas, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            buffer.ClearRenderTarget(false, true, Color.clear);

            for (var section = 0; section < _maskSections.Count; section++)
            {
                var maskSection = _maskSections[section];

                maskSection.Properties.SetVector(MaskTransformId, _maskTransforms[maskSection.GroupIndex + 1]);

                buffer.DrawMesh(
                    _mesh,
                    Matrix4x4.identity,
                    maskSection.Material,
                    maskSection.SubMeshIndex,
                    0,
                    maskSection.Properties);
            }
        }


        /// 입력: 없음; 반환: 없음.
        private unsafe void UpdateMaskTransforms()
        {
            if (_lastMaskUpdateFrame == Time.frameCount)
            {
                return;
            }

            _lastMaskUpdateFrame = Time.frameCount;

            var positions = (Vector3*)_positions.GetUnsafePtr();

            for (var group = 0; group < _maskGroupCount; group++)
            {
                var groupMembers = _maskGroupMembers[group];

                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);

                for (var m = 0; m < groupMembers.Length; m++)
                {
                    var drawableIndex = groupMembers[m];
                    var baseVertex = _vertexBase[drawableIndex];
                    var count = _vertexCount[drawableIndex];

                    for (var v = 0; v < count; v++)
                    {
                        var position = positions[baseVertex + v];

                        if (position.x < min.x) { min.x = position.x; }
                        if (position.y < min.y) { min.y = position.y; }
                        if (position.x > max.x) { max.x = position.x; }
                        if (position.y > max.y) { max.y = position.y; }
                    }
                }

                var size = max - min;
                var scale = Mathf.Max(size.x, size.y);

                if (scale < 1e-6f || min.x > max.x)
                {
                    scale = 1e-6f;
                }

                var center = (min + max) * 0.5f;

                _maskTransforms[group + 1] = new Vector4(center.x, center.y, 1.0f / scale, 0.0f);
            }
        }


        /// 입력: buffer(CommandBuffer); 반환: 없음.
        public void RecordMainDraws(CommandBuffer buffer)
        {
            if (!IsValid || _batches.Count < 1)
            {
                return;
            }

            var modelOpacity = Mathf.Clamp01(_controller.Opacity);

            // 완전 투명 출력은 모든 지원 blend mode에서 결과가 없으므로 draw와 대역폭 사용을 모두 건너뜁니다.
            if (modelOpacity <= 0.0f)
            {
                return;
            }

            // object-to-world는 부모 rig를 포함해야 합니다. AvatarRig가 위치·scale을 소유하므로 local TRS만 쓰면 원점·1배로 렌더됩니다.
            var matrix = _controller.transform.localToWorldMatrix;

            _modelProperties.SetFloat(ModelOpacityId, modelOpacity);
            _modelProperties.SetTexture(MaskTextureId, _maskAtlas != null ? (Texture)_maskAtlas : Texture2D.whiteTexture);

            // mask parameter 배열은 property block 대신 command-buffer global로 한 모델당 한 번 기록합니다. DrawMesh가 batch마다 block 전체를 복사하는 비용을 피하면서 command 순서로 모델별 값을 유지합니다.
            buffer.SetGlobalVectorArray(MaskTilesArrayId, _maskTiles);
            buffer.SetGlobalVectorArray(MaskTransformsArrayId, _maskTransforms);

            for (var batch = 0; batch < _batches.Count; batch++)
            {
                var currentBatch = _batches[batch];
                var material = GetBatchMaterial(currentBatch.TextureSlot, currentBatch.ColorBlend, currentBatch.IsDoubleSided);

                buffer.DrawMesh(
                    _mesh,
                    matrix,
                    material,
                    _maskSections.Count + batch,
                    0,
                    _modelProperties);
            }
        }

        #endregion


        #region Disposal

        /// 입력: 없음; 반환: bool.
        public unsafe bool ResumeAfterDisable()
        {
            if (!IsValid)
            {
                return false;
            }

            var model = _controller.Model;

            if (model == null
                || model.Drawables == null
                || model.Drawables.Length != _drawableCount)
            {
                return false;
            }

            var drawables = model.Drawables;
            var renderOrders = model.AllDrawObjectsRenderOrder;
            var positions = (Vector3*)_positions.GetUnsafePtr();

            for (var i = 0; i < drawables.Length; i++)
            {
                var drawable = drawables[i];
                var unmanagedIndex = drawable.UnmanagedIndex;

                if (unmanagedIndex < 0 || unmanagedIndex >= _drawableCount)
                {
                    return false;
                }

                // 구독이 끊긴 동안 core가 갱신했을 수 있으므로 현재 pose를 allocation 없이 읽습니다. 화면 전환마다 resume되므로 배열 getter의 큰 GC 할당을 피합니다.
                var baseVertex = _vertexBase[unmanagedIndex];

                if (drawable.ReadVertexPositionsInto(positions + baseVertex, _vertexCount[unmanagedIndex]) < 0)
                {
                    return false;
                }

                _renderOrders[unmanagedIndex] = renderOrders[unmanagedIndex];

                RecomputeColorRow(unmanagedIndex);
            }

            RebuildOrder();

            if (_controller.SortingMode == CubismSortingMode.BackToFrontZ)
            {
                RefreshSortZ(_controller.DepthOffset);
            }

            MarkPositionsDirty(0, _totalVertexCount);
            MarkStream1Dirty(0, _totalVertexCount);
            _indicesDirty = true;
            _lastFlushedFrame = -1;
            _lastMaskUpdateFrame = -1;

            // 비활성 구간 뒤 atlas 내용은 오래되었거나 버려졌을 수 있으므로 갱신 대상으로 표시합니다.
            _maskContentDirty = true;
            _maskAtlasContentValid = false;

            // 비활성·미구독 중 dirty event는 되돌아오지 않으므로 resume 뒤 첫 event를 전체 갱신으로 처리해 visibility·opacity·order를 절대값에서 동기화합니다.
            _receivedFirstData = false;

            // texture array는 Unity가 스스로 복원할 수 없는 분리 GPU 복사본이므로 숨긴 동안 내용이 사라졌을 경우를 대비해 다시 채웁니다.
            RefreshTextureArrayContent();

            return true;
        }


        /// 입력: 없음; 반환: 없음.
        public void RestoreLegacyRendererState()
        {
            // legacy 렌더러는 코어의 정점 관리 사본을 읽으므로 그리기 전에 다시 채워져야 합니다.
            // 아래 상태 push가 빠져나가더라도 억제는 반드시 풀어야 기하가 얼어붙지 않습니다.
            ReleaseManagedVertexCopySuppression();

            if (_isBroken || _renderersByDrawable == null)
            {
                return;
            }

            // scene 정리 중에는 renderer도 곧 파괴되므로 GPU 상태를 복구하지 않습니다.
            if (_controller == null || !_controller.gameObject.scene.isLoaded)
            {
                return;
            }

            for (var i = 0; i < _drawableCount; i++)
            {
                var renderer = _renderersByDrawable[i];

                if (renderer == null)
                {
                    continue;
                }

                renderer.MeshRenderer.enabled = _visible[i];
                renderer.SetDrawObjectRenderOrder(_renderOrders[i]);
            }
        }


        /// 입력: 없음; 반환: 없음.
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            DisposeResources();
        }


        /// 입력: 없음; 반환: 없음.
        private void DisposeResources()
        {
            ReleaseManagedVertexCopySuppression();

            if (_positions.IsCreated) { _positions.Dispose(); }
            if (_stream1.IsCreated) { _stream1.Dispose(); }
            if (_uvs.IsCreated) { _uvs.Dispose(); }
            if (_bakedIndices16.IsCreated) { _bakedIndices16.Dispose(); }
            if (_bakedIndices32.IsCreated) { _bakedIndices32.Dispose(); }
            if (_indexBuffer16.IsCreated) { _indexBuffer16.Dispose(); }
            if (_indexBuffer32.IsCreated) { _indexBuffer32.Dispose(); }

            if (_mesh != null)
            {
                UnityEngine.Object.DestroyImmediate(_mesh);
                _mesh = null;
            }

            if (_maskAtlas != null)
            {
                _maskAtlas.Release();
                UnityEngine.Object.DestroyImmediate(_maskAtlas);
                _maskAtlas = null;
            }

            if (_textureArray != null)
            {
                UnityEngine.Object.DestroyImmediate(_textureArray);
                _textureArray = null;
            }

            foreach (var material in _materials.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }
            _materials.Clear();
        }

        #endregion
    }
}
