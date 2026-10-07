/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 모델의 Raycastable Drawable 캐시를 만들고, 화면 광선을 Drawable 기하와 교차시킵니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Rendering;
using System.Collections.Generic;
using UnityEngine;


namespace Live2D.Cubism.Framework.Raycasting
{
    public sealed class CubismRaycaster : MonoBehaviour
    {
        private CubismRenderer[] Raycastables { get; set; }

        private CubismDrawable[] RaycastableDrawables { get; set; }

        private CubismRaycastablePrecision[] RaycastablePrecisions { get; set; }

        /// Raycastable별 삼각형 인덱스. 모델이 바뀌기 전엔 불변이라 Refresh에서 한 번만 뽑습니다.
        private int[][] RaycastableIndices { get; set; }

        /// 최대 정점 수에 맞춘 재사용 버퍼. 매 프레임 히트테스트에서 호출당 할당이 나면 안 됩니다.
        private Vector3[] _localVertexScratch;
        private Vector3[] _worldVertexScratch;


        /// 입력: 없음; 반환: 없음.
        public void Refresh()
        {
            var candidates = this
                .FindCubismModel()
                .Drawables;


            // 모델 Drawable 중 Raycastable 컴포넌트가 활성인 대상만 찾습니다.
            var raycastables = new List<CubismRenderer>();
            var raycastableDrawables = new List<CubismDrawable>();
            var raycastablePrecisions = new List<CubismRaycastablePrecision>();


            for (var i = 0; i < candidates.Length; i++)
            {
                var raycastable = candidates[i].GetComponent<CubismRaycastable>();
                // Raycastable이 없거나 비활성이면 입력·판정 cache에서 제외합니다.
                if (!raycastable
                    || !raycastable.isActiveAndEnabled)
                {
                    continue;
                }


                raycastables.Add(candidates[i].GetComponent<CubismRenderer>());
                raycastableDrawables.Add(candidates[i]);
                raycastablePrecisions.Add(candidates[i].GetComponent<CubismRaycastable>().Precision);
            }


            // 이번 검색 결과로 renderer·Drawable·정밀도 cache를 함께 교체합니다.
            Raycastables = raycastables.ToArray();
            RaycastableDrawables = raycastableDrawables.ToArray();
            RaycastablePrecisions = raycastablePrecisions.ToArray();


            // 불변인 삼각형 인덱스를 캐시하고 스크래치 버퍼 크기를 여기서 확정합니다.
            RaycastableIndices = new int[RaycastableDrawables.Length][];

            var maximumVertexCount = 0;

            for (var i = 0; i < RaycastableDrawables.Length; i++)
            {
                RaycastableIndices[i] = RaycastableDrawables[i].Indices;

                var vertexCount = RaycastableDrawables[i].VertexPositions.Length;

                if (vertexCount > maximumVertexCount)
                {
                    maximumVertexCount = vertexCount;
                }
            }

            _localVertexScratch = new Vector3[maximumVertexCount];
            _worldVertexScratch = new Vector3[maximumVertexCount];
        }

        #region Unity Event Handling

        /// 입력: 없음; 반환: 없음.
        private void Start()
        {
            // 첫 Raycast 전에 scene의 Raycastable 상태를 cache에 채웁니다.
            Refresh();
        }

        #endregion

        /// 입력: origin(Vector3), direction(Vector3), result(CubismRaycastHit[]), maximumDistance(float); 반환: int.
        public int Raycast(Vector3 origin, Vector3 direction, CubismRaycastHit[] result, float maximumDistance = 10000.0f)
        {
            return Raycast(new Ray(origin, direction), result, maximumDistance);
        }

        /// 입력: ray(Ray), result(CubismRaycastHit[]), maximumDistance(float); 반환: int.
        public int Raycast(Ray ray, CubismRaycastHit[] result, float maximumDistance = 10000.0f)
        {
            var origin = ray.origin;

            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new CubismRaycastHit();
            }

            // cache의 각 활성 Drawable에 같은 광선을 차례로 검사합니다.
            var hitCount = 0;

            for (var i = 0; i < Raycastables.Length; i++)
            {
                var raycastable = Raycastables[i];
                var precision = RaycastablePrecisions[i];
                if (!raycastable.MeshRenderer.enabled)
                {
                    continue;
                }

                if (RaycastDrawable(origin, ray.direction.normalized, maximumDistance, precision, RaycastableDrawables[i], RaycastableIndices[i], out var hitPosition, out var hitNormal, out var hitTime))
                {
                    CubismRaycastHit raycastHit;

                    raycastHit.Drawable = RaycastableDrawables[i];
                    raycastHit.Distance = hitTime * maximumDistance;
                    raycastHit.WorldPosition = hitPosition;
                    raycastHit.LocalPosition = transform.InverseTransformPoint(hitPosition);

                    result[hitCount] = raycastHit;

                    ++hitCount;

                    // 호출자 버퍼가 가득 차면 더 찾지 않아 범위를 넘겨 쓰지 않습니다.
                    if (hitCount == result.Length)
                    {
                        break;
                    }
                }
            }

            return hitCount;
        }

        /// 입력: origin(Vector3), normalizedDirection(Vector3), length(float), precision(CubismRaycastablePrecision), drawable(CubismDrawable), hitPosition(out Vector3), hitNormal(out Vector3), hitTime(out float); 반환: bool.
        private bool RaycastDrawable(Vector3 origin, Vector3 normalizedDirection, float length, CubismRaycastablePrecision precision, CubismDrawable drawable, int[] indices, out Vector3 hitPosition, out Vector3 hitNormal, out float hitTime)
        {
            // 기하는 core가 소유한 Drawable 데이터에서 읽으므로 legacy 개별 mesh와 batch 경로 모두에서 같습니다.
            // 재사용 버퍼로 읽습니다. 할당하는 VertexPositions getter는 raycast마다 가비지를 냅니다.
            var vertices = _localVertexScratch;
            var vertexCount = drawable.ReadVertexPositionsInto(vertices);

            if (vertexCount < 1)
            {
                hitPosition = Vector3.zero;
                hitNormal = Vector3.zero;
                hitTime = 0.0f;

                return false;
            }

            var min = vertices[0];
            var max = vertices[0];

            for (var i = 1; i < vertexCount; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }

            var bounds = new Bounds((min + max) * 0.5f, max - min);

            // 회전한 Drawable Bounds와 비교할 수 있도록 광선 양 끝을 Drawable local 좌표로 바꿉니다.
            var start = drawable.transform.InverseTransformPoint(origin);
            var end = drawable.transform.InverseTransformPoint(origin + normalizedDirection * length);
            if (!LineExtentBoxIntersection(bounds, start, end, Vector3.zero, out hitPosition, out hitNormal, out hitTime))
            {
                return false;
            }
            // local Bounds 교차 위치를 호출자에게 돌려줄 월드 좌표로 다시 바꿉니다.
            hitPosition = drawable.transform.TransformPoint(hitPosition);

            switch (precision)
            {
                case CubismRaycastablePrecision.BoundingBox:
                    {
                        // Bounds 판정이 이미 이 정밀도의 적중 여부를 확정했습니다.
                        break;
                    }
                case CubismRaycastablePrecision.Triangles:
                    {
                        // 인덱스는 vertexCount 미만만 참조하므로 스크래치가 더 커도 안전합니다.
                        var positions = _worldVertexScratch;
                        var drawableTransform = drawable.transform;

                        for (var i = 0; i < vertexCount; i++)
                        {
                            positions[i] = drawableTransform.TransformPoint(vertices[i]);
                        }

                        if (!RayIntersectMesh(origin, normalizedDirection, length, positions, indices, out hitPosition, out hitTime))
                        {
                            return false;
                        }

                        break;
                    }
                default:
                    {
                        return false;
                    }
            }

            return true;
        }

        /// 입력: origin(Vector3), direction(Vector3), length(float), positions(IReadOnlyList<Vector3>), indices(int[]), hitPosition(out Vector3), hitTime(out float); 반환: bool.
        private bool RayIntersectMesh(Vector3 origin, Vector3 direction, float length, IReadOnlyList<Vector3> positions, int[] indices, out Vector3 hitPosition, out float hitTime)
        {
            hitPosition = Vector3.zero;
            hitTime = 0.0f;
            for (var i = 0; i < indices.Length; i += 3)
            {
                var t0 = positions[indices[i]];
                var t1 = positions[indices[i + 1]];
                var t2 = positions[indices[i + 2]];

                if (RayIntersectTriangle(origin, direction, length, t0, t1, t2, out hitPosition, out hitTime))
                {
                    return true;
                }
            }

            return false;
        }

        /// 입력: origin(Vector3), direction(Vector3), length(float), t0(Vector3), t1(Vector3), t2(Vector3), hitPosition(out Vector3), hitTime(out float); 반환: bool.
        private bool RayIntersectTriangle(Vector3 origin, Vector3 direction, float length, Vector3 t0, Vector3 t1, Vector3 t2, out Vector3 hitPosition, out float hitTime)
        {
            hitPosition = Vector3.zero;
            hitTime = 0.0f;

            var e1 = t1 - t0;
            var e2 = t2 - t0;

            var p = Vector3.Cross(direction, e2);

            var det = Vector3.Dot(e1, p);

            if (Mathf.Approximately(det, 0))
            {
                return false;
            }

            var invDet = 1.0f / det;
            var t = origin - t0;

            var u = Vector3.Dot(t, p) * invDet;

            if (u < 0.0f || u > 1.0f)
            {
                return false;
            }


            var q = Vector3.Cross(t, e1);

            var v = Vector3.Dot(direction, q) * invDet;

            if (v < 0.0f || u + v > 1.0f)
            {
                return false;
            }

            var w = Vector3.Dot(e2, q) * invDet;

            hitTime = w / length;

            if (hitTime < 0.0f || hitTime > 1.0f)
            {
                return false;
            }

            hitPosition = origin + direction * w;

            return true;
        }

        /// 입력: inBox(Bounds), start(Vector3), end(Vector3), extent(Vector3), hitLocation(out Vector3), hitNormal(out Vector3), hitTime(out float); 반환: bool.
        private static bool LineExtentBoxIntersection(Bounds inBox, Vector3 start, Vector3 end, Vector3 extent, out Vector3 hitLocation, out Vector3 hitNormal, out float hitTime)
        {
            hitLocation = Vector3.zero;
            hitNormal = Vector3.zero;
            hitTime = 0.0f;

            var box = inBox;
            box.max += extent;
            box.min -= extent;

            var direction = (end - start);

            Vector3 time;
            var inside = true;
            var faceDirection = Vector3.one;

            if (start.x < box.min.x)
            {
                if (direction.x <= 0.0f)
                {
                    return false;
                }
                else
                {
                    inside = false;
                    faceDirection[0] = -1;
                    time.x = (box.min.x - start.x) / direction.x;
                }
            }
            else if (start.x > box.max.x)
            {
                if (direction.x >= 0.0f)
                {
                    return false;
                }
                else
                {
                    inside = false;
                    time.x = (box.max.x - start.x) / direction.x;
                }
            }
            else
            {
                time.x = 0.0f;
            }

            if (start.y < box.min.y)
            {
                if (direction.y <= 0.0f)
                {
                    return false;
                }
                else
                {
                    inside = false;
                    faceDirection[1] = -1;
                    time.y = (box.min.y - start.y) / direction.y;
                }
            }
            else if (start.y > box.max.y)
            {
                if (direction.y >= 0.0f)
                {
                    return false;
                }
                else
                {
                    inside = false;
                    time.y = (box.max.y - start.y) / direction.y;
                }
            }
            else
            {
                time.y = 0.0f;
            }

            if (start.z < box.min.z)
            {
                if (direction.z <= 0.0f)
                {
                    return false;
                }
                else
                {
                    inside = false;
                    faceDirection[2] = -1;
                    time.z = (box.min.z - start.z) / direction.z;
                }
            }
            else if (start.z > box.max.z)
            {
                if (direction.z >= 0.0f)
                {
                    return false;
                }
                else
                {
                    inside = false;
                    time.z = (box.max.z - start.z) / direction.z;
                }
            }
            else
            {
                time.z = 0.0f;
            }

            // 선분 시작점이 box 안이면 즉시 적중으로 처리합니다.
            if (inside)
            {
                hitLocation = start;
                hitNormal.z = 0;
                return true;
            }
            // 바깥에서 시작했다면 가장 먼저 만나는 축의 교차 시점을 계산합니다.
            else
            {
                if (time.y > time.z)
                {
                    hitTime = time.y;
                    hitNormal.y = faceDirection[1];
                }
                else
                {
                    hitTime = time.z;
                    hitNormal.z = faceDirection[2];
                }

                if (time.x > hitTime)
                {
                    hitTime = time.x;
                    hitNormal.x = faceDirection[0];
                }

                if (hitTime >= 0.0f && hitTime <= 1.0f)
                {
                    hitLocation = start + direction * hitTime;
                    const float BOX_SIDE_THRESHOLD = 0.1f;
                    if (hitLocation.x > box.min.x - BOX_SIDE_THRESHOLD && hitLocation.x < box.max.x + BOX_SIDE_THRESHOLD &&
                        hitLocation.y > box.min.y - BOX_SIDE_THRESHOLD && hitLocation.y < box.max.y + BOX_SIDE_THRESHOLD &&
                        hitLocation.z > box.min.z - BOX_SIDE_THRESHOLD && hitLocation.z < box.max.z + BOX_SIDE_THRESHOLD)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
