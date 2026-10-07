/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 네이티브 Cubism 모델의 drawable 한 개를 Unity GameObject와 연결하는 프록시입니다.


using Live2D.Cubism.Core.Unmanaged;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Rendering.Util;
using UnityEngine;


namespace Live2D.Cubism.Core
{
    [CubismDontMoveOnReimport]
    public sealed class CubismDrawable : MonoBehaviour
    {
        #region Factory Methods

        /// 입력: unmanagedModel(CubismUnmanagedModel); 반환: GameObject.
        internal static GameObject CreateDrawables(CubismUnmanagedModel unmanagedModel)
        {
            var root = new GameObject("Drawables");


            // native drawable 수만큼 Unity 프록시를 만들어 루트 아래에 연결합니다.
            var unmanagedDrawables = unmanagedModel.Drawables;
            var buffer = new CubismDrawable[unmanagedDrawables.Count];


            for (var i = 0; i < buffer.Length; ++i)
            {
                var proxy = new GameObject();


                buffer[i] = proxy.AddComponent<CubismDrawable>();


                buffer[i].transform.SetParent(root.transform);
                buffer[i].Reset(unmanagedModel, i);
            }


            return root;
        }

        #endregion


        private CubismUnmanagedDrawables UnmanagedDrawables { get; set; }


        [SerializeField, HideInInspector]
        private int _unmanagedIndex = -1;

        internal int UnmanagedIndex
        {
            get { return _unmanagedIndex; }
            private set { _unmanagedIndex = value; }
        }

        public int UnmanagedParentIndex
        {
            get { return UnmanagedDrawables.ParentPartIndices[UnmanagedIndex]; }
        }

        public string Id
        {
            get
            {
                // native drawable 배열에서 현재 인덱스의 ID를 읽습니다.
                return UnmanagedDrawables.Ids[UnmanagedIndex];
            }
        }

        public int TextureIndex
        {
            get
            {
                // native drawable 배열에서 현재 인덱스의 texture 번호를 읽습니다.
                return UnmanagedDrawables.TextureIndices[UnmanagedIndex];
            }
        }

        private Color _multiplyColor;

        public Color MultiplyColor
        {
            get
            {
                var index = UnmanagedIndex * 4;

                // native RGBA 값을 임시 Color에 채웁니다.
                _multiplyColor.r = UnmanagedDrawables.MultiplyColors[index];
                _multiplyColor.g = UnmanagedDrawables.MultiplyColors[index + 1];
                _multiplyColor.b = UnmanagedDrawables.MultiplyColors[index + 2];
                _multiplyColor.a = UnmanagedDrawables.MultiplyColors[index + 3];

                return _multiplyColor;
            }
        }

        public Color _screenColor;

        public Color ScreenColor
        {
            get
            {
                var index = UnmanagedIndex * 4;

                // native RGBA 값을 임시 Color에 채웁니다.
                _screenColor.r = UnmanagedDrawables.ScreenColors[index];
                _screenColor.g = UnmanagedDrawables.ScreenColors[index + 1];
                _screenColor.b = UnmanagedDrawables.ScreenColors[index + 2];
                _screenColor.a = UnmanagedDrawables.ScreenColors[index + 3];

                return _screenColor;
            }
        }

        public int ParentPartIndex
        {
            get
            {
                // native 배열에서 부모 Part 인덱스를 읽습니다.
                return UnmanagedDrawables.ParentPartIndices[UnmanagedIndex];
            }
        }

        public CubismDrawable[] Masks
        {
            get
            {
                var drawables = this
                    .FindCubismModel(true)
                    .Drawables;


                // native mask 인덱스 배열의 주소와 길이를 가져옵니다.
                var counts = UnmanagedDrawables.MaskCounts;
                var indices = UnmanagedDrawables.Masks;


                // mask 인덱스를 Unity Drawable 프록시 참조로 변환합니다.
                var buffer = new CubismDrawable[counts[UnmanagedIndex]];


                for (var i = 0; i < buffer.Length; ++i)
                {
                    for (var j = 0; j < drawables.Length; ++j)
                    {
                        if (drawables[j].UnmanagedIndex != indices[UnmanagedIndex][i])
                        {
                            continue;
                        }


                        buffer[i] = drawables[j];


                        break;
                    }
                }


                return buffer;
            }
        }

        public Vector3[] VertexPositions
        {
            get
            {
                // native 정점 좌표 배열의 주소와 길이를 가져옵니다.
                var counts = UnmanagedDrawables.VertexCounts;
                var positions = UnmanagedDrawables.VertexPositions;


                // native x/y 좌표를 Unity Vector3 배열로 복사합니다.
                var buffer = new Vector3[counts[UnmanagedIndex]];


                for (var i = 0; i < buffer.Length; ++i)
                {
                    buffer[i] = new Vector3(
                        positions[UnmanagedIndex][(i * 2) + 0],
                        positions[UnmanagedIndex][(i * 2) + 1]
                    );
                }


                return buffer;
            }
        }


        /// 입력: destination(Vector3*), capacity(int); 반환: int.
        internal unsafe int ReadVertexPositionsInto(Vector3* destination, int capacity)
        {
            return ReadVertexPositionsInto(destination, capacity, out _, out _, out _);
        }

        /// 위와 같되 destination이 이미 갖고 있던 값과 달라졌는지, 그리고 읽은 정점의 AABB도 알려줍니다.
        /// 코어는 재평가한 Drawable에 더티를 세우지 실제로 움직였는지로 세우지 않으므로,
        /// 복사하면서 비교해두면 업로드와 마스크 재렌더를 건너뛸 수 있습니다.
        /// AABB도 같은 이유로 함께 냅니다. 정점이 이미 레지스터에 있습니다.
        /// minimum·maximum은 반환값이 양수일 때만 유효합니다.
        internal unsafe int ReadVertexPositionsInto(Vector3* destination, int capacity, out bool changed, out Vector2 minimum, out Vector2 maximum)
        {
            changed = false;
            minimum = Vector2.zero;
            maximum = Vector2.zero;

            var index = UnmanagedIndex;
            var drawables = UnmanagedDrawables;

            if (drawables == null)
            {
                return -1;
            }

            var positionViews = drawables.VertexPositions;

            if (positionViews == null || index < 0 || index >= positionViews.Length)
            {
                return -1;
            }

            var positions = positionViews[index];
            var count = drawables.VertexCounts[index];

            // 정점이 0개인 drawable은 빈 native view라 읽을 수 없지만, 배열 크기 오류가 아닌 유효한 상태입니다.
            if (count == 0)
            {
                return 0;
            }

            if (count < 0 || count > capacity || !positions.IsValid || positions.Length < count * 2)
            {
                return -1;
            }

            var minimumX = float.MaxValue;
            var minimumY = float.MaxValue;
            var maximumX = float.MinValue;
            var maximumY = float.MinValue;

            for (var v = 0; v < count; ++v)
            {
                var x = positions[(v * 2) + 0];
                var y = positions[(v * 2) + 1];

                if (destination[v].x != x || destination[v].y != y)
                {
                    changed = true;
                }

                destination[v].x = x;
                destination[v].y = y;

                if (x < minimumX) { minimumX = x; }
                if (x > maximumX) { maximumX = x; }
                if (y < minimumY) { minimumY = y; }
                if (y > maximumY) { maximumY = y; }
            }

            minimum = new Vector2(minimumX, minimumY);
            maximum = new Vector2(maximumX, maximumY);

            return count;
        }

        /// 재사용 스크래치 배열로 정점을 읽는 관리 배열 오버로드. 읽은 정점 수, 실패 시 -1.
        internal unsafe int ReadVertexPositionsInto(Vector3[] destination)
        {
            if (destination == null || destination.Length < 1)
            {
                return -1;
            }

            fixed (Vector3* pinned = destination)
            {
                return ReadVertexPositionsInto(pinned, destination.Length, out _, out _, out _);
            }
        }

        /// 입력: center(out Vector3), size(out Vector2); 반환: bool.
        public bool TryGetVertexBounds(out Vector3 center, out Vector2 size)
        {
            center = default;
            size = default;

            var drawables = UnmanagedDrawables;
            var index = UnmanagedIndex;
            if (drawables == null)
            {
                return false;
            }

            var positionViews = drawables.VertexPositions;
            var counts = drawables.VertexCounts;
            if (positionViews == null || counts == null ||
                index < 0 || index >= positionViews.Length || index >= counts.Length)
            {
                return false;
            }

            var positions = positionViews[index];
            var count = counts[index];
            if (count <= 0 || !positions.IsValid || positions.Length < count * 2)
            {
                return false;
            }

            float minX = positions[0];
            float maxX = minX;
            float minY = positions[1];
            float maxY = minY;
            if (!IsFinite(minX) || !IsFinite(minY))
            {
                return false;
            }

            for (var i = 1; i < count; ++i)
            {
                float x = positions[(i * 2) + 0];
                float y = positions[(i * 2) + 1];
                if (!IsFinite(x) || !IsFinite(y))
                {
                    return false;
                }

                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f);
            size = new Vector2(maxX - minX, maxY - minY);
            return true;
        }

        /// 입력: value(float); 반환: bool.
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public Vector2[] VertexUvs
        {
            get
            {
                // native UV 배열의 주소와 길이를 가져옵니다.
                var counts = UnmanagedDrawables.VertexCounts;
                var uvs = UnmanagedDrawables.VertexUvs;


                // native UV를 Unity Vector2 배열로 복사합니다.
                var buffer = new Vector2[counts[UnmanagedIndex]];


                for (var i = 0; i < buffer.Length; ++i)
                {
                    buffer[i] = new Vector2(
                        uvs[UnmanagedIndex][(i * 2) + 0],
                        uvs[UnmanagedIndex][(i * 2) + 1]
                    );
                }


                return buffer;
            }
        }

        public int[] Indices
        {
            get
            {
                // native 삼각형 인덱스 배열의 주소와 길이를 가져옵니다.
                var counts = UnmanagedDrawables.IndexCounts;
                var indices = UnmanagedDrawables.Indices;


                // native 인덱스를 관리 배열로 복사합니다.
                var buffer = new int[counts[UnmanagedIndex]];


                for (var i = 0; i < buffer.Length; ++i)
                {
                    buffer[i] = indices[UnmanagedIndex][i];
                }


                return buffer;
            }
        }


        public bool IsDoubleSided
        {
            get
            {
                // native 양면 렌더링 플래그 주소를 가져옵니다.
                var flags = UnmanagedDrawables.ConstantFlags;


                // 현재 drawable의 양면 렌더링 플래그를 읽습니다.
                return flags[UnmanagedIndex].HasIsDoubleSidedFlag();
            }
        }

        public bool IsMasked
        {
            get
            {
                // native 마스크 개수 주소를 가져옵니다.
                var counts = UnmanagedDrawables.MaskCounts;


                // 현재 drawable의 마스크 개수를 읽습니다.
                return counts[UnmanagedIndex] > 0;
            }
        }

        public bool IsInverted
        {
            get
            {
                // native 반전 마스크 플래그 주소를 가져옵니다.
                var flags = UnmanagedDrawables.ConstantFlags;


                // 현재 drawable의 반전 마스크 플래그를 읽습니다.
                return flags[UnmanagedIndex].HasIsInvertedMaskFlag();
            }
        }

        public bool BlendAdditive
        {
            get
            {
                // native 가산 블렌드 플래그 주소를 가져옵니다.
                var flags = UnmanagedDrawables.ConstantFlags;


                // 현재 drawable의 가산 블렌드 플래그를 읽습니다.
                return flags[UnmanagedIndex].HasBlendAdditiveFlag();
            }
        }

        public bool MultiplyBlend
        {
            get
            {
                // native 곱셈 블렌드 플래그 주소를 가져옵니다.
                var flags = UnmanagedDrawables.ConstantFlags;


                // 현재 drawable의 곱셈 블렌드 플래그를 읽습니다.
                return flags[UnmanagedIndex].HasBlendMultiplicativeFlag();
            }
        }

        #region Cubism 5.3

        public BlendTypes.ColorBlend ColorBlend
        {
            get
            {
                // native blend mode의 색상 채널 값을 읽습니다.
                return (BlendTypes.ColorBlend)(UnmanagedDrawables.BlendModes[UnmanagedIndex] & 0xFF);
            }
        }

        public BlendTypes.AlphaBlend AlphaBlend
        {
            get
            {
                // native blend mode의 알파 채널 값을 읽습니다.
                return (BlendTypes.AlphaBlend)((UnmanagedDrawables.BlendModes[UnmanagedIndex] >> 8) & 0xFF);
            }
        }

        #endregion

        /// 입력: unmanagedModel(CubismUnmanagedModel); 반환: 없음.
        internal void Revive(CubismUnmanagedModel unmanagedModel)
        {
            UnmanagedDrawables = unmanagedModel.Drawables;
        }

        /// 입력: unmanagedModel(CubismUnmanagedModel), unmanagedIndex(int); 반환: 없음.
        private void Reset(CubismUnmanagedModel unmanagedModel, int unmanagedIndex)
        {
            Revive(unmanagedModel);

            UnmanagedIndex = unmanagedIndex;
            name = Id;
        }
    }
}
