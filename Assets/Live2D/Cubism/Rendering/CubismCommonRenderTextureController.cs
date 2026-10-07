/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// 공용 오프스크린 RenderTexture 풀의 해제 시점을 Unity 수명과 연결합니다.


using UnityEngine;

namespace Live2D.Cubism.Rendering
{
    [ExecuteInEditMode]
    public class CubismCommonRenderTextureController : MonoBehaviour
    {
        /// 입력: 없음; 반환: 없음.
        private void OnDestroy()
        {
            CubismOffscreenRenderTextureManager.GetInstance().Release();
        }
    }
}
