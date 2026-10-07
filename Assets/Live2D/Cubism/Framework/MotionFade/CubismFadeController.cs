/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// Animator layer의 겹친 motion을 시간·layer weight·parameter별 fade 설정으로 혼합해 모델 값에 씁니다.


using Live2D.Cubism.Core;
using Live2D.Cubism.Framework.Motion;
using UnityEngine;


namespace Live2D.Cubism.Framework.MotionFade
{
    [RequireComponent(typeof(Animator))]
    public class CubismFadeController : MonoBehaviour, ICubismUpdatable
    {
        #region Variable

        [SerializeField]
        public CubismFadeMotionList CubismFadeMotionList;

        private CubismParameter[] DestinationParameters { get; set; }

        private CubismPart[] DestinationParts { get; set; }

        private CubismMotionController _motionController;

        [HideInInspector]
        public bool HasUpdateController { get; set; }

        private ICubismFadeState[] _fadeStates;

        private Animator _animator;

        private CubismParameterStore _parameterStore;

        private bool[] _isFading;

        private sealed class FadeMotionCurveMap
        {
            public int[] ParameterCurveIndices;
            public int[] PartCurveIndices;
            public int SourceCurveCount;
        }

        private System.Collections.Generic.Dictionary<CubismFadeMotionData, FadeMotionCurveMap> _fadeMotionCurveMaps;
        private CubismParameter[] _curveMapParameters;
        private CubismPart[] _curveMapParts;

        /// 입력: fadeMotion(CubismFadeMotionData); 반환: FadeMotionCurveMap.
        private FadeMotionCurveMap GetFadeMotionCurveMap(CubismFadeMotionData fadeMotion)
        {
            // 모델 reload로 목적지 배열 참조가 달라지면 이전 curve 번호 map은 모두 무효입니다.
            if (_fadeMotionCurveMaps == null
                || _curveMapParameters != DestinationParameters
                || _curveMapParts != DestinationParts)
            {
                _fadeMotionCurveMaps = new System.Collections.Generic.Dictionary<CubismFadeMotionData, FadeMotionCurveMap>();
                _curveMapParameters = DestinationParameters;
                _curveMapParts = DestinationParts;
            }

            if (_fadeMotionCurveMaps.TryGetValue(fadeMotion, out var map)
                && map.SourceCurveCount == fadeMotion.ParameterIds.Length)
            {
                return map;
            }

            var ids = fadeMotion.ParameterIds;
            var idToCurve = new System.Collections.Generic.Dictionary<string, int>(ids.Length);

            // 같은 ID가 여러 번 나오면 기존 순방향 검색과 같게 첫 curve를 사용합니다.
            for (var k = 0; k < ids.Length; ++k)
            {
                if (ids[k] != null && !idToCurve.ContainsKey(ids[k]))
                {
                    idToCurve.Add(ids[k], k);
                }
            }

            map = new FadeMotionCurveMap
            {
                ParameterCurveIndices = new int[DestinationParameters.Length],
                PartCurveIndices = new int[DestinationParts.Length],
                SourceCurveCount = ids.Length
            };

            for (var j = 0; j < DestinationParameters.Length; ++j)
            {
                var id = DestinationParameters[j].Id;
                map.ParameterCurveIndices[j] = id != null && idToCurve.TryGetValue(id, out var k) ? k : -1;
            }

            for (var j = 0; j < DestinationParts.Length; ++j)
            {
                var id = DestinationParts[j].Id;
                map.PartCurveIndices[j] = id != null && idToCurve.TryGetValue(id, out var k) ? k : -1;
            }

            _fadeMotionCurveMaps[fadeMotion] = map;

            return map;
        }

        #endregion

        #region Function

        /// 입력: 없음; 반환: 없음.
        public void Refresh()
        {
            _animator = GetComponent<Animator>();

            // Animator가 없으면 fade 상태를 구성할 수 없으므로 현재 cache를 쓰지 않습니다.
            if (_animator == null)
            {
                return;
            }

            DestinationParameters = this.FindCubismModel().Parameters;
            DestinationParts = this.FindCubismModel().Parts;
            _motionController = GetComponent<CubismMotionController>();
            _parameterStore = GetComponent<CubismParameterStore>();

            // 공용 update controller가 LateUpdate 순서를 관리하는지 한 번 저장합니다.
            HasUpdateController = (GetComponent<CubismUpdateController>() != null);

            _fadeStates = (ICubismFadeState[])_animator.GetBehaviours<CubismFadeStateObserver>();

            if ((_fadeStates == null || _fadeStates.Length == 0) && _motionController != null)
            {
                _fadeStates = _motionController.GetFadeStates();
            }

            if (_fadeStates == null)
            {
                return;
            }
            _isFading = new bool[_fadeStates.Length];
        }

        public int ExecutionOrder
        {
            get { return CubismUpdateExecutionOrder.CubismFadeController; }
        }

        public bool NeedsUpdateOnEditing
        {
            get { return false; }
        }

        /// 입력: 없음; 반환: 없음.
        public void OnLateUpdate()
        {
            // 비활성·미초기화 상태에서는 model 값을 일부만 쓰지 않고 이번 갱신을 건너뜁니다.
            if (!enabled || _fadeStates == null || _parameterStore == null
               || DestinationParameters == null || DestinationParts == null)
            {
                return;
            }

            var time = Time.time;
            for (var i = 0; i < _fadeStates.Length; ++i)
            {
                _isFading[i] = false;

                var playingMotions = _fadeStates[i].GetPlayingMotions();
                if (playingMotions == null || playingMotions.Count <= 1)
                {
                    continue;
                }

                var latestPlayingMotion = playingMotions[playingMotions.Count - 1];

                var playingMotionData = latestPlayingMotion.Motion;
                var elapsedTime = time - latestPlayingMotion.FadeInStartTime;
                for (var j = 0; j < playingMotionData.ParameterFadeInTimes.Length; j++)
                {
                    if ((elapsedTime <= playingMotionData.FadeInTime) ||
                        ((0 <= playingMotionData.ParameterFadeInTimes[j]) &&
                         (elapsedTime <= playingMotionData.ParameterFadeInTimes[j])) ||
                        !_fadeStates[i].GetStateTransitionFinished())
                    {
                        _isFading[i] = true;
                        break;
                    }
                }
            }

            var isFadingAllFinished = true;
            for (var i = 0; i < _fadeStates.Length; ++i)
            {
                if (_isFading[i])
                {
                    isFadingAllFinished = false;
                    continue;
                }

                var playingMotions = _fadeStates[i].GetPlayingMotions();
                for (var j = playingMotions.Count - 2; j >= 0; --j)
                {
                    var playingMotion = playingMotions[j];
                    if (time <= playingMotion.EndTime)
                    {
                        continue;
                    }

                    // 새 motion fade-in이 끝났으면 종료 시각을 지난 이전 motion을 목록에서 제거합니다.
                    _fadeStates[i].StopAnimation(j);
                }
            }

            if (isFadingAllFinished)
            {
                return;
            }

            _parameterStore.RestoreParameters();


            // 진행 중 layer만 원본 parameter를 기준으로 다시 혼합해 목적지에 씁니다.
            for (var i = 0; i < _fadeStates.Length; ++i)
            {
                if (!_isFading[i])
                {
                    continue;
                }
                UpdateFade(_fadeStates[i]);
            }
        }

        /// 입력: fadeState(ICubismFadeState); 반환: 없음.
        private void UpdateFade(ICubismFadeState fadeState)
        {
            var playingMotions = fadeState.GetPlayingMotions();

            if (playingMotions == null)
            {
                // 재생 목록 자체가 없으면 전환할 motion도 없으므로 계산하지 않습니다.
                return;
            }

            // 이 layer의 결과 전체에 곱할 weight를 읽습니다.
            // 최상위 layer는 상태 구현에서 weight 1로 제공됩니다.
            var layerWeight = fadeState.GetLayerWeight();

            var time = Time.time;

            // 이전 motion부터 현재 motion까지 각각의 fade-in/out 값을 계산합니다.
            for (var i = 0; i < playingMotions.Count; i++)
            {
                var playingMotion = playingMotions[i];

                var fadeMotion = playingMotion.Motion;
                if (fadeMotion == null)
                {
                    continue;
                }

                var elapsedTime = time - playingMotion.FadeInStartTime;
                var endTime = playingMotion.EndTime - elapsedTime;

                var fadeInTime = fadeMotion.FadeInTime;
                var fadeOutTime = fadeMotion.FadeOutTime;


                var fadeInWeight = (fadeInTime <= 0.0f)
                    ? 1.0f
                    : CubismFadeMath.GetEasingSine(elapsedTime / fadeInTime);
                var fadeOutWeight = (fadeOutTime <= 0.0f || playingMotion.EndTime < 0.0f)
                    ? 1.0f
                    : CubismFadeMath.GetEasingSine((playingMotion.EndTime - time) / fadeOutTime);


                playingMotions[i] = playingMotion;

                var motionWeight = fadeInWeight * fadeOutWeight * layerWeight;

                var curveMap = GetFadeMotionCurveMap(fadeMotion);

                // motion curve가 있는 모델 parameter에 혼합 결과를 씁니다.
                for (var j = 0; j < DestinationParameters.Length; ++j)
                {
                    var index = curveMap.ParameterCurveIndices[j];

                    if (index < 0)
                    {
                        // 이 motion에 해당 parameter ID curve가 없으면 원래 값을 유지합니다.
                        continue;
                    }


                    var value = fadeMotion.ParameterCurves[index].Evaluate(elapsedTime);

                    if (DestinationParameters[j].IsRepeat())
                    {
                        value = DestinationParameters[j].GetParameterRepeatValue(value);
                    }
                    else
                    {
                        value = DestinationParameters[j].GetParameterClampValue(value);
                    }

                    value = Evaluate(
                        value, elapsedTime, endTime,
                        fadeInWeight, fadeOutWeight,
                        fadeMotion.ParameterFadeInTimes[index], fadeMotion.ParameterFadeOutTimes[index],
                        motionWeight, DestinationParameters[j].Value);


                    DestinationParameters[j].OverrideValue(value);
                }

                // motion curve가 있는 모델 part opacity에도 같은 fade 규칙을 적용합니다.
                for (var j = 0; j < DestinationParts.Length; ++j)
                {
                    var index = curveMap.PartCurveIndices[j];

                    if (index < 0)
                    {
                        // 이 motion에 해당 part ID curve가 없으면 원래 opacity를 유지합니다.
                        continue;
                    }

                    DestinationParts[j].Opacity = Evaluate(
                            fadeMotion.ParameterCurves[index], elapsedTime, endTime,
                            fadeInWeight, fadeOutWeight,
                            fadeMotion.ParameterFadeInTimes[index], fadeMotion.ParameterFadeOutTimes[index],
                            motionWeight, DestinationParts[j].Opacity);
                }

            }
        }

        /// 입력: curve(AnimationCurve), elapsedTime(float), endTime(float), fadeInTime(float), fadeOutTime(float), parameterFadeInTime(float), parameterFadeOutTime(float), motionWeight(float), currentValue(float); 반환: float.
        public float Evaluate(
            AnimationCurve curve, float elapsedTime, float endTime,
            float fadeInTime, float fadeOutTime,
            float parameterFadeInTime, float parameterFadeOutTime,
            float motionWeight, float currentValue)
        {
            if (curve.length <= 0)
            {
                return currentValue;
            }

            // curve에서 읽은 값을 공통 motion fade 계산으로 혼합합니다.
            return Evaluate(
                curve.Evaluate(elapsedTime), elapsedTime, endTime,
                fadeInTime, fadeOutTime,
                parameterFadeInTime, parameterFadeOutTime,
                motionWeight, currentValue);
        }

        /// 입력: value(float), elapsedTime(float), endTime(float), fadeInTime(float), fadeOutTime(float), parameterFadeInTime(float), parameterFadeOutTime(float), motionWeight(float), currentValue(float); 반환: float.
        public float Evaluate(
            float value, float elapsedTime, float endTime,
            float fadeInTime, float fadeOutTime,
            float parameterFadeInTime, float parameterFadeOutTime,
            float motionWeight, float currentValue)
        {

            // parameter별 시간이 없으면 motion 전체 weight만 현재 값에 적용합니다.
            if (parameterFadeInTime < 0.0f &&
                parameterFadeOutTime < 0.0f)
            {
                return currentValue + (value - currentValue) * motionWeight;
            }

            // parameter별 시간이 있으면 별도 fade-in/out easing을 계산합니다.
            float fadeInWeight, fadeOutWeight;
            if (parameterFadeInTime < 0.0f)
            {
                fadeInWeight = fadeInTime;
            }
            else
            {
                fadeInWeight = (parameterFadeInTime < float.Epsilon)
                    ? 1.0f
                    : CubismFadeMath.GetEasingSine(elapsedTime / parameterFadeInTime);
            }

            if (parameterFadeOutTime < 0.0f)
            {
                fadeOutWeight = fadeOutTime;
            }
            else
            {
                fadeOutWeight = (parameterFadeOutTime < float.Epsilon || (endTime < 0.0f))
                    ? 1.0f
                    : CubismFadeMath.GetEasingSine(endTime / parameterFadeOutTime);
            }

            var parameterWeight = fadeInWeight * fadeOutWeight;

            return currentValue + (value - currentValue) * parameterWeight;
        }

        #endregion

        #region Unity Events Handling

        /// 입력: 없음; 반환: 없음.
        private void OnEnable()
        {
            // 활성화 시 Animator·모델·fade layer 참조를 새로 찾습니다.
            Refresh();
        }

        /// 입력: 없음; 반환: 없음.
        private void LateUpdate()
        {
            if (!HasUpdateController)
            {
                OnLateUpdate();
            }
        }

        #endregion
    }
}
