/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */


using Live2D.Cubism.Core.Unmanaged;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Rendering.Util;
using UnityEngine;


namespace Live2D.Cubism.Core
{
    /// <summary>
    /// Single <see cref="CubismModel"/> drawable.
    /// </summary>
    [CubismDontMoveOnReimport]
    public sealed class CubismDrawable : MonoBehaviour
    {
        #region Factory Methods

        /// <summary>
        /// Creates drawables for a <see cref="CubismModel"/>.
        /// </summary>
        /// <param name="unmanagedModel">Handle to unmanaged model.</param>
        /// <returns>Drawables root.</returns>
        internal static GameObject CreateDrawables(CubismUnmanagedModel unmanagedModel)
        {
            var root = new GameObject("Drawables");


            // Create drawables.
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


        /// <summary>
        /// Unmanaged drawables from unmanaged model.
        /// </summary>
        private CubismUnmanagedDrawables UnmanagedDrawables { get; set; }


        /// <summary>
        /// <see cref="UnmanagedIndex"/> backing field.
        /// </summary>
        [SerializeField, HideInInspector]
        private int _unmanagedIndex = -1;

        /// <summary>
        /// Position in unmanaged arrays.
        /// </summary>
        internal int UnmanagedIndex
        {
            get { return _unmanagedIndex; }
            private set { _unmanagedIndex = value; }
        }

        /// <summary>
        /// Parent Part Position in unmanaged arrays.
        /// </summary>
        public int UnmanagedParentIndex
        {
            get { return UnmanagedDrawables.ParentPartIndices[UnmanagedIndex]; }
        }

        /// <summary>
        /// Copy of Id.
        /// </summary>
        public string Id
        {
            get
            {
                // Pull data.
                return UnmanagedDrawables.Ids[UnmanagedIndex];
            }
        }

        /// <summary>
        /// Texture UnmanagedIndex.
        /// </summary>
        public int TextureIndex
        {
            get
            {
                // Pull data.
                return UnmanagedDrawables.TextureIndices[UnmanagedIndex];
            }
        }

        /// <summary>
        /// <see cref="MultiplyColor"/> backing field.
        /// </summary>
        private Color _multiplyColor;

        /// <summary>
        /// Copy of MultiplyColor.
        /// </summary>
        public Color MultiplyColor
        {
            get
            {
                var index = UnmanagedIndex * 4;

                // Pull data.
                _multiplyColor.r = UnmanagedDrawables.MultiplyColors[index];
                _multiplyColor.g = UnmanagedDrawables.MultiplyColors[index + 1];
                _multiplyColor.b = UnmanagedDrawables.MultiplyColors[index + 2];
                _multiplyColor.a = UnmanagedDrawables.MultiplyColors[index + 3];

                return _multiplyColor;
            }
        }

        /// <summary>
        /// <see cref="ScreenColor"/> backing field.
        /// </summary>
        public Color _screenColor;

        /// <summary>
        /// Copy of ScreenColor.
        /// </summary>
        public Color ScreenColor
        {
            get
            {
                var index = UnmanagedIndex * 4;

                // Pull data.
                _screenColor.r = UnmanagedDrawables.ScreenColors[index];
                _screenColor.g = UnmanagedDrawables.ScreenColors[index + 1];
                _screenColor.b = UnmanagedDrawables.ScreenColors[index + 2];
                _screenColor.a = UnmanagedDrawables.ScreenColors[index + 3];

                return _screenColor;
            }
        }

        /// <summary>
        /// Index of Parent Part.
        /// </summary>
        public int ParentPartIndex
        {
            get
            {
                // Pull data.
                return UnmanagedDrawables.ParentPartIndices[UnmanagedIndex];
            }
        }

        /// <summary>
        /// Copy of the masks.
        /// </summary>
        public CubismDrawable[] Masks
        {
            get
            {
                var drawables = this
                    .FindCubismModel(true)
                    .Drawables;


                // Get addresses.
                var counts = UnmanagedDrawables.MaskCounts;
                var indices = UnmanagedDrawables.Masks;


                // Pull data.
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

        /// <summary>
        /// Copy of vertex positions.
        /// </summary>
        public Vector3[] VertexPositions
        {
            get
            {
                // Get addresses.
                var counts = UnmanagedDrawables.VertexCounts;
                var positions = UnmanagedDrawables.VertexPositions;


                // Pull data.
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


        /// <summary>
        /// Reads the current vertex positions straight into <paramref name="destination"/>
        /// without the managed-array allocation of <see cref="VertexPositions"/>.
        /// Only x/y are written; z is preserved for callers that store sort depth there.
        /// Returns the vertex count read, or -1 when the unmanaged data does not match
        /// the expected shape (caller should treat the model as changed).
        /// </summary>
        internal unsafe int ReadVertexPositionsInto(Vector3* destination, int capacity)
        {
            return ReadVertexPositionsInto(destination, capacity, out _);
        }


        /// <summary>
        /// <see cref="ReadVertexPositionsInto(Vector3*,int)"/> that also reports whether
        /// any value actually differs from what <paramref name="destination"/> held.
        /// The core raises its dirty flag whenever it re-evaluated a drawable, which is
        /// not the same as the drawable having moved; comparing during the copy costs
        /// nothing extra and lets callers skip uploads and mask re-renders.
        /// </summary>
        internal unsafe int ReadVertexPositionsInto(Vector3* destination, int capacity, out bool changed)
        {
            changed = false;

            var index = UnmanagedIndex;
            var positionViews = UnmanagedDrawables.VertexPositions;

            if (positionViews == null || index < 0 || index >= positionViews.Length)
            {
                return -1;
            }

            var positions = positionViews[index];
            var count = UnmanagedDrawables.VertexCounts[index];

            // Zero-vertex drawables have no view to read (IsValid is false for
            // empty views); that is a valid state, not a shape mismatch.
            if (count == 0)
            {
                return 0;
            }

            if (count < 0 || count > capacity || !positions.IsValid || positions.Length < count * 2)
            {
                return -1;
            }

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
            }

            return count;
        }


        /// <summary>
        /// Managed-array overload of <see cref="ReadVertexPositionsInto(Vector3*,int)"/>
        /// for callers that keep a reusable scratch buffer instead of native memory.
        /// </summary>
        internal unsafe int ReadVertexPositionsInto(Vector3[] destination)
        {
            if (destination == null || destination.Length < 1)
            {
                return -1;
            }

            fixed (Vector3* pinned = destination)
            {
                return ReadVertexPositionsInto(pinned, destination.Length);
            }
        }

        /// <summary>
        /// Copy of vertex texture coordinates.
        /// </summary>
        public Vector2[] VertexUvs
        {
            get
            {
                // Get addresses.
                var counts = UnmanagedDrawables.VertexCounts;
                var uvs = UnmanagedDrawables.VertexUvs;


                // Pull data.
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

        /// <summary>
        /// Copy of triangle indices.
        /// </summary>
        public int[] Indices
        {
            get
            {
                // Get addresses.
                var counts = UnmanagedDrawables.IndexCounts;
                var indices = UnmanagedDrawables.Indices;


                // Pull data.
                var buffer = new int[counts[UnmanagedIndex]];


                for (var i = 0; i < buffer.Length; ++i)
                {
                    buffer[i] = indices[UnmanagedIndex][i];
                }


                return buffer;
            }
        }


        /// <summary>
        /// True if double-sided.
        /// </summary>
        public bool IsDoubleSided
        {
            get
            {
                // Get address.
                var flags = UnmanagedDrawables.ConstantFlags;


                // Pull data.
                return flags[UnmanagedIndex].HasIsDoubleSidedFlag();
            }
        }

        /// <summary>
        /// True if masking is requested.
        /// </summary>
        public bool IsMasked
        {
            get
            {
                // Get address.
                var counts = UnmanagedDrawables.MaskCounts;


                // Pull data.
                return counts[UnmanagedIndex] > 0;
            }
        }

        /// <summary>
        /// True if inverted mask.
        /// </summary>
        public bool IsInverted
        {
            get
            {
                // Get address.
                var flags = UnmanagedDrawables.ConstantFlags;


                // Pull data.
                return flags[UnmanagedIndex].HasIsInvertedMaskFlag();
            }
        }

        /// <summary>
        /// True if additive blending is requested.
        /// </summary>
        public bool BlendAdditive
        {
            get
            {
                // Get address.
                var flags = UnmanagedDrawables.ConstantFlags;


                // Pull data.
                return flags[UnmanagedIndex].HasBlendAdditiveFlag();
            }
        }

        /// <summary>
        /// True if multiply blending is setd.
        /// </summary>
        public bool MultiplyBlend
        {
            get
            {
                // Get address.
                var flags = UnmanagedDrawables.ConstantFlags;


                // Pull data.
                return flags[UnmanagedIndex].HasBlendMultiplicativeFlag();
            }
        }

        #region Cubism 5.3

        /// <summary>
        /// Gets the color blend mode of the drawable.
        /// </summary>
        public BlendTypes.ColorBlend ColorBlend
        {
            get
            {
                // Pull data.
                return (BlendTypes.ColorBlend)(UnmanagedDrawables.BlendModes[UnmanagedIndex] & 0xFF);
            }
        }

        /// <summary>
        /// Gets the alpha blend mode of the drawable.
        /// </summary>
        public BlendTypes.AlphaBlend AlphaBlend
        {
            get
            {
                // Pull data.
                return (BlendTypes.AlphaBlend)((UnmanagedDrawables.BlendModes[UnmanagedIndex] >> 8) & 0xFF);
            }
        }

        #endregion

        /// <summary>
        /// Revives instance.
        /// </summary>
        /// <param name="unmanagedModel">Handle to unmanaged model.</param>
        internal void Revive(CubismUnmanagedModel unmanagedModel)
        {
            UnmanagedDrawables = unmanagedModel.Drawables;
        }

        /// <summary>
        /// Restores instance to initial state.
        /// </summary>
        /// <param name="unmanagedModel">Handle to unmanaged model.</param>
        /// <param name="unmanagedIndex">Position in unmanaged arrays.</param>
        private void Reset(CubismUnmanagedModel unmanagedModel, int unmanagedIndex)
        {
            Revive(unmanagedModel);

            UnmanagedIndex = unmanagedIndex;
            name = Id;
        }
    }
}
