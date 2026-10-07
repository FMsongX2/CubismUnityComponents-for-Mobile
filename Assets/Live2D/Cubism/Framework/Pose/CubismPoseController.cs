/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 같은 pose group의 파츠가 교차 전환될 때 앞·뒤 파츠의 opacity와 연결 파츠를 함께 갱신합니다.


using Live2D.Cubism.Core;
using System;
using UnityEngine;

namespace Live2D.Cubism.Framework.Pose
{
    public sealed class CubismPoseController : MonoBehaviour, ICubismUpdatable
    {
        #region variable

        [SerializeField]
        public int defaultPoseIndex = 0;

        private const float BackOpacityThreshold = 0.15f;

        private CubismModel _model;

        [HideInInspector]
        public bool HasUpdateController { get; set; }

        private CubismPoseData[][] _poseData;

        #endregion

        #region Function

        /// 입력: 없음; 반환: 없음.
        public void Refresh()
        {
            _model = this.FindCubismModel();

            // 모델이 없으면 part 참조를 만들 수 없으므로 기존 cache를 사용하지 않습니다.
            if (_model == null)
            {
                return;
            }

            var tags = _model
                .Parts
                .GetComponentsMany<CubismPosePart>();

            for(var i = 0; i < tags.Length; ++i)
            {
                var groupIndex = tags[i].GroupIndex;
                var partIndex = tags[i].PartIndex;

                if(_poseData == null || _poseData.Length <= groupIndex)
                {
                    Array.Resize(ref _poseData, groupIndex + 1);
                }

                if(_poseData[groupIndex] == null || _poseData[groupIndex].Length <= partIndex)
                {
                    Array.Resize(ref _poseData[groupIndex], partIndex + 1);
                }

                _poseData[groupIndex][partIndex].PosePart = tags[i];
                _poseData[groupIndex][partIndex].Part= tags[i].GetComponent<CubismPart>();

                defaultPoseIndex = (defaultPoseIndex < 0) ? 0 : defaultPoseIndex;
                if (partIndex != defaultPoseIndex)
                {
                    _poseData[groupIndex][partIndex].Part.Opacity = 0.0f;
                }

                _poseData[groupIndex][partIndex].Opacity = _poseData[groupIndex][partIndex].Part.Opacity;

                if(tags[i].Link == null || tags[i].Link.Length == 0)
                {
                    continue;
                }

                _poseData[groupIndex][partIndex].LinkParts = new CubismPart[tags[i].Link.Length];

                for(var j = 0; j < tags[i].Link.Length; ++j)
                {
                    var linkId = tags[i].Link[j];
                    _poseData[groupIndex][partIndex].LinkParts[j] = _model.Parts.FindById(linkId);
                }
            }

            // 공용 update controller가 이 pose 실행 순서를 관리하는지 저장합니다.
            HasUpdateController = (GetComponent<CubismUpdateController>() != null);
        }

        /// 입력: 없음; 반환: 없음.
        private void DoFade()
        {
            for(var groupIndex = 0; groupIndex < _poseData.Length; ++groupIndex)
            {
                var appearPartsGroupIndex = -1;
                var appearPartsGroupOpacity = 1.0f;

                // 직전 저장값보다 opacity가 커진 새 전면 part와 현재 opacity를 찾습니다.
                for (var i = 0; i < _poseData[groupIndex].Length; ++i)
                {
                    var part = _poseData[groupIndex][i].Part;

                    if(part.Opacity > _poseData[groupIndex][i].Opacity)
                    {
                        appearPartsGroupIndex = i;
                        appearPartsGroupOpacity = part.Opacity;
                        break;
                    }
                }

                // 나타나는 part가 없는 group은 뒤 part를 감출 필요가 없습니다.
                if(appearPartsGroupIndex < 0)
                {
                    continue;
                }

                // 새 part가 나타나는 동안 나머지 part는 겹침 한도를 넘지 않게 늦춰 사라집니다.
                for (var i = 0; i < _poseData[groupIndex].Length; ++i)
                {
                    // 현재 나타나는 part 자신은 뒤 opacity 제한 대상에서 제외합니다.
                    if(i == appearPartsGroupIndex)
                    {
                        continue;
                    }

                    var part = _poseData[groupIndex][i].Part;
                    var delayedOpacity = part.Opacity;
                    var backOpacity = (1.0f - delayedOpacity) * (1.0f - appearPartsGroupOpacity);

                    // 전면·후면이 동시에 보이는 비율이 임계값을 넘으면 후면 값을 낮춥니다.
                    if (backOpacity > BackOpacityThreshold)
                    {
                        delayedOpacity = 1.0f - BackOpacityThreshold / (1.0f - appearPartsGroupOpacity);
                    }

                    // animation이 더 큰 opacity를 썼더라도 계산한 후면 한도를 우선합니다.
                    if (part.Opacity > delayedOpacity)
                    {
                        part.Opacity = delayedOpacity;
                    }
                }
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void CopyPartOpacities()
        {
            for(var groupIndex = 0; groupIndex < _poseData.Length; ++groupIndex)
            {
                for (var partIndex = 0; partIndex < _poseData[groupIndex].Length; ++partIndex)
                {
                    var linkParts = _poseData[groupIndex][partIndex].LinkParts;

                    if(linkParts == null)
                    {
                        continue;
                    }

                    var opacity = _poseData[groupIndex][partIndex].Part.Opacity;

                    for (var linkIndex = 0; linkIndex < linkParts.Length; ++linkIndex)
                    {
                        var linkPart = linkParts[linkIndex];

                        if(linkPart != null)
                        {
                            linkPart.Opacity = opacity;
                        }
                    }
                }
            }
        }

        /// 입력: 없음; 반환: 없음.
        private void SavePartOpacities()
        {
            for(var groupIndex = 0; groupIndex < _poseData.Length; ++groupIndex)
            {
                for (var partIndex = 0; partIndex < _poseData[groupIndex].Length; ++partIndex)
                {
                    _poseData[groupIndex][partIndex].Opacity = _poseData[groupIndex][partIndex].Part.Opacity;
                }
            }
        }

        public int ExecutionOrder
        {
            get { return CubismUpdateExecutionOrder.CubismPoseController; }
        }

        public bool NeedsUpdateOnEditing
        {
            get { return false; }
        }

        /// 입력: 없음; 반환: 없음.
        public void OnLateUpdate()
        {
            // 비활성·미초기화 상태에서는 part opacity 일부만 바꾸지 않고 갱신을 건너뜁니다.
            if (!enabled || _model == null || _poseData == null)
            {
               return;
            }

            DoFade();
            CopyPartOpacities();
            SavePartOpacities();
        }

        #endregion

        #region Unity Event Handling

        /// 입력: 없음; 반환: 없음.
        private void OnEnable()
        {
            Refresh();
        }

        /// 입력: 없음; 반환: 없음.
        private void LateUpdate()
        {
            if(!HasUpdateController)
            {
                OnLateUpdate();
            }
        }

        #endregion
    }

}
