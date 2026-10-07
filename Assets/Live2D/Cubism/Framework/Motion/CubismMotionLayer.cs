/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 한 animation layer의 Playable 연결, 재생 motion 목록, fade 상태와 시작·종료 callback을 관리합니다.

using Live2D.Cubism.Framework.MotionFade;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Live2D.Cubism.Framework.Motion
{
    public class CubismMotionLayer : ICubismFadeState
    {
        #region Action

        public Action<int, int> AnimationBeginHandler;

        public Action<int, int> AnimationEndHandler;

        #endregion

        #region Variable

        public AnimationMixerPlayable PlayableOutput { get; private set; }

        private PlayableGraph _playableGraph;

        private List<CubismFadePlayingMotion> _playingMotions;

        private CubismMotionState _motionState;

        private CubismFadeMotionList _cubismFadeMotionList;

        private int _layerIndex;

        private float _layerWeight;

        private bool _isFinished;

        public bool IsFinished
        {
            get { return _isFinished; }
        }

        #endregion

        #region Fade State Interface

        /// 입력: 없음; 반환: List<CubismFadePlayingMotion>.
        public List<CubismFadePlayingMotion> GetPlayingMotions()
        {
            return _playingMotions;
        }

        /// 입력: 없음; 반환: bool.
        public bool IsDefaultState()
        {
            return false;
        }

        /// 입력: 없음; 반환: float.
        public float GetLayerWeight()
        {
            return _layerWeight;
        }

        /// 입력: 없음; 반환: bool.
        public bool GetStateTransitionFinished()
        {
            return true;
        }

        /// 입력: isFinished(bool); 반환: 없음.
        public void SetStateTransitionFinished(bool isFinished) {}

        /// 입력: index(int); 반환: 없음.
        public void StopAnimation(int index)
        {
            // 재생 중 motion 목록에서 입력 인덱스의 항목을 제거합니다.
            _playingMotions.RemoveAt(index);
        }


        /// 입력: 없음; 반환: 없음.
        public void StopAnimationClip()
        {
            // 상태 목록에서 재생이 끝난 motion state를 제거합니다.
            if (_motionState == null)
            {
                return;
            }

            _playableGraph.Disconnect(_motionState.ClipMixer, 0);
            _motionState = null;

            _isFinished = true;


            StopAllAnimation();
        }


        #endregion

        #region Function

        /// 입력: playableGraph(PlayableGraph), fadeMotionList(CubismFadeMotionList), layerIndex(int), layerWeight(float); 반환: CubismMotionLayer.
        public static CubismMotionLayer CreateCubismMotionLayer(PlayableGraph playableGraph, CubismFadeMotionList fadeMotionList, int layerIndex, float layerWeight = 1.0f)
        {
            var ret = new CubismMotionLayer();

            ret._playableGraph = playableGraph;
            ret._cubismFadeMotionList = fadeMotionList;
            ret._layerIndex = layerIndex;
            ret._layerWeight = layerWeight;
            ret._isFinished = true;
            ret._motionState = null;
            ret._playingMotions = new List<CubismFadePlayingMotion>();
            ret.PlayableOutput = AnimationMixerPlayable.Create(playableGraph, 1);

            return ret;
        }

        /// 입력: clip(AnimationClip), isLooping(bool), speed(float); 반환: CubismFadePlayingMotion.
        private CubismFadePlayingMotion CreateFadePlayingMotion(AnimationClip clip, bool isLooping, float speed = 1.0f)
        {
            var ret = new CubismFadePlayingMotion();

            var isNotFound = true;
            var instanceId = -1;
            var events = clip.events;
            for(var i = 0; i < events.Length; ++i)
            {
                if(events[i].functionName != "InstanceId")
                {
                    continue;
                }

                instanceId = events[i].intParameter;
            }

            for (int i = 0; i < _cubismFadeMotionList.MotionInstanceIds.Length; i++)
            {
                if(_cubismFadeMotionList.MotionInstanceIds[i] != instanceId)
                {
                    continue;
                }

                isNotFound = false;

                ret.Speed = speed;
                ret.StartTime = Time.time;
                ret.FadeInStartTime = Time.time;
                ret.Motion = _cubismFadeMotionList.CubismFadeMotionObjects[i];
                ret.EndTime = (ret.Motion.MotionLength <= 0)
                              ? -1
                              : ret.StartTime + ret.Motion.MotionLength / speed;
                ret.IsLooping = isLooping;
                ret.Weight = 0.0f;
                ret.InstanceId = instanceId;
                ret.IsAnimationEndEventInvoked = false;
                AnimationBeginHandler(_layerIndex, instanceId);

                break;
            }

            if(isNotFound)
            {
                Debug.LogError("CubismMotionController : Not found motion from CubismFadeMotionList.");
            }

            return ret;
        }

        /// 입력: clip(AnimationClip), isLoop(bool), speed(float); 반환: 없음.
        public void PlayAnimation(AnimationClip clip, bool isLoop = true, float speed = 1.0f)
        {
            if (_motionState != null)
            {
                _playableGraph.Disconnect(_motionState.ClipMixer, 0);
            }

            // 입력 clip의 재생 상태와 Playable을 만들고 layer 상태 목록에 연결합니다.
            _motionState = CubismMotionState.CreateCubismMotionState(_playableGraph, clip, isLoop, speed);


#if UNITY_2018_2_OR_NEWER
            PlayableOutput.DisconnectInput(0);
#else
            PlayableOutput.GetGraph().Disconnect(PlayableOutput, 0);
#endif
            PlayableOutput.ConnectInput(0, _motionState.ClipMixer, 0);
            PlayableOutput.SetInputWeight(0, 1.0f);


            // 이전 motion 종료 시각과 새 motion fade-in 시작 시각을 현재 시간으로 기록합니다.
            if ((_playingMotions.Count > 0) && (_playingMotions[_playingMotions.Count - 1].Motion != null))
            {
                var motion = _playingMotions[_playingMotions.Count - 1];

                var time = Time.time;

                var newEndTime = time + motion.Motion.FadeOutTime;

                if (newEndTime < 0.0f || newEndTime < motion.EndTime)
                {
                    motion.EndTime = newEndTime;
                }


                while (motion.IsLooping)
                {
                    if ((motion.StartTime + motion.Motion.MotionLength) >= time)
                    {
                        break;
                    }

                    motion.StartTime += motion.Motion.MotionLength;
                }

                motion.IsLooping = false;

                _playingMotions[_playingMotions.Count - 1] = motion;
            }

            // 생성한 상태를 fade 계산에 필요한 재생 motion 정보로 변환합니다.
            var playingMotion = CreateFadePlayingMotion(clip, isLoop, speed);
            _playingMotions.Add(playingMotion);

            _isFinished = false;
        }

        /// 입력: 없음; 반환: 없음.
        public void StopAllAnimation()
        {
            for(var i = _playingMotions.Count - 1; i >= 0; --i)
            {
                StopAnimation(i);
            }
        }

        /// 입력: weight(float); 반환: 없음.
        public void SetLayerWeight(float weight)
        {
            _layerWeight = weight;
        }

        /// 입력: index(int), speed(float); 반환: 없음.
        public void SetStateSpeed(int index, float speed)
        {
            // 요청 인덱스가 현재 재생 목록 밖이면 변경할 상태가 없으므로 반환합니다.
            if(index < 0)
            {
                return;
            }

            var playingMotionData = _playingMotions[index];
            var previousSpeed = playingMotionData.Speed;
            playingMotionData.Speed = speed;

            // speed 0은 일시정지입니다. 남은 시간과 클립 길이를 0으로 나누면 무한대가
            // 되므로 두 값을 현재 상태로 둡니다.
            if (speed > 0.0f && playingMotionData.EndTime >= 0.0f && previousSpeed > 0.0f)
            {
                playingMotionData.EndTime = Time.time + (playingMotionData.EndTime - Time.time) * previousSpeed / speed;
            }

            _playingMotions[index] = playingMotionData;

            _motionState.ClipMixer.SetSpeed(speed);

            if (speed > 0.0f)
            {
                _motionState.ClipPlayable.SetDuration(_motionState.Clip.length / speed - 0.0001f);
            }
        }

        /// 입력: index(int), isLoop(bool); 반환: 없음.
        public void SetStateIsLoop(int index, bool isLoop)
        {
            // 요청 인덱스가 현재 재생 목록 밖이면 변경할 상태가 없으므로 반환합니다.
            if(index < 0)
            {
                return;
            }

            if(isLoop)
            {
                _motionState.ClipPlayable.SetDuration(double.MaxValue);
            }
            else
            {
                _motionState.ClipPlayable.SetDuration(_motionState.Clip.length - 0.0001f);
            }
        }

        #endregion

        /// 입력: 없음; 반환: 없음.
        public void Update()
        {
            var isFinished = true;
            for (var i = 0; i < _playingMotions.Count; i++)
            {
                var playingMotion = _playingMotions[i];
                if (playingMotion.IsLooping)
                {
                    isFinished = false;
                    continue;
                }

                if (playingMotion.IsAnimationEndEventInvoked)
                {
                    continue;
                }

                if (Time.time > playingMotion.EndTime)
                {
                    playingMotion.IsAnimationEndEventInvoked = true;
                    _playingMotions[i] = playingMotion;
                    if (playingMotion.InstanceId.HasValue)
                    {
                        var instanceId = _playingMotions[i].InstanceId.Value;
                        AnimationEndHandler?.Invoke(_layerIndex, instanceId);
                    }
                }
                else
                {
                    isFinished = false;
                }
            }

            if (isFinished)
            {
                _isFinished = true;
            }
        }
    }
}
