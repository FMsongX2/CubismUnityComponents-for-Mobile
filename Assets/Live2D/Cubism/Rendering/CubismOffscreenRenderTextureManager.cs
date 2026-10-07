/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// Cubism offscreen 합성용 RenderTexture를 공용 pool로 만들고 대여·크기 동기화·해제를 관리합니다.


using System;
using UnityEngine;
using UnityEngine.Rendering;


namespace Live2D.Cubism.Rendering
{
    public class CubismOffscreenRenderTextureManager
    {
        private static readonly string RenderTextureControllerName = "CubismRenderTextureController";

        private static readonly int OffscreenRenderTextureDefaultCount = 0;

        private static CubismOffscreenRenderTextureManager Instance;

        /// 입력: 없음; 반환: CubismOffscreenRenderTextureManager.
        public static CubismOffscreenRenderTextureManager GetInstance()
        {
            if (Instance == null)
            {
                // 처음 요청한 호출자가 이후 모든 모델이 공유할 manager를 만듭니다.
                Instance = new CubismOffscreenRenderTextureManager();

                if (Application.isPlaying)
                {
                    // 이미 scene 전환 수명을 관리하는 공용 controller가 있는지 확인합니다.
                    Instance._isRenderTextureControllerInstantiated = GameObject.Find(RenderTextureControllerName) != null;
                }
            }

            return Instance;
        }

        private struct RenderTextureContainer
        {
            public RenderTexture RenderTexture;

            public bool InUse;
        }

        private RenderTextureContainer[] _offscreenRenderTextureContainers;

        private int _currentActiveRenderTextureCount;

        private bool _isRenderTextureControllerInstantiated;

        /// 입력: renderTexture(RenderTexture), baseTexture(RenderTexture); 반환: bool.
        private static bool NeedsResizeOrRecreate(RenderTexture renderTexture, RenderTexture baseTexture)
        {
            return !renderTexture.IsCreated()
                || renderTexture.width != baseTexture.width
                || renderTexture.height != baseTexture.height
                || renderTexture.format != baseTexture.format
                || renderTexture.antiAliasing != baseTexture.antiAliasing
                || renderTexture.wrapMode != TextureWrapMode.Repeat
                || renderTexture.filterMode != FilterMode.Point;
        }

        /// 입력: name(string), baseTexture(RenderTexture); 반환: RenderTexture.
        private RenderTexture CreateOffscreenRenderTexture(string name, RenderTexture baseTexture)
        {
            var renderTexture = new RenderTexture(baseTexture)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Point
            };

            renderTexture.Create();
            return renderTexture;
        }

        /// 입력: baseTexture(RenderTexture); 반환: 없음.
        private void Initialize(RenderTexture baseTexture)
        {
            if (Application.isPlaying && !_isRenderTextureControllerInstantiated)
            {
                var prefab = Resources.Load<GameObject>($"Live2D/Cubism/Prefabs/{RenderTextureControllerName}");

                var failedLog = string.Empty;
                if (prefab)
                {
                    // Resources prefab에서 공용 controller를 만들어 scene 전환 뒤에도 유지합니다.
                    var instance = GameObject.Instantiate(prefab);
                    if (instance)
                    {
                        instance.name = RenderTextureControllerName;
                        GameObject.DontDestroyOnLoad(instance);

                        _isRenderTextureControllerInstantiated = true;
                    }
                    else
                    {
                        // prefab은 찾았지만 instance 생성에 실패한 원인을 로그로 남깁니다.
                        failedLog =
                            $"{nameof(CubismOffscreenRenderTextureManager)}: Failed to instantiate prefab.";
                    }
                }
                else
                {
                    failedLog = $"{nameof(CubismOffscreenRenderTextureManager)}: Prefab not found in Resources/Live2D/Cubism/Prefabs/{RenderTextureControllerName}";
                }

                if (!_isRenderTextureControllerInstantiated)
                {
                    Debug.LogWarning(failedLog);
                    return;
                }
            }

            // 기준 texture가 바뀌었으므로 기존 pool의 GPU texture를 먼저 해제합니다.
            if (_offscreenRenderTextureContainers != null)
            {
                for (var i = 0; i < _offscreenRenderTextureContainers.Length; ++i)
                {
                    _offscreenRenderTextureContainers[i].RenderTexture.Release();
                }
            }

            // 새 기준 설정으로 기본 개수만큼 미사용 pool 항목을 만듭니다.
            _offscreenRenderTextureContainers = new RenderTextureContainer[OffscreenRenderTextureDefaultCount];
            for (var i = 0; i < OffscreenRenderTextureDefaultCount; ++i)
            {
                _offscreenRenderTextureContainers[i] = new RenderTextureContainer
                {
                    RenderTexture = CreateOffscreenRenderTexture("OffscreenRenderTexture_" + i, baseTexture),
                    InUse = false
                };
            }

            _currentActiveRenderTextureCount = 0;
        }

        /// 입력: commandBuffer(CommandBuffer); 반환: 없음.
        public void ClearRenderTextures(CommandBuffer commandBuffer)
        {
            for (var i = 0; i < _offscreenRenderTextureContainers?.Length; i++)
            {
                if (!_offscreenRenderTextureContainers[i].RenderTexture)
                {
                    continue;
                }

                commandBuffer.SetRenderTarget(_offscreenRenderTextureContainers[i].RenderTexture);
                commandBuffer.ClearRenderTarget(true, true, Color.clear);
            }
        }

        /// 입력: baseTexture(RenderTexture); 반환: RenderTexture.
        public RenderTexture GetOffscreenRenderTexture(RenderTexture baseTexture)
        {
            // 아직 pool이 없으면 이 기준 texture로 공용 수명을 초기화합니다.
            if (_offscreenRenderTextureContainers == null)
            {
                Initialize(baseTexture);
            }

            _currentActiveRenderTextureCount++;

            // 이미 만든 항목 중 현재 다른 controller가 쓰지 않는 texture를 찾습니다.
            for (var i = 0; i < _offscreenRenderTextureContainers?.Length; ++i)
            {
                if (_offscreenRenderTextureContainers[i].InUse
                    || !_offscreenRenderTextureContainers[i].RenderTexture)
                {
                    continue;
                }

                // 기준 texture와 생성·크기·format·sampling이 다르면 같은 항목을 다시 만듭니다.
                if (NeedsResizeOrRecreate(_offscreenRenderTextureContainers[i].RenderTexture, baseTexture))
                {
                    _offscreenRenderTextureContainers[i].RenderTexture.Release();
                    _offscreenRenderTextureContainers[i].RenderTexture.width = baseTexture.width;
                    _offscreenRenderTextureContainers[i].RenderTexture.height = baseTexture.height;
                    _offscreenRenderTextureContainers[i].RenderTexture.format = baseTexture.format;
                    _offscreenRenderTextureContainers[i].RenderTexture.antiAliasing = baseTexture.antiAliasing;
                    _offscreenRenderTextureContainers[i].RenderTexture.wrapMode = TextureWrapMode.Repeat;
                    _offscreenRenderTextureContainers[i].RenderTexture.filterMode = FilterMode.Point;
                    _offscreenRenderTextureContainers[i].RenderTexture.Create();
                }
                _offscreenRenderTextureContainers[i].InUse = true;

                // 찾은 항목을 사용 중으로 표시했으므로 호출자에게 넘깁니다.
                return _offscreenRenderTextureContainers[i].RenderTexture;
            }

            // 남은 항목이 없으면 pool을 한 칸 늘려 새 texture를 대여합니다.
            return CreateContainer(baseTexture).RenderTexture;
        }

        /// 입력: baseTexture(RenderTexture); 반환: RenderTextureContainer.
        private RenderTextureContainer CreateContainer(RenderTexture baseTexture)
        {
            if (_offscreenRenderTextureContainers == null)
            {
                Initialize(baseTexture);

                // controller 생성 실패 등으로 초기화 뒤에도 pool이 비어 있는지 다시 확인합니다.
                if (_offscreenRenderTextureContainers == null
                    || _offscreenRenderTextureContainers.Length < 1)
                {
                    // 첫 요청을 처리할 texture 항목 하나를 직접 만듭니다.
                    _offscreenRenderTextureContainers = new RenderTextureContainer[1];
                    _offscreenRenderTextureContainers[0] = new RenderTextureContainer
                    {
                        RenderTexture = CreateOffscreenRenderTexture("OffscreenRenderTexture_0", baseTexture),
                        InUse = false
                    };
                }

                _offscreenRenderTextureContainers[0].InUse = true;

                // 첫 항목을 사용 중으로 표시한 상태 그대로 반환합니다.
                return _offscreenRenderTextureContainers[0];
            }

            Array.Resize(ref _offscreenRenderTextureContainers, _offscreenRenderTextureContainers.Length + 1);
            _offscreenRenderTextureContainers[^1] = new RenderTextureContainer
            {
                RenderTexture = CreateOffscreenRenderTexture("OffscreenRenderTexture_" + (_offscreenRenderTextureContainers.Length - 1), baseTexture),
                InUse = true
            };

            return _offscreenRenderTextureContainers[^1];
        }

        /// 입력: renderController(CubismRenderController), renderTexture(RenderTexture); 반환: 없음.
        public void StopUsingRenderTexture(CubismRenderController renderController, RenderTexture renderTexture)
        {
            // 아직 대여한 pool 자체가 없으면 바꿀 상태가 없습니다.
            if (_offscreenRenderTextureContainers == null
                || !renderController)
            {
                return;
            }

            // 받은 texture와 같은 pool 항목만 찾아 대여 상태를 해제합니다.
            for (var i = 0; i < _offscreenRenderTextureContainers.Length; ++i)
            {
                if (_offscreenRenderTextureContainers[i].RenderTexture != renderTexture
                    || !_offscreenRenderTextureContainers[i].RenderTexture)
                {
                    continue;
                }

                // 다음 controller가 재사용할 수 있도록 미사용 상태로 되돌립니다.
                _offscreenRenderTextureContainers[i].InUse = false;

                _currentActiveRenderTextureCount--;
                break;
            }
        }

        /// 입력: 없음; 반환: 없음.
        public void Release()
        {
            // 아직 만든 pool이 없으면 해제할 GPU 자원도 없습니다.
            if (_offscreenRenderTextureContainers == null)
            {
                return;
            }

            // 모든 항목의 GPU texture와 대여 표시를 함께 지웁니다.
            for (var i = 0; i < _offscreenRenderTextureContainers.Length; ++i)
            {
                if (_offscreenRenderTextureContainers[i].RenderTexture != null)
                {
                    _offscreenRenderTextureContainers[i].RenderTexture.Release();
                    _offscreenRenderTextureContainers[i].RenderTexture = null;
                }
                _offscreenRenderTextureContainers[i].InUse = false;
            }

            _offscreenRenderTextureContainers = null;
            _currentActiveRenderTextureCount = 0;
        }
    }
}
