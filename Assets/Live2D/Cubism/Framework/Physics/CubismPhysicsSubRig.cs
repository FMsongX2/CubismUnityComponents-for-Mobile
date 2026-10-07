/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 한 physics rig 안에서 입력 parameter를 pendulum 파티클로 계산해 출력 parameter에 반영하는 하위 단위입니다.


using Live2D.Cubism.Core;
using System;
using UnityEngine;


namespace Live2D.Cubism.Framework.Physics
{
    [Serializable]
    public class CubismPhysicsSubRig
    {
        [SerializeField]
        public string Name;

        [SerializeField]
        public CubismPhysicsInput[] Input;

        [NonSerialized]
        public CubismPhysicsInput[] OriginalInput;

        [SerializeField]
        public CubismPhysicsOutput[] Output;

        [NonSerialized]
        public CubismPhysicsOutput[] OriginalOutput;

        [SerializeField]
        public CubismPhysicsParticle[] Particles;

        [SerializeField]
        public CubismPhysicsNormalization Normalization;

        public CubismPhysicsRig Rig
        {
            get { return _rig; }
            set { _rig = value; }
        }

        [NonSerialized]
        private CubismPhysicsRig _rig;


        private struct SubRigPhysicsOutput
        {
            public float[] Output;
        }

        [NonSerialized]
        private SubRigPhysicsOutput _currentRigOutput;

        [NonSerialized]
        private SubRigPhysicsOutput _previousRigOutput;

        /// 입력: weight(float); 반환: 없음.
        public void Interpolate(float weight)
        {
            // 현재 모델 파라미터를 읽어 물리 계산에 쓸 입력값으로 누적합니다.
            for (int i = 0; i < Output.Length; ++i)
            {
                if (Output[i].Destination == null)
                {
                    var destination = Rig.Controller.Parameters.FindById(Output[i].DestinationId);
                    if (destination == null)
                    {
                        continue;
                    }

                    Output[i].Destination = destination;
                }

                UpdateOutputParameterValue(
                    Output[i].Destination,
                    ref Output[i].Destination.Value,
                    _previousRigOutput.Output[i] * (1 - weight) + _currentRigOutput.Output[i] * weight,
                    Output[i]
                );
            }
        }

        /// 입력: parameter(CubismParameter), parameterValue(ref float), translation(float), output(CubismPhysicsOutput); 반환: 없음.
        private void UpdateOutputParameterValue(CubismParameter parameter, ref float parameterValue, float translation, CubismPhysicsOutput output)
        {
            var outputScale = 1.0f;

            outputScale = output.GetScale();

            var value = translation * outputScale;


            if (value < parameter.MinimumValue)
            {
                if (value < output.ValueBelowMinimum)
                {
                    output.ValueBelowMinimum = value;
                }


                value = parameter.MinimumValue;
            }
            else if (value > parameter.MaximumValue)
            {
                if (value > output.ValueExceededMaximum)
                {
                    output.ValueExceededMaximum = value;
                }


                value = parameter.MaximumValue;
            }


            var weight = (output.Weight / CubismPhysics.MaximumWeight);

            if (weight >= 1.0f)
            {
                parameterValue = value;
            }
            else
            {
                value = (parameterValue * (1.0f - weight)) + (value * weight);
                parameterValue = value;
            }
        }


        /// 입력: strand(CubismPhysicsParticle[]), totalTranslation(Vector2), totalAngle(float), wind(Vector2), thresholdValue(float), deltaTime(float); 반환: 없음.
        private void UpdateParticles(
            CubismPhysicsParticle[] strand,
            Vector2 totalTranslation,
            float totalAngle,
            Vector2 wind,
            float thresholdValue,
            float deltaTime
            )
        {
            strand[0].Position = totalTranslation;

            var totalRadian = CubismPhysicsMath.DegreesToRadian(totalAngle);
            var currentGravity = CubismPhysicsMath.RadianToDirection(totalRadian);
            currentGravity.Normalize();

            for (var i = 1; i < strand.Length; ++i)
            {
                strand[i].Force = (currentGravity * strand[i].Acceleration) + wind;

                strand[i].LastPosition = strand[i].Position;

                // Cubism Editor의 30 FPS 기준 계수를 실제 deltaTime에 맞춰 보정합니다.
                var delay = strand[i].Delay * deltaTime * 30.0f;

                var direction = strand[i].Position - strand[i - 1].Position;
                var radian = CubismPhysicsMath.DirectionToRadian(strand[i].LastGravity, currentGravity) / CubismPhysics.AirResistance;


                direction.x = ((Mathf.Cos(radian) * direction.x) - (direction.y * Mathf.Sin(radian)));
                direction.y = ((Mathf.Sin(radian) * direction.x) + (direction.y * Mathf.Cos(radian)));


                strand[i].Position = strand[i - 1].Position + direction;


                var velocity = strand[i].Velocity * delay;
                var force = strand[i].Force * delay * delay;


                strand[i].Position = strand[i].Position + velocity + force;


                var newDirection = strand[i].Position - strand[i - 1].Position;

                newDirection.Normalize();


                strand[i].Position = strand[i - 1].Position + newDirection * strand[i].Radius;

                if (Mathf.Abs(strand[i].Position.x) < thresholdValue)
                {
                    strand[i].Position.x = 0.0f;
                }


                if (delay != 0.0f)
                {
                    strand[i].Velocity =
                            ((strand[i].Position - strand[i].LastPosition) / delay) * strand[i].Mobility;
                }


                strand[i].Force = Vector2.zero;
                strand[i].LastGravity = currentGravity;
            }
        }

        /// 입력: strand(CubismPhysicsParticle[]), totalTranslation(Vector2), totalAngle(float), wind(Vector2), thresholdValue(float); 반환: 없음.
        private void UpdateParticlesForStabilization(
            CubismPhysicsParticle[] strand,
            Vector2 totalTranslation,
            float totalAngle,
            Vector2 wind,
            float thresholdValue
            )
        {
            strand[0].Position = totalTranslation;

            var totalRadian = CubismPhysicsMath.DegreesToRadian(totalAngle);
            var currentGravity = CubismPhysicsMath.RadianToDirection(totalRadian);
            currentGravity.Normalize();

            for (var i = 1; i < strand.Length; ++i)
            {
                strand[i].Force = (currentGravity * strand[i].Acceleration) + wind;

                strand[i].LastPosition = strand[i].Position;

                strand[i].Velocity = Vector2.zero;
                var force = strand[i].Force;
                force.Normalize();

                strand[i].Position = strand[i - 1].Position + force * strand[i].Radius;

                if (Mathf.Abs(strand[i].Position.x) < thresholdValue)
                {
                    strand[i].Position.x = 0.0f;
                }

                strand[i].Force = Vector2.zero;
                strand[i].LastGravity = currentGravity;
            }
        }

        /// 입력: 없음; 반환: 없음.
        public void Initialize()
        {
            var strand = Particles;

            // 첫 입자는 전체 이동·회전을 적용한 strand의 고정 기준점으로 초기화합니다.
            strand[0].InitialPosition = Vector2.zero;
            strand[0].LastPosition = strand[0].InitialPosition;
            strand[0].LastGravity = Rig.Gravity;
            strand[0].LastGravity.y *= -1.0f;


            // 나머지 입자를 이전 입자 위치와 기본 위치 관계로 초기화합니다.
            for (var i = 1; i < strand.Length; ++i)
            {
                var radius = Vector2.zero;
                radius.y = strand[i].Radius;
                strand[i].InitialPosition = strand[i - 1].InitialPosition + radius;
                strand[i].Position = strand[i].InitialPosition;
                strand[i].LastPosition = strand[i].InitialPosition;
                strand[i].LastGravity = Rig.Gravity;
                strand[i].LastGravity.y *= -1.0f;
            }


            // 입력 binding을 초기화하며, 캐시 인덱스는 현재 파라미터 배열에서 필요할 때 다시 찾게 합니다.
            OriginalInput = new CubismPhysicsInput[Input.Length];
            for (var i = 0; i < Input.Length; ++i)
            {
                OriginalInput[i] = Input[i];
                Input[i].InitializeGetter();
                Input[i].SourceIndex = -1;
            }

            _previousRigOutput = new SubRigPhysicsOutput();
            _currentRigOutput = new SubRigPhysicsOutput();

            Array.Resize(ref _previousRigOutput.Output, Output.Length);
            Array.Resize(ref _currentRigOutput.Output, Output.Length);

            // 출력 binding을 초기화하며, 대상 인덱스도 입력과 같이 필요할 때 다시 찾게 합니다.
            OriginalOutput = new CubismPhysicsOutput[Output.Length];
            for (var i = 0; i < Output.Length; ++i)
            {
                OriginalOutput[i] = Output[i];
                Output[i].InitializeGetter();
                Output[i].DestinationIndex = -1;
            }
        }


        /// 입력: deltaTime(float); 반환: 없음.
        public void Evaluate(float deltaTime)
        {
            var totalAngle = 0.0f;
            var totalTranslation = Vector2.zero;

            for (var i = 0; i < Input.Length; ++i)
            {
                ref var input = ref Input[i];
                var weight = input.Weight / CubismPhysics.MaximumWeight;

                if (input.Source == null)
                {
                    input.Source = Rig.Controller.Parameters.FindById(input.SourceId);
                    input.SourceIndex = Array.IndexOf(Rig.Controller.Parameters, input.Source);
                }
                else if (input.SourceIndex < 0)
                {
                    // Initialize 뒤 캐시 인덱스는 무효이므로 Source가 있어도 다시 찾아야 -1 접근 예외를 피합니다.
                    input.SourceIndex = Array.IndexOf(Rig.Controller.Parameters, input.Source);
                }

                var parameter = input.Source;
                input.GetNormalizedParameterValue(
                    ref totalTranslation,
                    ref totalAngle,
                    parameter,
                    ref Rig.ParametersCache[input.SourceIndex],
                    Normalization,
                    weight
                    );
            }


            var radAngle = CubismPhysicsMath.DegreesToRadian(-totalAngle);


            totalTranslation.x = (totalTranslation.x * Mathf.Cos(radAngle) - totalTranslation.y * Mathf.Sin(radAngle));
            totalTranslation.y = (totalTranslation.x * Mathf.Sin(radAngle) + totalTranslation.y * Mathf.Cos(radAngle));


            UpdateParticles(
                Particles,
                totalTranslation,
                totalAngle,
                Rig.Wind,
                CubismPhysics.MovementThreshold * Normalization.Position.Maximum,
                deltaTime
                );


            for (var i = 0; i < Output.Length; ++i)
            {
                ref var currentRigOutput = ref _currentRigOutput.Output[i];
                _previousRigOutput.Output[i] = currentRigOutput;

                ref var output = ref Output[i];

                if (output.Destination == null)
                {
                    var destination = Rig.Controller.Parameters.FindById(output.DestinationId);
                    if (destination == null)
                    {
                        continue;
                    }

                    output.Destination = destination;
                    output.DestinationIndex = -1;
                }

                var particleIndex = output.ParticleIndex;

                if (particleIndex < 1 || particleIndex >= Particles.Length)
                {
                    continue;
                }

                // controller 수명 동안 파라미터 배열은 같으므로 대상 binding이 바뀔 때만 인덱스를 찾고, 매 평가 선형 탐색을 피합니다.
                if (output.DestinationIndex < 0)
                {
                    output.DestinationIndex = Array.IndexOf(Rig.Controller.Parameters, output.Destination);

                    if (output.DestinationIndex < 0)
                    {
                        continue;
                    }
                }

                var translation = Particles[particleIndex].Position -
                                        Particles[particleIndex - 1].Position;

                var parameter = output.Destination;
                var outputValue = output.GetValue(
                    translation,
                    Particles,
                    particleIndex,
                    Rig.Gravity
                    );

                currentRigOutput = outputValue;

                UpdateOutputParameterValue(parameter, ref Rig.ParametersCache[output.DestinationIndex], outputValue, output);
            }
        }

        /// 입력: 없음; 반환: 없음.
        public void Stabilization()
        {
            var totalAngle = 0.0f;
            var totalTranslation = Vector2.zero;

            for (var i = 0; i < Input.Length; ++i)
            {
                var weight = Input[i].Weight / CubismPhysics.MaximumWeight;

                if (Input[i].Source == null)
                {
                    Input[i].Source = Rig.Controller.Parameters.FindById(Input[i].SourceId);
                    Input[i].SourceIndex = Array.IndexOf(Rig.Controller.Parameters, Input[i].Source);
                }
                else if (Input[i].SourceIndex < 0)
                {
                    Input[i].SourceIndex = Array.IndexOf(Rig.Controller.Parameters, Input[i].Source);
                }
                var index = Input[i].SourceIndex;

                var parameter = Input[i].Source;
                Input[i].GetNormalizedParameterValue(
                    ref totalTranslation,
                    ref totalAngle,
                    parameter,
                    ref Input[i].Source.Value,
                    Normalization,
                    weight
                    );
                Rig.ParametersCache[index] = Input[i].Source.Value;
            }


            var radAngle = CubismPhysicsMath.DegreesToRadian(-totalAngle);


            totalTranslation.x = (totalTranslation.x * Mathf.Cos(radAngle) - totalTranslation.y * Mathf.Sin(radAngle));
            totalTranslation.y = (totalTranslation.x * Mathf.Sin(radAngle) + totalTranslation.y * Mathf.Cos(radAngle));


            UpdateParticlesForStabilization(
                Particles,
                totalTranslation,
                totalAngle,
                Rig.Wind,
                CubismPhysics.MovementThreshold * Normalization.Position.Maximum
                );


            for (var i = 0; i < Output.Length; ++i)
            {
                _previousRigOutput.Output[i] = _currentRigOutput.Output[i];

                if (Output[i].Destination == null)
                {
                    var destination = Rig.Controller.Parameters.FindById(Output[i].DestinationId);
                    if (destination == null)
                    {
                        continue;
                    }

                    Output[i].Destination = destination;
                    Output[i].DestinationIndex = -1;
                }

                var particleIndex = Output[i].ParticleIndex;

                if (particleIndex < 1 || particleIndex >= Particles.Length)
                {
                    continue;
                }

                if (Output[i].DestinationIndex < 0)
                {
                    Output[i].DestinationIndex = Array.IndexOf(Rig.Controller.Parameters, Output[i].Destination);

                    if (Output[i].DestinationIndex < 0)
                    {
                        continue;
                    }
                }
                var index = Output[i].DestinationIndex;

                var translation = Particles[particleIndex].Position -
                                        Particles[particleIndex - 1].Position;

                var parameter = Output[i].Destination;
                var outputValue = Output[i].GetValue(
                    translation,
                    Particles,
                    particleIndex,
                    Rig.Gravity
                    );

                _currentRigOutput.Output[i] = outputValue;
                _previousRigOutput.Output[i] = outputValue;
                UpdateOutputParameterValue(parameter, ref Output[i].Destination.Value, outputValue, Output[i]);

                Rig.ParametersCache[index] = Output[i].Destination.Value;
            }
        }
    }
}
